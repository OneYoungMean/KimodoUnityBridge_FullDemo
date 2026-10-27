using NUnit.Framework;
using UnityEngine;

namespace KimodoBridge.Editor.Tests
{
    public class KimodoMotionMathTests
    {
        [Test]
        public void Compare_ReportsVerticalAndTiltRootMotion()
        {
            var origin = new MuscleSample();
            origin.SetRoot(new Vector3(1f, 0.5f, 2f), Quaternion.identity);
            var target = new MuscleSample();
            target.SetRoot(new Vector3(4f, 1.75f, -2f), Quaternion.Euler(15f, 30f, -8f));

            KimodoMotionMath.PoseDelta delta = KimodoMotionMath.Compare(origin, target);

            Assert.That(delta.RootPositionDelta, Is.EqualTo(new Vector3(3f, 1.25f, -4f)));
            Assert.That(delta.RootHeightDelta, Is.EqualTo(1.25f));
            Assert.That(delta.RootRotationDeltaDegrees, Is.GreaterThan(1f));
            Assert.That(Mathf.Abs(delta.RootPitchDeltaDegrees), Is.GreaterThan(1f));
            Assert.That(Mathf.Abs(delta.RootRollDeltaDegrees), Is.GreaterThan(1f));
        }

        [Test]
        public void ApplyPlanarOverride_PreservesHeightAndTilt()
        {
            Vector3 completePosition = new Vector3(1f, 2.5f, 3f);
            Quaternion completeRotation = Quaternion.Euler(18f, 25f, -11f);
            Quaternion desiredHeading = Quaternion.Euler(0f, 120f, 0f);

            Vector3 resultPosition = KimodoMotionMath.ApplyPlanarPosition(
                completePosition,
                new Vector3(8f, 999f, -4f));
            Quaternion resultRotation = KimodoMotionMath.ApplyPlanarHeading(completeRotation, desiredHeading);
            Quaternion originalTilt = Quaternion.Inverse(
                KimodoMotionMath.ResolvePlanarHeading(completeRotation)) * completeRotation;
            Quaternion resultTilt = Quaternion.Inverse(
                KimodoMotionMath.ResolvePlanarHeading(resultRotation)) * resultRotation;

            Assert.That(resultPosition, Is.EqualTo(new Vector3(8f, 2.5f, -4f)));
            Assert.That(
                Quaternion.Angle(KimodoMotionMath.ResolvePlanarHeading(resultRotation), desiredHeading),
                Is.LessThan(0.001f));
            Assert.That(Quaternion.Angle(resultTilt, originalTilt), Is.LessThan(0.001f));
        }

        [Test]
        public void PlanarProjection_UsesUnityXZAndPreservesHeightSeparately()
        {
            Vector3 position = new Vector3(2.5f, 7f, -4f);

            Assert.That(KimodoMotionMath.ToPlanar(position), Is.EqualTo(new Vector2(2.5f, -4f)));
            Assert.That(KimodoMotionMath.FromPlanar(new Vector2(-3f, 8f), position.y),
                Is.EqualTo(new Vector3(-3f, 7f, 8f)));
            Assert.That(KimodoMotionMath.PlanarDistance(position, new Vector3(5.5f, 100f, 0f)),
                Is.EqualTo(5f).Within(0.0001f));
        }

        [Test]
        public void LocalPlanarDelta_IgnoresHeightPitchAndRoll()
        {
            Vector3 worldDelta = new Vector3(2f, 9f, 1f);
            Quaternion frame = Quaternion.Euler(28f, 90f, -17f);

            Vector2 actual = KimodoMotionMath.ToPlanarLocalDelta(worldDelta, frame);
            Vector2 expected = KimodoMotionMath.ToPlanarLocalDelta(
                new Vector3(worldDelta.x, 0f, worldDelta.z),
                Quaternion.Euler(0f, 90f, 0f));

            Assert.That(Vector2.Distance(actual, expected), Is.LessThan(0.0001f));
        }

        [Test]
        public void InterpolatePlanarHeading_UsesShortestYawArc()
        {
            Vector2 result = KimodoMotionMath.InterpolatePlanarHeading(
                new Vector2(0f, 1f),
                new Vector2(1f, 0f),
                0.5f);

            Assert.That(result.x, Is.EqualTo(Mathf.Sqrt(0.5f)).Within(0.0001f));
            Assert.That(result.y, Is.EqualTo(Mathf.Sqrt(0.5f)).Within(0.0001f));
        }
    }
}
