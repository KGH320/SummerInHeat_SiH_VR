#include "pch.h"
#include "framework.h"

#include <Windows.h>
#include <d3d11.h>
#include <d3d11_1.h>

#include <atomic>
#include <cstdint>
#include <memory>
#include <mutex>
#include <new>
#include <unordered_map>

#include "IUnityInterface.h"
#include "IUnityGraphics.h"
#include "IUnityGraphicsD3D11.h"

#ifndef E_FAIL
#define E_FAIL 0x80004005
#endif

namespace
{
    constexpr uint32_t kUnityGraphicsHelperAbiVersion = 2;
    constexpr int kFallbackCopyEventId = 0x53494802; // "SIH" + ABI v2.

    // GPU 完成栅栏自旋上限:远小于托管侧 WaitD3D11CopyBatch 的 2000ms。
    // 正常两眼拷贝亚毫秒完成;250ms 只用于兜底 GPU 卡死(TDR)。
    constexpr uint32_t kCopyFenceTimeoutMs = 250;

    inline uint64_t QpcNow()
    {
        LARGE_INTEGER counter;
        QueryPerformanceCounter(&counter);
        return static_cast<uint64_t>(counter.QuadPart);
    }

    inline uint64_t QpcTicksPerMs()
    {
        static const uint64_t ticks = []() -> uint64_t {
            LARGE_INTEGER frequency;
            QueryPerformanceFrequency(&frequency);
            return static_cast<uint64_t>(frequency.QuadPart) / 1000ull;
        }();
        return ticks ? ticks : 1ull;
    }

    // ABI v2 values; keep in sync with NativeCopyBatchStatus in C#.
    enum class CopyBatchStatus : int32_t
    {
        Unknown = -1,
        Pending = 0,
        Executing = 1,
        Succeeded = 2,
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
    // GPU 完成栅栏。设备级单例,受 g_DeviceMutex 保护,随设备代际创建/销毁。
    // 不变式:批次串行执行(ExecuteCopyBatch 持 g_DeviceMutex + OnCopyRenderEvent 的 CAS),
    // 故单个 query 可安全复用。若未来改为多批次在飞,必须改为每批次独立 query。
    ID3D11Query* g_CopyFenceQuery = nullptr;
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

        if (g_CopyFenceQuery)
        {
            g_CopyFenceQuery->Release();
            g_CopyFenceQuery = nullptr;
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

                // 尽力预创建 GPU 栅栏 query;失败留待 ExecuteCopyBatch 懒创建兜底。
                D3D11_QUERY_DESC fenceDesc = {};
                fenceDesc.Query = D3D11_QUERY_EVENT;
                if (FAILED(g_D3D11Device->CreateQuery(&fenceDesc, &g_CopyFenceQuery)))
                {
                    g_CopyFenceQuery = nullptr;
                }
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

    bool IsBgra8Family(DXGI_FORMAT format)
    {
        return format == DXGI_FORMAT_B8G8R8A8_TYPELESS ||
               format == DXGI_FORMAT_B8G8R8A8_UNORM ||
               format == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB;
    }

    bool IsRgba8Family(DXGI_FORMAT format)
    {
        return format == DXGI_FORMAT_R8G8B8A8_TYPELESS ||
               format == DXGI_FORMAT_R8G8B8A8_UNORM ||
               format == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB ||
               format == DXGI_FORMAT_R8G8B8A8_UINT ||
               format == DXGI_FORMAT_R8G8B8A8_SNORM ||
               format == DXGI_FORMAT_R8G8B8A8_SINT;
    }

    bool AreFormatsCopyCompatible(DXGI_FORMAT destination, DXGI_FORMAT source)
    {
        return destination == source ||
               (IsBgra8Family(destination) && IsBgra8Family(source)) ||
               (IsRgba8Family(destination) && IsRgba8Family(source));
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

    bool ValidateCopyOperation(const CopyOperation& operation, ID3D11Device* expectedDevice)
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

        return destinationDescription.Width == sourceDescription.Width &&
               destinationDescription.Height == sourceDescription.Height &&
               destinationDescription.MipLevels == sourceDescription.MipLevels &&
               destinationDescription.ArraySize == sourceDescription.ArraySize &&
               destinationDescription.SampleDesc.Count == sourceDescription.SampleDesc.Count &&
               destinationDescription.SampleDesc.Quality == sourceDescription.SampleDesc.Quality &&
               AreFormatsCopyCompatible(destinationDescription.Format, sourceDescription.Format);
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

        if (!ValidateCopyOperation(batch->operations[0], g_D3D11Device) ||
            !ValidateCopyOperation(batch->operations[1], g_D3D11Device))
        {
            return CopyBatchStatus::InvalidResource;
        }

        // Unity owns the CopyTexture commands. This event is ordered after both
        // copies. We must not report success until the GPU has actually executed
        // them, otherwise the OpenXR runtime (Virtual Desktop / NVENC) may read the
        // swapchain image before the copy completes and crash the display driver.
        //
        // 懒创建/恢复 GPU 栅栏 query(InstallGraphicsDevice 时可能未建成)。
        if (!g_CopyFenceQuery)
        {
            D3D11_QUERY_DESC fenceDesc = {};
            fenceDesc.Query = D3D11_QUERY_EVENT;
            if (FAILED(g_D3D11Device->CreateQuery(&fenceDesc, &g_CopyFenceQuery)) || !g_CopyFenceQuery)
            {
                g_CopyFenceQuery = nullptr;

                // D3D11_QUERY_EVENT 对所有 D3D11 设备均保证支持,非设备移除情形下
                // 创建失败几乎不可能(OOM 级)。先辨别设备是否已移除。
                const HRESULT createFailReason = g_D3D11Device->GetDeviceRemovedReason();
                if (FAILED(createFailReason))
                {
                    g_LastDeviceRemovedReason.store(createFailReason, std::memory_order_release);
                    return CopyBatchStatus::DeviceLost;
                }

                // 降级:至少推交命令(不等待,残留极罕见竞态),保 VR 可用。
                g_ImmediateContext->Flush();
                return CopyBatchStatus::Succeeded;
            }
        }

        // EVENT query 只调用 End(不配 Begin)。它排在 Unity 已录入 immediate context
        // 的两条 CopyTexture 之后。随后显式 Flush 一次推动 GPU 开始执行——本屏障发生在
        // xrReleaseSwapchainImage / xrEndFrame(Present)之前,不能依赖 Unity 稍后 flush。
        g_ImmediateContext->End(g_CopyFenceQuery);
        g_ImmediateContext->Flush();

        const uint64_t deadline = QpcNow() + kCopyFenceTimeoutMs * QpcTicksPerMs();
        uint32_t spins = 0;
        for (;;)
        {
            BOOL done = FALSE;
            // 已显式 Flush 过,轮询用 DONOTFLUSH 避免每次重复 flush 的开销。
            const HRESULT getDataResult = g_ImmediateContext->GetData(
                g_CopyFenceQuery, &done, sizeof(done), D3D11_ASYNC_GETDATA_DONOTFLUSH);

            if (getDataResult == S_OK)
            {
                return CopyBatchStatus::Succeeded; // GPU 已真正执行完两条拷贝
            }
            if (FAILED(getDataResult))
            {
                return CopyBatchStatus::DeviceLost; // context/device 异常
            }
            // getDataResult == S_FALSE:尚未完成,继续等待。

            const HRESULT loopDeviceReason = g_D3D11Device->GetDeviceRemovedReason();
            if (FAILED(loopDeviceReason))
            {
                g_LastDeviceRemovedReason.store(loopDeviceReason, std::memory_order_release);
                return CopyBatchStatus::DeviceLost;
            }
            // 代际无需在循环内复查:全程持有 g_DeviceMutex,换代必先取此锁。

            if (QpcNow() >= deadline)
            {
                return CopyBatchStatus::DeviceLost; // GPU 卡死兜底(有上限)
            }

            if (++spins < 16)
            {
                YieldProcessor();  // 前若干次纯 CPU 暂停,GPU 通常微秒级完成
            }
            else
            {
                SwitchToThread();  // 之后让出时间片给本核就绪线程,无则立即返回
            }
        }
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
    uint64_t* ticketOutput)
{
    if (ticketOutput)
    {
        *ticketOutput = 0;
    }

    if (!ticketOutput || !destinationLeft || !sourceLeft || !destinationRight || !sourceRight)
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
            ValidateCopyOperation(batch->operations[0], expectedDevice) &&
            ValidateCopyOperation(batch->operations[1], expectedDevice);
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

extern "C" __declspec(dllexport) HRESULT CreateAndRegisterSRV(
    void* textureResource,
    int srvFormatDXGI,
    void** srvOutput)
{
    if (srvOutput)
    {
        *srvOutput = nullptr;
    }
    if (!textureResource || !srvOutput)
    {
        return E_FAIL;
    }

    ID3D11Device* device = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_DeviceMutex);
        if (!g_DeviceReady.load(std::memory_order_acquire) || !g_D3D11Device)
        {
            return E_FAIL;
        }
        device = g_D3D11Device;
        device->AddRef();
    }

    ID3D11Texture2D* texture = nullptr;
    HRESULT result = static_cast<ID3D11Resource*>(textureResource)->QueryInterface(
        __uuidof(ID3D11Texture2D),
        reinterpret_cast<void**>(&texture));
    if (FAILED(result) || !texture)
    {
        device->Release();
        return FAILED(result) ? result : E_FAIL;
    }

    D3D11_TEXTURE2D_DESC textureDescription = {};
    texture->GetDesc(&textureDescription);

    const DXGI_FORMAT srvFormat = static_cast<DXGI_FORMAT>(srvFormatDXGI);
    if (!ResourceBelongsToDevice(texture, device) ||
        (textureDescription.BindFlags & D3D11_BIND_SHADER_RESOURCE) == 0 ||
        textureDescription.SampleDesc.Count != 1 ||
        textureDescription.ArraySize != 1 ||
        !AreFormatsCopyCompatible(textureDescription.Format, srvFormat))
    {
        texture->Release();
        device->Release();
        return E_INVALIDARG;
    }

    D3D11_SHADER_RESOURCE_VIEW_DESC srvDescription = {};
    srvDescription.Format = srvFormat;
    srvDescription.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
    srvDescription.Texture2D.MostDetailedMip = 0;
    srvDescription.Texture2D.MipLevels = textureDescription.MipLevels == 0
        ? static_cast<UINT>(-1)
        : textureDescription.MipLevels;

    ID3D11ShaderResourceView* createdSrv = nullptr;
    result = device->CreateShaderResourceView(texture, &srvDescription, &createdSrv);
    if (SUCCEEDED(result))
    {
        *srvOutput = createdSrv;
    }
    texture->Release();
    device->Release();
    return result;
}

extern "C" __declspec(dllexport) void ReleaseNativeObject(void* object)
{
    if (object)
    {
        static_cast<IUnknown*>(object)->Release();
    }
}
