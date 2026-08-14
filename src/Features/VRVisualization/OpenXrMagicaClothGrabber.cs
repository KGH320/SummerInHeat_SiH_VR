#if MONO && OPENXR_BUILD
using HarmonyLib;
using UnityEngine.SceneManagement;
using UnityVRMod.Config;
using UnityVRMod.Core;

namespace UnityVRMod.Features.VrVisualization
{
    internal sealed class OpenXrMagicaClothGrabber
    {
        private const string HarmonyId = "com.newunitymodder.unityvrmod.openxrmagicaclothgrab";
        private const float GripPressThreshold = 0.65f;
        private const float GripReleaseThreshold = 0.45f;
        private const float GrabTransitionSeconds = 0.06f;
        private const float MaxTargetStepMeters = 0.08f;
        private const float AutoReleaseDistanceMeters = 0.35f;
        private const float ReleaseVelocityScale = 0.35f;
        private const float MaxReleaseVelocityMetersPerSecond = 1.2f;
        private const float PendingCaptureWarningSeconds = 0.5f;

        private static Harmony _harmony;
        private static bool _hooksInstalled;
        private static OpenXrMagicaClothGrabber _activeInstance;

        private readonly HandGrabState _leftHand = new("Left");
        private readonly HandGrabState _rightHand = new("Right");
        private readonly HashSet<long> _ownedParticles = [];

        private Type _magicaClothType;
        private Type _teamDataType;
        private Type _vertexAttributeType;
        private Type _float3Type;
        private PropertyInfo _magicaClothProcessProperty;
        private PropertyInfo _clothProcessTeamIdProperty;
        private PropertyInfo _magicaManagerTeamProperty;
        private PropertyInfo _magicaManagerSimulationProperty;
        private PropertyInfo _magicaManagerVMeshProperty;
        private PropertyInfo _teamDataIsValidProperty;
        private PropertyInfo _teamDataIsEnableProperty;
        private PropertyInfo _teamDataIsRunningProperty;
        private FieldInfo _teamDataArrayField;
        private FieldInfo _particleChunkField;
        private FieldInfo _proxyCommonChunkField;
        private FieldInfo _chunkStartIndexField;
        private FieldInfo _chunkDataLengthField;
        private FieldInfo _attributesField;
        private FieldInfo _proxyPositionsField;
        private FieldInfo _nextPosArrayField;
        private FieldInfo _oldPosArrayField;
        private FieldInfo _oldPositionArrayField;
        private FieldInfo _velocityPosArrayField;
        private FieldInfo _dispPosArrayField;
        private FieldInfo _velocityArrayField;
        private FieldInfo _realVelocityArrayField;
        private FieldInfo _float3XField;
        private FieldInfo _float3YField;
        private FieldInfo _float3ZField;
        private MethodInfo _vertexAttributeIsMoveMethod;
        private ArrayAccessor _teamDataArrayAccessor;
        private ArrayAccessor _float3ArrayAccessor;
        private ArrayAccessor _attributeArrayAccessor;
        private bool _bindingsResolved;
        private bool _disabledForLayout;
        private bool _layoutWarningLogged;
        private string _lastSceneName = string.Empty;

        public bool IsGripCaptured(bool isLeftHand)
        {
            return GetHand(isLeftHand).Particles.Count > 0;
        }

        public bool ShouldConsumeGrip(bool isLeftHand)
        {
            HandGrabState hand = GetHand(isLeftHand);
            return hand.PendingCapture
                || hand.Particles.Count > 0
                || (hand.SuppressUntilGripRelease && hand.GripPressed);
        }

        public void Update(
            bool hasLeftHandPose,
            float leftGripValue,
            bool hasRightHandPose,
            float rightGripValue)
        {
            bool configuredEnabled = ConfigManager.OpenXR_EnableMagicaClothGrab?.Value ?? true;
            string sceneName = SceneManager.GetActiveScene().name ?? string.Empty;
            if (!string.Equals(sceneName, _lastSceneName, StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(_lastSceneName))
                {
                    QueueAutomaticRelease(_leftHand);
                    QueueAutomaticRelease(_rightHand);
                    _leftHand.PendingCapture = false;
                    _rightHand.PendingCapture = false;
                }

                _lastSceneName = sceneName;
            }

            bool runtimeReady = configuredEnabled && EnsureBindings();
            if (runtimeReady)
            {
                _activeInstance = this;
            }
            else if (!configuredEnabled)
            {
                QueueAutomaticRelease(_leftHand);
                QueueAutomaticRelease(_rightHand);
            }

            Vector3 leftPosition = default;
            Quaternion leftRotation = Quaternion.identity;
            Vector3 rightPosition = default;
            Quaternion rightRotation = Quaternion.identity;
            bool hasLeftGrabPose = hasLeftHandPose
                && OpenXrHandModelAnchorRegistry.TryGetWorldGripPose(true, out leftPosition, out leftRotation);
            bool hasRightGrabPose = hasRightHandPose
                && OpenXrHandModelAnchorRegistry.TryGetWorldGripPose(false, out rightPosition, out rightRotation);

            UpdateHandProbes(_leftHand, isLeftHand: true, hasLeftGrabPose);
            UpdateHandProbes(_rightHand, isLeftHand: false, hasRightGrabPose);
            UpdateHandInput(_leftHand, leftGripValue, hasLeftGrabPose, leftPosition, leftRotation, runtimeReady);
            UpdateHandInput(_rightHand, rightGripValue, hasRightGrabPose, rightPosition, rightRotation, runtimeReady);
        }

        public void Reset()
        {
            ClearHand(_leftHand);
            ClearHand(_rightHand);
            _ownedParticles.Clear();
            _lastSceneName = string.Empty;
            if (ReferenceEquals(_activeInstance, this))
            {
                _activeInstance = null;
            }
        }

        private HandGrabState GetHand(bool isLeftHand)
        {
            return isLeftHand ? _leftHand : _rightHand;
        }

        private static void UpdateHandProbes(HandGrabState hand, bool isLeftHand, bool hasPose)
        {
            hand.ProbeCount = 0;
            if (hasPose)
            {
                OpenXrHandModelAnchorRegistry.TryFillWorldColliderPoses(isLeftHand, hand.ProbePoses, out hand.ProbeCount);
            }
        }

        private void UpdateHandInput(
            HandGrabState hand,
            float gripValue,
            bool hasPose,
            Vector3 worldPosition,
            Quaternion worldRotation,
            bool allowCapture)
        {
            float now = Time.unscaledTime;
            if (hand.PendingCapture
                && !hand.PendingCaptureWarningLogged
                && now - hand.PendingCaptureTime >= PendingCaptureWarningSeconds)
            {
                hand.PendingCaptureWarningLogged = true;
                VRModCore.LogWarning($"[Physics][OpenXR][MagicaGrab] {hand.Name} capture request has not reached OnEarlyClothUpdate after {PendingCaptureWarningSeconds:F1}s.");
            }

            hand.HasPose = hasPose && IsFinite(worldPosition);
            if (hand.HasPose)
            {
                if (hand.HasPreviousPose)
                {
                    float deltaTime = now - hand.PreviousPoseTime;
                    if (deltaTime > 0.0001f && deltaTime < 0.25f)
                    {
                        Vector3 instantaneousVelocity = (worldPosition - hand.PreviousPosition) / deltaTime;
                        float blend = Mathf.Clamp01(deltaTime * 20f);
                        hand.Velocity = Vector3.Lerp(hand.Velocity, instantaneousVelocity, blend);
                    }
                }
                else
                {
                    hand.Velocity = Vector3.zero;
                }

                hand.Position = worldPosition;
                hand.Rotation = worldRotation;
                hand.PreviousPosition = worldPosition;
                hand.PreviousPoseTime = now;
                hand.HasPreviousPose = true;
            }
            else
            {
                hand.HasPreviousPose = false;
                hand.Velocity = Vector3.zero;
                QueueAutomaticRelease(hand);
            }

            gripValue = Mathf.Clamp01(gripValue);
            if (!hand.GripPressed && gripValue >= GripPressThreshold)
            {
                hand.GripPressed = true;
                hand.SuppressUntilGripRelease = false;
                if (allowCapture && hand.HasPose)
                {
                    hand.PendingCapture = true;
                    hand.PendingCaptureTime = now;
                    hand.PendingCaptureWarningLogged = false;
                    VRModCore.Log(
                        $"[Physics][OpenXR][MagicaGrab] {hand.Name} Grip press queued, value={gripValue:F2}, "
                        + $"anchor=({hand.Position.x:F3},{hand.Position.y:F3},{hand.Position.z:F3}), probes={hand.ProbeCount}.");
                }
                else if (allowCapture)
                {
                    VRModCore.LogWarning($"[Physics][OpenXR][MagicaGrab] {hand.Name} Grip press ignored because no valid hand grab pose is available.");
                }
            }
            else if (hand.GripPressed && gripValue <= GripReleaseThreshold)
            {
                hand.GripPressed = false;
                hand.PendingCapture = false;
                hand.PendingCaptureWarningLogged = false;
                hand.SuppressUntilGripRelease = false;
                if (hand.Particles.Count > 0)
                {
                    hand.ReleaseRequested = true;
                    hand.InheritReleaseVelocity = true;
                }
            }
        }

        private void QueueAutomaticRelease(HandGrabState hand)
        {
            if (hand.Particles.Count == 0)
            {
                return;
            }

            hand.ReleaseRequested = true;
            hand.InheritReleaseVelocity = false;
            hand.SuppressUntilGripRelease = hand.GripPressed;
        }

        private bool EnsureBindings()
        {
            if (_disabledForLayout)
            {
                return false;
            }

            if (_bindingsResolved)
            {
                return true;
            }

            try
            {
                Type magicaManagerType = ResolveTypeAnyAssembly("MagicaCloth2.MagicaManager");
                Type teamManagerType = ResolveTypeAnyAssembly("MagicaCloth2.TeamManager");
                Type simulationManagerType = ResolveTypeAnyAssembly("MagicaCloth2.SimulationManager");
                Type virtualMeshManagerType = ResolveTypeAnyAssembly("MagicaCloth2.VirtualMeshManager");
                Type clothManagerType = ResolveTypeAnyAssembly("MagicaCloth2.ClothManager");
                Type clothProcessType = ResolveTypeAnyAssembly("MagicaCloth2.ClothProcess");
                Type dataChunkType = ResolveTypeAnyAssembly("MagicaCloth2.DataChunk");
                _magicaClothType = ResolveTypeAnyAssembly("MagicaCloth2.MagicaCloth");
                _teamDataType = ResolveTypeAnyAssembly("MagicaCloth2.TeamManager+TeamData");
                _vertexAttributeType = ResolveTypeAnyAssembly("MagicaCloth2.VertexAttribute");

                if (magicaManagerType == null || teamManagerType == null || simulationManagerType == null
                    || virtualMeshManagerType == null || clothManagerType == null || clothProcessType == null
                    || dataChunkType == null || _magicaClothType == null || _teamDataType == null
                    || _vertexAttributeType == null)
                {
                    return DisableForLayout("required MagicaCloth2 runtime types were not found");
                }

                const BindingFlags instanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                const BindingFlags staticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                _magicaClothProcessProperty = _magicaClothType.GetProperty("Process", instanceFlags);
                _clothProcessTeamIdProperty = clothProcessType.GetProperty("TeamId", instanceFlags);
                _magicaManagerTeamProperty = magicaManagerType.GetProperty("Team", staticFlags);
                _magicaManagerSimulationProperty = magicaManagerType.GetProperty("Simulation", staticFlags);
                _magicaManagerVMeshProperty = magicaManagerType.GetProperty("VMesh", staticFlags);
                _teamDataArrayField = teamManagerType.GetField("teamDataArray", instanceFlags);
                _particleChunkField = _teamDataType.GetField("particleChunk", instanceFlags);
                _proxyCommonChunkField = _teamDataType.GetField("proxyCommonChunk", instanceFlags);
                _teamDataIsValidProperty = _teamDataType.GetProperty("IsValid", instanceFlags);
                _teamDataIsEnableProperty = _teamDataType.GetProperty("IsEnable", instanceFlags);
                _teamDataIsRunningProperty = _teamDataType.GetProperty("IsRunning", instanceFlags);
                _chunkStartIndexField = dataChunkType.GetField("startIndex", instanceFlags);
                _chunkDataLengthField = dataChunkType.GetField("dataLength", instanceFlags);
                _attributesField = virtualMeshManagerType.GetField("attributes", instanceFlags);
                _proxyPositionsField = virtualMeshManagerType.GetField("positions", instanceFlags);
                _nextPosArrayField = simulationManagerType.GetField("nextPosArray", instanceFlags);
                _oldPosArrayField = simulationManagerType.GetField("oldPosArray", instanceFlags);
                _oldPositionArrayField = simulationManagerType.GetField("oldPositionArray", instanceFlags);
                _velocityPosArrayField = simulationManagerType.GetField("velocityPosArray", instanceFlags);
                _dispPosArrayField = simulationManagerType.GetField("dispPosArray", instanceFlags);
                _velocityArrayField = simulationManagerType.GetField("velocityArray", instanceFlags);
                _realVelocityArrayField = simulationManagerType.GetField("realVelocityArray", instanceFlags);
                _vertexAttributeIsMoveMethod = _vertexAttributeType.GetMethod("IsMove", instanceFlags, null, Type.EmptyTypes, null);

                if (!ValidateRequiredBindings())
                {
                    return DisableForLayout("the expected MagicaCloth2 fields or properties are missing");
                }

                _float3Type = GetSingleGenericArgument(_dispPosArrayField.FieldType);
                if (_float3Type == null || !string.Equals(_float3Type.FullName, "Unity.Mathematics.float3", StringComparison.Ordinal))
                {
                    return DisableForLayout("simulation position arrays are not ExNativeArray<float3>");
                }

                if (!HasArrayElementType(_nextPosArrayField, _float3Type)
                    || !HasArrayElementType(_oldPosArrayField, _float3Type)
                    || !HasArrayElementType(_oldPositionArrayField, _float3Type)
                    || !HasArrayElementType(_velocityPosArrayField, _float3Type)
                    || !HasArrayElementType(_velocityArrayField, _float3Type)
                    || !HasArrayElementType(_realVelocityArrayField, _float3Type)
                    || !HasArrayElementType(_proxyPositionsField, _float3Type)
                    || !HasArrayElementType(_attributesField, _vertexAttributeType)
                    || !HasArrayElementType(_teamDataArrayField, _teamDataType))
                {
                    return DisableForLayout("one or more MagicaCloth2 array element types do not match the inspected layout");
                }

                _float3XField = _float3Type.GetField("x", instanceFlags);
                _float3YField = _float3Type.GetField("y", instanceFlags);
                _float3ZField = _float3Type.GetField("z", instanceFlags);
                _teamDataArrayAccessor = new ArrayAccessor(_teamDataArrayField.FieldType);
                _float3ArrayAccessor = new ArrayAccessor(_dispPosArrayField.FieldType);
                _attributeArrayAccessor = new ArrayAccessor(_attributesField.FieldType);
                if (_float3XField == null || _float3YField == null || _float3ZField == null
                    || !_teamDataArrayAccessor.IsReadable || !_float3ArrayAccessor.IsReadWrite
                    || !_attributeArrayAccessor.IsReadable)
                {
                    return DisableForLayout("ExNativeArray accessors or float3 fields do not match the inspected layout");
                }

                MethodInfo clothUpdate = clothManagerType.GetMethod("ClothUpdate", instanceFlags, null, Type.EmptyTypes, null);
                MethodInfo earlyClothUpdate = clothManagerType.GetMethod("OnEarlyClothUpdate", instanceFlags, null, Type.EmptyTypes, null);
                if (clothUpdate == null || earlyClothUpdate == null)
                {
                    return DisableForLayout("safe ClothManager update methods were not found");
                }

                if (!_hooksInstalled)
                {
                    MethodInfo prefix = typeof(OpenXrMagicaClothGrabber).GetMethod(nameof(OnClothUpdatePrefix), BindingFlags.NonPublic | BindingFlags.Static);
                    MethodInfo postfix = typeof(OpenXrMagicaClothGrabber).GetMethod(nameof(OnEarlyClothUpdatePostfix), BindingFlags.NonPublic | BindingFlags.Static);
                    _harmony = new Harmony(HarmonyId);
                    _harmony.Patch(clothUpdate, prefix: new HarmonyMethod(prefix));
                    _harmony.Patch(earlyClothUpdate, postfix: new HarmonyMethod(postfix));
                    _hooksInstalled = true;
                    VRModCore.Log("[Physics][OpenXR][MagicaGrab] Installed MagicaCloth2 safe-timing hooks.");
                }

                _bindingsResolved = true;
                return true;
            }
            catch (Exception ex)
            {
                return DisableForLayout($"binding failed: {ex.Message}");
            }
        }

        private bool ValidateRequiredBindings()
        {
            return _magicaClothProcessProperty != null
                && _clothProcessTeamIdProperty != null
                && _magicaManagerTeamProperty != null
                && _magicaManagerSimulationProperty != null
                && _magicaManagerVMeshProperty != null
                && _teamDataArrayField != null
                && _particleChunkField != null
                && _proxyCommonChunkField != null
                && _teamDataIsValidProperty != null
                && _teamDataIsEnableProperty != null
                && _teamDataIsRunningProperty != null
                && _chunkStartIndexField != null
                && _chunkDataLengthField != null
                && _attributesField != null
                && _proxyPositionsField != null
                && _nextPosArrayField != null
                && _oldPosArrayField != null
                && _oldPositionArrayField != null
                && _velocityPosArrayField != null
                && _dispPosArrayField != null
                && _velocityArrayField != null
                && _realVelocityArrayField != null
                && _vertexAttributeIsMoveMethod != null;
        }

        private bool DisableForLayout(string reason)
        {
            _disabledForLayout = true;
            ClearHand(_leftHand);
            ClearHand(_rightHand);
            _ownedParticles.Clear();
            if (!_layoutWarningLogged)
            {
                _layoutWarningLogged = true;
                VRModCore.LogWarning($"[Physics][OpenXR][MagicaGrab] Particle grabbing disabled; {reason}.");
            }

            return false;
        }

        private static void OnClothUpdatePrefix()
        {
            _activeInstance?.ApplyLatestTargetsBeforeSimulation();
        }

        private static void OnEarlyClothUpdatePostfix()
        {
            _activeInstance?.ProcessRequestsAfterJobs();
        }

        private void ApplyLatestTargetsBeforeSimulation()
        {
            if (_disabledForLayout || !(ConfigManager.OpenXR_EnableMagicaClothGrab?.Value ?? true))
            {
                return;
            }

            try
            {
                if (!TryGetRuntimeBuffers(out RuntimeBuffers buffers))
                {
                    return;
                }

                ApplyHandPins(_leftHand, buffers);
                ApplyHandPins(_rightHand, buffers);
            }
            catch (Exception ex)
            {
                DisableForLayout($"runtime array write failed: {ex.Message}");
            }
        }

        private void ProcessRequestsAfterJobs()
        {
            if (_disabledForLayout)
            {
                return;
            }

            try
            {
                bool configuredEnabled = ConfigManager.OpenXR_EnableMagicaClothGrab?.Value ?? true;
                string sceneName = SceneManager.GetActiveScene().name ?? string.Empty;
                if (!configuredEnabled || !string.Equals(sceneName, _lastSceneName, StringComparison.Ordinal))
                {
                    QueueAutomaticRelease(_leftHand);
                    QueueAutomaticRelease(_rightHand);
                    _leftHand.PendingCapture = false;
                    _rightHand.PendingCapture = false;
                }

                if (!TryGetRuntimeBuffers(out RuntimeBuffers buffers))
                {
                    QueueAutomaticRelease(_leftHand);
                    QueueAutomaticRelease(_rightHand);
                    _leftHand.PendingCapture = false;
                    _rightHand.PendingCapture = false;
                    ReleaseHandWithoutRuntime(_leftHand);
                    ReleaseHandWithoutRuntime(_rightHand);
                    return;
                }

                ProcessRelease(_leftHand, buffers);
                ProcessRelease(_rightHand, buffers);

                if (configuredEnabled && string.Equals(sceneName, _lastSceneName, StringComparison.Ordinal))
                {
                    ProcessCapture(_leftHand, buffers);
                    ProcessCapture(_rightHand, buffers);
                    ApplyHandPins(_leftHand, buffers);
                    ApplyHandPins(_rightHand, buffers);
                }
            }
            catch (Exception ex)
            {
                DisableForLayout($"runtime request processing failed: {ex.Message}");
            }
        }

        private void ProcessCapture(HandGrabState hand, RuntimeBuffers buffers)
        {
            if (!hand.PendingCapture)
            {
                return;
            }

            hand.PendingCapture = false;
            hand.PendingCaptureWarningLogged = false;
            if (!hand.GripPressed || !hand.HasPose || hand.Particles.Count > 0)
            {
                VRModCore.LogWarning(
                    $"[Physics][OpenXR][MagicaGrab] {hand.Name} capture request discarded: "
                    + $"grip={hand.GripPressed}, pose={hand.HasPose}, existing={hand.Particles.Count}.");
                return;
            }

            float radius = Mathf.Clamp(ConfigManager.OpenXR_MagicaClothGrabRadius?.Value ?? 0.075f, 0.02f, 0.20f);
            int maxParticles = Mathf.Clamp(ConfigManager.OpenXR_MagicaClothGrabMaxParticles?.Value ?? 4, 1, 8);
            float radiusSquared = radius * radius;
            var candidates = new List<ParticleCandidate>();
            var visitedTeams = new HashSet<int>();
            int resolvedTeamCount = 0;
            int validTeamCount = 0;
            int enabledTeamCount = 0;
            int runningTeamCount = 0;
            int scannedParticleCount = 0;
            int movableParticleCount = 0;
            float nearestMovableDistanceSquared = float.PositiveInfinity;

            UnityEngine.Object[] cloths = UnityEngine.Object.FindObjectsOfType(_magicaClothType);
            for (int i = 0; i < cloths.Length; i++)
            {
                object process = TryGetProperty(_magicaClothProcessProperty, cloths[i]);
                if (!TryGetIntProperty(_clothProcessTeamIdProperty, process, out int teamId) || teamId < 0 || !visitedTeams.Add(teamId))
                {
                    continue;
                }

                resolvedTeamCount++;

                if (!TryGetTeamData(buffers, teamId, requireEnabled: false, out object teamData, out DataChunkInfo particleChunk, out DataChunkInfo proxyChunk))
                {
                    continue;
                }


                validTeamCount++;
                if (!GetBoolProperty(_teamDataIsEnableProperty, teamData))
                {
                    continue;
                }

                enabledTeamCount++;
                if (GetBoolProperty(_teamDataIsRunningProperty, teamData))
                {
                    runningTeamCount++;
                }

                int particleCount = Mathf.Min(particleChunk.Length, proxyChunk.Length);
                for (int localIndex = 0; localIndex < particleCount; localIndex++)
                {
                    scannedParticleCount++;
                    long ownershipKey = MakeOwnershipKey(teamId, localIndex);
                    if (_ownedParticles.Contains(ownershipKey))
                    {
                        continue;
                    }

                    int particleIndex = particleChunk.Start + localIndex;
                    int proxyIndex = proxyChunk.Start + localIndex;
                    if (!IsArrayIndexValid(buffers.DispPos, particleIndex)
                        || !IsArrayIndexValid(buffers.Attributes, proxyIndex))
                    {
                        continue;
                    }

                    object attribute = _attributeArrayAccessor.Get(buffers.Attributes, proxyIndex);
                    object moveResult = _vertexAttributeIsMoveMethod.Invoke(attribute, null);
                    if (moveResult is not bool isMove || !isMove)
                    {
                        continue;
                    }

                    Vector3 position = ReadFloat3(_float3ArrayAccessor.Get(buffers.DispPos, particleIndex));
                    movableParticleCount++;
                    float distanceSquared = GetNearestProbeDistanceSquared(hand, position);
                    nearestMovableDistanceSquared = Mathf.Min(nearestMovableDistanceSquared, distanceSquared);
                    if (distanceSquared <= radiusSquared)
                    {
                        candidates.Add(new ParticleCandidate(teamId, localIndex, position, distanceSquared));
                    }
                }
            }

            candidates.Sort((a, b) => a.DistanceSquared.CompareTo(b.DistanceSquared));
            int captureCount = Mathf.Min(maxParticles, candidates.Count);
            for (int i = 0; i < captureCount; i++)
            {
                ParticleCandidate candidate = candidates[i];
                long ownershipKey = MakeOwnershipKey(candidate.TeamId, candidate.LocalIndex);
                if (!_ownedParticles.Add(ownershipKey))
                {
                    continue;
                }

                Vector3 localOffset = Quaternion.Inverse(hand.Rotation) * (candidate.Position - hand.Position);
                hand.Particles.Add(new GrabbedParticle(
                    candidate.TeamId,
                    candidate.LocalIndex,
                    ownershipKey,
                    candidate.Position,
                    localOffset));
            }

            if (hand.Particles.Count > 0)
            {
                hand.CaptureTime = Time.unscaledTime;
                hand.SuppressUntilGripRelease = true;
            }

            string nearestDistance = float.IsPositiveInfinity(nearestMovableDistanceSquared)
                ? "none"
                : Mathf.Sqrt(nearestMovableDistanceSquared).ToString("F3") + "m";
            VRModCore.Log(
                $"[Physics][OpenXR][MagicaGrab] {hand.Name} capture scan: cloths={cloths.Length}, "
                + $"teams={resolvedTeamCount}, valid={validTeamCount}, enabled={enabledTeamCount}, running={runningTeamCount}, "
                + $"particles={scannedParticleCount}, movable={movableParticleCount}, probes={hand.ProbeCount + 1}, "
                + $"nearest={nearestDistance}, radius={radius:F3}m, captured={hand.Particles.Count}.");
        }

        private static float GetNearestProbeDistanceSquared(HandGrabState hand, Vector3 position)
        {
            float nearest = (position - hand.Position).sqrMagnitude;
            for (int i = 0; i < hand.ProbeCount; i++)
            {
                float distanceSquared = (position - hand.ProbePoses[i].WorldPosition).sqrMagnitude;
                if (distanceSquared < nearest)
                {
                    nearest = distanceSquared;
                }
            }

            return nearest;
        }

        private void ProcessRelease(HandGrabState hand, RuntimeBuffers buffers)
        {
            if (!hand.ReleaseRequested)
            {
                return;
            }

            bool inheritVelocity = hand.InheritReleaseVelocity;
            hand.ReleaseRequested = false;
            hand.InheritReleaseVelocity = false;
            Vector3 releaseVelocity = inheritVelocity ? hand.Velocity * ReleaseVelocityScale : Vector3.zero;
            if (releaseVelocity.magnitude > MaxReleaseVelocityMetersPerSecond)
            {
                releaseVelocity = releaseVelocity.normalized * MaxReleaseVelocityMetersPerSecond;
            }

            object velocityValue = CreateFloat3(releaseVelocity);
            for (int i = 0; i < hand.Particles.Count; i++)
            {
                GrabbedParticle particle = hand.Particles[i];
                if (TryResolveParticle(buffers, particle, out int particleIndex, out _, out _))
                {
                    _float3ArrayAccessor.Set(buffers.Velocity, particleIndex, velocityValue);
                    _float3ArrayAccessor.Set(buffers.RealVelocity, particleIndex, velocityValue);
                }

                _ownedParticles.Remove(particle.OwnershipKey);
            }

            if (hand.Particles.Count > 0)
            {
                VRModCore.LogRuntimeDebug($"[Physics][OpenXR][MagicaGrab] {hand.Name} released {hand.Particles.Count} particle(s), velocity={releaseVelocity.magnitude:F2}m/s.");
            }

            hand.Particles.Clear();
        }

        private void ReleaseHandWithoutRuntime(HandGrabState hand)
        {
            if (!hand.ReleaseRequested)
            {
                return;
            }

            for (int i = 0; i < hand.Particles.Count; i++)
            {
                _ownedParticles.Remove(hand.Particles[i].OwnershipKey);
            }

            hand.Particles.Clear();
            hand.ReleaseRequested = false;
            hand.InheritReleaseVelocity = false;
        }

        private void ApplyHandPins(HandGrabState hand, RuntimeBuffers buffers)
        {
            if (hand.Particles.Count == 0)
            {
                return;
            }

            if (!hand.HasPose)
            {
                QueueAutomaticRelease(hand);
                return;
            }

            for (int i = 0; i < hand.Particles.Count; i++)
            {
                GrabbedParticle particle = hand.Particles[i];
                if (!TryResolveParticle(buffers, particle, out int particleIndex, out int proxyIndex, out Vector3 currentPosition))
                {
                    QueueAutomaticRelease(hand);
                    return;
                }

                Vector3 hardTarget = hand.Position + (hand.Rotation * particle.LocalOffset);
                if ((hardTarget - currentPosition).magnitude > AutoReleaseDistanceMeters)
                {
                    QueueAutomaticRelease(hand);
                    return;
                }

                particle.ResolvedParticleIndex = particleIndex;
                particle.ResolvedProxyIndex = proxyIndex;
                particle.CurrentPosition = currentPosition;
                particle.HardTarget = hardTarget;
            }

            float transition = Mathf.Clamp01((Time.unscaledTime - hand.CaptureTime) / GrabTransitionSeconds);
            object zero = CreateFloat3(Vector3.zero);
            for (int i = 0; i < hand.Particles.Count; i++)
            {
                GrabbedParticle particle = hand.Particles[i];
                Vector3 target = Vector3.Lerp(particle.CapturePosition, particle.HardTarget, transition);
                target = Vector3.MoveTowards(particle.CurrentPosition, target, MaxTargetStepMeters);
                object targetValue = CreateFloat3(target);

                _float3ArrayAccessor.Set(buffers.NextPos, particle.ResolvedParticleIndex, targetValue);
                _float3ArrayAccessor.Set(buffers.OldPos, particle.ResolvedParticleIndex, targetValue);
                _float3ArrayAccessor.Set(buffers.OldPosition, particle.ResolvedParticleIndex, targetValue);
                _float3ArrayAccessor.Set(buffers.VelocityPos, particle.ResolvedParticleIndex, targetValue);
                _float3ArrayAccessor.Set(buffers.DispPos, particle.ResolvedParticleIndex, targetValue);
                _float3ArrayAccessor.Set(buffers.ProxyPositions, particle.ResolvedProxyIndex, targetValue);
                _float3ArrayAccessor.Set(buffers.Velocity, particle.ResolvedParticleIndex, zero);
                _float3ArrayAccessor.Set(buffers.RealVelocity, particle.ResolvedParticleIndex, zero);
            }
        }

        private bool TryResolveParticle(
            RuntimeBuffers buffers,
            GrabbedParticle particle,
            out int particleIndex,
            out int proxyIndex,
            out Vector3 position)
        {
            particleIndex = -1;
            proxyIndex = -1;
            position = default;
            if (!TryGetTeamData(buffers, particle.TeamId, requireEnabled: false, out _, out DataChunkInfo particleChunk, out DataChunkInfo proxyChunk)
                || particle.LocalIndex < 0
                || particle.LocalIndex >= particleChunk.Length
                || particle.LocalIndex >= proxyChunk.Length)
            {
                return false;
            }

            particleIndex = particleChunk.Start + particle.LocalIndex;
            proxyIndex = proxyChunk.Start + particle.LocalIndex;
            if (!ArePinIndicesValid(buffers, particleIndex, proxyIndex))
            {
                return false;
            }

            position = ReadFloat3(_float3ArrayAccessor.Get(buffers.DispPos, particleIndex));
            return IsFinite(position);
        }

        private bool TryGetRuntimeBuffers(out RuntimeBuffers buffers)
        {
            buffers = null;
            object teamManager = TryGetProperty(_magicaManagerTeamProperty, null);
            object simulationManager = TryGetProperty(_magicaManagerSimulationProperty, null);
            object virtualMeshManager = TryGetProperty(_magicaManagerVMeshProperty, null);
            if (teamManager == null || simulationManager == null || virtualMeshManager == null)
            {
                return false;
            }

            buffers = new RuntimeBuffers
            {
                TeamData = _teamDataArrayField.GetValue(teamManager),
                Attributes = _attributesField.GetValue(virtualMeshManager),
                ProxyPositions = _proxyPositionsField.GetValue(virtualMeshManager),
                NextPos = _nextPosArrayField.GetValue(simulationManager),
                OldPos = _oldPosArrayField.GetValue(simulationManager),
                OldPosition = _oldPositionArrayField.GetValue(simulationManager),
                VelocityPos = _velocityPosArrayField.GetValue(simulationManager),
                DispPos = _dispPosArrayField.GetValue(simulationManager),
                Velocity = _velocityArrayField.GetValue(simulationManager),
                RealVelocity = _realVelocityArrayField.GetValue(simulationManager)
            };

            return buffers.AllPresent;
        }

        private bool TryGetTeamData(
            RuntimeBuffers buffers,
            int teamId,
            bool requireEnabled,
            out object teamData,
            out DataChunkInfo particleChunk,
            out DataChunkInfo proxyChunk)
        {
            teamData = null;
            particleChunk = default;
            proxyChunk = default;
            if (teamId < 0 || !IsArrayIndexValid(buffers.TeamData, teamId))
            {
                return false;
            }

            teamData = _teamDataArrayAccessor.Get(buffers.TeamData, teamId);
            if (!GetBoolProperty(_teamDataIsValidProperty, teamData)
                || (requireEnabled && !GetBoolProperty(_teamDataIsEnableProperty, teamData)))
            {
                return false;
            }

            particleChunk = ReadChunk(_particleChunkField.GetValue(teamData));
            proxyChunk = ReadChunk(_proxyCommonChunkField.GetValue(teamData));
            return particleChunk.IsValid && proxyChunk.IsValid;
        }

        private DataChunkInfo ReadChunk(object chunk)
        {
            if (chunk == null)
            {
                return default;
            }

            return new DataChunkInfo(
                (int)_chunkStartIndexField.GetValue(chunk),
                (int)_chunkDataLengthField.GetValue(chunk));
        }

        private bool ArePinIndicesValid(RuntimeBuffers buffers, int particleIndex, int proxyIndex)
        {
            return IsArrayIndexValid(buffers.NextPos, particleIndex)
                && IsArrayIndexValid(buffers.OldPos, particleIndex)
                && IsArrayIndexValid(buffers.OldPosition, particleIndex)
                && IsArrayIndexValid(buffers.VelocityPos, particleIndex)
                && IsArrayIndexValid(buffers.DispPos, particleIndex)
                && IsArrayIndexValid(buffers.Velocity, particleIndex)
                && IsArrayIndexValid(buffers.RealVelocity, particleIndex)
                && IsArrayIndexValid(buffers.ProxyPositions, proxyIndex);
        }

        private bool IsArrayIndexValid(object array, int index)
        {
            if (array == null || index < 0)
            {
                return false;
            }

            ArrayAccessor accessor = ReferenceEquals(array, null) ? null
                : (array.GetType() == _teamDataArrayField.FieldType ? _teamDataArrayAccessor
                    : (array.GetType() == _attributesField.FieldType ? _attributeArrayAccessor : _float3ArrayAccessor));
            return accessor != null && index < accessor.GetCount(array);
        }

        private Vector3 ReadFloat3(object value)
        {
            return new Vector3(
                (float)_float3XField.GetValue(value),
                (float)_float3YField.GetValue(value),
                (float)_float3ZField.GetValue(value));
        }

        private object CreateFloat3(Vector3 value)
        {
            object boxed = Activator.CreateInstance(_float3Type);
            _float3XField.SetValue(boxed, value.x);
            _float3YField.SetValue(boxed, value.y);
            _float3ZField.SetValue(boxed, value.z);
            return boxed;
        }

        private static bool GetBoolProperty(PropertyInfo property, object target)
        {
            object value = TryGetProperty(property, target);
            return value is bool result && result;
        }

        private static bool TryGetIntProperty(PropertyInfo property, object target, out int result)
        {
            object value = TryGetProperty(property, target);
            if (value is int intValue)
            {
                result = intValue;
                return true;
            }

            result = default;
            return false;
        }

        private static object TryGetProperty(PropertyInfo property, object target)
        {
            if (property == null)
            {
                return null;
            }

            try
            {
                return property.GetValue(target, null);
            }
            catch
            {
                return null;
            }
        }

        private static Type GetSingleGenericArgument(Type type)
        {
            Type[] arguments = type?.GetGenericArguments();
            return arguments != null && arguments.Length == 1 ? arguments[0] : null;
        }

        private static bool HasArrayElementType(FieldInfo field, Type elementType)
        {
            return field != null && GetSingleGenericArgument(field.FieldType) == elementType;
        }

        private static long MakeOwnershipKey(int teamId, int localIndex)
        {
            return ((long)teamId << 32) | (uint)localIndex;
        }

        private static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x)
                && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
                && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }

        private static void ClearHand(HandGrabState hand)
        {
            hand.Particles.Clear();
            hand.PendingCapture = false;
            hand.PendingCaptureWarningLogged = false;
            hand.ReleaseRequested = false;
            hand.InheritReleaseVelocity = false;
            hand.SuppressUntilGripRelease = false;
            hand.GripPressed = false;
            hand.HasPose = false;
            hand.HasPreviousPose = false;
            hand.Velocity = Vector3.zero;
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
                type = assemblies[i]?.GetType(fullTypeName, false);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        private sealed class ArrayAccessor
        {
            private readonly PropertyInfo _countProperty;
            private readonly PropertyInfo _itemProperty;

            public ArrayAccessor(Type arrayType)
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                _countProperty = arrayType?.GetProperty("Count", flags);
                _itemProperty = arrayType?.GetProperty("Item", flags);
            }

            public bool IsReadable => _countProperty?.CanRead == true && _itemProperty?.CanRead == true;
            public bool IsReadWrite => IsReadable && _itemProperty?.CanWrite == true;

            public int GetCount(object array)
            {
                return (int)_countProperty.GetValue(array, null);
            }

            public object Get(object array, int index)
            {
                return _itemProperty.GetValue(array, [index]);
            }

            public void Set(object array, int index, object value)
            {
                _itemProperty.SetValue(array, value, [index]);
            }
        }

        private sealed class RuntimeBuffers
        {
            public object TeamData;
            public object Attributes;
            public object ProxyPositions;
            public object NextPos;
            public object OldPos;
            public object OldPosition;
            public object VelocityPos;
            public object DispPos;
            public object Velocity;
            public object RealVelocity;

            public bool AllPresent => TeamData != null && Attributes != null && ProxyPositions != null
                && NextPos != null && OldPos != null && OldPosition != null && VelocityPos != null
                && DispPos != null && Velocity != null && RealVelocity != null;
        }

        private sealed class HandGrabState
        {
            public HandGrabState(string name)
            {
                Name = name;
            }

            public string Name { get; }
            public List<GrabbedParticle> Particles { get; } = [];
            public OpenXrHandWorldColliderPose[] ProbePoses { get; } = new OpenXrHandWorldColliderPose[OpenXrHandPoseModel.MaxColliderPoseCount];
            public bool GripPressed;
            public bool PendingCapture;
            public bool PendingCaptureWarningLogged;
            public bool ReleaseRequested;
            public bool InheritReleaseVelocity;
            public bool SuppressUntilGripRelease;
            public bool HasPose;
            public bool HasPreviousPose;
            public Vector3 Position;
            public Quaternion Rotation = Quaternion.identity;
            public Vector3 PreviousPosition;
            public float PreviousPoseTime;
            public Vector3 Velocity;
            public float CaptureTime;
            public float PendingCaptureTime;
            public int ProbeCount;
        }

        private sealed class GrabbedParticle
        {
            public GrabbedParticle(
                int teamId,
                int localIndex,
                long ownershipKey,
                Vector3 capturePosition,
                Vector3 localOffset)
            {
                TeamId = teamId;
                LocalIndex = localIndex;
                OwnershipKey = ownershipKey;
                CapturePosition = capturePosition;
                LocalOffset = localOffset;
            }

            public int TeamId { get; }
            public int LocalIndex { get; }
            public long OwnershipKey { get; }
            public Vector3 CapturePosition { get; }
            public Vector3 LocalOffset { get; }
            public int ResolvedParticleIndex;
            public int ResolvedProxyIndex;
            public Vector3 CurrentPosition;
            public Vector3 HardTarget;
        }

        private readonly struct ParticleCandidate
        {
            public ParticleCandidate(int teamId, int localIndex, Vector3 position, float distanceSquared)
            {
                TeamId = teamId;
                LocalIndex = localIndex;
                Position = position;
                DistanceSquared = distanceSquared;
            }

            public int TeamId { get; }
            public int LocalIndex { get; }
            public Vector3 Position { get; }
            public float DistanceSquared { get; }
        }

        private readonly struct DataChunkInfo
        {
            public DataChunkInfo(int start, int length)
            {
                Start = start;
                Length = length;
            }

            public int Start { get; }
            public int Length { get; }
            public bool IsValid => Start >= 0 && Length > 0;
        }
    }
}
#else
namespace UnityVRMod.Features.VrVisualization
{
    internal sealed class OpenXrMagicaClothGrabber
    {
        public bool IsGripCaptured(bool isLeftHand) => false;
        public bool ShouldConsumeGrip(bool isLeftHand) => false;

        public void Update(
            bool hasLeftHandPose,
            float leftGripValue,
            bool hasRightHandPose,
            float rightGripValue)
        {
        }

        public void Reset()
        {
        }
    }
}
#endif
