#include "pch.h"
#include "framework.h"

#include <Windows.h>
#include <d3d11.h>

#include <atomic>
#include <cstdint>
#include <memory>
#include <mutex>
#include <new>
#include <unordered_map>

#include "IUnityInterface.h"
#include "IUnityGraphics.h"
#include "IUnityGraphicsD3D11.h"

namespace
{
    constexpr uint32_t kUnityGraphicsHelperAbiVersion = 4;
    constexpr int kFallbackCopyEventId = 0x53494804; // "SIH" + ABI v4.

    // ABI v4 values; keep in sync with NativeCopyBatchStatus in C#.
    enum class CopyBatchStatus : int32_t
    {
        Unknown = -1,
        Pending = 0,
        Executing = 1,
        Submitted = 2,
        Cancelled = 3,
        InvalidResource = 4,
        DeviceLost = 5,
        Timeout = 6,
    };

    struct CopyOperation
    {
        ID3D11Resource* destination = nullptr;
        ID3D11Resource* source = nullptr;
    };

    struct CopyBatch
    {
        CopyOperation operations[2];
        DXGI_FORMAT expectedSourceFormat = DXGI_FORMAT_UNKNOWN;
        DXGI_FORMAT expectedDestinationFormat = DXGI_FORMAT_UNKNOWN;
        uint64_t deviceGeneration = 0;
        std::atomic<CopyBatchStatus> status{ CopyBatchStatus::Pending };
        HANDLE completionEvent = nullptr;

        ~CopyBatch()
        {
            for (CopyOperation& operation : operations)
            {
                if (operation.destination)
                {
                    operation.destination->Release();
                    operation.destination = nullptr;
                }

                if (operation.source)
                {
                    operation.source->Release();
                    operation.source = nullptr;
                }
            }

            if (completionEvent)
            {
                CloseHandle(completionEvent);
                completionEvent = nullptr;
            }
        }
    };

    IUnityInterfaces* g_UnityInterfaces = nullptr;
    IUnityGraphics* g_UnityGraphics = nullptr;

    std::mutex g_DeviceMutex;
    ID3D11Device* g_D3D11Device = nullptr;
    ID3D11DeviceContext* g_ImmediateContext = nullptr;
    std::atomic<bool> g_DeviceReady{ false };
    std::atomic<uint64_t> g_DeviceGeneration{ 0 };
    std::atomic<int32_t> g_LastDeviceRemovedReason{ S_OK };
    std::atomic<bool> g_ShuttingDown{ false };
    std::atomic<int> g_CopyEventId{ kFallbackCopyEventId };

    std::mutex g_BatchesMutex;
    std::unordered_map<uint64_t, std::shared_ptr<CopyBatch>> g_CopyBatches;
    std::atomic<uint64_t> g_NextCopyTicket{ 1 };

    int32_t ToInt(CopyBatchStatus status)
    {
        return static_cast<int32_t>(status);
    }

    void SignalBatch(const std::shared_ptr<CopyBatch>& batch, CopyBatchStatus status)
    {
        batch->status.store(status, std::memory_order_release);
        SetEvent(batch->completionEvent);
    }

    std::shared_ptr<CopyBatch> FindBatch(uint64_t ticket)
    {
        std::lock_guard<std::mutex> lock(g_BatchesMutex);
        const auto found = g_CopyBatches.find(ticket);
        return found != g_CopyBatches.end() ? found->second : nullptr;
    }

    void FailPendingBatches(CopyBatchStatus status)
    {
        std::lock_guard<std::mutex> lock(g_BatchesMutex);
        for (const auto& entry : g_CopyBatches)
        {
            const std::shared_ptr<CopyBatch>& batch = entry.second;
            CopyBatchStatus expected = CopyBatchStatus::Pending;
            if (batch->status.compare_exchange_strong(
                    expected,
                    status,
                    std::memory_order_acq_rel,
                    std::memory_order_acquire))
            {
                SetEvent(batch->completionEvent);
            }
        }
    }

    void ReleaseGraphicsDeviceLocked()
    {
        g_DeviceReady.store(false, std::memory_order_release);

        if (g_D3D11Device)
        {
            const HRESULT deviceReason = g_D3D11Device->GetDeviceRemovedReason();
            if (FAILED(deviceReason))
            {
                g_LastDeviceRemovedReason.store(deviceReason, std::memory_order_release);
            }
        }

        if (g_ImmediateContext)
        {
            g_ImmediateContext->Release();
            g_ImmediateContext = nullptr;
        }

        if (g_D3D11Device)
        {
            g_D3D11Device->Release();
            g_D3D11Device = nullptr;
        }
    }

    bool InstallGraphicsDevice(ID3D11Device* device)
    {
        if (!device || g_ShuttingDown.load(std::memory_order_acquire))
        {
            return false;
        }

        bool deviceChanged = false;
        bool installed = false;
        {
            std::lock_guard<std::mutex> lock(g_DeviceMutex);

            if (g_DeviceReady.load(std::memory_order_relaxed) &&
                g_D3D11Device == device &&
                g_ImmediateContext)
            {
                return true;
            }

            ReleaseGraphicsDeviceLocked();

            device->AddRef();
            g_D3D11Device = device;
            g_D3D11Device->GetImmediateContext(&g_ImmediateContext);
            installed = g_ImmediateContext != nullptr;
            if (!installed)
            {
                ReleaseGraphicsDeviceLocked();
            }
            else
            {
                g_LastDeviceRemovedReason.store(S_OK, std::memory_order_release);
                g_DeviceGeneration.fetch_add(1, std::memory_order_acq_rel);
                g_DeviceReady.store(true, std::memory_order_release);

            }

            deviceChanged = true;
        }

        if (deviceChanged)
        {
            FailPendingBatches(CopyBatchStatus::DeviceLost);
        }

        return installed;
    }

    void ClearGraphicsDevice()
    {
        bool hadDevice = false;
        {
            std::lock_guard<std::mutex> lock(g_DeviceMutex);
            hadDevice = g_D3D11Device != nullptr || g_ImmediateContext != nullptr ||
                        g_DeviceReady.load(std::memory_order_relaxed);
            ReleaseGraphicsDeviceLocked();
            if (hadDevice)
            {
                g_DeviceGeneration.fetch_add(1, std::memory_order_acq_rel);
            }
        }

        if (hadDevice)
        {
            FailPendingBatches(CopyBatchStatus::DeviceLost);
        }
    }

    bool ResourceBelongsToDevice(ID3D11Resource* resource, ID3D11Device* expectedDevice)
    {
        if (!resource || !expectedDevice)
        {
            return false;
        }

        ID3D11Device* resourceDevice = nullptr;
        resource->GetDevice(&resourceDevice);
        const bool matches = resourceDevice == expectedDevice;
        if (resourceDevice)
        {
            resourceDevice->Release();
        }
        return matches;
    }

    bool GetTextureDescription(ID3D11Resource* resource, D3D11_TEXTURE2D_DESC& description)
    {
        if (!resource)
        {
            return false;
        }

        ID3D11Texture2D* texture = nullptr;
        const HRESULT result = resource->QueryInterface(__uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&texture));
        if (FAILED(result) || !texture)
        {
            return false;
        }

        texture->GetDesc(&description);
        texture->Release();
        return true;
    }

    bool IsExpectedBgra8StorageFormat(DXGI_FORMAT resourceFormat, DXGI_FORMAT expectedTypedFormat)
    {
        // OpenXR and Unity may allocate a typeless BGRA8 resource and apply the
        // requested 87/91 color semantics through their own views. A typed
        // resource must still match exactly so 87 and 91 are never mixed.
        return resourceFormat == expectedTypedFormat ||
               resourceFormat == DXGI_FORMAT_B8G8R8A8_TYPELESS;
    }

    bool ValidateCopyOperation(
        const CopyOperation& operation,
        ID3D11Device* expectedDevice,
        DXGI_FORMAT expectedSourceFormat,
        DXGI_FORMAT expectedDestinationFormat)
    {
        if (!ResourceBelongsToDevice(operation.destination, expectedDevice) ||
            !ResourceBelongsToDevice(operation.source, expectedDevice))
        {
            return false;
        }

        D3D11_TEXTURE2D_DESC destinationDescription = {};
        D3D11_TEXTURE2D_DESC sourceDescription = {};
        if (!GetTextureDescription(operation.destination, destinationDescription) ||
            !GetTextureDescription(operation.source, sourceDescription))
        {
            return false;
        }

        const bool expectedFormatIsSupported =
            (expectedSourceFormat == DXGI_FORMAT_B8G8R8A8_UNORM ||
             expectedSourceFormat == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB) &&
            (expectedDestinationFormat == DXGI_FORMAT_B8G8R8A8_UNORM ||
             expectedDestinationFormat == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB);
        const bool destinationFormatIsCompatible = IsExpectedBgra8StorageFormat(
            destinationDescription.Format, expectedDestinationFormat);
        const bool sourceFormatIsCompatible = IsExpectedBgra8StorageFormat(
            sourceDescription.Format, expectedSourceFormat);

        return expectedFormatIsSupported &&
               destinationFormatIsCompatible &&
               sourceFormatIsCompatible &&
               destinationDescription.Width == sourceDescription.Width &&
               destinationDescription.Height == sourceDescription.Height &&
               destinationDescription.MipLevels >= 1 &&
               sourceDescription.MipLevels >= 1 &&
               destinationDescription.ArraySize == 1 &&
               sourceDescription.ArraySize == 1 &&
               destinationDescription.SampleDesc.Count == 1 &&
               sourceDescription.SampleDesc.Count == 1;
    }

    bool CopyBatchDimensionsMatch(const CopyBatch& batch)
    {
        D3D11_TEXTURE2D_DESC leftDestination = {};
        D3D11_TEXTURE2D_DESC leftSource = {};
        D3D11_TEXTURE2D_DESC rightDestination = {};
        D3D11_TEXTURE2D_DESC rightSource = {};
        if (!GetTextureDescription(batch.operations[0].destination, leftDestination) ||
            !GetTextureDescription(batch.operations[0].source, leftSource) ||
            !GetTextureDescription(batch.operations[1].destination, rightDestination) ||
            !GetTextureDescription(batch.operations[1].source, rightSource))
        {
            return false;
        }

        return leftDestination.Width == rightDestination.Width &&
               leftDestination.Height == rightDestination.Height &&
               leftSource.Width == rightSource.Width &&
               leftSource.Height == rightSource.Height;
    }

    CopyBatchStatus ExecuteCopyBatch(const std::shared_ptr<CopyBatch>& batch)
    {
        std::lock_guard<std::mutex> lock(g_DeviceMutex);

        const HRESULT deviceReason = g_D3D11Device
            ? g_D3D11Device->GetDeviceRemovedReason()
            : DXGI_ERROR_DEVICE_REMOVED;
        if (FAILED(deviceReason))
        {
            g_LastDeviceRemovedReason.store(deviceReason, std::memory_order_release);
        }

        if (!g_DeviceReady.load(std::memory_order_acquire) ||
            !g_D3D11Device ||
            !g_ImmediateContext ||
            batch->deviceGeneration != g_DeviceGeneration.load(std::memory_order_acquire) ||
            FAILED(deviceReason))
        {
            return CopyBatchStatus::DeviceLost;
        }

        if (!ValidateCopyOperation(
                batch->operations[0], g_D3D11Device,
                batch->expectedSourceFormat, batch->expectedDestinationFormat) ||
            !ValidateCopyOperation(
                batch->operations[1], g_D3D11Device,
                batch->expectedSourceFormat, batch->expectedDestinationFormat) ||
            !CopyBatchDimensionsMatch(*batch))
        {
            return CopyBatchStatus::InvalidResource;
        }

        g_ImmediateContext->CopySubresourceRegion(
            batch->operations[0].destination, 0, 0, 0, 0,
            batch->operations[0].source, 0, nullptr);
        g_ImmediateContext->CopySubresourceRegion(
            batch->operations[1].destination, 0, 0, 0, 0,
            batch->operations[1].source, 0, nullptr);
        g_ImmediateContext->Flush();

        const HRESULT submitDeviceReason = g_D3D11Device->GetDeviceRemovedReason();
        if (FAILED(submitDeviceReason))
        {
            g_LastDeviceRemovedReason.store(submitDeviceReason, std::memory_order_release);
            return CopyBatchStatus::DeviceLost;
        }

        return batch->deviceGeneration == g_DeviceGeneration.load(std::memory_order_acquire)
            ? CopyBatchStatus::Submitted
            : CopyBatchStatus::DeviceLost;
    }

    void UNITY_INTERFACE_API OnCopyRenderEvent(int eventId, void* data)
    {
        const int currentEventId = g_CopyEventId.load(std::memory_order_acquire);
        if ((eventId != currentEventId && eventId != kFallbackCopyEventId) || !data)
        {
            return;
        }

        const uint64_t ticket = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(data));
        const std::shared_ptr<CopyBatch> batch = FindBatch(ticket);
        if (!batch)
        {
            return;
        }

        CopyBatchStatus expected = CopyBatchStatus::Pending;
        if (!batch->status.compare_exchange_strong(
                expected,
                CopyBatchStatus::Executing,
                std::memory_order_acq_rel,
                std::memory_order_acquire))
        {
            return;
        }

        SignalBatch(batch, ExecuteCopyBatch(batch));
    }

    void UNITY_INTERFACE_API OnGraphicsDeviceEvent(UnityGfxDeviceEventType eventType)
    {
        switch (eventType)
        {
        case kUnityGfxDeviceEventInitialize:
        case kUnityGfxDeviceEventAfterReset:
        {
            ID3D11Device* unityDevice = nullptr;
            if (g_UnityInterfaces && g_UnityGraphics &&
                g_UnityGraphics->GetRenderer() == kUnityGfxRendererD3D11)
            {
                IUnityGraphicsD3D11* d3d11 = g_UnityInterfaces->Get<IUnityGraphicsD3D11>();
                if (d3d11)
                {
                    unityDevice = d3d11->GetDevice();
                }
            }

            if (!InstallGraphicsDevice(unityDevice))
            {
                ClearGraphicsDevice();
            }
            break;
        }

        case kUnityGfxDeviceEventBeforeReset:
        case kUnityGfxDeviceEventShutdown:
            ClearGraphicsDevice();
            break;
        }
    }
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* unityInterfaces)
{
    g_ShuttingDown.store(false, std::memory_order_release);
    g_UnityInterfaces = unityInterfaces;
    g_UnityGraphics = unityInterfaces ? unityInterfaces->Get<IUnityGraphics>() : nullptr;

    if (g_UnityGraphics)
    {
        const int reservedEventId = g_UnityGraphics->ReserveEventIDRange(1);
        if (reservedEventId >= 0)
        {
            g_CopyEventId.store(reservedEventId, std::memory_order_release);
        }
        g_UnityGraphics->RegisterDeviceEventCallback(OnGraphicsDeviceEvent);
    }

    // A plugin can be loaded after Unity's initialize event has already fired.
    OnGraphicsDeviceEvent(kUnityGfxDeviceEventInitialize);
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginUnload()
{
    g_ShuttingDown.store(true, std::memory_order_release);

    if (g_UnityGraphics)
    {
        g_UnityGraphics->UnregisterDeviceEventCallback(OnGraphicsDeviceEvent);
    }

    ClearGraphicsDevice();
    FailPendingBatches(CopyBatchStatus::DeviceLost);

    {
        std::lock_guard<std::mutex> lock(g_BatchesMutex);
        g_CopyBatches.clear();
    }

    g_UnityGraphics = nullptr;
    g_UnityInterfaces = nullptr;
    g_CopyEventId.store(kFallbackCopyEventId, std::memory_order_release);
}

extern "C" __declspec(dllexport) uint32_t GetUnityGraphicsHelperAbiVersion()
{
    return kUnityGraphicsHelperAbiVersion;
}

extern "C" __declspec(dllexport) int32_t IsGraphicsDeviceReady()
{
    return g_DeviceReady.load(std::memory_order_acquire) ? 1 : 0;
}

extern "C" __declspec(dllexport) uint64_t GetGraphicsDeviceGeneration()
{
    return g_DeviceGeneration.load(std::memory_order_acquire);
}

extern "C" __declspec(dllexport) int32_t GetLastGraphicsDeviceRemovedReason()
{
    return g_LastDeviceRemovedReason.load(std::memory_order_acquire);
}

extern "C" __declspec(dllexport) int32_t InitializeGraphicsDeviceFromResource(void* resourcePointer)
{
    if (!resourcePointer || g_ShuttingDown.load(std::memory_order_acquire))
    {
        return 0;
    }

    ID3D11Resource* resource = static_cast<ID3D11Resource*>(resourcePointer);
    ID3D11Device* resourceDevice = nullptr;
    resource->GetDevice(&resourceDevice);
    if (!resourceDevice)
    {
        return 0;
    }

    const bool installed = InstallGraphicsDevice(resourceDevice);
    resourceDevice->Release();
    return installed ? 1 : 0;
}

extern "C" __declspec(dllexport) UnityRenderingEventAndData GetCopyRenderEventFunc()
{
    return OnCopyRenderEvent;
}

extern "C" __declspec(dllexport) int32_t GetCopyRenderEventId()
{
    return g_CopyEventId.load(std::memory_order_acquire);
}

extern "C" __declspec(dllexport) int32_t CreateD3D11CopyBatch(
    void* destinationLeft,
    void* sourceLeft,
    void* destinationRight,
    void* sourceRight,
    int32_t expectedSourceDxgiFormat,
    int32_t expectedDxgiFormat,
    uint64_t* ticketOutput)
{
    if (ticketOutput)
    {
        *ticketOutput = 0;
    }

    if (!ticketOutput || !destinationLeft || !sourceLeft || !destinationRight || !sourceRight ||
        (expectedSourceDxgiFormat != DXGI_FORMAT_B8G8R8A8_UNORM &&
         expectedSourceDxgiFormat != DXGI_FORMAT_B8G8R8A8_UNORM_SRGB) ||
        (expectedDxgiFormat != DXGI_FORMAT_B8G8R8A8_UNORM &&
         expectedDxgiFormat != DXGI_FORMAT_B8G8R8A8_UNORM_SRGB))
    {
        return ToInt(CopyBatchStatus::InvalidResource);
    }

    if (!g_DeviceReady.load(std::memory_order_acquire))
    {
        return ToInt(CopyBatchStatus::DeviceLost);
    }

    try
    {
        std::shared_ptr<CopyBatch> batch = std::make_shared<CopyBatch>();
        batch->completionEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!batch->completionEvent)
        {
            return ToInt(CopyBatchStatus::Unknown);
        }

        batch->operations[0].destination = static_cast<ID3D11Resource*>(destinationLeft);
        batch->operations[0].source = static_cast<ID3D11Resource*>(sourceLeft);
        batch->operations[1].destination = static_cast<ID3D11Resource*>(destinationRight);
        batch->operations[1].source = static_cast<ID3D11Resource*>(sourceRight);
        batch->expectedSourceFormat = static_cast<DXGI_FORMAT>(expectedSourceDxgiFormat);
        batch->expectedDestinationFormat = static_cast<DXGI_FORMAT>(expectedDxgiFormat);

        for (CopyOperation& operation : batch->operations)
        {
            operation.destination->AddRef();
            operation.source->AddRef();
        }

        batch->deviceGeneration = g_DeviceGeneration.load(std::memory_order_acquire);

        ID3D11Device* expectedDevice = nullptr;
        {
            std::lock_guard<std::mutex> lock(g_DeviceMutex);
            if (!g_DeviceReady.load(std::memory_order_relaxed) || !g_D3D11Device)
            {
                return ToInt(CopyBatchStatus::DeviceLost);
            }
            expectedDevice = g_D3D11Device;
            expectedDevice->AddRef();
        }

        const bool resourcesAreValid =
            ValidateCopyOperation(
                batch->operations[0], expectedDevice,
                batch->expectedSourceFormat, batch->expectedDestinationFormat) &&
            ValidateCopyOperation(
                batch->operations[1], expectedDevice,
                batch->expectedSourceFormat, batch->expectedDestinationFormat) &&
            CopyBatchDimensionsMatch(*batch);
        expectedDevice->Release();
        if (!resourcesAreValid)
        {
            return ToInt(CopyBatchStatus::InvalidResource);
        }

        uint64_t ticket = g_NextCopyTicket.fetch_add(1, std::memory_order_relaxed);
        if (ticket == 0)
        {
            ticket = g_NextCopyTicket.fetch_add(1, std::memory_order_relaxed);
        }

        {
            std::lock_guard<std::mutex> lock(g_BatchesMutex);
            g_CopyBatches.emplace(ticket, batch);
        }

        *ticketOutput = ticket;
        return ToInt(CopyBatchStatus::Pending);
    }
    catch (const std::bad_alloc&)
    {
        return ToInt(CopyBatchStatus::Unknown);
    }
    catch (...)
    {
        return ToInt(CopyBatchStatus::Unknown);
    }
}

extern "C" __declspec(dllexport) int32_t WaitD3D11CopyBatch(uint64_t ticket, uint32_t timeoutMilliseconds)
{
    const std::shared_ptr<CopyBatch> batch = FindBatch(ticket);
    if (!batch)
    {
        return ToInt(CopyBatchStatus::Unknown);
    }

    CopyBatchStatus status = batch->status.load(std::memory_order_acquire);
    if (status != CopyBatchStatus::Pending && status != CopyBatchStatus::Executing)
    {
        return ToInt(status);
    }

    const DWORD waitResult = WaitForSingleObject(batch->completionEvent, timeoutMilliseconds);
    if (waitResult == WAIT_TIMEOUT)
    {
        return ToInt(CopyBatchStatus::Timeout);
    }
    if (waitResult != WAIT_OBJECT_0)
    {
        return ToInt(CopyBatchStatus::Unknown);
    }

    return ToInt(batch->status.load(std::memory_order_acquire));
}

extern "C" __declspec(dllexport) int32_t CancelD3D11CopyBatch(uint64_t ticket)
{
    const std::shared_ptr<CopyBatch> batch = FindBatch(ticket);
    if (!batch)
    {
        return ToInt(CopyBatchStatus::Unknown);
    }

    CopyBatchStatus expected = CopyBatchStatus::Pending;
    if (batch->status.compare_exchange_strong(
            expected,
            CopyBatchStatus::Cancelled,
            std::memory_order_acq_rel,
            std::memory_order_acquire))
    {
        SetEvent(batch->completionEvent);
        return ToInt(CopyBatchStatus::Cancelled);
    }

    return ToInt(expected);
}

extern "C" __declspec(dllexport) void ReleaseD3D11CopyBatch(uint64_t ticket)
{
    std::shared_ptr<CopyBatch> batch;
    {
        std::lock_guard<std::mutex> lock(g_BatchesMutex);
        const auto found = g_CopyBatches.find(ticket);
        if (found == g_CopyBatches.end())
        {
            return;
        }

        batch = found->second;
        g_CopyBatches.erase(found);
    }

    CopyBatchStatus expected = CopyBatchStatus::Pending;
    if (batch->status.compare_exchange_strong(
            expected,
            CopyBatchStatus::Cancelled,
            std::memory_order_acq_rel,
            std::memory_order_acquire))
    {
        SetEvent(batch->completionEvent);
    }
}

extern "C" UNITY_INTERFACE_EXPORT void* UNITY_INTERFACE_API GetD3D11Device()
{
    std::lock_guard<std::mutex> lock(g_DeviceMutex);
    return g_D3D11Device;
}
