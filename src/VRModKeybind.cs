using UnityVRMod.Config;
using UniverseLib.Input; // Using UniverseLib's InputManager for universal input

#pragma warning disable IDE0130
namespace UnityVRMod.Core
#pragma warning restore IDE0130
{
    public static class VRModKeybind
    {
        public static void Update()
        {
            if (ConfigManager.ToggleSafeModeKey != null && InputManager.GetKeyDown(ConfigManager.ToggleSafeModeKey.Value))
            {
                VRModCore.LogRuntimeDebug("Toggle Safe Mode key pressed!");
                if (VRModCore.VrVisualizationFeature != null)
                {
                    VRModCore.VrVisualizationFeature.ToggleUserSafeMode();
                }
                else
                {
                    VRModCore.LogWarning("VrVisualizationManager (VrVisFeature) is null. Cannot toggle safe mode.");
                }
            }

#if OPENXR_BUILD
            if (ConfigManager.OpenXR_TogglePassthroughKey != null && InputManager.GetKeyDown(ConfigManager.OpenXR_TogglePassthroughKey.Value))
            {
                VRModCore.LogRuntimeDebug("Toggle OpenXR passthrough key pressed!");
                if (VRModCore.VrVisualizationFeature != null)
                {
                    VRModCore.VrVisualizationFeature.ToggleOpenXrPassthroughMode();
                }
                else
                {
                    VRModCore.LogWarning("VrVisualizationManager (VrVisFeature) is null. Cannot toggle OpenXR passthrough.");
                }
            }
#endif

            // REMINDER: Add other mod-specific keybind checks here if needed in the future.
        }
    }
}
