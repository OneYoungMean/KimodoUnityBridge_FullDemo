using UnityEngine;

namespace KimodoBridge
{
    public static class KimodoRuntimeUtility
    {
        public static string SanitizeName(string input, string defaultName = "joint")
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return string.IsNullOrWhiteSpace(defaultName) ? "joint" : defaultName;
            }

            return input.Replace("/", "_").Replace("\\", "_").Replace(":", "_");
        }

        public static Vector3 QuaternionToAxisAngleVector(Quaternion q)
        {
            q.Normalize();
            q.ToAngleAxis(out float degrees, out Vector3 axis);
            if (float.IsNaN(axis.x) || axis == Vector3.zero)
            {
                return Vector3.zero;
            }

            if (degrees > 180f)
            {
                degrees -= 360f;
            }

            float radians = degrees * Mathf.Deg2Rad;
            return axis.normalized * radians;
        }
    }
}
