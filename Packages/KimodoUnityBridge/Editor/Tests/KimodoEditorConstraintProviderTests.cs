using System.Collections.Generic;
using System.Linq;
using KimodoBridge;
using KimodoBridge.Editor;
using KimodoUnityBridge;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace KimodoUnityBridge.Command.Tests
{
    public sealed class KimodoEditorConstraintProviderTests
    {
        [Test]
        public void RawConstraintValidation_RejectsFastRootTrajectory()
        {
            string json = new JArray
            {
                new JObject
                {
                    ["type"] = "fullbody",
                    ["frame_indices"] = new JArray(0, 1),
                    ["root_positions"] = new JArray(
                        new JArray(0f, 0f, 0f),
                        new JArray(0f, 0f, 1f))
                }
            }.ToString();

            Assert.That(
                KimodoPostApplyOutConstraintUtility.TryValidateRawConstraintRootMotion(
                    json,
                    30f,
                    out string error),
                Is.False);
            Assert.That(error, Does.Contain("30.000 m/s"));
        }

        [Test]
        public void RawConstraintValidation_RejectsAccelerationEvenWhenSpeedIsWithinLimit()
        {
            string json = new JArray
            {
                CreateRootConstraint(new[] { 0, 6, 12 }, new[] { 0f, 0f, 0.2f })
            }.ToString();

            Assert.That(
                KimodoPostApplyOutConstraintUtility.TryValidateRawConstraintRootMotion(
                    json,
                    60f,
                    out string error),
                Is.False);
            Assert.That(error, Does.Contain("20.000 m/s²"));
        }

        [Test]
        public void PostApplyInOutSameFrame_GivesStartConstraintPriority()
        {
            string inJson = CreateRootConstraint(0, 1f);
            string outJson = CreateRootConstraint(0, 2f);

            JArray result = JArray.Parse(KimodoPostApplyOutConstraintUtility.ApplyInOut(
                "[]", inJson, outJson, null, KimodoMotionModelProfiles.DefaultModelName,
                30f, 0, null));

            Assert.That(result, Has.Count.EqualTo(1), result.ToString());
            Assert.That(result[0]["frame_indices"][0].Value<int>(), Is.Zero);
            Assert.That(result[0]["root_positions"][0][2].Value<float>(), Is.EqualTo(1f));
        }

        [Test]
        public void PostApplySparseWindow_PreservesImmediateConstraintsBetweenSamples()
        {
            string existing = new JArray
            {
                new JObject
                {
                    ["type"] = "effector",
                    ["frame_indices"] = new JArray(0, 5, 10),
                    ["target_positions"] = new JArray(
                        new JArray(0f, 0f, 0f), new JArray(5f, 0f, 0f), new JArray(10f, 0f, 0f))
                }
            }.ToString();
            string deferredIn = new JArray
            {
                CreateRootConstraint(new[] { 0, 10 }, new[] { 1f, 2f })
            }.ToString();

            JArray result = JArray.Parse(KimodoPostApplyOutConstraintUtility.ApplyInOut(
                existing, deferredIn, "[]", null,
                KimodoMotionModelProfiles.DefaultModelName, 30f, 0, null));
            JObject effector = result.Values<JObject>().Single(item => item.Value<string>("type") == "effector");
            CollectionAssert.AreEqual(new[] { 5 }, effector["frame_indices"].Values<int>().ToArray());
            Assert.That(effector["target_positions"].Count(), Is.EqualTo(1));
            Assert.That(effector["target_positions"][0][0].Value<float>(), Is.EqualTo(5f));
        }

        [TestCase(true, 0, 90, new[] { 0, 5, 11 }, true,
            TestName = "InsideIn_AnchorsAtWindowStart")]
        [TestCase(false, 0, 90, new[] { 78, 84, 89 }, false,
            TestName = "InsideOut_AnchorsAtWindowEnd")]
        [TestCase(true, 12, 90, new[] { 0, 5, 11 }, false,
            TestName = "OutsideIn_AnchorsAtWindowEndNearSeam")]
        [TestCase(false, 0, 90, new[] { 90, 95, 101 }, true,
            TestName = "OutsideOut_AnchorsAtWindowStartNearSeam")]
        public void DeferredWindow_ChoosesTheSampleNearestToTheOutputSeam(
            bool isIn,
            int trimStart,
            int targetFrames,
            int[] sampleFrames,
            bool expectedPreferStart)
        {
            Assert.That(
                KimodoPostApplyOutConstraintUtility.PreferStartForDeferredWindow(
                    sampleFrames, trimStart, targetFrames, isIn),
                Is.EqualTo(expectedPreferStart));
        }

        [Test]
        public void PostApplyTrajectoryPass_RequiresAnEnabledDeferredBoundary()
        {
            var clip = ScriptableObject.CreateInstance<KimodoPlayableClip>();
            try
            {
                clip.inOutConstraintMode = KimodoInOutConstraintMode.Inside;
                clip.postApplyInConstraint = true;
                clip.enableInConstraint = false;
                clip.enableOutConstraint = true;

                Assert.That(KimodoPlayableClipGenerationExecutionService.RequiresTrajectoryPass(clip, null), Is.False);

                clip.enableInConstraint = true;
                Assert.That(KimodoPlayableClipGenerationExecutionService.RequiresTrajectoryPass(clip, null), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(clip);
            }
        }

        private static string CreateRootConstraint(int frame, float rootZ)
        {
            return new JArray
            {
                new JObject
                {
                    ["type"] = "fullbody",
                    ["frame_indices"] = new JArray(frame),
                    ["root_positions"] = new JArray { new JArray { 0f, 0f, rootZ } }
                }
            }.ToString();
        }

        private static JObject CreateRootConstraint(int[] frames, float[] rootZ)
        {
            return new JObject
            {
                ["type"] = "fullbody",
                ["frame_indices"] = new JArray(frames),
                ["root_positions"] = new JArray(rootZ.Select(value => new JArray(0f, 0f, value)))
            };
        }

    }
}
