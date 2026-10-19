using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoConstraintProtocolBoundaryTests
    {
        [TestCase("fullbody")]
        [TestCase("right-hand")]
        public void PayloadSerialization_FoldsSameFrameRoot2DIntoRootContext(string type)
        {
            var payload = new KimodoConstraintPayload
            {
                json = new JArray(
                    new JObject
                    {
                        ["type"] = "root2d",
                        ["frame_indices"] = new JArray(4),
                        ["smooth_root_2d"] = new JArray { new JArray { 1f, 2f } },
                        ["global_root_heading"] = new JArray { new JArray { 0f, -1f } }
                    },
                    new JObject
                    {
                        ["type"] = type,
                        ["frame_indices"] = new JArray(4),
                        ["root_positions"] = new JArray { new JArray { 0f, 1f, 0f } },
                        ["local_joints_rot"] = new JArray { new JArray { new JArray { 0f, 0f, 0f } } }
                    }).ToString()
            };

            JArray serialized = JArray.Parse(payload.Serialize(
                KimodoMotionModelProfiles.DefaultModelName, new List<byte[]>()));
            Assert.That(serialized, Has.Count.EqualTo(1));
            Assert.That(serialized[0]["type"].Value<string>(), Is.EqualTo(type));
            Assert.That(serialized[0]["root_positions"][0][0].Value<float>(), Is.EqualTo(1f));
            Assert.That(serialized[0]["root_positions"][0][1].Value<float>(), Is.EqualTo(1f));
            Assert.That(serialized[0]["root_positions"][0][2].Value<float>(), Is.EqualTo(2f));
            Assert.That(
                Math.Abs(serialized[0]["local_joints_rot"][0][0][1].Value<float>()),
                Is.EqualTo(Math.PI / 2).Within(1e-3));
        }

        [Test]
        public void PayloadSerialization_PreservesRoot2DAndFullBodyAtDifferentFrames()
        {
            var payload = new KimodoConstraintPayload
            {
                json = new JArray(
                    new JObject
                    {
                        ["type"] = "root2d",
                        ["frame_indices"] = new JArray(4),
                        ["smooth_root_2d"] = new JArray { new JArray { 1f, 2f } }
                    },
                    new JObject
                    {
                        ["type"] = "fullbody",
                        ["frame_indices"] = new JArray(5),
                        ["root_positions"] = new JArray { new JArray { 3f, 1f, 4f } },
                        ["local_joints_rot"] = new JArray { new JArray { new JArray { 0f, 0f, 0f } } }
                    }).ToString()
            };

            JArray serialized = JArray.Parse(payload.Serialize(
                KimodoMotionModelProfiles.DefaultModelName,
                new List<byte[]>()));

            Assert.That(serialized, Has.Count.EqualTo(2));
            Assert.That(serialized[0]["frame_indices"][0].Value<int>(), Is.EqualTo(4));
            Assert.That(serialized[1]["frame_indices"][0].Value<int>(), Is.EqualTo(5));
            Assert.That(serialized[1]["root_positions"][0][0].Value<float>(), Is.EqualTo(3f));
            Assert.That(serialized[1]["root_positions"][0][2].Value<float>(), Is.EqualTo(4f));
        }
    }
}
