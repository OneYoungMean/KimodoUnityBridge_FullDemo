using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KimodoUnityBridge.Command;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

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
            Assert.That(poseReference["oneOf"]?.Values<JObject>().Count(), Is.EqualTo(5));

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
            JObject retarget = Tool(document, "kimodo_retarget_animation");

            Assert.That(generate["description"]?.Value<string>(), Does.Contain("request_id"));
            AssertRequiredProperty(get, "request_id");
            AssertRequiredProperty(cancel, "request_id");
            Assert.That(get["inputSchema"]?["properties"]?["include_request_json"]?.Value<string>("type"), Is.EqualTo("boolean"));
            Assert.That(analyze["inputSchema"]?["properties"]?["clips"], Is.Not.Null);
            Assert.That(analyze["description"]?.Value<string>(), Does.Contain("start_frame"));
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

            Assert.That(Tool(document, "session.clear")["inputSchema"]["required"], Is.Null.Or.Empty);
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
