using System;
using KimodoUnityBridge;
using UnityEngine;

namespace KimodoBridge
{
    /// <summary>
    /// Canonical 70-float sample layout. The payload is deliberately fixed;
    /// channel enablement and validity are carried by separate KimodoConstraintMask values.
    /// Each transform is translation (x,y,z) followed by quaternion (x,y,z,w).
    /// </summary>
    public static class KimodoSampleDataLayout
    {
        public const int BodyMuscleOffset = 0;
        public const int BodyMuscleCount = 49;
        public const int RootTqOffset = BodyMuscleOffset + BodyMuscleCount;
        public const int RootTqCount = 7;
        public const int LeftFootTqOffset = RootTqOffset + RootTqCount;
        public const int FootTqCount = 7;
        public const int RightFootTqOffset = LeftFootTqOffset + FootTqCount;
        public const int SampleDataLength = RightFootTqOffset + FootTqCount;

        public static float[] CreateBuffer() => new float[SampleDataLength];

        public static bool IsValid(KimodoBridge.MuscleSample sample) =>
            sample != null && TryValidate(sample.data, out _);

        public static KimodoBridge.MuscleSample FromBuffer(float[] data)
        {
            return new KimodoBridge.MuscleSample
            {
                data = data != null ? (float[])data.Clone() : CreateBuffer()
            };
        }

        public static float[] ToBuffer(KimodoBridge.MuscleSample sample) =>
            sample?.data != null ? (float[])sample.data.Clone() : CreateBuffer();

        public static bool IsValidLength(float[] data) =>
            data != null && data.Length == SampleDataLength;

        public static void SetTransform(float[] data, int offset, Vector3 position, Quaternion rotation)
        {
            RequireBuffer(data, offset);
            data[offset] = position.x;
            data[offset + 1] = position.y;
            data[offset + 2] = position.z;
            data[offset + 3] = rotation.x;
            data[offset + 4] = rotation.y;
            data[offset + 5] = rotation.z;
            data[offset + 6] = rotation.w;
        }

        public static void GetTransform(
            float[] data,
            int offset,
            out Vector3 position,
            out Quaternion rotation)
        {
            RequireBuffer(data, offset);
            position = new Vector3(data[offset], data[offset + 1], data[offset + 2]);
            rotation = new Quaternion(
                data[offset + 3],
                data[offset + 4],
                data[offset + 5],
                data[offset + 6]);
        }

        public static bool TryValidate(float[] data, out string error)
        {
            if (!IsValidLength(data))
            {
                error = $"sampleData must contain exactly {SampleDataLength} values.";
                return false;
            }

            for (int i = 0; i < data.Length; i++)
            {
                if (float.IsNaN(data[i]) || float.IsInfinity(data[i]))
                {
                    error = $"sampleData[{i}] must be finite.";
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }

        public static bool TryValidate(KimodoBridge.MuscleSample sample, out string error) =>
            TryValidate(sample?.data, out error);

        private static void RequireBuffer(float[] data, int offset)
        {
            if (!IsValidLength(data) || offset < 0 || offset + 6 >= data.Length)
            {
                throw new ArgumentException("sampleData must be a valid 70-value buffer.", nameof(data));
            }
        }
    }
}

namespace KimodoBridge
{
    /// <summary>
    /// Shared math for a Humanoid sample's complete root motion.
    /// Root2D is a planar navigation override only; the sampled root's Y,
    /// pitch and roll remain motion data and are never discarded here.
    /// </summary>
    public static class KimodoMotionMath
    {
        public readonly struct PoseDelta
        {
            public PoseDelta(float meanBodyMuscleDelta, Vector3 rootPositionDelta,
                Vector3 rootRotationDeltaEulerDegrees, float rootRotationDeltaDegrees)
            {
                MeanBodyMuscleDelta = meanBodyMuscleDelta;
                RootPositionDelta = rootPositionDelta;
                RootRotationDeltaEulerDegrees = rootRotationDeltaEulerDegrees;
                RootRotationDeltaDegrees = rootRotationDeltaDegrees;
            }

            public float MeanBodyMuscleDelta { get; }
            public Vector3 RootPositionDelta { get; }
            public Vector3 RootRotationDeltaEulerDegrees { get; }
            public float RootRotationDeltaDegrees { get; }
            public float RootHeightDelta => RootPositionDelta.y;
            public float RootPitchDeltaDegrees => RootRotationDeltaEulerDegrees.x;
            public float RootYawDeltaDegrees => RootRotationDeltaEulerDegrees.y;
            public float RootRollDeltaDegrees => RootRotationDeltaEulerDegrees.z;
        }

        public static PoseDelta Compare(MuscleSample origin, MuscleSample target)
        {
            if (!KimodoSampleDataLayout.IsValid(origin) || !KimodoSampleDataLayout.IsValid(target))
                throw new ArgumentException("Motion comparison requires two valid Humanoid samples.");
            origin.GetRoot(out Vector3 originPosition, out Quaternion originRotation);
            target.GetRoot(out Vector3 targetPosition, out Quaternion targetRotation);
            return Compare(origin, target, originPosition, originRotation, targetPosition, targetRotation);
        }

        public static PoseDelta Compare(MuscleSample origin, MuscleSample target,
            Vector3 originRootPosition, Quaternion originRootRotation,
            Vector3 targetRootPosition, Quaternion targetRootRotation)
        {
            if (!KimodoSampleDataLayout.IsValid(origin) || !KimodoSampleDataLayout.IsValid(target))
                throw new ArgumentException("Motion comparison requires two valid Humanoid samples.");
            float bodyDelta = 0f;
            for (int index = 0; index < KimodoSampleDataLayout.BodyMuscleCount; index++)
                bodyDelta += Mathf.Abs(target.data[index] - origin.data[index]);
            Quaternion relativeRotation = Quaternion.Inverse(NormalizeRotation(originRootRotation)) *
                NormalizeRotation(targetRootRotation);
            return new PoseDelta(bodyDelta / KimodoSampleDataLayout.BodyMuscleCount,
                targetRootPosition - originRootPosition, SignedEulerDegrees(relativeRotation),
                Quaternion.Angle(NormalizeRotation(originRootRotation), NormalizeRotation(targetRootRotation)));
        }

        public static Quaternion ResolvePlanarHeading(Quaternion rotation)
        {
            Vector3 forward = Vector3.ProjectOnPlane(NormalizeRotation(rotation) * Vector3.forward, Vector3.up);
            return forward.sqrMagnitude > 1e-8f
                ? Quaternion.LookRotation(forward.normalized, Vector3.up) : Quaternion.identity;
        }

        public static float ResolvePlanarYawDegrees(Quaternion rotation) => ResolvePlanarHeading(rotation).eulerAngles.y;

        /// <summary>Returns a Unity world position's XZ projection.</summary>
        public static Vector2 ToPlanar(Vector3 position) => new Vector2(position.x, position.z);

        /// <summary>Returns the XZ displacement from <paramref name="from"/> to <paramref name="to"/>.</summary>
        public static Vector2 ToPlanarDelta(Vector3 from, Vector3 to) => ToPlanar(to - from);

        /// <summary>Rebuilds a world position from an XZ projection and its preserved height.</summary>
        public static Vector3 FromPlanar(Vector2 planarPosition, float height) =>
            new Vector3(planarPosition.x, height, planarPosition.y);

        /// <summary>Projects a world-space displacement into a planar frame, ignoring height, pitch and roll.</summary>
        public static Vector2 ToPlanarLocalDelta(Vector3 worldDelta, Quaternion frameRotation)
        {
            worldDelta.y = 0f;
            return ToPlanar(Quaternion.Inverse(ResolvePlanarHeading(frameRotation)) * worldDelta);
        }

        /// <summary>Returns the normalized XZ forward vector for a rotation.</summary>
        public static Vector2 ToPlanarHeadingVector(Quaternion rotation)
        {
            return ToPlanarHeadingVector(ResolvePlanarHeading(rotation) * Vector3.forward);
        }

        public static Vector2 ToPlanarHeadingVector(Vector3 forward)
        {
            Vector2 heading = ToPlanar(forward);
            return heading.sqrMagnitude > 1e-8f ? heading.normalized : Vector2.up;
        }

        public static float PlanarDistance(Vector3 from, Vector3 to) => ToPlanarDelta(from, to).magnitude;

        public static float PlanarHeadingAngleDegrees(Quaternion from, Quaternion to) =>
            Vector2.Angle(ToPlanarHeadingVector(from), ToPlanarHeadingVector(to));

        /// <summary>Returns Unity yaw in degrees from an XZ forward vector.</summary>
        public static float ResolvePlanarYawDegrees(Vector2 heading)
        {
            if (heading.sqrMagnitude <= 1e-8f) return 0f;
            return Mathf.Atan2(heading.x, heading.y) * Mathf.Rad2Deg;
        }

        /// <summary>Interpolates normalized XZ forward vectors by the shortest yaw arc.</summary>
        public static Vector2 InterpolatePlanarHeading(Vector2 from, Vector2 to, float t)
        {
            from = from.sqrMagnitude > 1e-8f ? from.normalized : Vector2.up;
            to = to.sqrMagnitude > 1e-8f ? to.normalized : Vector2.up;
            float angle = Mathf.LerpAngle(
                ResolvePlanarYawDegrees(from),
                ResolvePlanarYawDegrees(to),
                t) * Mathf.Deg2Rad;
            return new Vector2(Mathf.Sin(angle), Mathf.Cos(angle));
        }

        public static Vector3 SignedEulerDegrees(Quaternion rotation)
        {
            Vector3 euler = NormalizeRotation(rotation).eulerAngles;
            return new Vector3(Mathf.DeltaAngle(0f, euler.x), Mathf.DeltaAngle(0f, euler.y),
                Mathf.DeltaAngle(0f, euler.z));
        }

        public static Vector3 RelativeEulerDegrees(Quaternion origin, Quaternion target) =>
            SignedEulerDegrees(Quaternion.Inverse(NormalizeRotation(origin)) * NormalizeRotation(target));

        public static Vector3 ApplyPlanarPosition(Vector3 completePosition, Vector3 planarPosition) =>
            new Vector3(planarPosition.x, completePosition.y, planarPosition.z);

        public static Vector3 NormalizePlanarPosition(
            Vector3 completePosition,
            Vector3 originPosition,
            Quaternion originHeading)
        {
            Vector2 localDelta = ToPlanarLocalDelta(completePosition - originPosition, originHeading);
            return ApplyPlanarPosition(
                completePosition,
                new Vector3(localDelta.x, 0f, localDelta.y));
        }

        public static Quaternion ApplyPlanarHeading(Quaternion completeRotation, Quaternion planarHeading)
        {
            Quaternion currentHeading = ResolvePlanarHeading(completeRotation);
            Quaternion desiredHeading = ResolvePlanarHeading(planarHeading);
            return (desiredHeading * Quaternion.Inverse(currentHeading) * NormalizeRotation(completeRotation)).normalized;
        }

        public static Quaternion NormalizePlanarHeading(
            Quaternion completeRotation,
            Quaternion originHeading)
        {
            Quaternion origin = ResolvePlanarHeading(originHeading);
            Quaternion relativeHeading = Quaternion.Inverse(origin) *
                ResolvePlanarHeading(completeRotation);
            return ApplyPlanarHeading(completeRotation, relativeHeading);
        }

        private static Quaternion NormalizeRotation(Quaternion rotation)
        {
            float magnitudeSquared = rotation.x * rotation.x + rotation.y * rotation.y +
                rotation.z * rotation.z + rotation.w * rotation.w;
            return magnitudeSquared > 1e-8f ? rotation.normalized : Quaternion.identity;
        }

    }
}
