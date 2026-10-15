using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoConstraintPoseTests
    {
        [Test]
        public void BuildPoseJson_UsesTheSharedConstraintPoseShape()
        {
            var result = KimodoConstraintPoseUtility.BuildCapturedSampleResult(
                new MuscleSample(), null, 0.25);

            JObject pose = KimodoConstraintPoseUtility.BuildPoseJson(result);
            Assert.That(((JArray)pose["muscles"]).Count, Is.EqualTo(KimodoSampleDataLayout.BodyMuscleCount));
            CollectionAssert.AreEqual(new[] { "left", "right" }, ((JObject)pose["hands"]).Properties().Select(item => item.Name));
            CollectionAssert.AreEqual(new[] { "left", "right" }, ((JObject)pose["feet"]).Properties().Select(item => item.Name));
            Assert.That(pose["root"]?["t"], Is.TypeOf<JArray>());
            Assert.That(pose["root"]?["q"], Is.TypeOf<JArray>());
        }

        [Test]
        public void TryAppendSmplxConstraints_UsesRawConstraintShape()
        {
            string[] jointNames =
            {
                "left_hip", "right_hip", "spine1", "left_knee", "right_knee", "spine2",
                "left_ankle", "right_ankle", "spine3", "left_foot", "right_foot", "neck",
                "left_collar", "right_collar", "head", "left_shoulder", "right_shoulder",
                "left_elbow", "right_elbow", "left_wrist", "right_wrist"
            };
            var analysis = new JObject
            {
                ["joint_names"] = new JArray(jointNames),
                ["poses"] = new JArray(new JObject
                {
                    ["frame"] = 0,
                    ["timestamp_seconds"] = 0.0,
                    ["global_orient_axis_angle"] = new JArray(0.0, 0.0, 0.0),
                    ["body_pose_axis_angle"] = new JArray(Enumerable.Repeat(0.0, 63)),
                    ["translation_m"] = new JArray(0.0, 0.0, 0.0)
                })
            };

            Assert.That(KimodoConstraintPoseUtility.TryAppendSmplxConstraints(analysis, out string error),
                Is.True, error);
            JObject constraint = (JObject)((JArray)analysis["constraints"])[0];
            Assert.That(constraint["type"]?.Value<string>(), Is.EqualTo("fullbody"));
            Assert.That(((JArray)constraint["frame_indices"]).Count, Is.EqualTo(1));
            Assert.That(((JArray)constraint["root_positions"]).Count, Is.EqualTo(1));
            Assert.That(((JArray)constraint["local_joints_rot"]).Count, Is.EqualTo(1));
            Assert.That(((JArray)constraint["local_joints_rot"]?[0]).Count, Is.EqualTo(22));
            Assert.That(analysis["poses"]?[0]?["constraints"], Is.Null);
        }
    }
}
