using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using KimodoBridge;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using UnityEngine.TestTools;

namespace KimodoUnityBridge.Command.Tests
{
    public sealed class AnimationAnalyzeCommandIntegrationTests
    {
        private const string PackageRoot = "Packages/com.unity.kimodo_unity_motion_tools";
        private const string ModelPath = PackageRoot + "/Editor/Model/Armature.fbx";
        private const string ArcWalkPath = PackageRoot + "/Editor/Tests/Fixtures/arc_walk_left_loop.anim";
        private const int TestAnalysisResolution = 1920;

        [Test]
        public void AnalysisKeyframes_UseOnlyAbsoluteTimelineFrameAndNormalizeIdempotently()
        {
            Type contextType = typeof(command_dispatcher).Assembly
                .GetType("KimodoUnityBridge.Command.command_context");
            MethodInfo normalize = contextType.GetMethod(
                "NormalizeAnalysisContract",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(normalize, Is.Not.Null);

            var analysis = JObject.Parse("{ 'keyframes': [{ 'frame': 6, 'time_seconds': 2.1, 'local_time_seconds': 0.1 }] }");
            normalize.Invoke(null, new object[] { analysis, 120, 240 });
            normalize.Invoke(null, new object[] { analysis, 120, 240 });

            JObject keyframe = analysis["keyframes"]?.Children<JObject>().Single();
            Assert.That(keyframe?.Value<int>("timeline_frame_60"), Is.EqualTo(126));
            Assert.That(keyframe?["time_seconds"], Is.Null);
            Assert.That(keyframe?["local_time_seconds"], Is.Null);
            Assert.That(keyframe?["local_frame_60"], Is.Null);
            Assert.That(keyframe?["frame"], Is.Null);
        }

        [UnityTest]
        public IEnumerator AnimationAnalyze_ArcWalkFixture_WritesCompositePng()
        {
            GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(ArcWalkPath);
            Assert.That(source, Is.Not.Null, "Missing Armature fixture.");
            Assert.That(clip, Is.Not.Null, "Missing arc walk left loop fixture.");

            GameObject character = UnityEngine.Object.Instantiate(source);
            character.name = "KimodoTest_Armature";
            object testSession = null;
            try
            {
                Avatar modelAvatar = AssetDatabase.LoadAllAssetsAtPath(ModelPath)
                    .OfType<Avatar>()
                    .FirstOrDefault();
                Assert.That(modelAvatar, Is.Not.Null.And.Property("isHuman").True,
                    "Armature fixture requires a Humanoid Avatar.");
                Animator animator = character.GetComponentInChildren<Animator>(true) ?? character.AddComponent<Animator>();
                animator.avatar = modelAvatar;
                animator.runtimeAnimatorController = null;
                animator.Rebind();

                testSession = CreateCurrentTestContext(character, animator);
                JObject analysis = Require("animation_analyze", new JObject
                {
                    ["clips"] = new JArray(new JObject
                    {
                        ["character"] = character.name,
                        ["clip"] = ArcWalkPath
                    }),
                    ["picture"] = new JObject { ["output"] = "composite" },
                    ["resolution"] = TestAnalysisResolution
                });

                Assert.That(analysis.Value<string>("analysis_schema_version"), Is.EqualTo("5-command-60-single-clip-v1"));
                Assert.That(analysis["picture"], Is.Null);
                Assert.That(analysis["clips"], Is.Null);
                Assert.That(analysis["pictures"]?.Value<string>("aspect"), Is.EqualTo("16:9"));
                Assert.That(analysis["pictures"]?.Value<int>("tile_count"), Is.EqualTo(20));
                Assert.That(analysis["pictures"]?.Value<string>("render_version"), Is.EqualTo("63-height-time-ratio5-dim-hdrp-lights"));
                Assert.That(analysis["pictures"]?["title"], Is.Null);
                Assert.That(analysis["pictures"]?["header"], Is.Null);
                Assert.That(analysis["pictures"]?["headers"], Is.Null);

                JObject compactContext = Require("scene.context", new JObject());
                Assert.That(compactContext["history"], Is.Null, "History should be omitted from the default scene context response.");
                JObject sceneContext = Require("scene.context", new JObject { ["include_history"] = true });
                Assert.That(sceneContext["session"]?.Value<string>("session_id"), Is.Not.Empty);
                Assert.That(sceneContext["session"]?.Value<int>("session_revision"), Is.GreaterThan(0));
                JObject indexedAnalysis = (sceneContext["history"]?["analyses"] as JArray)?.Values<JObject>()
                    .SingleOrDefault(item => item.Value<string>("analysis_id") == analysis["clip"]?.Value<string>("analysis_id"));
                Assert.That(indexedAnalysis, Is.Not.Null);
                Assert.That(indexedAnalysis.Value<string>("animation_id"), Is.Not.Empty);
                CollectionAssert.Contains(indexedAnalysis["picture_paths"].Values<string>(), analysis["pictures"]?.Value<string>("image_path"));
                Assert.That((sceneContext["history"]?["animations"] as JArray)?.Values<JObject>()
                    .Any(item => item.Value<string>("animation_id") == indexedAnalysis.Value<string>("animation_id")), Is.True);

                JObject clipAnalysis = analysis["clip"] as JObject;
                Assert.That(clipAnalysis?.Value<int>("start_timeline_frame_60"), Is.EqualTo(0));
                Assert.That(clipAnalysis?.Value<int>("end_timeline_frame_60_exclusive"), Is.GreaterThan(1));
                Assert.That(clipAnalysis?["analysis_schema_version"], Is.Null);
                Assert.That(clipAnalysis?.Value<string>("phase_track_version"), Is.EqualTo("1-temporal-cluster-v2-command-60"));
                JArray phases = clipAnalysis?["phase_track"] as JArray;
                Assert.That(phases, Is.Not.Null.And.Not.Empty);
                JArray keyframes = clipAnalysis["keyframes"] as JArray;
                Assert.That(keyframes, Is.Not.Null.And.Not.Empty);
                foreach (JObject keyframe in keyframes.OfType<JObject>())
                {
                    Assert.That(keyframe["timeline_frame_60"], Is.Not.Null);
                    Assert.That(keyframe["time_seconds"], Is.Null);
                    Assert.That(keyframe["local_time_seconds"], Is.Null);
                    Assert.That(keyframe["local_frame_60"], Is.Null);
                    Assert.That(keyframe["frame"], Is.Null);
                }
                Assert.That(phases.First().Value<int>("start_timeline_frame_60"), Is.EqualTo(0));
                Assert.That(phases.First()["start_frame"], Is.Null);
                Assert.That(phases.First()["duration_seconds"], Is.Null);
                int analyzedFrameCount = clipAnalysis["root_trajectory"]?.Value<int>("frame_count") ?? 0;
                Assert.That(analyzedFrameCount, Is.GreaterThan(1));
                Assert.That(phases.Last().Value<int>("end_timeline_frame_60_exclusive"), Is.EqualTo(analyzedFrameCount));
                Assert.That(clipAnalysis["foot_contacts"]?.Count(), Is.GreaterThan(0));
                Assert.That(clipAnalysis["foot_contacts"]?.FirstOrDefault()?["timeline_frame_60"], Is.Not.Null);
                Assert.That(clipAnalysis["foot_contacts"]?.FirstOrDefault()?["frame"], Is.Null);
                Assert.That(clipAnalysis["root_trajectory"]?["duration_seconds"], Is.Null);
                Assert.That(clipAnalysis["root_trajectory"]?["samples"]?.FirstOrDefault()?["timeline_frame_60"], Is.Not.Null);
                Assert.That(clipAnalysis["root_trajectory"]?["samples"]?.FirstOrDefault()?["frame"], Is.Null);
                Assert.That(clipAnalysis["motion_profile"]?.Value<float>("path_length_xz"), Is.GreaterThan(0.5f));
                Assert.That(Math.Abs(clipAnalysis["motion_profile"]?.Value<float>("heading_change_degrees") ?? 0f),
                    Is.GreaterThan(45f));

                yield return null;

                string relativePng = analysis["pictures"]?.Value<string>("image_path");
                string absolutePng = string.IsNullOrWhiteSpace(relativePng)
                    ? string.Empty
                    : Path.GetFullPath(Path.Combine(Application.dataPath, "..", relativePng));
                Assert.That(relativePng, Is.Not.Null.And.EndsWith(".png"));
                Assert.That(relativePng.Replace('\\', '/'), Does.StartWith("Library/KimodoData/"));
                Assert.That(File.Exists(absolutePng), Is.True, "animation_analyze did not write its composite PNG.");
                Assert.That(new FileInfo(absolutePng).Length, Is.GreaterThan(0));
            }
            finally
            {
                ResetCurrentTestContext(testSession);
                UnityEngine.Object.DestroyImmediate(character);
            }
        }

        [Test]
        public void RootPathSamples_UseOnlyThePathEndpoints()
        {
            Type contextType = typeof(command_dispatcher).Assembly
                .GetType("KimodoUnityBridge.Command.command_context");
            MethodInfo buildPath = contextType.GetMethod(
                "BuildRootPathConstraints",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(buildPath, Is.Not.Null);

            var path = new KimodoRootPathData
            {
                length = 2f,
                knots = new List<KimodoRootPathKnot>
                {
                    new KimodoRootPathKnot { position = Vector2.zero },
                    new KimodoRootPathKnot { position = Vector2.up },
                    new KimodoRootPathKnot { position = Vector2.one }
                }
            };
            var samples = ((IEnumerable<KimodoMarkerSampleResult>)buildPath.Invoke(null, new object[]
            {
                path, 0, 12, 73, 0f, 1f,
                new HashSet<int>(), new HashSet<int>()
            })).ToArray();

            Assert.That(samples.Select(sample => sample.sampleTime * 60d).ToArray(),
                Is.EqualTo(new[] { 12d, 72d }));
        }

        private static object CreateCurrentTestContext(GameObject character, Animator animator)
        {
            Type contextType = typeof(command_dispatcher).Assembly
                .GetType("KimodoUnityBridge.Command.command_context");
            Type sessionType = contextType.GetNestedType("TimelineSessionRecord", BindingFlags.NonPublic);
            Type metadataType = typeof(command_dispatcher).Assembly
                .GetType("KimodoUnityBridge.Command.KimodoCommandSessionMetadata");
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            timeline.editorSettings.frameRate = 60.0;
            var metadata = ScriptableObject.CreateInstance(metadataType);
            string sessionName = "AnimationAnalyze_ArcWalkFixture_CurrentCommand";
            metadataType.GetField("schemaVersion").SetValue(metadata, "vNext.transition_clip.1");
            metadataType.GetField("sessionId").SetValue(metadata, Guid.NewGuid().ToString("D"));
            metadataType.GetField("sessionName").SetValue(metadata, sessionName);
            metadataType.GetField("isAutomatic").SetValue(metadata, true);
            GameObject sessionRoot = new GameObject("KimodoSession_ArcWalkFixture_CurrentCommand");
            sessionRoot.hideFlags = HideFlags.DontSaveInBuild | HideFlags.DontSaveInEditor;
            GameObject directorObject = new GameObject("Kimodo_CommandSession_ArcWalkFixture_CurrentCommand");
            directorObject.hideFlags = HideFlags.DontSaveInBuild | HideFlags.DontSaveInEditor;
            directorObject.transform.SetParent(sessionRoot.transform, false);
            PlayableDirector director = directorObject.AddComponent<PlayableDirector>();
            director.playableAsset = timeline;

            ConstructorInfo sessionConstructor = sessionType.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(Guid), typeof(string), typeof(PlayableDirector), typeof(TimelineAsset), typeof(string), typeof(bool), metadataType, typeof(GameObject) },
                null);
            object session = sessionConstructor.Invoke(new object[]
            {
                Guid.NewGuid(), sessionName, director, timeline, string.Empty, true, metadata, sessionRoot
            });

            MethodInfo addCharacter = contextType.GetMethod("AddCharacterTrack", BindingFlags.Static | BindingFlags.NonPublic);
            object[] addArguments = { session, character, animator, true, null, true };
            Assert.That((bool)addCharacter.Invoke(null, addArguments), Is.True,
                "AddCharacterTrack failed: " + addArguments[4]);
            FieldInfo currentSession = contextType.GetField("currentTimelineSession", BindingFlags.Static | BindingFlags.NonPublic);
            currentSession.SetValue(null, session);
            MethodInfo activate = contextType.GetMethod("ActivateTimelineSession", BindingFlags.Static | BindingFlags.NonPublic);
            activate.Invoke(null, new[] { session });
            return session;
        }

        private static void ResetCurrentTestContext(object session)
        {
            Type contextType = typeof(command_dispatcher).Assembly
                .GetType("KimodoUnityBridge.Command.command_context");
            FieldInfo currentSession = contextType.GetField("currentTimelineSession", BindingFlags.Static | BindingFlags.NonPublic);
            currentSession.SetValue(null, null);
            if (session == null) return;
            Type sessionType = session.GetType();
            GameObject root = sessionType.GetProperty("SessionRoot")?.GetValue(session) as GameObject;
            TimelineAsset timeline = sessionType.GetProperty("TimelineAsset")?.GetValue(session) as TimelineAsset;
            if (root != null) UnityEngine.Object.DestroyImmediate(root);
            if (timeline != null) UnityEngine.Object.DestroyImmediate(timeline);
        }

        private static JObject Require(string command, JObject arguments)
        {
            JObject response = JObject.Parse(command_dispatcher.Invoke(command, arguments.ToString()));
            Assert.That(response.Value<bool?>("ok"), Is.True, command + " failed: " + response["error"]);
            return response;
        }
    }
}
