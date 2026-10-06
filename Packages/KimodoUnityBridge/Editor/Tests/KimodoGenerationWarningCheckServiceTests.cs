using Newtonsoft.Json.Linq;
using NUnit.Framework;
using KimodoBridge.Editor;
using KimodoBridge;
using KimodoUnityBridge;
using UnityEngine;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoGenerationWarningCheckServiceTests
    {
        [Test]
        public void RootTrajectory_ReportsSpeedAndAccelerationWithoutBlocking()
        {
            JObject args = new JObject
            {
                ["constraints"] = new JArray
                {
                    new JObject { ["frame"] = 0, ["type"] = "root2d", ["root2d"] = Root(0f, 0f) },
                    new JObject { ["frame"] = 1, ["type"] = "root2d", ["root2d"] = Root(1f, 0f) }
                }
            };

            JObject result = KimodoGenerationWarningCheckService.Build(args, 60, 60f);

            Assert.That(result.Value<bool>("blocking"), Is.False);
            Assert.That(result.Value<string>("overall"), Is.EqualTo("warning"));
            Assert.That(result["root_trajectory"].Value<float>("max_speed_mps"), Is.GreaterThan(7f));
            Assert.That(result["root_trajectory"]["issues"].ToObject<JArray>().Count, Is.GreaterThan(0));
        }

        [Test]
        public void Overlap_ReportsRootConflictOnSameFrame()
        {
            JObject args = new JObject
            {
                ["constraints"] = new JArray
                {
                    new JObject { ["frame"] = 10, ["type"] = "root2d", ["root2d"] = Root(0f, 0f) },
                    new JObject { ["frame"] = 10, ["type"] = "root_effect", ["root2d"] = Root(1f, 0f), ["effect"] = new JObject { ["right_hand"] = new JObject { ["position"] = new JArray(0, 1, 0) } } }
                }
            };

            JObject result = KimodoGenerationWarningCheckService.Build(args, 60, 60f);

            Assert.That(result["constraint_overlap"].Value<string>("status"), Is.EqualTo("warning"));
            Assert.That(result["constraint_overlap"]["issues"].ToObject<JArray>().Count, Is.EqualTo(1));
        }

        [Test]
        public void FullBodyEffector_IsReportedAsNonBlockingDiagnostic()
        {
            JObject args = new JObject
            {
                ["constraints"] = new JArray
                {
                    new JObject { ["frame"] = 0, ["type"] = "fullbody", ["pose"] = new JObject { ["track"] = "Pose", ["index"] = 0 } }
                }
            };
            var sample = new KimodoMarkerSampleResult
            {
                sampleTime = 0,
                validMask = new KimodoConstraintMask { leftHand = true },
                effectors = new KimodoConstraintEffectors { leftHand = new KimodoRigidTransform { t = Vector3.one } }
            };

            JObject result = KimodoGenerationWarningCheckService.Build(args, 60, 60f, generatedSamples: new[] { sample });

            Assert.That(result.Value<bool>("blocking"), Is.False);
            Assert.That(result["effectors"].Value<string>("status"), Is.EqualTo("warning"));
            Assert.That(result["effectors"]["checked_count"].Value<int>(), Is.EqualTo(1));
        }

        private static JObject Root(float x, float z)
        {
            return new JObject
            {
                ["position"] = new JArray(x, z),
                ["heading"] = new JArray(0, 1)
            };
        }
    }
}

