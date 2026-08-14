#if OPENXR_BUILD
using System.Collections.Generic;
using UnityEngine;

namespace UnityVRMod.Features.VrVisualization
{
    internal enum OpenXrHandFinger
    {
        Thumb,
        Index,
        Middle,
        Ring,
        Little
    }

    internal enum OpenXrHandColliderAnchor
    {
        Palm = 0,
        ThumbTip = 1,
        IndexTip = 2,
        MiddleTip = 3,
        RingTip = 4,
        LittleTip = 5
    }

    internal readonly struct OpenXrHandWorldColliderPose
    {
        public OpenXrHandWorldColliderPose(string name, Vector3 worldPosition, Quaternion worldRotation, float radiusScale)
        {
            Name = name;
            WorldPosition = worldPosition;
            WorldRotation = worldRotation;
            RadiusScale = radiusScale;
        }

        public string Name { get; }
        public Vector3 WorldPosition { get; }
        public Quaternion WorldRotation { get; }
        public float RadiusScale { get; }
    }

    internal readonly struct OpenXrHandWorldGripPose
    {
        public OpenXrHandWorldGripPose(Vector3 worldPosition, Quaternion worldRotation)
        {
            WorldPosition = worldPosition;
            WorldRotation = worldRotation;
        }

        public Vector3 WorldPosition { get; }
        public Quaternion WorldRotation { get; }
    }

    internal static class OpenXrHandPoseModel
    {
        public const int FallbackColliderPoseCount = 6;
        public const int MaxColliderPoseCount = 32;
        public const float ReferenceHandColliderRadius = 0.06f;

        public static float GetAnchorRadiusScale(OpenXrHandColliderAnchor anchor)
        {
            return anchor switch
            {
                OpenXrHandColliderAnchor.Palm => 0.88f,
                OpenXrHandColliderAnchor.ThumbTip => 0.42f,
                OpenXrHandColliderAnchor.IndexTip => 0.38f,
                OpenXrHandColliderAnchor.MiddleTip => 0.42f,
                OpenXrHandColliderAnchor.RingTip => 0.38f,
                OpenXrHandColliderAnchor.LittleTip => 0.34f,
                _ => 0.4f
            };
        }
    }

    internal static class OpenXrHandModelAnchorRegistry
    {
        private static readonly OpenXrHandWorldColliderPose[] LeftPoses = new OpenXrHandWorldColliderPose[OpenXrHandPoseModel.MaxColliderPoseCount];
        private static readonly OpenXrHandWorldColliderPose[] RightPoses = new OpenXrHandWorldColliderPose[OpenXrHandPoseModel.MaxColliderPoseCount];
        private static bool _hasLeftPoses;
        private static bool _hasRightPoses;
        private static bool _hasLeftGripPose;
        private static bool _hasRightGripPose;
        private static int _leftPoseCount;
        private static int _rightPoseCount;
        private static OpenXrHandWorldGripPose _leftGripPose;
        private static OpenXrHandWorldGripPose _rightGripPose;

        public static void Clear(bool isLeftHand)
        {
            if (isLeftHand)
            {
                _hasLeftPoses = false;
                _hasLeftGripPose = false;
                _leftPoseCount = 0;
            }
            else
            {
                _hasRightPoses = false;
                _hasRightGripPose = false;
                _rightPoseCount = 0;
            }
        }

        public static void UpdateGripPose(
            bool isLeftHand,
            Vector3 palmPosition,
            Quaternion palmRotation,
            Transform middleTip,
            Transform ringTip,
            Transform littleTip)
        {
            bool hasGripAnchors = middleTip != null && ringTip != null && littleTip != null;
            if (!hasGripAnchors)
            {
                if (isLeftHand)
                {
                    _hasLeftGripPose = false;
                }
                else
                {
                    _hasRightGripPose = false;
                }

                return;
            }

            Vector3 fingertipCenter = (middleTip.position + ringTip.position + littleTip.position) / 3f;
            var pose = new OpenXrHandWorldGripPose(
                Vector3.Lerp(palmPosition, fingertipCenter, 0.35f),
                palmRotation);

            if (isLeftHand)
            {
                _leftGripPose = pose;
                _hasLeftGripPose = true;
            }
            else
            {
                _rightGripPose = pose;
                _hasRightGripPose = true;
            }
        }

        public static bool TryGetWorldGripPose(bool isLeftHand, out Vector3 worldPosition, out Quaternion worldRotation)
        {
            bool hasPose = isLeftHand ? _hasLeftGripPose : _hasRightGripPose;
            OpenXrHandWorldGripPose pose = isLeftHand ? _leftGripPose : _rightGripPose;
            worldPosition = pose.WorldPosition;
            worldRotation = pose.WorldRotation;
            return hasPose;
        }

        public static void Update(bool isLeftHand, Transform palm, Transform thumbTip, Transform indexTip, Transform middleTip, Transform ringTip, Transform littleTip)
        {
            OpenXrHandWorldColliderPose[] target = isLeftHand ? LeftPoses : RightPoses;
            bool hasAllAnchors = palm != null
                && thumbTip != null
                && indexTip != null
                && middleTip != null
                && ringTip != null
                && littleTip != null;

            if (!hasAllAnchors)
            {
                Clear(isLeftHand);
                return;
            }

            Fill(target, OpenXrHandColliderAnchor.Palm, "Palm", palm);
            Fill(target, OpenXrHandColliderAnchor.ThumbTip, "ThumbTip", thumbTip);
            Fill(target, OpenXrHandColliderAnchor.IndexTip, "IndexTip", indexTip);
            Fill(target, OpenXrHandColliderAnchor.MiddleTip, "MiddleTip", middleTip);
            Fill(target, OpenXrHandColliderAnchor.RingTip, "RingTip", ringTip);
            Fill(target, OpenXrHandColliderAnchor.LittleTip, "LittleTip", littleTip);

            if (isLeftHand)
            {
                _hasLeftPoses = true;
                _leftPoseCount = OpenXrHandPoseModel.FallbackColliderPoseCount;
            }
            else
            {
                _hasRightPoses = true;
                _rightPoseCount = OpenXrHandPoseModel.FallbackColliderPoseCount;
            }
        }

        public static void Update(bool isLeftHand, IReadOnlyList<OpenXrHandWorldColliderPose> poses)
        {
            if (poses == null || poses.Count == 0)
            {
                Clear(isLeftHand);
                return;
            }

            OpenXrHandWorldColliderPose[] target = isLeftHand ? LeftPoses : RightPoses;
            int count = Mathf.Min(poses.Count, OpenXrHandPoseModel.MaxColliderPoseCount);
            for (int i = 0; i < count; i++)
            {
                target[i] = poses[i];
            }

            if (isLeftHand)
            {
                _hasLeftPoses = true;
                _leftPoseCount = count;
            }
            else
            {
                _hasRightPoses = true;
                _rightPoseCount = count;
            }
        }

        public static bool TryFillWorldColliderPoses(bool isLeftHand, OpenXrHandWorldColliderPose[] output)
        {
            return TryFillWorldColliderPoses(isLeftHand, output, out _);
        }

        public static bool TryFillWorldColliderPoses(bool isLeftHand, OpenXrHandWorldColliderPose[] output, out int count)
        {
            if (output == null || output.Length == 0)
            {
                count = 0;
                return false;
            }

            OpenXrHandWorldColliderPose[] source = isLeftHand ? LeftPoses : RightPoses;
            bool hasPoses = isLeftHand ? _hasLeftPoses : _hasRightPoses;
            count = isLeftHand ? _leftPoseCount : _rightPoseCount;
            if (!hasPoses)
            {
                count = 0;
                return false;
            }

            count = Mathf.Min(count, Mathf.Min(output.Length, OpenXrHandPoseModel.MaxColliderPoseCount));
            for (int i = 0; i < count; i++)
            {
                output[i] = source[i];
            }

            return count > 0;
        }

        private static void Fill(OpenXrHandWorldColliderPose[] target, OpenXrHandColliderAnchor anchor, string name, Transform transform)
        {
            int index = (int)anchor;
            target[index] = new OpenXrHandWorldColliderPose(
                name,
                transform.position,
                transform.rotation,
                OpenXrHandPoseModel.GetAnchorRadiusScale(anchor));
        }
    }
}
#endif
