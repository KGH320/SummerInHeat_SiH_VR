using System.Runtime.InteropServices;
using UnityVRMod.Core;

namespace UnityVRMod.Features.VRVisualization.OpenXR
{
    internal enum NativeCopyBatchStatus : int
    {
        Unknown = -1,
        Pending = 0,
        Executing = 1,
        Succeeded = 2,
        Cancelled = 3,
        InvalidResource = 4,
        DeviceLost = 5,
        Timeout = 6
    }

    internal static class NativeBridge
    {
        private const string NativeHelperDll = "UnityGraphicsHelper";
        public const uint RequiredAbiVersion = 2;

        [DllImport(NativeHelperDll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "GetUnityGraphicsHelperAbiVersion")]
        private static extern uint GetUnityGraphicsHelperAbiVersion_Internal();

        [DllImport(NativeHelperDll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "InitializeGraphicsDeviceFromResource")]
        private static extern int InitializeGraphicsDeviceFromResource_Internal(IntPtr textureResource);

        [DllImport(NativeHelperDll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "IsGraphicsDeviceReady")]
        private static extern int IsGraphicsDeviceReady_Internal();

        [DllImport(NativeHelperDll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "GetGraphicsDeviceGeneration")]
        public static extern ulong GetGraphicsDeviceGeneration();

        [DllImport(NativeHelperDll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "GetLastGraphicsDeviceRemovedReason")]
        public static extern int GetLastGraphicsDeviceRemovedReason();

        [DllImport(NativeHelperDll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "GetCopyRenderEventFunc")]
        public static extern IntPtr GetCopyRenderEventFunc();

        [DllImport(NativeHelperDll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "GetCopyRenderEventId")]
        public static extern int GetCopyRenderEventId();

        [DllImport(NativeHelperDll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "CreateD3D11CopyBatch")]
        public static extern NativeCopyBatchStatus CreateD3D11CopyBatch(
            IntPtr destinationLeft,
            IntPtr sourceLeft,
            IntPtr destinationRight,
            IntPtr sourceRight,
            out ulong ticket);

        [DllImport(NativeHelperDll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "WaitD3D11CopyBatch")]
        public static extern NativeCopyBatchStatus WaitD3D11CopyBatch(ulong ticket, uint timeoutMilliseconds);

        [DllImport(NativeHelperDll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "CancelD3D11CopyBatch")]
        public static extern NativeCopyBatchStatus CancelD3D11CopyBatch(ulong ticket);

        [DllImport(NativeHelperDll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ReleaseD3D11CopyBatch")]
        public static extern void ReleaseD3D11CopyBatch(ulong ticket);

        [DllImport(NativeHelperDll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "GetD3D11Device")]
        private static extern IntPtr GetCachedD3D11Device_Internal();

        public static bool TryInitializeForOpenXr(
            Texture textureForFallback,
            out IntPtr d3d11Device,
            out IntPtr copyRenderEvent,
            out int copyRenderEventId,
            out ulong deviceGeneration,
            out string error)
        {
            d3d11Device = IntPtr.Zero;
            copyRenderEvent = IntPtr.Zero;
            copyRenderEventId = 0;
            deviceGeneration = 0;
            error = null;

            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Direct3D11)
            {
                error = $"UnityGraphicsHelper ABI v{RequiredAbiVersion} requires Direct3D 11; current device is {SystemInfo.graphicsDeviceType}.";
                return false;
            }

            try
            {
                uint actualAbiVersion = GetUnityGraphicsHelperAbiVersion_Internal();
                if (actualAbiVersion != RequiredAbiVersion)
                {
                    error = $"UnityGraphicsHelper ABI mismatch. Required v{RequiredAbiVersion}, loaded v{actualAbiVersion}. VR initialization was stopped to avoid unsafe texture copies.";
                    return false;
                }

                int helperWasReady = IsGraphicsDeviceReady_Internal();
                if (textureForFallback != null)
                {
                    IntPtr nativeTexturePtr = textureForFallback.GetNativeTexturePtr();
                    if (nativeTexturePtr == IntPtr.Zero || InitializeGraphicsDeviceFromResource_Internal(nativeTexturePtr) == 0)
                    {
                        error = "UnityGraphicsHelper failed to validate or initialize its D3D11 device from Unity's fallback texture.";
                        return false;
                    }
                }
                else if (helperWasReady == 0)
                {
                    error = "UnityGraphicsHelper has no graphics device and no fallback texture was available.";
                    return false;
                }

                d3d11Device = GetCachedD3D11Device_Internal();
                copyRenderEvent = GetCopyRenderEventFunc();
                copyRenderEventId = GetCopyRenderEventId();
                deviceGeneration = GetGraphicsDeviceGeneration();

                if (d3d11Device == IntPtr.Zero || copyRenderEvent == IntPtr.Zero || deviceGeneration == 0)
                {
                    error = "UnityGraphicsHelper ABI v2 initialized without a valid D3D11 device, render callback, or device generation.";
                    return false;
                }

                return true;
            }
            catch (DllNotFoundException ex)
            {
                error = $"UnityGraphicsHelper could not be loaded: {ex.Message}";
                return false;
            }
            catch (EntryPointNotFoundException ex)
            {
                error = $"The loaded UnityGraphicsHelper is older than ABI v{RequiredAbiVersion}: {ex.Message}";
                return false;
            }
            catch (Exception ex)
            {
                VRModCore.LogError("UnityGraphicsHelper ABI v2 initialization failed:", ex);
                error = ex.Message;
                return false;
            }
        }
    }
}
