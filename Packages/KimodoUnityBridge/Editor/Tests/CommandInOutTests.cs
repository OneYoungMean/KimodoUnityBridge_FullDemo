using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using KimodoBridge;
using KimodoBridge.Editor;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using UnityEngine.Timeline;
using Object = UnityEngine.Object;

namespace KimodoUnityBridge.Command.Tests
{
    public sealed class CommandInOutTests
    {
        private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

        [TestCase(false, true, -15, -1, 1.5)]
        [TestCase(false, false, 60, 74, 0.0)]
        [TestCase(true, true, 0, 14, 0.0)]
        [TestCase(true, false, 45, 59, 1.5)]
        public void Windows_ConvertCommandFramesAndPreserveSampleOrder(bool inside, bool isIn, int first, int last, double sourceStart)
        {
            var side = JObject.Parse(@"{'source':{'clip':'Walk'},'window_frames':30,'sample_count':5}");
            var window = command_context.ResolveCommandInOutWindow(side, inside, isIn, 2, 60, 30);
            Assert.That(window.FrameCount, Is.EqualTo(15));
            Assert.That(window.OutputFrames.First(), Is.EqualTo(first));
            Assert.That(window.OutputFrames.Last(), Is.EqualTo(last));
            Assert.That(window.OutputFrames.Distinct().Count(), Is.EqualTo(5));
            Assert.That(window.SourceTimes.First(), Is.EqualTo(sourceStart).Within(1e-6));
            Assert.That(window.SourceTimes.Last() - window.SourceTimes.First(), Is.EqualTo(14.0 / 30).Within(1e-6));
        }

        [TestCase(0, 1, 2, "positive")]
        [TestCase(30, 16, 2, "duplicate")]
        [TestCase(30, 0, 2, "sample_count")]
        [TestCase(30, 5, 0.2, "too short")]
        public void InvalidWindows_AreRejected(int frames, int count, double duration, string error)
        {
            var side = JObject.Parse(@"{'source':{'clip':'Walk'}}");
            side["window_frames"] = frames; side["sample_count"] = count;
            Assert.That(() => command_context.ResolveCommandInOutWindow(side, false, true, duration, 60, 30),
                Throws.TypeOf<InvalidOperationException>().With.Message.Contains(error));
        }

        [Test]
        public void SingleSample_UsesExplicitBoundaryAndDefaultsToOneModelFrame()
        {
            var side = JObject.Parse(@"{'source':{'clip':'Walk','frame':60}}");
            var window = command_context.ResolveCommandInOutWindow(side, false, true, 2, 60, 30);
            CollectionAssert.AreEqual(new[] { 1.0 }, window.SourceTimes);
            CollectionAssert.AreEqual(new[] { -1 }, window.OutputFrames);
            side["window_frames"] = 30;
            window = command_context.ResolveCommandInOutWindow(side, false, true, 2, 60, 30);
            CollectionAssert.AreEqual(new[] { 1.0 }, window.SourceTimes);
            CollectionAssert.AreEqual(new[] { -1 }, window.OutputFrames);
        }

        [TestCase("{'constraints':[{'inout':{}}]}", false, "requires")]
        [TestCase("{'constraints':[{'inout':{'mode':'typo'}}]}", false, "mode")]
        [TestCase("{'constraints':[{'inout':{'typo':1}}]}", false, "Unknown")]
        [TestCase("{'constraints':[{'inout':{},'frame':0}]}", false, "standalone")]
        [TestCase("{'constraints':[{'inout':{}},{'inout':{}}]}", false, "standalone")]
        [TestCase("{'constraints':[{'inout':{}}]}", true, "loop")]
        public void InvalidInOut_IsRejectedBeforeSampling(string json, bool loop, string error)
        {
            var build = typeof(command_context).GetMethod("BuildCommandInOutConstraints", PrivateStatic);
            var exception = Assert.Throws<TargetInvocationException>(() => build.Invoke(null,
                new object[] { JObject.Parse(json), null, KimodoMotionModelProfiles.DefaultModelName, 60, 30f, loop, new List<KimodoMarkerSampleResult>() }));
            Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.That(exception.InnerException.Message, Does.Contain(error));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CommandSampling_UsesExplicitClipsAndBuildsPaddedRequest(bool inside)
        {
            const string model = KimodoMotionModelProfiles.DefaultModelName;
            Assert.That(KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(model, out var avatar, out var error), Is.True, error);
            Assert.That(KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(avatar, "CommandInOutFixture", out var skeleton, out error), Is.True, error);
            var directorObject = new GameObject("CommandInOutDirector");
            var director = directorObject.AddComponent<PlayableDirector>();
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            AnimationClip sourceClip = null;
            try
            {
                Assert.That(KimodoRetargetSamplingUtility.TryCaptureMuscleSample(skeleton, out var pose, out error), Is.True, error);
                pose.GetRoot(out var position, out var rotation);
                var frames = Enumerable.Range(0, 121).Select(i =>
                {
                    var frame = pose.Clone(); frame.SetRoot(position + Vector3.forward * (i / 30f), rotation); return frame;
                }).ToArray();
                Assert.That(KimodoRetargetSamplingUtility.TryCreateTransientMuscleClip(frames, 30, out sourceClip, out error), Is.True, error);
                // Initialize native clip bindings before Timeline detects root curves.
                sourceClip.SampleAnimation(skeleton.root, 0);
                director.playableAsset = timeline;
                // Match command Session character setup: Timeline owns root motion.
                skeleton.animator.applyRootMotion = false;
                skeleton.animator.Rebind();
                var track = timeline.CreateTrack<AnimationTrack>(null, "TestCharacter");
                track.trackOffset = TrackOffset.ApplyTransformOffsets;
                track.position = new Vector3(3, 0.5f, -2); track.rotation = Quaternion.Euler(0, 35, 0);
                director.SetGenericBinding(track, skeleton.animator);
                var character = new command_context.TimelineCharacterRecord("test", skeleton.root, skeleton.animator, avatar, track, null, "");
                foreach (var name in new[] { "Previous", "Next" })
                {
                    var source = track.CreateClip<AnimationPlayableAsset>();
                    ((AnimationPlayableAsset)source.asset).clip = sourceClip;
                    ((AnimationPlayableAsset)source.asset).removeStartOffset = false;
                    source.start = name == "Previous" ? 0 : 4; source.duration = 2;
                    source.clipIn = 0.25; source.timeScale = 1.2;
                    character.Animations.Add(new command_context.TimelineAnimationRecord(Guid.NewGuid(), name, "added",
                        sourceClip, source, null, null, 0, 120));
                }
                var sessionType = typeof(command_context).GetNestedType("TimelineSessionRecord", BindingFlags.NonPublic);
                var session = Activator.CreateInstance(sessionType, new object[] { Guid.NewGuid(), "InOutTest", director, timeline, "", false, null, directorObject });
                ((List<command_context.TimelineCharacterRecord>)sessionType.GetProperty("Characters").GetValue(session)).Add(character);
                var traceType = typeof(command_context).GetNestedType("TimelineGenerationTrace", BindingFlags.NonPublic);
                var trace = Activator.CreateInstance(traceType, new object[] { session, character, 8.0, 2.0 });
                var arguments = JObject.Parse(@"{'constraints':[{'inout':{'in':{'source':{'clip':'Previous'},'window_frames':30,'sample_count':5},'out':{'source':{'clip':'Next'},'window_frames':12,'sample_count':3}}}]}");
                arguments["constraints"][0]["inout"]["mode"] = inside ? "inside" : "outside";
                var build = typeof(command_context).GetMethod("BuildCommandInOutConstraints", PrivateStatic);
                director.RebuildGraph();
                director.Evaluate();
                void Reject(int targetFrames, List<KimodoMarkerSampleResult> points, string message)
                {
                    var exception = Assert.Throws<TargetInvocationException>(() => build.Invoke(null,
                        new object[] { arguments, trace, model, targetFrames, 30f, false, points }));
                    Assert.That(exception.InnerException.Message, Does.Contain(message));
                }
                Reject(inside ? 20 : 300, new List<KimodoMarkerSampleResult>(), inside ? "overlap" : "exceeding");
                if (inside)
                    Reject(60, new List<KimodoMarkerSampleResult> { new KimodoMarkerSampleResult { sampleTime = 0 } }, "conflicts");
                var external = (KimodoExternalConstraintRequest)build.Invoke(null,
                    new object[] { arguments, trace, model, 60, 30f, false, new List<KimodoMarkerSampleResult>() });
                Assert.That(external.ConstraintSamples.Count, Is.EqualTo(8));
                Assert.That(director.time, Is.EqualTo(0).Within(1e-8), "Sampling must restore Timeline time.");
                var diagnostics = (JObject)traceType.GetProperty("InOutSampling").GetValue(trace);
                Assert.That(diagnostics["in"]["source_times_seconds"], Is.Null);
                Assert.That(diagnostics["in"]["source_timeline_times_seconds"], Is.Null);
                Assert.That(diagnostics["in"]["source_timeline_frames_60"], Is.TypeOf<JArray>());
                Assert.That(diagnostics["runtime_model_frame_count"], Is.Not.Null);
                Assert.That(diagnostics["crop_start_model_frame"], Is.Not.Null);
                var firstTime = diagnostics["in"]["source_timeline_frames_60"][0].Value<int>() / 60.0;
                var sampleContext = new KimodoTimelineInOutConstraintContext
                { Director = director, Track = track, Animator = skeleton.animator, SourceClip = character.Animations[0].TimelineClip, ModelName = model };
                Assert.That(KimodoTimelineConstraintSampler.TrySampleMarker(sampleContext, firstTime, 0, "fullbody", model, out var expected, out error), Is.True, error);
                Assert.That(Vector3.Distance(external.ConstraintSamples[0].rootOverride.t, expected.rootOverride.t), Is.LessThan(0.001f), "Batch and single-frame sampling must agree.");
                Assert.That(Math.Abs(external.ConstraintSamples[0].rootOverride.t.x), Is.GreaterThan(1f), "Track offset must be retained in Character world samples.");

                var playable = (KimodoPlayableClip)typeof(command_context).GetMethod("CreateGenerationPlayableClip", PrivateStatic)
                    .Invoke(null, new object[] { trace, "CommandOutput", true });
                var output = (TimelineClip)traceType.GetProperty("TimelineClip").GetValue(trace);
                playable.bridgeModelName = model;
                Assert.That(playable.enableInConstraint || playable.enableOutConstraint, Is.False,
                    "Explicit sources must not mix with automatically inferred Session boundaries.");
                Assert.That(playable.autoBeginAnchor, Is.True,
                    "Command-created clips must retain the automatic begin anchor; real explicit samples take precedence during normalization.");
                var request = KimodoPlayableClipGenerationHostService.BuildRequest(playable, "stand", external, CancellationToken.None, timelineClipOverride: output);
                Assert.That(request.TargetFrameCount, Is.EqualTo(60));
                Assert.That(request.RuntimeFrameCount, Is.EqualTo(inside ? 60 : 81));
                Assert.That(request.RuntimeTrimStartFrame, Is.EqualTo(inside ? 0 : 15));
                var exported = JArray.Parse(request.Constraints.json).Values<JObject>().Single(c => c.Value<string>("type") == "fullbody");
                var expectedFrames = diagnostics["in"]["runtime_model_frames"].Values<int>().Concat(diagnostics["out"]["runtime_model_frames"].Values<int>());
                CollectionAssert.AreEqual(expectedFrames.ToArray(), exported["frame_indices"].Values<int>().ToArray());
                Assert.That(output.duration, Is.EqualTo(2));
                var before = external.ConstraintSamples[0].sampleTime;
                Assert.That(before, Is.EqualTo(inside ? 0 : -0.5).Within(1e-8), "Provider must not mutate source snapshots.");
            }
            finally
            {
                director.Stop();
                foreach (var track in timeline.GetOutputTracks().ToArray())
                {
                    foreach (var clip in track.GetClips()) Object.DestroyImmediate(clip.asset);
                    Object.DestroyImmediate(track);
                }
                Object.DestroyImmediate(timeline); Object.DestroyImmediate(directorObject);
                if (sourceClip != null) Object.DestroyImmediate(sourceClip);
                skeleton.Dispose();
            }
        }

        [Test]
        public void PoseGet_MatchesAnalysisWorldCoordinatesAtSameTimelineTime()
        {
            const string model = KimodoMotionModelProfiles.DefaultModelName;
            Assert.That(KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(model, out var avatar, out var error), Is.True, error);
            Assert.That(KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(avatar, "PoseGetWorldCoordinateFixture", out var skeleton, out error), Is.True, error);
            foreach (Transform transform in skeleton.root.GetComponentsInChildren<Transform>(true))
                transform.gameObject.hideFlags = HideFlags.None;
            SceneManager.MoveGameObjectToScene(skeleton.root, SceneManager.GetActiveScene());
            var sessionRoot = new GameObject(KimodoRuntimeUtility.SessionRootPrefix + "PoseGetWorldCoordinateTest");
            var directorObject = new GameObject("PoseGetWorldCoordinateDirector");
            directorObject.transform.SetParent(sessionRoot.transform, false);
            var director = directorObject.AddComponent<PlayableDirector>();
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            AnimationClip sourceClip = null;
            FieldInfo currentSessionField = typeof(command_context).GetField("currentTimelineSession", PrivateStatic);
            try
            {
                Assert.That(KimodoRetargetSamplingUtility.TryCaptureMuscleSample(skeleton, out var pose, out error), Is.True, error);
                pose.GetRoot(out var position, out var rotation);
                var frames = Enumerable.Range(0, 121).Select(i =>
                {
                    var frame = pose.Clone();
                    frame.SetRoot(position + Vector3.forward * (i / 30f), rotation);
                    return frame;
                }).ToArray();
                Assert.That(KimodoRetargetSamplingUtility.TryCreateTransientMuscleClip(frames, 30, out sourceClip, out error), Is.True, error);
                sourceClip.SampleAnimation(skeleton.root, 0);

                director.playableAsset = timeline;
                skeleton.animator.applyRootMotion = false;
                skeleton.animator.Rebind();
                var track = timeline.CreateTrack<AnimationTrack>(null, "PoseGetCharacter");
                track.trackOffset = TrackOffset.ApplyTransformOffsets;
                track.position = new Vector3(3f, 0.5f, -2f);
                track.rotation = Quaternion.Euler(0f, 35f, 0f);
                director.SetGenericBinding(track, skeleton.animator);
                var poseTrack = timeline.CreateTrack<AnimationTrack>(track, "PoseGetCharacter.Poses");
                var timelineClip = track.CreateClip<AnimationPlayableAsset>();
                ((AnimationPlayableAsset)timelineClip.asset).clip = sourceClip;
                ((AnimationPlayableAsset)timelineClip.asset).removeStartOffset = false;
                timelineClip.start = 0.0;
                timelineClip.duration = 2.0;
                timelineClip.clipIn = 0.25;
                timelineClip.timeScale = 1.2;
                var character = new command_context.TimelineCharacterRecord(
                    "PoseGetCharacter", skeleton.root, skeleton.animator, avatar, track, poseTrack, "");
                character.Animations.Add(new command_context.TimelineAnimationRecord(
                    Guid.NewGuid(), "Sample", "test", sourceClip, timelineClip, null, null, 0, 120));

                var sessionType = typeof(command_context).GetNestedType("TimelineSessionRecord", BindingFlags.NonPublic);
                var session = Activator.CreateInstance(sessionType,
                    new object[] { Guid.NewGuid(), "PoseGetWorldCoordinateTest", director, timeline, "", false, null, sessionRoot });
                ((List<command_context.TimelineCharacterRecord>)sessionType.GetProperty("Characters").GetValue(session)).Add(character);
                currentSessionField.SetValue(null, session);

                director.RebuildGraph();
                director.Evaluate();
                const double timelineTime = 1.0;
                JObject timelineResponse = JObject.Parse(command_context.PoseGet(
                    "{'source':{'character':'PoseGetCharacter','timeline_frame_60':48},'full_data':true}"));
                Assert.That(timelineResponse.Value<bool>("ok"), Is.True, timelineResponse.ToString());
                JObject middleResponse = JObject.Parse(command_context.PoseGet(
                    "{'source':{'character':'PoseGetCharacter','timeline_frame_60':60},'full_data':true}"));
                Assert.That(middleResponse.Value<bool>("ok"), Is.True, middleResponse.ToString());
                JObject tailResponse = JObject.Parse(command_context.PoseGet(
                    "{'source':{'character':'PoseGetCharacter','timeline_frame_60':72},'full_data':true}"));
                Assert.That(tailResponse.Value<bool>("ok"), Is.True, tailResponse.ToString());

                var expectedContext = new KimodoTimelineInOutConstraintContext
                {
                    Director = director,
                    Track = track,
                    Animator = skeleton.animator,
                    SourceClip = timelineClip,
                    ModelName = model
                };
                Assert.That(KimodoTimelineConstraintSampler.TrySampleMarker(
                    expectedContext, timelineTime, 0, "fullbody", model, out var expected, out error), Is.True, error);
                Assert.That(KimodoTimelineConstraintSampler.TrySampleMarker(
                    expectedContext, 0.8, 0, "fullbody", model, out var expectedTimeline, out error), Is.True, error);
                Assert.That(KimodoTimelineConstraintSampler.TrySampleMarker(
                    expectedContext, 1.2, 0, "fullbody", model, out var expectedTail, out error), Is.True, error);
                KimodoConstraintMarker[] actualMarkers = poseTrack.GetMarkers()
                    .OfType<KimodoConstraintMarker>().OrderBy(marker => marker.time).ToArray();
                Assert.That(actualMarkers, Has.Length.EqualTo(3));
                KimodoMarkerSampleResult actual = actualMarkers[1].SampleData;
                KimodoMarkerSampleResult timelineActual = actualMarkers[0].SampleData;
                KimodoMarkerSampleResult tailActual = actualMarkers[2].SampleData;
                // Muscle/Root2D decoding can introduce up to 10 cm of world-space pose error.
                Assert.That(Vector3.Distance(timelineActual.rootOverride.t, expectedTimeline.rootOverride.t), Is.LessThan(0.1f),
                    "timeline_frame_60=48 pose root world position");
                Assert.That(Vector3.Distance(actual.rootOverride.t, expected.rootOverride.t), Is.LessThan(0.1f),
                    "timeline_frame_60=60 pose root world position");
                Assert.That(Vector3.Distance(tailActual.rootOverride.t, expectedTail.rootOverride.t), Is.LessThan(0.1f),
                    "timeline_frame_60=72 pose root world position");
                KimodoTimelineTrackOffsetUtility.ResolveWorldOffset(
                    track, skeleton.animator, out Vector3 resolvedTrackPosition,
                    out Quaternion resolvedTrackRotation, out bool resolvedSceneOffset);

                Assert.That(Vector3.Distance(actual.rootOverride.t, expected.rootOverride.t), Is.LessThan(0.1f),
                    $"hips world position actual={actual.rootOverride.t} expected={expected.rootOverride.t} " +
                    $"track={resolvedTrackPosition} sceneOffset={resolvedSceneOffset} " +
                    $"characterRoot={skeleton.root.transform.position} " +
                    $"sourceTime={KimodoMarkerSamplingUtility.ResolveAnimationSourceTime(timelineClip, timelineTime):F6}");
                foreach (var pair in new[]
                {
                    (actual.effectors.leftHand, expected.effectors.leftHand, "left hand"),
                    (actual.effectors.rightHand, expected.effectors.rightHand, "right hand"),
                    (actual.effectors.leftFoot, expected.effectors.leftFoot, "left foot"),
                    (actual.effectors.rightFoot, expected.effectors.rightFoot, "right foot")
                })
                {
                    Assert.That(Vector3.Distance(pair.Item1.t, pair.Item2.t), Is.LessThan(0.1f), pair.Item3 + " world position");
                }

                Vector3 sceneRootPosition = new Vector3(8f, 1.5f, -4f);
                Quaternion sceneRootRotation = Quaternion.Euler(0f, 27f, 0f);
                skeleton.root.transform.SetPositionAndRotation(sceneRootPosition, sceneRootRotation);
                JObject scenePoseResponse = JObject.Parse(command_context.PoseGet(
                    "{'source':{'character':'PoseGetCharacter','timeline_frame_60':60}}"));
                Assert.That(scenePoseResponse.Value<bool>("ok"), Is.True, scenePoseResponse.ToString());
                JToken characterRoot = scenePoseResponse["character_root"];
                Assert.That(characterRoot["space"]?.Value<string>(), Is.EqualTo("world"));
                Assert.That(characterRoot["position"].Values<float>().ToArray(), Is.EqualTo(new[]
                {
                    sceneRootPosition.x, sceneRootPosition.y, sceneRootPosition.z
                }));
                Assert.That(characterRoot["rotation"].Values<float>().ToArray(), Is.EqualTo(new[]
                {
                    sceneRootRotation.x, sceneRootRotation.y, sceneRootRotation.z, sceneRootRotation.w
                }));

                JObject scenePoseCapture = JObject.Parse(command_context.PoseGet(
                    "{'source':{'character':'PoseGetCharacter','mode':'scene_pose'},'full_data':true}"));
                Assert.That(scenePoseCapture.Value<bool>("ok"), Is.True, scenePoseCapture.ToString());
                Assert.That(scenePoseCapture["source"]?["mode"]?.Value<string>(), Is.EqualTo("scene_pose"));
                Assert.That(scenePoseCapture["pose"]?["track"]?.Value<string>(), Is.EqualTo(poseTrack.name));
                JToken capturedRoot = scenePoseCapture["character_root"];
                Assert.That(capturedRoot["position"].Values<float>().ToArray(), Is.EqualTo(new[]
                {
                    sceneRootPosition.x, sceneRootPosition.y, sceneRootPosition.z
                }));
                Assert.That(capturedRoot["rotation"].Values<float>().ToArray(), Is.EqualTo(new[]
                {
                    sceneRootRotation.x, sceneRootRotation.y, sceneRootRotation.z, sceneRootRotation.w
                }));
                JArray sampledRootPosition = (JArray)scenePoseCapture["data"]?["root"]?["t"];
                Assert.That(sampledRootPosition[0].Value<float>(), Is.EqualTo(sceneRootPosition.x).Within(0.1f));
                Assert.That(sampledRootPosition[1].Value<float>(), Is.EqualTo(sceneRootPosition.y).Within(0.1f));
                Assert.That(sampledRootPosition[2].Value<float>(), Is.EqualTo(sceneRootPosition.z).Within(0.1f));
            }
            finally
            {
                currentSessionField?.SetValue(null, null);
                if (director != null) director.Stop();
                foreach (var track in timeline.GetOutputTracks().ToArray())
                {
                    foreach (var clip in track.GetClips().ToArray()) Object.DestroyImmediate(clip.asset);
                    Object.DestroyImmediate(track);
                }
                Object.DestroyImmediate(timeline);
                Object.DestroyImmediate(sessionRoot);
                if (sourceClip != null) Object.DestroyImmediate(sourceClip);
                skeleton.Dispose();
            }
        }

        [Test]
        public void EveryCommand_HasExamplesInStaticAndLiveHelp()
        {
            var definitions = JObject.Parse(command_dispatcher.GetCommandDefinitionsJson());
            var built = JObject.Parse((string)typeof(command_context).GetMethod("BuildCommandDefinitionsJson", PrivateStatic).Invoke(null, null));
            Assert.That(JToken.DeepEquals(built, definitions), Is.True, "Regenerate Command/help.json from its definition source.");
            var overview = JObject.Parse(command_dispatcher.Invoke("kimodo_help", "{}"));
            foreach (var command in definitions["tools"].Values<JObject>())
            {
                string name = command.Value<string>("name");
                Assert.That(command["examples"] is JArray examples && examples.Count > 0, Is.True, name);
                JObject documentation = command["documentation"] as JObject;
                Assert.That(documentation, Is.Not.Null, name);
                Assert.That(documentation.Value<string>("when_to_use"), Is.Not.Null.And.Not.Empty, name);
                foreach (string section in new[] { "behavior", "result_notes", "scene_effects", "limits", "related_commands" })
                    Assert.That(documentation[section] is JArray values && values.Count > 0, Is.True, $"{name}.{section}");
                var detail = JObject.Parse(command_dispatcher.Invoke("kimodo_help", new JObject { ["command"] = name }.ToString()));
                Assert.That(JToken.DeepEquals(detail["manual"]?["documentation"], documentation), Is.True, name);
                Assert.That(JToken.DeepEquals(detail["manual"]["examples"], command["examples"]), Is.True, name);
                JObject overviewCommand = overview["commands"].Values<JObject>().Single(c => c.Value<string>("name") == name);
                Assert.That(overviewCommand["examples"].Count(), Is.GreaterThan(0), name);
                Assert.That(JToken.DeepEquals(overviewCommand["documentation"], documentation), Is.True, name);
            }

            JObject generate = definitions["tools"].Values<JObject>().Single(c => c.Value<string>("name") == "kimodo_generate_animation");
            Assert.That(generate["inputSchema"]?["properties"]?["prompt"]?.Value<string>("description"), Does.Contain("25 words or fewer"));
        }

        [Test]
        public void PoseTest_HelpSeparatesMeasuredPoseFromGenerationWarnings()
        {
            JObject manual = JObject.Parse(command_dispatcher.Invoke(
                "kimodo_help", new JObject { ["command"] = "pose_test" }.ToString()))["manual"] as JObject;
            Assert.That(manual, Is.Not.Null);
            Assert.That(manual["inputSchema"]?["required"]?.Values<string>(), Does.Contain("pose"));
            Assert.That(manual["inputSchema"]?["properties"]?["targets"]?["items"]?["properties"]?["tolerance_m"]?.Value<double>("default"), Is.EqualTo(0.05));
            Assert.That(string.Join(" ", manual["documentation"]?["result_notes"]?.Values<string>() ?? Enumerable.Empty<string>()), Does.Contain("measured").IgnoreCase);
            Assert.That(string.Join(" ", manual["documentation"]?["limits"]?.Values<string>() ?? Enumerable.Empty<string>()), Does.Contain("Generation-time warnings").IgnoreCase);
        }

        [TestCase("{'pose':{'track':'poses','index':0},'effect':{'right_hand':{'position':[1,2,3]}}}", true)]
        [TestCase("{'effect':{'right_hand':{'position':[1,2,3]}}}", false)]
        [TestCase("{'pose':{'track':'poses','index':0}}", false)]
        [TestCase("{'root2d':{'position':[0,0],'heading':[0,1]},'effect':{'right_hand':{'position':[1,2,3]}}}", false)]
        public void PoseEffectSafetyPreflight_RequiresExplicitPoseAndEffect(string json, bool expected)
        {
            Assert.That(command_context.HasPoseAndEffect(JObject.Parse(json)), Is.EqualTo(expected));
        }
    }
}
