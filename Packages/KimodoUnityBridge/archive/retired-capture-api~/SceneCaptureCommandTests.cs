using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace KimodoUnityBridge.Command.Tests
{
    public sealed class SceneCaptureCommandTests
    {
        [Test]
        [Category("Capture")]
        public void CaptureTransform_SceneFrameReturnsImageSynchronouslyWithoutRequestId()
        {
            JObject context = JObject.Parse(command_dispatcher.Invoke("scene.context", "{}"));
            JObject character = (context["characters"] as JArray)?.OfType<JObject>().FirstOrDefault(item => item.Value<bool?>("avatar_valid_humanoid") == true);
            Assert.That(character, Is.Not.Null, context.ToString());
            var view = SceneView.lastActiveSceneView ?? ScriptableObject.CreateInstance<SceneView>();
            bool ownsView = view != SceneView.lastActiveSceneView;
            try
            {
                if (ownsView) { view.position = new Rect(0, 0, 900, 700); view.ShowUtility(); view.Focus(); }
                var args = new JObject
                {
                    ["character_path"] = character.Value<string>("name"),
                    ["timeline_frame_60"] = 0,
                    ["transforms"] = new JArray(character.Value<string>("name")),
                    ["camera"] = new JObject { ["mode"] = "scene", ["scope"] = "targets", ["auto_frame"] = true }
                };
                JObject result = JObject.Parse(command_dispatcher.Invoke("capture.transform", args.ToString()));
                Assert.That(result.Value<bool>("ok"), Is.True, result.ToString());
                Assert.That(result.Property("request_id"), Is.Null);
                string path = Path.Combine(Application.dataPath, result.Value<string>("image_path").Substring("Assets/".Length));
                Assert.That(File.Exists(path), Is.True, path);
                var image = new Texture2D(2, 2);
                try
                {
                    Assert.That(image.LoadImage(File.ReadAllBytes(path)), Is.True);
                    Assert.That(image.width, Is.GreaterThan(0));
                    Assert.That(image.GetPixels32().Count(pixel => pixel.r + pixel.g + pixel.b > 60), Is.GreaterThan(100));
                }
                finally { UnityEngine.Object.DestroyImmediate(image); }
            }
            finally { if (ownsView && view != null) view.Close(); }
        }

        [Test]
        [Category("Capture")]
        public void CaptureClip_ArcWalkReturnsTwentyTileImageSynchronouslyWithoutRequestId()
        {
            JObject context = JObject.Parse(command_dispatcher.Invoke("scene.context", "{}"));
            JObject character = (context["characters"] as JArray)?.OfType<JObject>().FirstOrDefault(item => item.Value<bool?>("avatar_valid_humanoid") == true);
            Assert.That(character, Is.Not.Null, context.ToString());
            var args = new JObject
            {
                ["clips"] = new JArray(new JObject
                {
                    ["character"] = character.Value<string>("name"),
                    ["clip"] = "Packages/com.unity.kimodo_unity_motion_tools/Editor/Tests/Fixtures/arc_walk_left_loop.anim"
                }),
                ["resolution"] = 1280
            };
            JObject result = JObject.Parse(command_dispatcher.Invoke("capture.clip", args.ToString()));
            Assert.That(result.Value<bool>("ok"), Is.True, result.ToString());
            Assert.That(result.Property("request_id"), Is.Null);
            Assert.That(result.Value<int>("tile_count"), Is.EqualTo(20));
            Assert.That(result["pictures"]?["images"]?.Count(), Is.EqualTo(20));
            string path = Path.Combine(Application.dataPath, result.Value<string>("image_path").Substring("Assets/".Length));
            Assert.That(File.Exists(path), Is.True, path);
            var image = new Texture2D(2, 2);
            try
            {
                Assert.That(image.LoadImage(File.ReadAllBytes(path)), Is.True);
                Assert.That(image.width, Is.EqualTo(1280));
                Assert.That(image.height, Is.EqualTo(720));
                Assert.That(image.GetPixels32().Count(pixel => pixel.r + pixel.g + pixel.b > 80), Is.GreaterThan(image.width * image.height / 10));
            }
            finally { UnityEngine.Object.DestroyImmediate(image); }
        }
    }
}
