#if OPENXR_BUILD
using System;
using System.Collections.Generic;
using System.Globalization;

namespace UnityVRMod.Features.VrVisualization
{
    internal sealed class OpenXrHandPoseJsonSide
    {
        public OpenXrHandPoseJsonBone[] Open { get; set; } = Array.Empty<OpenXrHandPoseJsonBone>();
        public OpenXrHandPoseJsonBone[] Fist { get; set; } = Array.Empty<OpenXrHandPoseJsonBone>();
        public OpenXrHandPoseJsonBone[] IndexCurl { get; set; } = Array.Empty<OpenXrHandPoseJsonBone>();
    }

    internal sealed class OpenXrHandPoseJsonBone
    {
        public string Bone { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float W { get; set; }
    }

    internal sealed class OpenXrHandColliderLayoutJsonSide
    {
        public OpenXrHandColliderLayoutJsonCollider[] Colliders { get; set; } = Array.Empty<OpenXrHandColliderLayoutJsonCollider>();
    }

    internal sealed class OpenXrHandColliderLayoutJsonCollider
    {
        public string Name { get; set; }
        public string Bone { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float Radius { get; set; }
    }

    internal static class OpenXrHandAssetJsonParser
    {
        public static bool TryParsePoses(string json, out OpenXrHandPoseJsonSide left, out OpenXrHandPoseJsonSide right)
        {
            left = null;
            right = null;
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            left = ParsePoseSide(json, "left");
            right = ParsePoseSide(json, "right");
            return left != null || right != null;
        }

        public static bool TryParseColliderLayout(string json, out OpenXrHandColliderLayoutJsonSide left, out OpenXrHandColliderLayoutJsonSide right)
        {
            left = null;
            right = null;
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            left = ParseColliderLayoutSide(json, "left");
            right = ParseColliderLayoutSide(json, "right");
            return left != null || right != null;
        }

        private static OpenXrHandPoseJsonSide ParsePoseSide(string json, string sideName)
        {
            string sideObject = ExtractObject(json, sideName);
            if (string.IsNullOrEmpty(sideObject))
            {
                return null;
            }

            return new OpenXrHandPoseJsonSide
            {
                Open = ParsePoseBones(ExtractArray(sideObject, "open")),
                Fist = ParsePoseBones(ExtractArray(sideObject, "fist")),
                IndexCurl = ParsePoseBones(ExtractArray(sideObject, "indexCurl"))
            };
        }

        private static OpenXrHandColliderLayoutJsonSide ParseColliderLayoutSide(string json, string sideName)
        {
            string sideObject = ExtractObject(json, sideName);
            if (string.IsNullOrEmpty(sideObject))
            {
                return null;
            }

            return new OpenXrHandColliderLayoutJsonSide
            {
                Colliders = ParseColliderSpheres(ExtractArray(sideObject, "colliders"))
            };
        }

        private static OpenXrHandPoseJsonBone[] ParsePoseBones(string arrayJson)
        {
            if (string.IsNullOrWhiteSpace(arrayJson))
            {
                return Array.Empty<OpenXrHandPoseJsonBone>();
            }

            var bones = new List<OpenXrHandPoseJsonBone>(32);
            foreach (string objectJson in EnumerateObjects(arrayJson))
            {
                if (TryParsePoseBone(objectJson, out OpenXrHandPoseJsonBone bone))
                {
                    bones.Add(bone);
                }
            }

            return bones.ToArray();
        }

        private static OpenXrHandColliderLayoutJsonCollider[] ParseColliderSpheres(string arrayJson)
        {
            if (string.IsNullOrWhiteSpace(arrayJson))
            {
                return Array.Empty<OpenXrHandColliderLayoutJsonCollider>();
            }

            var colliders = new List<OpenXrHandColliderLayoutJsonCollider>(24);
            foreach (string objectJson in EnumerateObjects(arrayJson))
            {
                if (TryParseColliderSphere(objectJson, out OpenXrHandColliderLayoutJsonCollider collider))
                {
                    colliders.Add(collider);
                }
            }

            return colliders.ToArray();
        }

        private static bool TryParsePoseBone(string objectJson, out OpenXrHandPoseJsonBone bone)
        {
            bone = null;
            if (!TryReadString(objectJson, "bone", out string boneName)
                || !TryReadFloat(objectJson, "x", out float x)
                || !TryReadFloat(objectJson, "y", out float y)
                || !TryReadFloat(objectJson, "z", out float z)
                || !TryReadFloat(objectJson, "w", out float w))
            {
                return false;
            }

            bone = new OpenXrHandPoseJsonBone
            {
                Bone = boneName,
                X = x,
                Y = y,
                Z = z,
                W = w
            };
            return true;
        }

        private static bool TryParseColliderSphere(string objectJson, out OpenXrHandColliderLayoutJsonCollider collider)
        {
            collider = null;
            if (!TryReadString(objectJson, "name", out string name)
                || !TryReadString(objectJson, "bone", out string bone)
                || !TryReadFloat(objectJson, "x", out float x)
                || !TryReadFloat(objectJson, "y", out float y)
                || !TryReadFloat(objectJson, "z", out float z)
                || !TryReadFloat(objectJson, "radius", out float radius))
            {
                return false;
            }

            collider = new OpenXrHandColliderLayoutJsonCollider
            {
                Name = name,
                Bone = bone,
                X = x,
                Y = y,
                Z = z,
                Radius = radius
            };
            return true;
        }

        private static IEnumerable<string> EnumerateObjects(string arrayJson)
        {
            int searchIndex = 0;
            while (searchIndex < arrayJson.Length)
            {
                int objectStart = arrayJson.IndexOf('{', searchIndex);
                if (objectStart < 0)
                {
                    yield break;
                }

                int objectEnd = FindMatching(arrayJson, objectStart, '{', '}');
                if (objectEnd < 0)
                {
                    yield break;
                }

                yield return arrayJson.Substring(objectStart, objectEnd - objectStart + 1);
                searchIndex = objectEnd + 1;
            }
        }

        private static string ExtractObject(string json, string propertyName)
        {
            int propertyIndex = FindProperty(json, propertyName);
            if (propertyIndex < 0)
            {
                return null;
            }

            int colonIndex = json.IndexOf(':', propertyIndex);
            if (colonIndex < 0)
            {
                return null;
            }

            int objectStart = json.IndexOf('{', colonIndex + 1);
            if (objectStart < 0)
            {
                return null;
            }

            int objectEnd = FindMatching(json, objectStart, '{', '}');
            return objectEnd > objectStart ? json.Substring(objectStart, objectEnd - objectStart + 1) : null;
        }

        private static string ExtractArray(string json, string propertyName)
        {
            int propertyIndex = FindProperty(json, propertyName);
            if (propertyIndex < 0)
            {
                return null;
            }

            int colonIndex = json.IndexOf(':', propertyIndex);
            if (colonIndex < 0)
            {
                return null;
            }

            int arrayStart = json.IndexOf('[', colonIndex + 1);
            if (arrayStart < 0)
            {
                return null;
            }

            int arrayEnd = FindMatching(json, arrayStart, '[', ']');
            return arrayEnd > arrayStart ? json.Substring(arrayStart, arrayEnd - arrayStart + 1) : null;
        }

        private static bool TryReadString(string json, string propertyName, out string value)
        {
            value = null;
            int propertyIndex = FindProperty(json, propertyName);
            if (propertyIndex < 0)
            {
                return false;
            }

            int colonIndex = json.IndexOf(':', propertyIndex);
            if (colonIndex < 0)
            {
                return false;
            }

            int quoteStart = json.IndexOf('"', colonIndex + 1);
            if (quoteStart < 0)
            {
                return false;
            }

            int quoteEnd = FindStringEnd(json, quoteStart);
            if (quoteEnd <= quoteStart)
            {
                return false;
            }

            value = Unescape(json.Substring(quoteStart + 1, quoteEnd - quoteStart - 1));
            return true;
        }

        private static bool TryReadFloat(string json, string propertyName, out float value)
        {
            value = 0f;
            int propertyIndex = FindProperty(json, propertyName);
            if (propertyIndex < 0)
            {
                return false;
            }

            int colonIndex = json.IndexOf(':', propertyIndex);
            if (colonIndex < 0)
            {
                return false;
            }

            int numberStart = colonIndex + 1;
            while (numberStart < json.Length && char.IsWhiteSpace(json[numberStart]))
            {
                numberStart++;
            }

            int numberEnd = numberStart;
            while (numberEnd < json.Length)
            {
                char c = json[numberEnd];
                if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E')
                {
                    numberEnd++;
                    continue;
                }

                break;
            }

            return numberEnd > numberStart
                && float.TryParse(
                    json.Substring(numberStart, numberEnd - numberStart),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out value);
        }

        private static int FindProperty(string json, string propertyName)
        {
            return json.IndexOf("\"" + propertyName + "\"", StringComparison.Ordinal);
        }

        private static int FindMatching(string json, int startIndex, char open, char close)
        {
            bool inString = false;
            bool escaped = false;
            int depth = 0;
            for (int i = startIndex; i < json.Length; i++)
            {
                char c = json[i];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (c == '\\')
                    {
                        escaped = true;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                }
                else if (c == open)
                {
                    depth++;
                }
                else if (c == close)
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        private static int FindStringEnd(string json, int quoteStart)
        {
            bool escaped = false;
            for (int i = quoteStart + 1; i < json.Length; i++)
            {
                char c = json[i];
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    return i;
                }
            }

            return -1;
        }

        private static string Unescape(string value)
        {
            return value.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
    }
}
#endif
