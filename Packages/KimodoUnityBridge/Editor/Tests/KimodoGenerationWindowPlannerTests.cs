using System.Collections.Generic;
using System.Linq;
using KimodoBridge.Editor;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Newtonsoft.Json;
using UnityEngine;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoGenerationWindowPlannerTests
    {
        [Test]
        public void FifteenSecondPlan_PreservesTypedConstraintsAndBuildsSparseSeamRoot()
        {
            const int fps = 30;
            var constraints = new List<KimodoGlobalRawConstraint>
            {
                Anchor(3 * fps, KimodoRawConstraintKind.Root2D,
                    new Vector2(0f, 0f), Vector2.up,
                    new JObject { ["root2d"] = new JObject { ["position"] = new JArray(0, 0) } }),
                Anchor(6 * fps, KimodoRawConstraintKind.FullBody,
                    new Vector2(6f, 0f), Vector2.up,
                    new JObject { ["pose"] = new JObject { ["track"] = "test.Poses", ["index"] = 0 } }),
                Anchor(9 * fps, KimodoRawConstraintKind.Root2D,
                    new Vector2(9f, 3f), Vector2.right,
                    new JObject { ["root2d"] = new JObject { ["position"] = new JArray(9, 3) } }),
                Anchor(12 * fps, KimodoRawConstraintKind.Effector,
                    new Vector2(12f, 3f), Vector2.right,
                    new JObject { ["effect"] = new JObject { ["right_hand"] = new JObject { ["position"] = new JArray(1, 2, 3) } } })
            };

            List<KimodoGenerationWindow> windows = KimodoGenerationWindowPlanner.Plan(
                totalFrames: 15 * fps,
                frameRate: fps,
                maxRequestFrames: 10 * fps,
                overlapFrames: fps,
                minimumCoreFrames: 3 * fps,
                maximumCoreFrames: 8 * fps,
                preferredCoreFrames: 6 * fps,
                constraints: constraints,
                constraintGuardFrames: fps / 2);

            Assert.That(windows, Has.Count.EqualTo(2));
            Assert.That(windows.All(window => window.CoreFrameCount >= 3 * fps && window.CoreFrameCount <= 8 * fps), Is.True);
            Assert.That(windows.All(window => window.RequestFrameCount <= 10 * fps), Is.True);
            Assert.That(windows[0].RequestEndFrame - windows[1].RequestStartFrame, Is.EqualTo(fps));
            Assert.That(constraints.Any(item =>
                Mathf.Abs(item.Frame - windows[0].CoreEndFrame) <= fps / 2), Is.False,
                "The selected cut must avoid the authored constraint guard bands.");

            List<KimodoWindowConstraint> left = KimodoGenerationWindowPlanner.MapConstraints(windows[0], constraints);
            List<KimodoWindowConstraint> right = KimodoGenerationWindowPlanner.MapConstraints(windows[1], constraints);
            CollectionAssert.AreEqual(
                new[] { KimodoRawConstraintKind.Root2D, KimodoRawConstraintKind.FullBody },
                left.Select(item => item.Kind));
            CollectionAssert.AreEqual(
                new[] { KimodoRawConstraintKind.Root2D, KimodoRawConstraintKind.Effector },
                right.Select(item => item.Kind));
            CollectionAssert.AreEqual(
                constraints.Where(item => item.Frame >= windows[0].RequestStartFrame && item.Frame < windows[0].RequestEndFrame)
                    .Select(item => item.Frame - windows[0].RequestStartFrame),
                left.Select(item => item.LocalFrame));
            CollectionAssert.AreEqual(
                constraints.Where(item => item.Frame >= windows[1].RequestStartFrame && item.Frame < windows[1].RequestEndFrame)
                    .Select(item => item.Frame - windows[1].RequestStartFrame),
                right.Select(item => item.LocalFrame));

            // At 7.5 seconds the request windows overlap. Its Root2D is inferred
            // between the known FullBody root at 6s and Root2D root at 9s.
            KimodoGlobalRawConstraint seamRoot = KimodoGenerationWindowPlanner.InterpolateRoot2D(
                constraints[1], constraints[2], frame: 225);
            Assert.That(seamRoot.Kind, Is.EqualTo(KimodoRawConstraintKind.Root2D));
            Assert.That(seamRoot.RootPosition.x, Is.EqualTo(7.5f).Within(0.001f));
            Assert.That(seamRoot.RootPosition.y, Is.EqualTo(1.5f).Within(0.001f));
            Assert.That(seamRoot.Heading.x, Is.EqualTo(Mathf.Sqrt(0.5f)).Within(0.001f));
            Assert.That(seamRoot.Heading.y, Is.EqualTo(Mathf.Sqrt(0.5f)).Within(0.001f));

            List<KimodoWindowConstraint> leftWithSeam = KimodoGenerationWindowPlanner.MapConstraints(
                windows[0], constraints.Append(seamRoot));
            Assert.That(leftWithSeam.Any(item =>
                item.LocalFrame == 225 && item.Kind == KimodoRawConstraintKind.Root2D), Is.True);
            Assert.That(seamRoot.Frame - windows[0].RequestStartFrame, Is.EqualTo(225));
            Assert.That(seamRoot.Frame, Is.GreaterThanOrEqualTo(windows[0].RequestStartFrame));
            Assert.That(seamRoot.Frame, Is.LessThan(windows[0].RequestEndFrame));
        }

        [Test]
        public void WindowConstraintsJson_MapsFourAuthoredConstraintTypesToLocalFrames()
        {
            const int fps = 30;
            string rawConstraints = new JArray
            {
                Constraint("root2d", 3 * fps, "smooth_root_2d", new JArray(1, 2)),
                Constraint("fullbody", 6 * fps, "root_positions", new JArray(3, 4, 5)),
                Constraint("root2d", 9 * fps, "smooth_root_2d", new JArray(6, 7)),
                Constraint("right-hand", 12 * fps, "target_positions", new JArray(8, 9, 10))
            }.ToString(Formatting.None);

            List<KimodoGenerationWindow> windows = KimodoGenerationWindowPlanner.Plan(
                totalFrames: 15 * fps,
                frameRate: fps,
                maxRequestFrames: 10 * fps,
                overlapFrames: fps,
                minimumCoreFrames: 3 * fps,
                maximumCoreFrames: 8 * fps,
                preferredCoreFrames: 6 * fps,
                constraints: new List<KimodoGlobalRawConstraint>(),
                constraintGuardFrames: 0);

            var first = JArray.Parse(KimodoGenerationWindowPlanner.BuildWindowConstraintsJson(windows[0], rawConstraints));
            var second = JArray.Parse(KimodoGenerationWindowPlanner.BuildWindowConstraintsJson(windows[1], rawConstraints));

            CollectionAssert.AreEqual(new[] { "root2d", "fullbody" }, first.Values<JObject>().Select(x => x.Value<string>("type")));
            CollectionAssert.AreEqual(new[] { "root2d", "right-hand" }, second.Values<JObject>().Select(x => x.Value<string>("type")));
            CollectionAssert.AreEqual(new[] { 90, 180 }.Select(frame => frame - windows[0].RequestStartFrame),
                first.SelectMany(x => x["frame_indices"].Values<int>()));
            CollectionAssert.AreEqual(new[] { 270, 360 }.Select(frame => frame - windows[1].RequestStartFrame),
                second.SelectMany(x => x["frame_indices"].Values<int>()));
            Assert.That(first[1]["root_positions"]?[0]?[0]?.Value<int>(), Is.EqualTo(3));
            Assert.That(second[1]["target_positions"]?[0]?[2]?.Value<int>(), Is.EqualTo(10));

            JObject interpolatedRoot = KimodoGenerationWindowPlanner.CreateRoot2DConstraint(
                localFrame: 225 - windows[0].RequestStartFrame,
                position: new Vector2(7.5f, 1.5f),
                heading: new Vector2(Mathf.Sqrt(0.5f), Mathf.Sqrt(0.5f)));
            Assert.That(interpolatedRoot.Value<string>("type"), Is.EqualTo("root2d"));
            Assert.That(interpolatedRoot["frame_indices"]?[0]?.Value<int>(), Is.EqualTo(225));
            Assert.That(interpolatedRoot["smooth_root_2d"], Is.TypeOf<JArray>());
            Assert.That(((JArray)interpolatedRoot["smooth_root_2d"]).Count, Is.EqualTo(1));
            Assert.That(interpolatedRoot["smooth_root_2d"]?[0], Is.TypeOf<JArray>());
            Assert.That(interpolatedRoot["smooth_root_2d"]?[0]?[0]?.Value<float>(), Is.EqualTo(7.5f).Within(0.001f));
            Assert.That(((JArray)interpolatedRoot["global_root_heading"]).Count, Is.EqualTo(1));
            Assert.That(interpolatedRoot["global_root_heading"]?[0], Is.TypeOf<JArray>());
            Assert.That(interpolatedRoot["global_root_heading"]?[0]?[0]?.Value<float>(), Is.EqualTo(Mathf.Sqrt(0.5f)).Within(0.001f));
        }

        [Test]
        public void ReadRootAnchors_RecognizesRootFullBodyRootEffectorAtThreeSixNineTwelveSeconds()
        {
            const int fps = 30;
            string rawConstraints = new JArray
            {
                new JObject
                {
                    ["type"] = "root2d",
                    ["frame_indices"] = new JArray(3 * fps),
                    ["smooth_root_2d"] = new JArray(new JArray(0, 0)),
                    ["global_root_heading"] = new JArray(new JArray(0, 1))
                },
                new JObject
                {
                    ["type"] = "fullbody",
                    ["frame_indices"] = new JArray(6 * fps),
                    ["root_positions"] = new JArray(new JArray(6, 0, 0)),
                    ["local_joints_rot"] = new JArray(new JArray(new JArray(0, 0, 0)))
                },
                new JObject
                {
                    ["type"] = "root2d",
                    ["frame_indices"] = new JArray(9 * fps),
                    ["smooth_root_2d"] = new JArray(new JArray(9, 3)),
                    ["global_root_heading"] = new JArray(new JArray(1, 0))
                },
                new JObject
                {
                    ["type"] = "right-hand",
                    ["frame_indices"] = new JArray(12 * fps),
                    ["smooth_root_2d"] = new JArray(new JArray(12, 3)),
                    ["global_root_heading"] = new JArray(new JArray(1, 0)),
                    ["target_positions"] = new JArray(new JArray(1, 2, 3))
                }
            }.ToString(Formatting.None);

            List<KimodoGlobalRawConstraint> anchors = KimodoGenerationWindowPlanner.ReadRootAnchors(rawConstraints);
            CollectionAssert.AreEqual(
                new[] { 90, 180, 270, 360 },
                anchors.Select(anchor => anchor.Frame));
            CollectionAssert.AreEqual(
                new[]
                {
                    KimodoRawConstraintKind.Root2D,
                    KimodoRawConstraintKind.FullBody,
                    KimodoRawConstraintKind.Root2D,
                    KimodoRawConstraintKind.Effector
                },
                anchors.Select(anchor => anchor.Kind));
            Assert.That(anchors[1].RootPosition, Is.EqualTo(new Vector2(6f, 0f)));
            Assert.That(anchors[2].RootPosition, Is.EqualTo(new Vector2(9f, 3f)));
        }

        private static JObject Constraint(string type, int frame, string valuesProperty, JArray values)
        {
            return new JObject
            {
                ["type"] = type,
                ["frame_indices"] = new JArray(frame),
                [valuesProperty] = new JArray(values)
            };
        }

        private static KimodoGlobalRawConstraint Anchor(
            int frame,
            KimodoRawConstraintKind kind,
            Vector2 rootPosition,
            Vector2 heading,
            JObject payload)
        {
            return new KimodoGlobalRawConstraint
            {
                Frame = frame,
                Kind = kind,
                RootPosition = rootPosition,
                Heading = heading,
                Payload = payload
            };
        }
    }
}
