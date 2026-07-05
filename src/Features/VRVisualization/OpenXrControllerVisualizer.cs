#if OPENXR_BUILD
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityVRMod.Config;
using UnityVRMod.Core;

namespace UnityVRMod.Features.VrVisualization
{
    internal sealed class OpenXrControllerVisualizer
    {
        private static readonly Vector3 LeftModelPositionOffset = new Vector3(-0.05f, 0.05f, -0.05f);
        private static readonly Vector3 RightModelPositionOffset = new Vector3(0.05f, 0.05f, -0.05f);
        private static readonly Quaternion LeftModelRotationOffset = Quaternion.Euler(55f, 15f, 0f);
        private static readonly Quaternion RightModelRotationOffset = Quaternion.Euler(55f, -15f, 0f);

        private Transform _rigTransform;
        private GameObject _root;
        private RuntimeHandModel _leftHand;
        private RuntimeHandModel _rightHand;
        private object _assetBundle;
        private RuntimeHandPoseData _handPoseData;
        private RuntimeHandColliderLayoutData _handColliderLayoutData;
        private string _loadedBundlePath = string.Empty;
        private bool _loadAttempted;
        private bool _missingBundleLogged;
        private MethodInfo _assetBundleLoadAssetMethod;
        private MethodInfo _assetBundleUnloadMethod;

        public void Update(
            GameObject vrRig,
            int handRenderLayer,
            bool hasLeftPose,
            Vector3 leftWorldPos,
            Quaternion leftWorldRot,
            float leftGripValue,
            float leftTriggerValue,
            bool hasRightPose,
            Vector3 rightWorldPos,
            Quaternion rightWorldRot,
            float rightGripValue,
            float rightTriggerValue,
            OpenXrControlHand activeControlHand)
        {
            if (vrRig == null)
            {
                SetVisible(false, false);
                return;
            }

            EnsureInitialized(vrRig, handRenderLayer);
            if (_root == null)
            {
                return;
            }

            UpdateHand(_leftHand, isLeftHand: true, hasLeftPose, leftWorldPos, leftWorldRot, LeftModelPositionOffset, LeftModelRotationOffset, leftGripValue, leftTriggerValue);
            UpdateHand(_rightHand, isLeftHand: false, hasRightPose, rightWorldPos, rightWorldRot, RightModelPositionOffset, RightModelRotationOffset, rightGripValue, rightTriggerValue);
        }

        public void Teardown()
        {
            if (_root != null)
            {
                UnityEngine.Object.Destroy(_root);
                _root = null;
            }

            _leftHand = null;
            _rightHand = null;
            _rigTransform = null;
            OpenXrHandModelAnchorRegistry.Clear(isLeftHand: true);
            OpenXrHandModelAnchorRegistry.Clear(isLeftHand: false);

            if (_assetBundle != null && _assetBundleUnloadMethod != null)
            {
                try
                {
                    _assetBundleUnloadMethod.Invoke(_assetBundle, new object[] { false });
                }
                catch
                {
                }
            }

            _assetBundle = null;
            _handPoseData = null;
            _handColliderLayoutData = null;
            _loadedBundlePath = string.Empty;
            _loadAttempted = false;
            _missingBundleLogged = false;
        }

        private void EnsureInitialized(GameObject vrRig, int handRenderLayer)
        {
            if (vrRig == null)
            {
                return;
            }

            if (_rigTransform != null && _rigTransform != vrRig.transform)
            {
                Teardown();
            }

            if (_root != null)
            {
                return;
            }

            _rigTransform = vrRig.transform;
            _root = new GameObject("OpenXR_HandVisualizer");
            _root.transform.SetParent(vrRig.transform, false);
            _root.transform.localPosition = Vector3.zero;
            _root.transform.localRotation = Quaternion.identity;
            _root.transform.localScale = Vector3.one;

            if (!EnsureAssetBundleLoaded())
            {
                return;
            }

            _handPoseData = LoadHandPoseData();
            _handColliderLayoutData = LoadHandColliderLayoutData();

            string leftPrefabName = ConfigManager.OpenXR_LeftHandModelName?.Value ?? "LeftHand";
            string rightPrefabName = ConfigManager.OpenXR_RightHandModelName?.Value ?? "RightHand";
            _leftHand = InstantiateHandModel(leftPrefabName, "OpenXR_LeftHandModel", isLeftHand: true, handRenderLayer);
            _rightHand = InstantiateHandModel(rightPrefabName, "OpenXR_RightHandModel", isLeftHand: false, handRenderLayer);

            if (_leftHand == null || _rightHand == null)
            {
                VRModCore.LogWarning($"[OpenXR][HandModel] Failed to instantiate hand prefabs. Left='{leftPrefabName}', Right='{rightPrefabName}', Bundle='{_loadedBundlePath}'");
                return;
            }

            VRModCore.Log($"[OpenXR][HandModel] Loaded hand prefabs from AssetBundle. Left='{leftPrefabName}', Right='{rightPrefabName}'");
        }

        private bool EnsureAssetBundleLoaded()
        {
            string bundlePath = ResolveHandModelBundlePath();
            if (string.IsNullOrWhiteSpace(bundlePath) || !File.Exists(bundlePath))
            {
                if (!_missingBundleLogged)
                {
                    _missingBundleLogged = true;
                    VRModCore.LogWarning($"[OpenXR][HandModel] Hand AssetBundle not found: '{bundlePath}'. No procedural hand fallback will be created.");
                }

                return false;
            }

            if (_assetBundle != null && string.Equals(_loadedBundlePath, bundlePath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (_loadAttempted && _assetBundle == null && string.Equals(_loadedBundlePath, bundlePath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            _loadAttempted = true;
            _loadedBundlePath = bundlePath;

            Type assetBundleType = ResolveTypeAnyAssembly("UnityEngine.AssetBundle");
            MethodInfo loadFromFileMethod = assetBundleType?.GetMethod("LoadFromFile", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            _assetBundleLoadAssetMethod = assetBundleType?.GetMethod("LoadAsset", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string), typeof(Type) }, null);
            _assetBundleUnloadMethod = assetBundleType?.GetMethod("Unload", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(bool) }, null);

            if (assetBundleType == null || loadFromFileMethod == null || _assetBundleLoadAssetMethod == null)
            {
                VRModCore.LogWarning("[OpenXR][HandModel] UnityEngine.AssetBundle API not found; external hand models cannot be loaded in this runtime.");
                return false;
            }

            try
            {
                _assetBundle = loadFromFileMethod.Invoke(null, new object[] { bundlePath });
                if (_assetBundle == null)
                {
                    VRModCore.LogWarning($"[OpenXR][HandModel] AssetBundle.LoadFromFile returned null: '{bundlePath}'");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                VRModCore.LogWarning($"[OpenXR][HandModel] Failed loading AssetBundle '{bundlePath}': {ex.Message}");
                _assetBundle = null;
                return false;
            }
        }

        private RuntimeHandModel InstantiateHandModel(string prefabName, string objectName, bool isLeftHand, int handRenderLayer)
        {
            if (_assetBundle == null || _assetBundleLoadAssetMethod == null || string.IsNullOrWhiteSpace(prefabName))
            {
                return null;
            }

            try
            {
                object asset = _assetBundleLoadAssetMethod.Invoke(_assetBundle, new object[] { prefabName, typeof(GameObject) });
                if (asset is not GameObject prefab)
                {
                    VRModCore.LogWarning($"[OpenXR][HandModel] Prefab '{prefabName}' not found in AssetBundle.");
                    return null;
                }

                GameObject instance = UnityEngine.Object.Instantiate(prefab);
                instance.name = objectName;
                instance.transform.SetParent(_root.transform, false);
                instance.transform.localPosition = Vector3.zero;
                instance.transform.localRotation = Quaternion.identity;
                float scale = Mathf.Clamp(ConfigManager.OpenXR_HandModelScale?.Value ?? 1.0f, 0.01f, 100f);
                instance.transform.localScale = new Vector3(scale, scale, scale);
                SetLayerRecursively(instance, handRenderLayer);

                var model = new RuntimeHandModel(instance, isLeftHand, _handPoseData?.GetSide(isLeftHand), _handColliderLayoutData?.GetSide(isLeftHand));
                model.PrepareRuntimeComponents();
                model.ResolveBones();
                return model;
            }
            catch (Exception ex)
            {
                VRModCore.LogWarning($"[OpenXR][HandModel] Failed instantiating prefab '{prefabName}': {ex.Message}");
                return null;
            }
        }

        private RuntimeHandPoseData LoadHandPoseData()
        {
            if (_assetBundle == null || _assetBundleLoadAssetMethod == null)
            {
                return null;
            }

            string[] assetNames =
            {
                "OpenXRHandPoses",
                "OpenXRHandPoses.json",
                "assets/unityvrmod/openxrhandmodels/openxrhandposes.json"
            };

            for (int i = 0; i < assetNames.Length; i++)
            {
                string assetName = assetNames[i];
                try
                {
                    object asset = _assetBundleLoadAssetMethod.Invoke(_assetBundle, new object[] { assetName, typeof(TextAsset) });
                    if (asset is not TextAsset textAsset || string.IsNullOrWhiteSpace(textAsset.text))
                    {
                        continue;
                    }

                    RuntimeHandPoseData poseData = RuntimeHandPoseData.TryParse(textAsset.text);
                    if (poseData == null)
                    {
                        VRModCore.LogWarning($"[OpenXR][HandModel] Hand pose asset '{assetName}' was found but could not be parsed.");
                        return null;
                    }

                    VRModCore.Log($"[OpenXR][HandModel] Loaded baked hand poses from AssetBundle asset '{assetName}'.");
                    return poseData;
                }
                catch (Exception ex)
                {
                    VRModCore.LogWarning($"[OpenXR][HandModel] Failed loading hand pose asset '{assetName}': {ex.Message}");
                    return null;
                }
            }

            VRModCore.LogWarning("[OpenXR][HandModel] Baked hand pose asset 'OpenXRHandPoses' not found; falling back to runtime axis curl.");
            return null;
        }

        private RuntimeHandColliderLayoutData LoadHandColliderLayoutData()
        {
            if (_assetBundle == null || _assetBundleLoadAssetMethod == null)
            {
                return null;
            }

            string[] assetNames =
            {
                "OpenXRHandColliders",
                "OpenXRHandColliders.json",
                "assets/unityvrmod/openxrhandmodels/openxrhandcolliders.json"
            };

            for (int i = 0; i < assetNames.Length; i++)
            {
                string assetName = assetNames[i];
                try
                {
                    object asset = _assetBundleLoadAssetMethod.Invoke(_assetBundle, new object[] { assetName, typeof(TextAsset) });
                    if (asset is not TextAsset textAsset || string.IsNullOrWhiteSpace(textAsset.text))
                    {
                        continue;
                    }

                    RuntimeHandColliderLayoutData colliderLayoutData = RuntimeHandColliderLayoutData.TryParse(textAsset.text);
                    if (colliderLayoutData == null)
                    {
                        VRModCore.LogWarning($"[OpenXR][HandModel] Hand collider layout asset '{assetName}' was found but could not be parsed.");
                        return null;
                    }

                    VRModCore.Log($"[OpenXR][HandModel] Loaded baked hand collider layout from AssetBundle asset '{assetName}'.");
                    return colliderLayoutData;
                }
                catch (Exception ex)
                {
                    VRModCore.LogWarning($"[OpenXR][HandModel] Failed loading hand collider layout asset '{assetName}': {ex.Message}");
                    return null;
                }
            }

            VRModCore.LogWarning("[OpenXR][HandModel] Baked hand collider layout asset 'OpenXRHandColliders' not found; falling back to palm/fingertip colliders.");
            return null;
        }

        private void UpdateHand(
            RuntimeHandModel hand,
            bool isLeftHand,
            bool hasPose,
            Vector3 worldPos,
            Quaternion worldRot,
            Vector3 localOffset,
            Quaternion rotationOffset,
            float gripValue,
            float triggerValue)
        {
            if (hand == null || hand.Root == null)
            {
                OpenXrHandModelAnchorRegistry.Clear(isLeftHand);
                return;
            }

            if (!hasPose)
            {
                if (hand.Root.activeSelf)
                {
                    hand.Root.SetActive(false);
                }

                OpenXrHandModelAnchorRegistry.Clear(isLeftHand);
                return;
            }

            if (!hand.Root.activeSelf)
            {
                hand.Root.SetActive(true);
            }

            hand.Root.transform.position = worldPos + (worldRot * localOffset);
            hand.Root.transform.rotation = worldRot * rotationOffset;
            hand.UpdatePose(gripValue, triggerValue, ConfigManager.OpenXR_HandFingerCurlDegrees?.Value ?? 65f);
            hand.UpdateColliderAnchorRegistry();
        }

        private static string ResolveHandModelBundlePath()
        {
            string configuredPath = ConfigManager.OpenXR_HandModelBundlePath?.Value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                configuredPath = @"OpenXRHandModels\openxr_hands";
            }

            configuredPath = configuredPath.Trim().Trim('"');
            if (Path.IsPathRooted(configuredPath))
            {
                return configuredPath;
            }

            string assemblyDir = Path.GetDirectoryName(typeof(OpenXrControllerVisualizer).Assembly.Location) ?? string.Empty;
            return Path.Combine(assemblyDir, configuredPath);
        }

        private static Type ResolveTypeAnyAssembly(string fullTypeName)
        {
            Type type = Type.GetType(fullTypeName, false);
            if (type != null)
            {
                return type;
            }

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Assembly assembly = assemblies[i];
                if (assembly == null)
                {
                    continue;
                }

                type = assembly.GetType(fullTypeName, false);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        private static void SetLayerRecursively(GameObject root, int layer)
        {
            if (root == null) return;
            root.layer = layer;
            int childCount = root.transform.childCount;
            for (int i = 0; i < childCount; i++)
            {
                SetLayerRecursively(root.transform.GetChild(i).gameObject, layer);
            }
        }

        private void SetVisible(bool leftVisible, bool rightVisible)
        {
            if (_leftHand?.Root != null && _leftHand.Root.activeSelf != leftVisible)
            {
                _leftHand.Root.SetActive(leftVisible);
            }

            if (_rightHand?.Root != null && _rightHand.Root.activeSelf != rightVisible)
            {
                _rightHand.Root.SetActive(rightVisible);
            }

            if (!leftVisible)
            {
                OpenXrHandModelAnchorRegistry.Clear(isLeftHand: true);
            }

            if (!rightVisible)
            {
                OpenXrHandModelAnchorRegistry.Clear(isLeftHand: false);
            }
        }

        private sealed class RuntimeHandModel
        {
            private readonly bool _isLeftHand;
            private readonly RuntimeHandPoseSet _poseSet;
            private readonly RuntimeHandColliderLayoutSet _colliderLayoutSet;
            private readonly RuntimeFingerBones _thumb = new(OpenXrHandFinger.Thumb);
            private readonly RuntimeFingerBones _index = new(OpenXrHandFinger.Index);
            private readonly RuntimeFingerBones _middle = new(OpenXrHandFinger.Middle);
            private readonly RuntimeFingerBones _ring = new(OpenXrHandFinger.Ring);
            private readonly RuntimeFingerBones _little = new(OpenXrHandFinger.Little);
            private RuntimeBoundHandPoseSet _boundPoseSet;
            private RuntimeBoundHandColliderLayoutSet _boundColliderLayoutSet;
            private Transform _palm;
            private float _lastGrip = -1f;
            private float _lastTrigger = -1f;
            private float _lastCurlDegrees = float.NaN;
            private static string _cachedHandModelColorRaw = string.Empty;
            private static Color _cachedHandModelColor = new(0.15f, 0.15f, 0.15f, 0.30f);
            private static bool _hasLoggedInvalidHandModelColor;

            public RuntimeHandModel(GameObject root, bool isLeftHand, RuntimeHandPoseSet poseSet, RuntimeHandColliderLayoutSet colliderLayoutSet)
            {
                Root = root;
                _isLeftHand = isLeftHand;
                _poseSet = poseSet;
                _colliderLayoutSet = colliderLayoutSet;
            }

            public GameObject Root { get; }

            public void PrepareRuntimeComponents()
            {
                DisablePoseOverriders();
                NormalizeRenderMaterials();
            }

            public void ResolveBones()
            {
                Transform[] transforms = Root.GetComponentsInChildren<Transform>(true);
                _palm = FindPalm(transforms) ?? Root.transform;
                ResolveFinger(transforms, _thumb);
                ResolveFinger(transforms, _index);
                ResolveFinger(transforms, _middle);
                ResolveFinger(transforms, _ring);
                ResolveFinger(transforms, _little);
                Vector3 fourFingerCurlAxisWorld = ResolveFourFingerCurlAxisWorld();
                _thumb.CaptureCurlAxes(_palm, null);
                _index.CaptureCurlAxes(_palm, fourFingerCurlAxisWorld);
                _middle.CaptureCurlAxes(_palm, fourFingerCurlAxisWorld);
                _ring.CaptureCurlAxes(_palm, fourFingerCurlAxisWorld);
                _little.CaptureCurlAxes(_palm, fourFingerCurlAxisWorld);
                _boundPoseSet = _poseSet?.Bind(Root.transform);
                _boundColliderLayoutSet = _colliderLayoutSet?.Bind(Root.transform);

                int resolvedFingerCount = 0;
                if (_thumb.HasTip) resolvedFingerCount++;
                if (_index.HasTip) resolvedFingerCount++;
                if (_middle.HasTip) resolvedFingerCount++;
                if (_ring.HasTip) resolvedFingerCount++;
                if (_little.HasTip) resolvedFingerCount++;

                VRModCore.Log($"[OpenXR][HandModel] {Root.name} resolved palm='{_palm.name}', fingerTips={resolvedFingerCount}/5.");
                if (_boundPoseSet != null)
                {
                    VRModCore.Log($"[OpenXR][HandModel] {Root.name} bound baked pose bones={_boundPoseSet.BoneCount}.");
                }

                if (_boundColliderLayoutSet != null)
                {
                    VRModCore.Log($"[OpenXR][HandModel] {Root.name} bound baked collider layout spheres={_boundColliderLayoutSet.ColliderCount}.");
                }
            }

            public void UpdatePose(float gripValue, float triggerValue, float curlDegrees)
            {
                float grip = Mathf.Clamp01(gripValue);
                float trigger = Mathf.Clamp01(triggerValue);
                if (Mathf.Abs(grip - _lastGrip) < 0.002f
                    && Mathf.Abs(trigger - _lastTrigger) < 0.002f
                    && Mathf.Abs(curlDegrees - _lastCurlDegrees) < 0.01f)
                {
                    return;
                }

                _lastGrip = grip;
                _lastTrigger = trigger;
                _lastCurlDegrees = curlDegrees;

                if (_boundPoseSet != null)
                {
                    _boundPoseSet.Apply(grip, trigger);
                    return;
                }

                ApplyFingerCurl(_thumb, Mathf.Clamp01(grip * 0.45f + trigger * 0.15f), curlDegrees * 0.55f);
                ApplyFingerCurl(_index, trigger, curlDegrees);
                ApplyFingerCurl(_middle, grip, curlDegrees);
                ApplyFingerCurl(_ring, grip, curlDegrees);
                ApplyFingerCurl(_little, grip, curlDegrees);
            }

            public void UpdateColliderAnchorRegistry()
            {
                if (_boundColliderLayoutSet != null)
                {
                    OpenXrHandModelAnchorRegistry.Update(_isLeftHand, _boundColliderLayoutSet.BuildWorldColliderPoses());
                    return;
                }

                OpenXrHandModelAnchorRegistry.Update(
                    _isLeftHand,
                    _palm,
                    _thumb.Tip,
                    _index.Tip,
                    _middle.Tip,
                    _ring.Tip,
                    _little.Tip);
            }

            private static void ApplyFingerCurl(RuntimeFingerBones finger, float curl, float curlDegrees)
            {
                if (finger == null || !finger.HasAnyBone)
                {
                    return;
                }

                float proximalDegrees = curlDegrees * curl * 0.45f;
                float intermediateDegrees = curlDegrees * curl * 0.35f;
                float distalDegrees = curlDegrees * curl * 0.25f;

                ApplyLocalAxis(finger.Proximal, finger.ProximalOpenRotation, finger.ProximalCurlAxis, proximalDegrees);
                ApplyLocalAxis(finger.Intermediate, finger.IntermediateOpenRotation, finger.IntermediateCurlAxis, intermediateDegrees);
                ApplyLocalAxis(finger.Distal, finger.DistalOpenRotation, finger.DistalCurlAxis, distalDegrees);
            }

            private static void ApplyLocalAxis(Transform bone, Quaternion openRotation, Vector3 localAxis, float degrees)
            {
                if (bone == null)
                {
                    return;
                }

                if (localAxis.sqrMagnitude <= 0.0001f)
                {
                    localAxis = Vector3.right;
                }

                bone.localRotation = openRotation * Quaternion.AngleAxis(degrees, localAxis.normalized);
            }

            private static Transform FindPalm(Transform[] transforms)
            {
                return FindNamedTransform(transforms, "wrist_r")
                    ?? FindNamedTransform(transforms, "wrist_l")
                    ?? FindBestTransform(transforms, new[] { "palm", "hand", "wrist" }, Array.Empty<string>());
            }

            private static void ResolveFinger(Transform[] transforms, RuntimeFingerBones finger)
            {
                string[] aliases = GetFingerAliases(finger.Finger);
                var candidates = new List<Transform>(8);
                for (int i = 0; i < transforms.Length; i++)
                {
                    Transform transform = transforms[i];
                    string normalized = NormalizeName(transform.name);
                    if (ContainsAny(normalized, aliases))
                    {
                        candidates.Add(transform);
                    }
                }

                if (candidates.Count == 0)
                {
                    return;
                }

                candidates.Sort(static (a, b) => GetHierarchyDepth(a).CompareTo(GetHierarchyDepth(b)));
                string steamVrNamePrefix = GetSteamVrFingerPrefix(finger.Finger);
                finger.Proximal = FindSteamVrFingerBone(candidates, steamVrNamePrefix, "0")
                    ?? FindBestTransform(candidates, new[] { "proximal", "metacarpal", "base", "00", "0" }, new[] { "aux", "meta", "end" })
                    ?? candidates[0];
                finger.Intermediate = FindSteamVrFingerBone(candidates, steamVrNamePrefix, "1")
                    ?? FindBestTransform(candidates, new[] { "intermediate", "middle", "01", "1" }, new[] { "aux", "meta", "end", "tip" })
                    ?? FindChildAfter(finger.Proximal, candidates);
                finger.Distal = FindSteamVrFingerBone(candidates, steamVrNamePrefix, "2")
                    ?? FindBestTransform(candidates, new[] { "distal", "02", "2" }, new[] { "aux", "meta", "end" })
                    ?? FindChildAfter(finger.Intermediate, candidates);
                finger.Tip = FindSteamVrFingerBone(candidates, steamVrNamePrefix, "end")
                    ?? FindBestTransform(candidates, new[] { "tip", "end", "03", "3" }, new[] { "aux", "meta" })
                    ?? finger.Distal
                    ?? finger.Intermediate
                    ?? finger.Proximal;

                finger.CaptureOpenRotations();
            }

            private Vector3 ResolveFourFingerCurlAxisWorld()
            {
                Vector3 axisWorld;
                if (TryGetAxisBetweenFingerBases(_index, _little, out axisWorld)
                    || TryGetAxisBetweenFingerBases(_index, _ring, out axisWorld)
                    || TryGetAxisBetweenFingerBases(_middle, _little, out axisWorld))
                {
                    return axisWorld;
                }

                return Vector3.zero;
            }

            private static bool TryGetAxisBetweenFingerBases(RuntimeFingerBones from, RuntimeFingerBones to, out Vector3 axisWorld)
            {
                axisWorld = Vector3.zero;
                Transform fromBase = from?.BaseBone;
                Transform toBase = to?.BaseBone;
                if (fromBase == null || toBase == null)
                {
                    return false;
                }

                Vector3 delta = toBase.position - fromBase.position;
                if (delta.sqrMagnitude <= 0.000001f)
                {
                    return false;
                }

                axisWorld = delta.normalized;
                return true;
            }

            private void DisablePoseOverriders()
            {
                Component[] components = Root.GetComponentsInChildren<Component>(true);
                for (int i = 0; i < components.Length; i++)
                {
                    Component component = components[i];
                    if (component == null)
                    {
                        continue;
                    }

                    Type componentType = component.GetType();
                    string typeName = componentType.FullName ?? componentType.Name;
                    if (typeName.IndexOf("Animator", StringComparison.OrdinalIgnoreCase) >= 0
                        || typeName.IndexOf("Animation", StringComparison.OrdinalIgnoreCase) >= 0
                        || typeName.IndexOf("SteamVR", StringComparison.OrdinalIgnoreCase) >= 0
                        || typeName.IndexOf("Skeleton", StringComparison.OrdinalIgnoreCase) >= 0
                        || typeName.IndexOf("Poser", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (component is Behaviour behaviour)
                        {
                            behaviour.enabled = false;
                        }
                    }
                }
            }

            private void NormalizeRenderMaterials()
            {
                Renderer[] renderers = Root.GetComponentsInChildren<Renderer>(true);
                bool overrideMaterial = ConfigManager.OpenXR_OverrideHandModelMaterial?.Value ?? true;
                Color configuredColor = GetConfiguredHandModelColor();
                // 使用真正支持 alpha blend 的着色器；避开加性混合，加性混合的结果与背景颜色强相关，
                // 会造成"距离/背景不同时透明感差别很大"的错觉。Sprites/Default 不受光照与雾影响，最稳定。
                Shader transparentShader = Shader.Find("Sprites/Default")
                    ?? Shader.Find("Legacy Shaders/Transparent/Diffuse")
                    ?? Shader.Find("Unlit/Transparent Colored")
                    ?? Shader.Find("Standard");

                int normalizedRendererCount = 0;
                for (int r = 0; r < renderers.Length; r++)
                {
                    Renderer renderer = renderers[r];
                    if (renderer == null)
                    {
                        continue;
                    }

                    renderer.shadowCastingMode = ShadowCastingMode.Off;
                    renderer.receiveShadows = false;

                    Material[] materials = renderer.materials;
                    for (int m = 0; m < materials.Length; m++)
                    {
                        Material material = materials[m];
                        if (material == null)
                        {
                            continue;
                        }

                        if (!overrideMaterial)
                        {
                            continue;
                        }

                        if (transparentShader != null)
                        {
                            material.shader = transparentShader;
                        }

                        ApplyConfiguredTransparentHandMaterial(material, configuredColor);
                    }

                    renderer.materials = materials;
                    normalizedRendererCount++;
                }

                string mode = overrideMaterial ? $"transparent configured color {FormatColorForLog(configuredColor)}" : "AssetBundle materials";
                VRModCore.Log($"[OpenXR][HandModel] {Root.name} normalized renderers={normalizedRendererCount} using {mode}.");
            }

            private static void ApplyConfiguredTransparentHandMaterial(Material material, Color color)
            {
                if (material == null)
                {
                    return;
                }

                if (material.HasProperty("_Color")) material.color = color;
                if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
                if (material.HasProperty("_TintColor")) material.SetColor("_TintColor", color);
                if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", null);
                if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", null);
                if (material.HasProperty("_BumpMap")) material.SetTexture("_BumpMap", null);
                if (material.HasProperty("_MetallicGlossMap")) material.SetTexture("_MetallicGlossMap", null);
                if (material.HasProperty("_SpecGlossMap")) material.SetTexture("_SpecGlossMap", null);
                if (material.HasProperty("_EmissionMap")) material.SetTexture("_EmissionMap", null);

                if (material.HasProperty("_Mode")) material.SetFloat("_Mode", 3f);
                if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
                if (material.HasProperty("_Blend")) material.SetFloat("_Blend", 0f);
                if (material.HasProperty("_SrcBlend")) material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                if (material.HasProperty("_DstBlend")) material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                if (material.HasProperty("_ZWrite")) material.SetInt("_ZWrite", 0);
                if (material.HasProperty("_Cull")) material.SetInt("_Cull", (int)CullMode.Back);
                if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", 0f);
                if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);
                if (material.HasProperty("_SpecularHighlights")) material.SetFloat("_SpecularHighlights", 0f);
                if (material.HasProperty("_GlossyReflections")) material.SetFloat("_GlossyReflections", 0f);

                material.SetOverrideTag("RenderType", "Transparent");
                material.DisableKeyword("_ALPHATEST_ON");
                material.EnableKeyword("_ALPHABLEND_ON");
                material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                material.DisableKeyword("_NORMALMAP");
                material.DisableKeyword("_METALLICGLOSSMAP");
                material.DisableKeyword("_SPECGLOSSMAP");
                material.DisableKeyword("_EMISSION");
                material.renderQueue = (int)RenderQueue.Transparent + 50;
            }

            private static Color GetConfiguredHandModelColor()
            {
                string rawColor = ConfigManager.OpenXR_HandModelColor?.Value ?? "0.15 0.15 0.15 0.30";
                if (!string.Equals(rawColor, _cachedHandModelColorRaw, StringComparison.Ordinal))
                {
                    _cachedHandModelColorRaw = rawColor;
                    if (TryParseConfiguredRgbaColor(rawColor, out Color parsedColor))
                    {
                        _cachedHandModelColor = parsedColor;
                        _hasLoggedInvalidHandModelColor = false;
                    }
                    else
                    {
                        _cachedHandModelColor = new Color(0.15f, 0.15f, 0.15f, 0.30f);
                        if (!_hasLoggedInvalidHandModelColor)
                        {
                            VRModCore.LogWarning($"[OpenXR][HandModel] Invalid OpenXR Hand Model Color '{rawColor}'. Expected 'R G B A' or 'R,G,B,A'. Falling back to 0.15 0.15 0.15 0.30.");
                            _hasLoggedInvalidHandModelColor = true;
                        }
                    }
                }

                return _cachedHandModelColor;
            }

            private static bool TryParseConfiguredRgbaColor(string raw, out Color color)
            {
                color = new Color(0.15f, 0.15f, 0.15f, 0.30f);
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return false;
                }

                string[] tokens = raw.Split(new[] { ' ', ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length != 4)
                {
                    return false;
                }

                if (!TryParseColorComponent(tokens[0], out float r) ||
                    !TryParseColorComponent(tokens[1], out float g) ||
                    !TryParseColorComponent(tokens[2], out float b) ||
                    !TryParseAlphaComponent(tokens[3], out float a))
                {
                    return false;
                }

                color = new Color(r, g, b, a);
                return true;
            }

            private static bool TryParseColorComponent(string raw, out float value)
            {
                value = 0f;
                if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
                {
                    return false;
                }

                if (parsed > 1f)
                {
                    parsed /= 255f;
                }

                value = Mathf.Clamp01(parsed);
                return true;
            }

            private static bool TryParseAlphaComponent(string raw, out float value)
            {
                value = 0.30f;
                if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
                {
                    return false;
                }

                value = Mathf.Clamp01(parsed);
                return true;
            }

            private static string FormatColorForLog(Color color)
            {
                return string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###}, {2:0.###}, {3:0.###})", color.r, color.g, color.b, color.a);
            }

            private static Transform FindNamedTransform(IReadOnlyList<Transform> transforms, string exactName)
            {
                string normalizedExactName = NormalizeName(exactName);
                for (int i = 0; i < transforms.Count; i++)
                {
                    Transform transform = transforms[i];
                    if (transform != null && string.Equals(NormalizeName(transform.name), normalizedExactName, StringComparison.Ordinal))
                    {
                        return transform;
                    }
                }

                return null;
            }

            private static Transform FindSteamVrFingerBone(IReadOnlyList<Transform> transforms, string fingerPrefix, string segment)
            {
                if (string.IsNullOrEmpty(fingerPrefix) || string.IsNullOrEmpty(segment))
                {
                    return null;
                }

                string expected = NormalizeName($"finger_{fingerPrefix}_{segment}_r");
                for (int i = 0; i < transforms.Count; i++)
                {
                    Transform transform = transforms[i];
                    if (transform != null && string.Equals(NormalizeName(transform.name), expected, StringComparison.Ordinal))
                    {
                        return transform;
                    }
                }

                string expectedLeft = NormalizeName($"finger_{fingerPrefix}_{segment}_l");
                for (int i = 0; i < transforms.Count; i++)
                {
                    Transform transform = transforms[i];
                    if (transform != null && string.Equals(NormalizeName(transform.name), expectedLeft, StringComparison.Ordinal))
                    {
                        return transform;
                    }
                }

                return null;
            }

            private static Transform FindBestTransform(IReadOnlyList<Transform> transforms, string[] includeTokens, string[] excludeTokens)
            {
                for (int i = 0; i < transforms.Count; i++)
                {
                    Transform transform = transforms[i];
                    string normalized = NormalizeName(transform.name);
                    if (ContainsAny(normalized, includeTokens) && !ContainsAny(normalized, excludeTokens))
                    {
                        return transform;
                    }
                }

                return null;
            }

            private static Transform FindChildAfter(Transform current, IReadOnlyList<Transform> candidates)
            {
                if (current == null)
                {
                    return null;
                }

                int currentDepth = GetHierarchyDepth(current);
                Transform best = null;
                int bestDepth = int.MaxValue;
                for (int i = 0; i < candidates.Count; i++)
                {
                    Transform candidate = candidates[i];
                    int candidateDepth = GetHierarchyDepth(candidate);
                    if (candidateDepth <= currentDepth || !IsDescendantOf(candidate, current))
                    {
                        continue;
                    }

                    if (candidateDepth < bestDepth)
                    {
                        best = candidate;
                        bestDepth = candidateDepth;
                    }
                }

                return best;
            }

            private static bool IsDescendantOf(Transform transform, Transform possibleParent)
            {
                Transform current = transform.parent;
                while (current != null)
                {
                    if (current == possibleParent)
                    {
                        return true;
                    }

                    current = current.parent;
                }

                return false;
            }

            private static int GetHierarchyDepth(Transform transform)
            {
                int depth = 0;
                Transform current = transform;
                while (current != null)
                {
                    depth++;
                    current = current.parent;
                }

                return depth;
            }

            private static string[] GetFingerAliases(OpenXrHandFinger finger)
            {
                return finger switch
                {
                    OpenXrHandFinger.Thumb => new[] { "thumb" },
                    OpenXrHandFinger.Index => new[] { "index", "pointer" },
                    OpenXrHandFinger.Middle => new[] { "middle" },
                    OpenXrHandFinger.Ring => new[] { "ring" },
                    OpenXrHandFinger.Little => new[] { "little", "pinky", "pinkie" },
                    _ => Array.Empty<string>()
                };
            }

            private static string GetSteamVrFingerPrefix(OpenXrHandFinger finger)
            {
                return finger switch
                {
                    OpenXrHandFinger.Thumb => "thumb",
                    OpenXrHandFinger.Index => "index",
                    OpenXrHandFinger.Middle => "middle",
                    OpenXrHandFinger.Ring => "ring",
                    OpenXrHandFinger.Little => "pinky",
                    _ => string.Empty
                };
            }

            private static bool ContainsAny(string normalized, string[] tokens)
            {
                if (string.IsNullOrEmpty(normalized) || tokens == null || tokens.Length == 0)
                {
                    return false;
                }

                for (int i = 0; i < tokens.Length; i++)
                {
                    if (!string.IsNullOrEmpty(tokens[i]) && normalized.IndexOf(tokens[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }

                return false;
            }

            private static string NormalizeName(string value)
            {
                if (string.IsNullOrEmpty(value))
                {
                    return string.Empty;
                }

                return value.Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
            }
        }

        private sealed class RuntimeHandPoseData
        {
            private RuntimeHandPoseData(RuntimeHandPoseSet left, RuntimeHandPoseSet right)
            {
                Left = left;
                Right = right;
            }

            private RuntimeHandPoseSet Left { get; }
            private RuntimeHandPoseSet Right { get; }

            public RuntimeHandPoseSet GetSide(bool isLeftHand)
            {
                return isLeftHand ? Left : Right;
            }

            public static RuntimeHandPoseData TryParse(string json)
            {
                if (string.IsNullOrWhiteSpace(json))
                {
                    return null;
                }

                try
                {
                    if (!OpenXrHandAssetJsonParser.TryParsePoses(json, out OpenXrHandPoseJsonSide leftJson, out OpenXrHandPoseJsonSide rightJson))
                    {
                        return null;
                    }

                    RuntimeHandPoseSet left = RuntimeHandPoseSet.FromJson(leftJson);
                    RuntimeHandPoseSet right = RuntimeHandPoseSet.FromJson(rightJson);
                    if ((left == null || left.Count == 0) && (right == null || right.Count == 0))
                    {
                        return null;
                    }

                    return new RuntimeHandPoseData(left, right);
                }
                catch (Exception ex)
                {
                    VRModCore.LogWarning($"[OpenXR][HandModel] Failed parsing baked hand pose JSON: {ex.Message}");
                    return null;
                }
            }
        }

        private sealed class RuntimeHandColliderLayoutData
        {
            private RuntimeHandColliderLayoutData(RuntimeHandColliderLayoutSet left, RuntimeHandColliderLayoutSet right)
            {
                Left = left;
                Right = right;
            }

            private RuntimeHandColliderLayoutSet Left { get; }
            private RuntimeHandColliderLayoutSet Right { get; }

            public RuntimeHandColliderLayoutSet GetSide(bool isLeftHand)
            {
                return isLeftHand ? Left : Right;
            }

            public static RuntimeHandColliderLayoutData TryParse(string json)
            {
                if (string.IsNullOrWhiteSpace(json))
                {
                    return null;
                }

                try
                {
                    if (!OpenXrHandAssetJsonParser.TryParseColliderLayout(json, out OpenXrHandColliderLayoutJsonSide leftJson, out OpenXrHandColliderLayoutJsonSide rightJson))
                    {
                        return null;
                    }

                    RuntimeHandColliderLayoutSet left = RuntimeHandColliderLayoutSet.FromJson(leftJson);
                    RuntimeHandColliderLayoutSet right = RuntimeHandColliderLayoutSet.FromJson(rightJson);
                    if ((left == null || left.Count == 0) && (right == null || right.Count == 0))
                    {
                        return null;
                    }

                    return new RuntimeHandColliderLayoutData(left, right);
                }
                catch (Exception ex)
                {
                    VRModCore.LogWarning($"[OpenXR][HandModel] Failed parsing baked hand collider layout JSON: {ex.Message}");
                    return null;
                }
            }
        }

        private sealed class RuntimeHandColliderLayoutSet
        {
            private readonly RuntimeHandColliderLayoutSphere[] _spheres;

            private RuntimeHandColliderLayoutSet(RuntimeHandColliderLayoutSphere[] spheres)
            {
                _spheres = spheres ?? Array.Empty<RuntimeHandColliderLayoutSphere>();
            }

            public int Count => _spheres.Length;

            public static RuntimeHandColliderLayoutSet FromJson(OpenXrHandColliderLayoutJsonSide json)
            {
                if (json == null || json.Colliders == null || json.Colliders.Length == 0)
                {
                    return null;
                }

                var spheres = new List<RuntimeHandColliderLayoutSphere>(Mathf.Min(json.Colliders.Length, OpenXrHandPoseModel.MaxColliderPoseCount));
                for (int i = 0; i < json.Colliders.Length && spheres.Count < OpenXrHandPoseModel.MaxColliderPoseCount; i++)
                {
                    OpenXrHandColliderLayoutJsonCollider collider = json.Colliders[i];
                    if (collider == null
                        || string.IsNullOrWhiteSpace(collider.Name)
                        || string.IsNullOrWhiteSpace(collider.Bone)
                        || collider.Radius <= 0f)
                    {
                        continue;
                    }

                    spheres.Add(new RuntimeHandColliderLayoutSphere(
                        collider.Name,
                        collider.Bone,
                        new Vector3(collider.X, collider.Y, collider.Z),
                        collider.Radius));
                }

                return spheres.Count > 0 ? new RuntimeHandColliderLayoutSet(spheres.ToArray()) : null;
            }

            public RuntimeBoundHandColliderLayoutSet Bind(Transform root)
            {
                if (root == null || _spheres.Length == 0)
                {
                    return null;
                }

                Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
                var transformByName = new Dictionary<string, Transform>(StringComparer.Ordinal);
                for (int i = 0; i < transforms.Length; i++)
                {
                    Transform transform = transforms[i];
                    if (transform != null && !transformByName.ContainsKey(transform.name))
                    {
                        transformByName.Add(transform.name, transform);
                    }
                }

                var bound = new List<RuntimeBoundHandColliderLayoutSphere>(_spheres.Length);
                for (int i = 0; i < _spheres.Length; i++)
                {
                    RuntimeHandColliderLayoutSphere sphere = _spheres[i];
                    if (!transformByName.TryGetValue(sphere.BoneName, out Transform anchor))
                    {
                        continue;
                    }

                    bound.Add(new RuntimeBoundHandColliderLayoutSphere(anchor, sphere));
                }

                return bound.Count > 0 ? new RuntimeBoundHandColliderLayoutSet(bound.ToArray()) : null;
            }
        }

        private sealed class RuntimeBoundHandColliderLayoutSet
        {
            private readonly RuntimeBoundHandColliderLayoutSphere[] _spheres;
            private readonly OpenXrHandWorldColliderPose[] _poseBuffer;

            public RuntimeBoundHandColliderLayoutSet(RuntimeBoundHandColliderLayoutSphere[] spheres)
            {
                _spheres = spheres ?? Array.Empty<RuntimeBoundHandColliderLayoutSphere>();
                _poseBuffer = new OpenXrHandWorldColliderPose[_spheres.Length];
            }

            public int ColliderCount => _spheres.Length;

            public IReadOnlyList<OpenXrHandWorldColliderPose> BuildWorldColliderPoses()
            {
                for (int i = 0; i < _spheres.Length; i++)
                {
                    _poseBuffer[i] = _spheres[i].BuildWorldColliderPose();
                }

                return _poseBuffer;
            }
        }

        private sealed class RuntimeBoundHandColliderLayoutSphere
        {
            private readonly Transform _anchor;
            private readonly RuntimeHandColliderLayoutSphere _sphere;

            public RuntimeBoundHandColliderLayoutSphere(Transform anchor, RuntimeHandColliderLayoutSphere sphere)
            {
                _anchor = anchor;
                _sphere = sphere;
            }

            public OpenXrHandWorldColliderPose BuildWorldColliderPose()
            {
                if (_anchor == null || _sphere == null)
                {
                    return default;
                }

                return new OpenXrHandWorldColliderPose(
                    _sphere.Name,
                    _anchor.TransformPoint(_sphere.LocalPosition),
                    _anchor.rotation,
                    _sphere.Radius / OpenXrHandPoseModel.ReferenceHandColliderRadius);
            }
        }

        private sealed class RuntimeHandColliderLayoutSphere
        {
            public RuntimeHandColliderLayoutSphere(string name, string boneName, Vector3 localPosition, float radius)
            {
                Name = name;
                BoneName = boneName;
                LocalPosition = localPosition;
                Radius = radius;
            }

            public string Name { get; }
            public string BoneName { get; }
            public Vector3 LocalPosition { get; }
            public float Radius { get; }
        }

        private sealed class RuntimeHandPoseSet
        {
            private readonly Dictionary<string, RuntimeHandPoseBone> _bones;

            private RuntimeHandPoseSet(Dictionary<string, RuntimeHandPoseBone> bones)
            {
                _bones = bones ?? new Dictionary<string, RuntimeHandPoseBone>(StringComparer.Ordinal);
            }

            public int Count => _bones.Count;

            public static RuntimeHandPoseSet FromJson(OpenXrHandPoseJsonSide json)
            {
                if (json == null || json.Open == null || json.Open.Length == 0)
                {
                    return null;
                }

                Dictionary<string, Quaternion> open = BuildRotationMap(json.Open);
                Dictionary<string, Quaternion> fist = BuildRotationMap(json.Fist);
                Dictionary<string, Quaternion> indexCurl = BuildRotationMap(json.IndexCurl);
                var bones = new Dictionary<string, RuntimeHandPoseBone>(StringComparer.Ordinal);

                foreach (KeyValuePair<string, Quaternion> pair in open)
                {
                    string boneName = pair.Key;
                    if (string.IsNullOrEmpty(boneName) || boneName.IndexOf("finger_", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    Quaternion openRotation = pair.Value;
                    Quaternion fistRotation = fist.TryGetValue(boneName, out Quaternion fistValue) ? fistValue : openRotation;
                    Quaternion indexRotation = indexCurl.TryGetValue(boneName, out Quaternion indexValue) ? indexValue : openRotation;
                    if (!TryResolveFingerFromBoneName(boneName, out OpenXrHandFinger finger))
                    {
                        continue;
                    }

                    bones[boneName] = new RuntimeHandPoseBone(boneName, finger, openRotation, fistRotation, indexRotation);
                }

                return new RuntimeHandPoseSet(bones);
            }

            public RuntimeBoundHandPoseSet Bind(Transform root)
            {
                if (root == null || _bones.Count == 0)
                {
                    return null;
                }

                Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
                var transformByName = new Dictionary<string, Transform>(StringComparer.Ordinal);
                for (int i = 0; i < transforms.Length; i++)
                {
                    Transform transform = transforms[i];
                    if (transform != null && !transformByName.ContainsKey(transform.name))
                    {
                        transformByName.Add(transform.name, transform);
                    }
                }

                var boundBones = new List<RuntimeBoundHandPoseBone>(_bones.Count);
                foreach (RuntimeHandPoseBone poseBone in _bones.Values)
                {
                    if (!transformByName.TryGetValue(poseBone.BoneName, out Transform transform))
                    {
                        continue;
                    }

                    boundBones.Add(new RuntimeBoundHandPoseBone(transform, poseBone));
                }

                return boundBones.Count > 0 ? new RuntimeBoundHandPoseSet(boundBones.ToArray()) : null;
            }

            private static Dictionary<string, Quaternion> BuildRotationMap(OpenXrHandPoseJsonBone[] json)
            {
                var map = new Dictionary<string, Quaternion>(StringComparer.Ordinal);
                if (json == null)
                {
                    return map;
                }

                for (int i = 0; i < json.Length; i++)
                {
                    OpenXrHandPoseJsonBone bone = json[i];
                    if (bone == null || string.IsNullOrEmpty(bone.Bone))
                    {
                        continue;
                    }

                    map[bone.Bone] = Normalize(new Quaternion(bone.X, bone.Y, bone.Z, bone.W));
                }

                return map;
            }

            private static Quaternion Normalize(Quaternion rotation)
            {
                float length = Mathf.Sqrt(
                    rotation.x * rotation.x
                    + rotation.y * rotation.y
                    + rotation.z * rotation.z
                    + rotation.w * rotation.w);
                if (length <= 0.000001f)
                {
                    return Quaternion.identity;
                }

                return new Quaternion(rotation.x / length, rotation.y / length, rotation.z / length, rotation.w / length);
            }

            private static bool TryResolveFingerFromBoneName(string boneName, out OpenXrHandFinger finger)
            {
                if (boneName.IndexOf("thumb", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    finger = OpenXrHandFinger.Thumb;
                    return true;
                }

                if (boneName.IndexOf("index", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    finger = OpenXrHandFinger.Index;
                    return true;
                }

                if (boneName.IndexOf("middle", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    finger = OpenXrHandFinger.Middle;
                    return true;
                }

                if (boneName.IndexOf("ring", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    finger = OpenXrHandFinger.Ring;
                    return true;
                }

                if (boneName.IndexOf("pinky", StringComparison.OrdinalIgnoreCase) >= 0
                    || boneName.IndexOf("little", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    finger = OpenXrHandFinger.Little;
                    return true;
                }

                finger = default;
                return false;
            }
        }

        private sealed class RuntimeBoundHandPoseSet
        {
            private readonly RuntimeBoundHandPoseBone[] _bones;

            public RuntimeBoundHandPoseSet(RuntimeBoundHandPoseBone[] bones)
            {
                _bones = bones ?? Array.Empty<RuntimeBoundHandPoseBone>();
            }

            public int BoneCount => _bones.Length;

            public void Apply(float grip, float trigger)
            {
                grip = Mathf.Clamp01(grip);
                trigger = Mathf.Clamp01(trigger);
                for (int i = 0; i < _bones.Length; i++)
                {
                    _bones[i].Apply(grip, trigger);
                }
            }
        }

        private sealed class RuntimeBoundHandPoseBone
        {
            private readonly Transform _transform;
            private readonly RuntimeHandPoseBone _poseBone;

            public RuntimeBoundHandPoseBone(Transform transform, RuntimeHandPoseBone poseBone)
            {
                _transform = transform;
                _poseBone = poseBone;
            }

            public void Apply(float grip, float trigger)
            {
                if (_transform == null || _poseBone == null)
                {
                    return;
                }

                Quaternion target;
                switch (_poseBone.Finger)
                {
                    case OpenXrHandFinger.Index:
                        target = Quaternion.Slerp(_poseBone.OpenRotation, _poseBone.IndexCurlRotation, trigger);
                        break;
                    case OpenXrHandFinger.Thumb:
                        float thumbGrip = Mathf.Clamp01(grip * 0.45f);
                        float thumbTrigger = Mathf.Clamp01(trigger * 0.35f);
                        target = Quaternion.Slerp(_poseBone.OpenRotation, _poseBone.FistRotation, thumbGrip);
                        target = Quaternion.Slerp(target, _poseBone.IndexCurlRotation, thumbTrigger);
                        break;
                    default:
                        target = Quaternion.Slerp(_poseBone.OpenRotation, _poseBone.FistRotation, grip);
                        break;
                }

                _transform.localRotation = target;
            }
        }

        private sealed class RuntimeHandPoseBone
        {
            public RuntimeHandPoseBone(
                string boneName,
                OpenXrHandFinger finger,
                Quaternion openRotation,
                Quaternion fistRotation,
                Quaternion indexCurlRotation)
            {
                BoneName = boneName;
                Finger = finger;
                OpenRotation = openRotation;
                FistRotation = fistRotation;
                IndexCurlRotation = indexCurlRotation;
            }

            public string BoneName { get; }
            public OpenXrHandFinger Finger { get; }
            public Quaternion OpenRotation { get; }
            public Quaternion FistRotation { get; }
            public Quaternion IndexCurlRotation { get; }
        }

        private sealed class RuntimeFingerBones
        {
            public RuntimeFingerBones(OpenXrHandFinger finger)
            {
                Finger = finger;
            }

            public OpenXrHandFinger Finger { get; }
            public Transform Proximal { get; set; }
            public Transform Intermediate { get; set; }
            public Transform Distal { get; set; }
            public Transform Tip { get; set; }
            public Quaternion ProximalOpenRotation { get; private set; }
            public Quaternion IntermediateOpenRotation { get; private set; }
            public Quaternion DistalOpenRotation { get; private set; }
            public Vector3 ProximalCurlAxis { get; private set; } = Vector3.right;
            public Vector3 IntermediateCurlAxis { get; private set; } = Vector3.right;
            public Vector3 DistalCurlAxis { get; private set; } = Vector3.right;
            public bool HasTip => Tip != null;
            public bool HasAnyBone => Proximal != null || Intermediate != null || Distal != null;
            public Transform BaseBone => Proximal ?? Intermediate ?? Distal ?? Tip;

            public void CaptureOpenRotations()
            {
                ProximalOpenRotation = Proximal != null ? Proximal.localRotation : Quaternion.identity;
                IntermediateOpenRotation = Intermediate != null ? Intermediate.localRotation : Quaternion.identity;
                DistalOpenRotation = Distal != null ? Distal.localRotation : Quaternion.identity;
            }

            public void CaptureCurlAxes(Transform palm, Vector3? sharedHingeAxisWorld)
            {
                ProximalCurlAxis = ComputeCurlAxis(Proximal, ProximalOpenRotation, Intermediate ?? Distal ?? Tip, palm, Tip, sharedHingeAxisWorld);
                IntermediateCurlAxis = ComputeCurlAxis(Intermediate, IntermediateOpenRotation, Distal ?? Tip, palm, Tip, sharedHingeAxisWorld);
                DistalCurlAxis = ComputeCurlAxis(Distal, DistalOpenRotation, Tip, palm, Tip, sharedHingeAxisWorld);
            }

            private static Vector3 ComputeCurlAxis(
                Transform bone,
                Quaternion openRotation,
                Transform child,
                Transform palm,
                Transform sampleTip,
                Vector3? sharedHingeAxisWorld)
            {
                if (bone == null || palm == null)
                {
                    return Vector3.right;
                }

                if (sharedHingeAxisWorld.HasValue && sharedHingeAxisWorld.Value.sqrMagnitude > 0.000001f)
                {
                    Vector3 localSharedAxis = bone.InverseTransformDirection(sharedHingeAxisWorld.Value.normalized);
                    return OrientAxisTowardPalm(bone, openRotation, localSharedAxis, sampleTip ?? child, palm);
                }

                Vector3 fingerDirectionWorld;
                if (child != null && (child.position - bone.position).sqrMagnitude > 0.000001f)
                {
                    fingerDirectionWorld = (child.position - bone.position).normalized;
                }
                else
                {
                    fingerDirectionWorld = bone.forward;
                }

                Vector3 palmDirectionWorld = palm.position - bone.position;
                if (palmDirectionWorld.sqrMagnitude <= 0.000001f)
                {
                    return Vector3.right;
                }

                palmDirectionWorld.Normalize();
                Vector3 hingeAxisWorld = Vector3.Cross(fingerDirectionWorld, palmDirectionWorld);
                if (hingeAxisWorld.sqrMagnitude <= 0.000001f)
                {
                    return Vector3.right;
                }

                Vector3 localAxis = bone.InverseTransformDirection(hingeAxisWorld.normalized);
                return OrientAxisTowardPalm(bone, openRotation, localAxis, sampleTip ?? child, palm);
            }

            private static Vector3 OrientAxisTowardPalm(Transform bone, Quaternion openRotation, Vector3 localAxis, Transform sampleTip, Transform palm)
            {
                if (bone == null || palm == null || sampleTip == null || localAxis.sqrMagnitude <= 0.000001f)
                {
                    return localAxis.sqrMagnitude > 0.000001f ? localAxis.normalized : Vector3.right;
                }

                localAxis.Normalize();
                Quaternion originalRotation = bone.localRotation;
                float plusDistance;
                float minusDistance;

                try
                {
                    bone.localRotation = openRotation * Quaternion.AngleAxis(12f, localAxis);
                    plusDistance = (sampleTip.position - palm.position).sqrMagnitude;

                    bone.localRotation = openRotation * Quaternion.AngleAxis(-12f, localAxis);
                    minusDistance = (sampleTip.position - palm.position).sqrMagnitude;
                }
                finally
                {
                    bone.localRotation = originalRotation;
                }

                return minusDistance < plusDistance ? -localAxis : localAxis;
            }
        }
    }
}
#endif
