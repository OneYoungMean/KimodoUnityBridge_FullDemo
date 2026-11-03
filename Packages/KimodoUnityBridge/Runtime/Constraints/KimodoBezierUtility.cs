using UnityEngine;

namespace KimodoBridge
{
    internal static class KimodoBezierUtility
    {
        internal static Vector2 EvaluateCubic(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
        {
            float oneMinus = 1f - t;
            return oneMinus * oneMinus * oneMinus * p0 +
                3f * oneMinus * oneMinus * t * p1 +
                3f * oneMinus * t * t * p2 +
                t * t * t * p3;
        }
    }
}
