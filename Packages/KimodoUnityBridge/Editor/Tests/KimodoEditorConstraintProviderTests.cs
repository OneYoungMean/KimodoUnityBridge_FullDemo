using System.Collections.Generic;
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
        public void DeferredOutRootBaseline_IsExcludedFromSpeedValidationAndKeepsPosePayload()
        {
            KimodoMarkerSampleResult inSample = CreateSample(0.0, 12f);
            KimodoMarkerSampleResult deferredOutSample = CreateSample(1.5, 0f);
            deferredOutSample.sampleData.data[0] = 0.42f;
            var samplesForValidation = new List<KimodoMarkerSampleResult> { inSample };

            KimodoEditorConstraintProvider.AppendDeferredOutSamplesForRootMotionValidation(
                new[] { deferredOutSample },
                samplesForValidation,
                sampleTimeOffsetSeconds: 0.25);

            Assert.That(samplesForValidation, Has.Count.EqualTo(2));
            Assert.That(samplesForValidation[1].sampleTime, Is.EqualTo(1.75).Within(1e-8));
            Assert.That(samplesForValidation[1].rootOverride, Is.Null,
                "Deferred Out root coordinates use a separate clip baseline until post-apply calibration.");
            Assert.That(samplesForValidation[1].sampleData.data[0], Is.EqualTo(0.42f),
                "Only the uncalibrated root is excluded; the Out pose payload remains available.");
            Assert.That(deferredOutSample.rootOverride, Is.Not.Null,
                "Preparing the validation copy must not mutate the deferred generation sample.");

            Assert.That(KimodoGenerationWarningCheckService.TryValidateRootMotionConstraints(
                samplesForValidation,
                30f,
                out string error), Is.True, error);
        }

        [Test]
        public void DeferredOutBaselineExclusion_DoesNotAllowFastImmediateRootMotion()
        {
            var samplesForValidation = new List<KimodoMarkerSampleResult>
            {
                CreateSample(0.0, 0f),
                CreateSample(0.5, 5f)
            };
            KimodoMarkerSampleResult deferredOutSample = CreateSample(1.0, 100f);

            KimodoEditorConstraintProvider.AppendDeferredOutSamplesForRootMotionValidation(
                new[] { deferredOutSample },
                samplesForValidation,
                sampleTimeOffsetSeconds: 0.0);

            Assert.That(KimodoGenerationWarningCheckService.TryValidateRootMotionConstraints(
                samplesForValidation,
                30f,
                out string error), Is.False);
            Assert.That(error, Does.Contain("10.000 m/s"));
            Assert.That(error, Does.Contain("exceeding the 7.0 m/s constraint limit"));
        }

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

        private static KimodoMarkerSampleResult CreateSample(double sampleTime, float rootZ)
        {
            return new KimodoMarkerSampleResult
            {
                sampleTime = sampleTime,
                sampleValid = true,
                constraintMode = "fullbody",
                rootOverride = new KimodoRigidTransform
                {
                    position = new Vector3(0f, 0f, rootZ),
                    rotation = Quaternion.identity
                }
            };
        }
    }
}
