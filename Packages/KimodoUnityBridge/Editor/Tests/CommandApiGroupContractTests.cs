using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KimodoUnityBridge.Command;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace KimodoUnityBridge.Command.Tests
{
    /// <summary>
    /// Keeps the public command surface grouped by the three useful API workflows.
    /// These tests validate handle and schema composition without creating a strong
    /// runtime session dependency between otherwise independent commands.
    /// </summary>
    public sealed class CommandApiGroupContractTests
    {
        [Test]
        [Category("Pose")]
        public void PoseCommands_ShareThePoseReferenceContract()
        {
            JObject document = Definitions();
            JObject poseGet = Tool(document, "pose_get");
            JObject poseSet = Tool(document, "pose_set");
            JObject transformCapture = Tool(document, "transform_capture");
            JObject generation = Tool(document, "kimodo_generate_animation");

            JObject poseReference = (JObject)poseGet["inputSchema"]["properties"]["source"];
            Assert.That(poseReference["properties"], Is.Not.Null);
            Assert.That(poseReference["required"], Is.Not.Null);
            CollectionAssert.Contains(((JArray)poseReference["required"]).Values<string>(), "character");
            CollectionAssert.AreEquivalent(new[] { "scene_pose", "skeleton_rest_pose" },
                ((JArray)poseReference["properties"]?["mode"]?["enum"]).Values<string>());
            Assert.That(poseReference["oneOf"]?.Values<JObject>().Count(), Is.EqualTo(3));
            Assert.That(poseReference["properties"]?["timeline_frame_60"]?.Value<string>("type"), Is.EqualTo("integer"));
            Assert.That(poseReference["properties"]?["timeline_time_seconds"], Is.Null);
            Assert.That(poseReference["properties"]?["clip_time_seconds"], Is.Null);

            AssertPosePropertyUsesTrackIndex(poseSet["inputSchema"]["properties"]["pose"]);
            AssertPosePropertyUsesTrackIndex(transformCapture["inputSchema"]["properties"]["pose"]);

            JObject point = ((JArray)generation["inputSchema"]["properties"]["constraints"]["items"]["oneOf"])
                .Values<JObject>()
                .Single(branch => branch["properties"]?["pose"] != null && branch["properties"]?["root2d"] != null);
            AssertPosePropertyUsesTrackIndex(point["properties"]?["pose"]);
            Assert.That(point["properties"]?["root2d"], Is.Not.Null);
            Assert.That(point["properties"]?["effect"], Is.Not.Null);
            Assert.That(point["properties"]?["type"], Is.Null);
        }

        [Test]
        [Category("Generation")]
        public void GenerationCommands_ShareRequestIdLifecycleContract()
        {
            JObject document = Definitions();
            JObject generate = Tool(document, "kimodo_generate_animation");
            JObject get = Tool(document, "kimodo_get_generation");
            JObject cancel = Tool(document, "kimodo_cancel_generation");
            JObject analyze = Tool(document, "animation_analyze");
            JObject videoAnalysis = Tool(document, "analysis.video");
            JObject pictureAnalysis = Tool(document, "analysis.picture");
            JObject retarget = Tool(document, "kimodo_retarget_animation");

            Assert.That(generate["description"]?.Value<string>(), Does.Contain("request_id"));
            AssertRequiredProperty(get, "request_id");
            AssertRequiredProperty(cancel, "request_id");
            Assert.That(get["inputSchema"]?["properties"]?["include_request_json"]?.Value<string>("type"), Is.EqualTo("boolean"));
            JObject analyzeClips = (JObject)analyze["inputSchema"]?["properties"]?["clips"];
            Assert.That(analyzeClips?.Value<int>("minItems"), Is.EqualTo(1));
            Assert.That(analyzeClips?.Value<int>("maxItems"), Is.EqualTo(1));
            Assert.That(analyzeClips?["items"]?["properties"]?["character"], Is.Not.Null);
            Assert.That(analyzeClips?["items"]?["properties"]?["clip"], Is.Not.Null);
            Assert.That(analyze["description"]?.Value<string>(), Does.Contain("one scene character"));
            Assert.That(analyze["description"]?.Value<string>(), Does.Contain("Timeline-global 60 FPS frames"));
            Assert.That(videoAnalysis["inputSchema"]?["properties"]?["video_path"], Is.Not.Null);
            Assert.That(pictureAnalysis["inputSchema"]?["properties"]?["image_path"], Is.Not.Null);
            Assert.That(videoAnalysis["inputSchema"]?["properties"]?["person"]?["enum"], Is.Not.Null);
            Assert.That(videoAnalysis["outputSchema"]?["properties"]?["request_id"], Is.Not.Null);
            Assert.That(get["outputSchema"]?["properties"]?["poses"]?["items"]?["properties"]?["body_pose_axis_angle"], Is.Not.Null);
            JObject duration = (JObject)generate["inputSchema"]?["properties"]?["duration_frames"];
            Assert.That(duration?.Value<int>("minimum"), Is.EqualTo(1));
            Assert.That(duration?.Value<int>("default"), Is.EqualTo(300));
            Assert.That(retarget["inputSchema"]?["properties"]?["source_character"], Is.Not.Null);
            Assert.That(retarget["inputSchema"]?["properties"]?["animation"], Is.Not.Null);

            // The returned generation handle is the only required composition point;
            // polling and cancellation both consume exactly that handle.
            Assert.That(get["inputSchema"]["properties"]["request_id"].Value<string>("type"), Is.EqualTo("string"));
            Assert.That(cancel["inputSchema"]["properties"]["request_id"].Value<string>("type"), Is.EqualTo("string"));

            JObject pointExample = ((JArray)generate["examples"]).Values<JObject>()
                .Select(example => example["arguments"]?["constraints"] as JArray)
                .Where(items => items != null)
                .SelectMany(items => items.Values<JObject>())
                .First(item => item["root2d"] != null && item["effect"] != null);
            Assert.That(pointExample["type"], Is.Null);
            Assert.That(pointExample["pose"], Is.Not.Null);
        }

        [Test]
        [Category("Generation")]
        public void PointConstraintShape_UsesOptionalDataToSelectChannels()
        {
            AssertPointShapeValid(new JObject { ["frame"] = 0, ["pose"] = new JObject { ["track"] = "P", ["index"] = 0 } });
            AssertPointShapeValid(new JObject { ["frame"] = 0, ["root2d"] = new JObject() });
            AssertPointShapeValid(new JObject { ["frame"] = 0, ["effect"] = new JObject() });
            AssertPointShapeValid(new JObject
            {
                ["frame"] = 0,
                ["pose"] = new JObject { ["track"] = "P", ["index"] = 0 },
                ["root2d"] = new JObject(),
                ["effect"] = new JObject()
            });

            InvalidOperationException empty = Assert.Throws<InvalidOperationException>(
                () => ValidatePointShape(new JObject { ["frame"] = 0 }));
            Assert.That(empty.Message, Does.Contain("pose, root2d, or effect"));
            InvalidOperationException typeTag = Assert.Throws<InvalidOperationException>(
                () => ValidatePointShape(new JObject { ["frame"] = 0, ["type"] = "root2d", ["root2d"] = new JObject() }));
            Assert.That(typeTag.Message, Does.Contain("type is not used"));
        }

        [Test]
        [Category("Other")]
        public void OtherCommands_AreDiscoverableAsIndependentContracts()
        {
            JObject document = Definitions();
            string[] names =
            {
                "kimodo_help",
                "kimodo_install_server",
                "scene.context",
                "session.clear",
                "animation_analyze"
            };

            foreach (string name in names)
            {
                JObject command = Tool(document, name);
                Assert.That(command["inputSchema"], Is.Not.Null, name);
                Assert.That(command["examples"], Is.TypeOf<JArray>(), name);
                foreach (JObject example in ((JArray)command["examples"]).Values<JObject>())
                    Assert.That(example["arguments"], Is.TypeOf<JObject>(), name);
            }

            JObject sceneContext = Tool(document, "scene.context");
            Assert.That(sceneContext["inputSchema"]?["additionalProperties"]?.Value<bool>(), Is.False);
            Assert.That(sceneContext["inputSchema"]?["properties"]?["include_history"]?["type"]?.Value<string>(), Is.EqualTo("boolean"));
            Assert.That(sceneContext["description"]?.Value<string>(), Does.Contain("authoritative source"));
            Assert.That(sceneContext["outputSchema"]?["properties"]?["session"]?["properties"]?["session_id"]?.Value<string>("type"), Is.EqualTo("string"));
            Assert.That(sceneContext["outputSchema"]?["properties"]?["session"]?["properties"]?["session_revision"]?.Value<string>("type"), Is.EqualTo("integer"));
            Assert.That(sceneContext["outputSchema"]?["properties"]?["history"]?["properties"]?["analyses"]?["items"]?["properties"]?["analysis_id"], Is.Not.Null);
            Assert.That(sceneContext["outputSchema"]?["properties"]?["history"]?["properties"]?["animations"]?["items"]?["properties"]?["animation_id"], Is.Not.Null);
            Assert.That(sceneContext["outputSchema"]?["properties"]?["history"]?["properties"]?["generations"]?["items"]?["properties"]?["animation_id"], Is.Not.Null);
            Assert.That(Tool(document, "session.clear")["inputSchema"]["required"], Is.Null.Or.Empty);

            JObject poseGet = Tool(document, "pose_get");
            Assert.That(poseGet["outputSchema"]?["properties"]?["character_root"]?["properties"]?["position"]?["type"]?.Value<string>(), Is.EqualTo("array"));
            Assert.That(poseGet["outputSchema"]?["properties"]?["character_root"]?["properties"]?["rotation"]?["maxItems"]?.Value<int>(), Is.EqualTo(4));
            Assert.That(poseGet["description"]?.Value<string>(), Does.Contain("data.root is the sampled Pose root channel"));
        }

        [Test]
        [Category("Other")]
        public void EveryCommand_PublishesItsTopLevelOutputContract()
        {
            JObject document = Definitions();
            foreach (JObject command in ((JArray)document["tools"]).Values<JObject>())
            {
                JObject output = command["outputSchema"] as JObject;
                Assert.That(output, Is.Not.Null, command.Value<string>("name"));
                Assert.That(output["properties"]?["ok"]?.Value<string>("type"), Is.EqualTo("boolean"));
                Assert.That(output["properties"]?["error"], Is.Not.Null);
                CollectionAssert.Contains(output["required"].Values<string>(), "ok");
            }

            JObject generate = Tool(document, "kimodo_generate_animation");
            Assert.That(generate["outputSchema"]?["properties"]?["request_id"], Is.Not.Null);
            Assert.That(generate["outputSchema"]?["properties"]?["check"], Is.Not.Null);
            Assert.That(generate["outputSchema"]?["properties"]?["warnings"]?["type"]?.Value<string>(), Is.EqualTo("array"));
            Assert.That(generate["outputSchema"]?["properties"]?["model"], Is.Null);
            Assert.That(generate["outputSchema"]?["properties"]?["constraints"], Is.Null);

            JObject install = Tool(document, "kimodo_install_server");
            CollectionAssert.AreEquivalent(
                new[] { "ok", "error", "request_id", "status" },
                ((JObject)install["outputSchema"]["properties"]).Properties().Select(property => property.Name));

            JObject poll = Tool(document, "kimodo_get_generation");
            Assert.That(poll["outputSchema"]?["properties"]?["clip"], Is.Not.Null);
            Assert.That(poll["outputSchema"]?["properties"]?["asset_path"], Is.Null);
            Assert.That(poll["outputSchema"]?["properties"]?["clip"]?["properties"]?["asset_path"]?.Value<string>("type"), Is.EqualTo("string"));
            Assert.That(poll["outputSchema"]?["properties"]?["clip"]?["properties"]?["end_timeline_frame_60_exclusive"]?.Value<string>("type"), Is.EqualTo("integer"));
            Assert.That(poll["outputSchema"]?["properties"]?["clip"]?["properties"]?["duration_frames"], Is.Null);

            JObject cancel = Tool(document, "kimodo_cancel_generation");
            CollectionAssert.AreEquivalent(
                new[] { "ok", "error", "request_id", "status", "stage", "message", "canceled", "clip" },
                ((JObject)cancel["outputSchema"]["properties"]).Properties().Select(property => property.Name));

            JObject analyze = Tool(document, "animation_analyze");
            Assert.That(analyze["outputSchema"]?["properties"]?["picture"], Is.Null);
            Assert.That(analyze["outputSchema"]?["properties"]?["clip"]?["properties"]?["analysis_id"]?.Value<string>("type"), Is.EqualTo("string"));
            Assert.That(analyze["outputSchema"]?["properties"]?["clip"]?["properties"]?["session_id"]?.Value<string>("type"), Is.EqualTo("string"));
            Assert.That(analyze["outputSchema"]?["properties"]?["clip"]?["properties"]?["analysis_schema_version"], Is.Null);
            Assert.That(analyze["outputSchema"]?["properties"]?["pictures"]?["properties"]?["title"], Is.Null);
            Assert.That(analyze["outputSchema"]?["properties"]?["pictures"]?["properties"]?["header"], Is.Null);

            JObject poll2 = Tool(document, "kimodo_get_generation");
            Assert.That(poll2["outputSchema"]?["properties"]?["animation_id"]?.Value<string>("type"), Is.EqualTo("string"));
            Assert.That(analyze["outputSchema"]?["properties"]?["pictures"]?["properties"]?["headers"], Is.Null);

            JObject error = (JObject)poll2["outputSchema"]["properties"]["error"];
            Assert.That(error.Value<string>("type"), Is.EqualTo("object"));
            Assert.That(error["oneOf"], Is.Null);

            Assert.That(Tool(document, "pose_set_root_transform").Value<string>("description"), Does.Contain("Compatibility alias"));
            Assert.That(Tool(document, "pose_set_muscle").Value<string>("description"), Does.Contain("Compatibility alias"));
        }

        [Test]
        [Category("Other")]
        public void PackagedHelpJson_MatchesLiveOutputSchemas()
        {
            const string helpPath = "Packages/com.unity.kimodo_unity_motion_tools/Command/help.json";
            TextAsset help = AssetDatabase.LoadAssetAtPath<TextAsset>(helpPath);
            Assert.That(help, Is.Not.Null, helpPath);
            JObject packaged = JObject.Parse(help.text);
            JObject live = Definitions();

            foreach (JObject command in ((JArray)live["tools"]).Values<JObject>())
            {
                JObject packagedCommand = Tool(packaged, command.Value<string>("name"));
                Assert.That(
                    JToken.DeepEquals(command["outputSchema"], packagedCommand["outputSchema"]),
                    Is.True,
                    command.Value<string>("name"));
            }
        }

        [Test]
        [Category("Generation")]
        public void AsyncResponseNormalization_RemovesEchoAndUsesOneLifecycleShape()
        {
            Type context = typeof(command_dispatcher).Assembly.GetType("KimodoUnityBridge.Command.command_context");
            MethodInfo installNormalizer = context.GetMethod("NormalizeInstallStatus", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo generationNormalizer = context.GetMethod("NormalizeGenerationStatus", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo cancellationNormalizer = context.GetMethod("BuildCancellationStatus", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(installNormalizer, Is.Not.Null);
            Assert.That(generationNormalizer, Is.Not.Null);
            Assert.That(cancellationNormalizer, Is.Not.Null);

            var installSource = new JObject
            {
                ["task_id"] = "install:123",
                ["status"] = "loading",
                ["progress"] = "0/0",
                ["eta_seconds"] = 30
            };
            JObject install = (JObject)installNormalizer.Invoke(null, new object[] { installSource, "install:123" });
            Assert.That(install.Value<string>("request_id"), Is.EqualTo("install:123"));
            Assert.That(install.Value<string>("status"), Is.EqualTo("running"));
            Assert.That(install["task_id"], Is.Null);
            Assert.That(install["progress"], Is.Null);

            var completedSource = new JObject
            {
                ["status"] = "completed",
                ["error"] = string.Empty,
                ["prompt"] = "walk",
                ["seed"] = 123,
                ["path"] = "Assets/Walk.anim",
                ["animation"] = "Walk",
                ["character"] = "Ada",
                ["start_timeline_frame_60"] = 240,
                ["duration_frames"] = 120,
                ["inout_sampling"] = new JObject { ["mode"] = "outside" }
            };
            JObject completed = (JObject)generationNormalizer.Invoke(null, new object[] { completedSource, "request-123", false });
            Assert.That(completed.Value<string>("request_id"), Is.EqualTo("request-123"));
            Assert.That(completed["prompt"], Is.Null);
            Assert.That(completed.Value<int>("seed"), Is.EqualTo(123));
            Assert.That(completed["path"], Is.Null);
            Assert.That(completed["animation"], Is.Null);
            Assert.That(completed["error"], Is.Null);
            Assert.That(completed["clip"]?.Value<string>("name"), Is.EqualTo("Walk"));
            Assert.That(completed["clip"]?.Value<string>("asset_path"), Is.EqualTo("Assets/Walk.anim"));
            Assert.That(completed["clip"]?.Value<int>("end_timeline_frame_60_exclusive"), Is.EqualTo(360));
            Assert.That(completed["clip"]?["duration_frames"], Is.Null);
            Assert.That(completed["inout_sampling"]?["mode"]?.Value<string>(), Is.EqualTo("outside"));

            JObject canceled = (JObject)cancellationNormalizer.Invoke(null, new object[]
            {
                completed,
                "request-123",
                true
            });
            CollectionAssert.AreEquivalent(
                new[] { "request_id", "status", "stage", "message", "canceled", "clip" },
                canceled.Properties().Select(property => property.Name));
            Assert.That(canceled.Value<bool>("canceled"), Is.True);
            Assert.That(canceled["clip"]?["end_timeline_frame_60_exclusive"]?.Value<int>(), Is.EqualTo(360));

            JObject failed = (JObject)generationNormalizer.Invoke(null, new object[]
            {
                new JObject { ["status"] = "failed", ["error"] = "backend unavailable" },
                "request-456",
                false
            });
            Assert.That(failed["error"]?.Value<string>("code"), Is.EqualTo("generation_failed"));
            Assert.That(failed["error"]?.Value<string>("message"), Is.EqualTo("backend unavailable"));
        }

        private static JObject Definitions()
        {
            return JObject.Parse(command_dispatcher.GetCommandDefinitionsJson());
        }

        private static JObject Tool(JObject document, string name)
        {
            JObject tool = ((JArray)document["tools"]).Values<JObject>()
                .SingleOrDefault(item => string.Equals(item.Value<string>("name"), name, StringComparison.Ordinal));
            Assert.That(tool, Is.Not.Null, $"Missing command definition: {name}");
            return tool;
        }

        private static void AssertRequiredProperty(JObject command, string name)
        {
            CollectionAssert.Contains(
                ((JArray)command["inputSchema"]["required"]).Values<string>(), name,
                $"{command.Value<string>("name")} must require {name}");
        }

        private static void AssertPosePropertyUsesTrackIndex(JToken property)
        {
            Assert.That(property, Is.Not.Null);
            Assert.That(property["properties"]?["track"]?["type"]?.Value<string>(), Is.EqualTo("string"));
            Assert.That(property["properties"]?["index"]?["type"]?.Value<string>(), Is.EqualTo("integer"));
            CollectionAssert.Contains(((JArray)property["required"]).Values<string>(), "track");
            CollectionAssert.Contains(((JArray)property["required"]).Values<string>(), "index");
        }

        private static void AssertPointShapeValid(JObject value) => Assert.DoesNotThrow(() => ValidatePointShape(value));

        private static void ValidatePointShape(JObject value)
        {
            Type context = typeof(command_dispatcher).Assembly.GetType("KimodoUnityBridge.Command.command_context");
            MethodInfo validator = context?.GetMethod("ValidatePointConstraintShape", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(validator, Is.Not.Null);
            try
            {
                validator.Invoke(null, new object[] { value, 0 });
            }
            catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException validationError)
            {
                throw validationError;
            }
        }
    }
}
