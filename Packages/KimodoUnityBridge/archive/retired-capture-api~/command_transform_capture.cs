using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;

namespace KimodoUnityBridge.Command
{
    internal static partial class command_context
    {
        private sealed class TransformSceneCaptureRequest
        {
            public string File;
            public string Error;
            public JObject PendingResult;
            public JObject Result;
            public Action Cleanup;
        }

        private static readonly Dictionary<string, TransformSceneCaptureRequest> TransformSceneCaptureRequests =
            new Dictionary<string, TransformSceneCaptureRequest>(StringComparer.Ordinal);

        public static string TransformCapture(string argumentsJson) => Execute(argumentsJson, arguments =>
        {
            if (arguments["frames"] != null)
                throw new InvalidOperationException("frames is not a sampling input. Use timeline_frame_60 for one frame, or pose.get for an explicit Pose reference.");
            bool hasTimelineFrame = arguments["timeline_frame_60"] != null;
            if (hasTimelineFrame && arguments["pose"] != null)
                throw new InvalidOperationException("timeline_frame_60 and pose are mutually exclusive.");
            EvaluatedPosePreview preview = null;
            var visibility = new Dictionary<Renderer, bool>();
            List<LightCaptureState> lightStates = null;
            PlayableDirector sampledDirector = null;
            double originalTime = 0.0;
            bool restorePlaying = false;
            bool cleanupDeferred = false;
            Action cleanup = () =>
            {
                if (sampledDirector != null)
                {
                    sampledDirector.time = originalTime;
                    sampledDirector.Evaluate();
                    if (restorePlaying) sampledDirector.Play();
                }
                foreach (var entry in visibility)
                    if (entry.Key != null) entry.Key.forceRenderingOff = entry.Value;
                RestoreLightingAfterCapture(lightStates);
                preview?.Dispose();
            };
            try
            {
                JObject source;
                GameObject character;
                JObject pose = arguments["pose"] as JObject;
                if (hasTimelineFrame)
                {
                    string characterPath = RequiredStringValue(arguments, "character_path");
                    int timelineFrame = RequiredNonNegativeFrame(arguments, "timeline_frame_60");
                    character = FindSceneObjectByPath(characterPath);
                    if (character == null)
                        throw new InvalidOperationException($"Scene character path '{characterPath}' was not found.");
                    Scene activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                    sampledDirector = Resources.FindObjectsOfTypeAll<PlayableDirector>()
                        .Where(item => item != null && item.gameObject != null && item.gameObject.scene == activeScene && !IsSessionObject(item.gameObject))
                        .FirstOrDefault(item => item.enabled)
                        ?? RequireCurrentTimelineSession().Director;
                    if (sampledDirector == null)
                        throw new InvalidOperationException("timeline_frame_60 requires an active Timeline director.");
                    originalTime = sampledDirector.time;
                    restorePlaying = sampledDirector.state == PlayState.Playing;
                    if (restorePlaying) sampledDirector.Pause();
                    sampledDirector.time = timelineFrame / SessionFrameRate;
                    sampledDirector.Evaluate();
                    source = new JObject
                    {
                        ["type"] = "scene",
                        ["character_path"] = characterPath,
                        ["timeline_frame_60"] = timelineFrame
                    };
                }
                else if (pose != null)
                {
                    PoseReference reference = RequirePoseReference(pose);
                    KimodoConstraintMarker marker = RequirePoseMarker(reference, out TimelineCharacterRecord owner);
                    preview = CreatePipelinePosePreview(owner, marker.SampleData.Clone());
                    character = preview.Root;
                    source = new JObject
                    {
                        ["type"] = "external_pose", ["character"] = owner.Name,
                        ["pose"] = PoseReferenceJson(reference.Track, reference.Index),
                        ["clip"] = marker.sourceRole == "pose_get" ? (JToken)marker.sourceClipKey : JValue.CreateNull(),
                        ["frame"] = marker.sourceRole == "pose_get" ? (JToken)marker.frame : JValue.CreateNull(),
                        ["timeline_frame_60"] = hasTimelineFrame
                            ? (JToken)new JValue(arguments.Value<int>("timeline_frame_60"))
                            : JValue.CreateNull()
                    };
                }
                else
                {
                    string characterPath = RequiredStringValue(arguments, "character_path");
                    character = FindSceneObjectByPath(characterPath);
                    if (character == null) throw new InvalidOperationException($"Scene character path '{characterPath}' was not found.");
                    source = new JObject { ["type"] = "scene", ["character_path"] = characterPath };
                }
                Animator captureAnimator = preview?.Animator ?? (hasTimelineFrame ? character.GetComponentInChildren<Animator>(true) : null);
                return RenderTransformCapture(arguments, character, source, captureAnimator, visibility, ref lightStates,
                    cleanup, ref cleanupDeferred);
            }
            finally
            {
                if (!cleanupDeferred) cleanup();
            }
        });

        public static string TransformCaptureGet(string argumentsJson) => Execute(argumentsJson, arguments =>
        {
            string requestId = RequiredStringValue(arguments, "request_id");
            if (!TransformSceneCaptureRequests.TryGetValue(requestId, out TransformSceneCaptureRequest request))
                throw new InvalidOperationException($"Unknown transform capture request '{requestId}'.");
            if (!string.IsNullOrEmpty(request.Error))
                return Ok(new JObject
                {
                    ["request_id"] = requestId,
                    ["status"] = "failed",
                    ["error_message"] = request.Error
                });
            if (request.Result == null)
                return Ok(new JObject { ["request_id"] = requestId, ["status"] = "running" });
            JObject result = (JObject)request.Result.DeepClone();
            result["request_id"] = requestId;
            result["status"] = "completed";
            return Ok(result);
        });

        private static string RenderTransformCapture(JObject arguments, GameObject character, JObject source,
            Animator poseAnimator, Dictionary<Renderer, bool> visibility, ref List<LightCaptureState> lightStates,
            Action cleanup, ref bool cleanupDeferred)
        {
            JArray transformValues = arguments["transforms"] as JArray
                ?? throw new InvalidOperationException("transforms must be an array.");
            if (transformValues.Count < 1)
                throw new InvalidOperationException("transforms must contain at least one path.");
            int size = Mathf.Clamp(arguments.Value<int?>("resolution") ?? 1024, 64, 4096);
            JObject cameraOptions = arguments["camera"] as JObject;
            string cameraMode = cameraOptions?.Value<string>("mode") ?? "auto";
            if (!string.Equals(cameraMode, "auto", StringComparison.Ordinal) &&
                !string.Equals(cameraMode, "scene", StringComparison.Ordinal))
                throw new InvalidOperationException("camera.mode must be 'auto' or 'scene'.");
            string scope = cameraOptions?.Value<string>("scope") ?? "targets";
            if (!string.Equals(scope, "targets", StringComparison.Ordinal) &&
                !string.Equals(scope, "scene", StringComparison.Ordinal))
                throw new InvalidOperationException("camera.scope must be 'targets' or 'scene'.");
            var targets = transformValues.Values<string>().Select(path =>
            {
                bool isPose = path == "@pose" || (path?.StartsWith("@pose/", StringComparison.Ordinal) ?? false);
                if (isPose && poseAnimator == null) throw new InvalidOperationException("@pose requires a pose reference returned by pose.get.");
                Transform transform = isPose
                    ? (path == "@pose" ? character.transform : character.transform.Find(path.Substring(6)))
                    : FindRelativeTransform(character.transform, path) ?? FindSceneTransform(path);
                if (transform == null) throw new InvalidOperationException($"Transform path '{path}' was not found in the requested character or active scene.");
                return new CaptureTarget(path, transform);
            }).ToList();
            var renderers = character.GetComponentsInChildren<Renderer>(true);
            Bounds bounds = default;
            for (int index = 0; index < targets.Count; index++)
            {
                CaptureTarget target = targets[index];
                Renderer[] targetRenderers = target.Transform.IsChildOf(character.transform)
                    ? renderers
                    : target.Transform.root.GetComponentsInChildren<Renderer>(true);
                Bounds targetBounds = CalculateTransformBounds(target.Transform, targetRenderers);
                if (index == 0) bounds = targetBounds;
                else bounds.Encapsulate(targetBounds);
            }

            // In targets scope, render only requested subjects so live characters and
            // session duplicates cannot overlap the pose being inspected. Scene scope
            // deliberately leaves the current scene intact.
            if (!string.Equals(scope, "scene", StringComparison.Ordinal))
            {
                foreach (Renderer renderer in Resources.FindObjectsOfTypeAll<Renderer>())
                {
                    if (renderer == null || !renderer.gameObject.scene.IsValid()) continue;
                    visibility[renderer] = renderer.forceRenderingOff;
                    renderer.forceRenderingOff = !targets.Any(target => IsRelevantRenderer(renderer, target.Transform));
                }
            }

            string folder = Path.Combine(Application.dataPath, "KimodoGeneratedClips", "TransformCaptures");
            Directory.CreateDirectory(folder);
            // Scene poses, lighting and visibility can change between identical requests.
            string fileName = "transform_capture_" + Guid.NewGuid().ToString("N");
            string file = Path.Combine(folder, fileName + ".png");
            var descriptions = new JArray();
            bool sceneCameraMode = string.Equals(cameraMode, "scene", StringComparison.Ordinal);
            int outputWidth = size * (sceneCameraMode ? 1 : 2);
            int outputHeight = size * (sceneCameraMode ? 1 : 2);
            if (sceneCameraMode)
            {
                SceneView sceneView = SceneView.lastActiveSceneView;
                if (sceneView == null || sceneView.camera == null)
                    throw new InvalidOperationException("No active Scene View camera is available.");
                JObject sceneCameraOptions = arguments["camera"] as JObject ?? new JObject();
                if (sceneCameraOptions.Value<bool?>("auto_frame") ?? true)
                {
                    Vector3 direction = ReadSceneVector(sceneCameraOptions["direction"], "camera.direction", new Vector3(1f, .75f, -1f));
                    FrameSceneView(sceneView, bounds, direction,
                        sceneCameraOptions.Value<bool?>("orthographic") ?? true,
                        Mathf.Max(.01f, sceneCameraOptions.Value<float?>("padding") ?? 1.1f));
                }
                lightStates = null;
                JObject result = new JObject
                {
                    ["image_path"] = "Assets/KimodoGeneratedClips/TransformCaptures/" + Path.GetFileName(file),
                    ["width"] = JValue.CreateNull(),
                    ["height"] = JValue.CreateNull(),
                    ["tile_size"] = JValue.CreateNull(),
                    ["tile_border_px"] = JValue.CreateNull(),
                    ["camera_mode"] = cameraMode,
                    ["capture_type"] = "scene_view_target",
                    ["scene_view_camera"] = true,
                    ["scope"] = scope,
                    ["tiles"] = new JArray
                    {
                        new JObject
                        {
                            ["tile"] = "1-1",
                            ["view"] = "scene_view",
                            ["projection"] = sceneView.camera.orthographic ? "orthographic" : "perspective",
                            ["transforms"] = new JArray(targets.Select(target => target.Path)),
                            ["bounds"] = new JObject
                            {
                                ["min"] = new JArray(bounds.min.x, bounds.min.y, bounds.min.z),
                                ["max"] = new JArray(bounds.max.x, bounds.max.y, bounds.max.z)
                            }
                        }
                    },
                    ["source"] = source.DeepClone(),
                    ["targets"] = new JArray(targets.Select(target => new JObject
                    {
                        ["key"] = target.Path,
                        ["world_position"] = CapturePositionJson(target.Transform.position)
                    }))
                };
                if (poseAnimator != null && poseAnimator.isHuman)
                    result["evaluated_joints_world"] = CaptureJointsJson(poseAnimator);
                Texture2D image = null;
                try
                {
                    image = SceneViewCaptureBridge.Render(sceneView, Mathf.Max(64, sceneView.camera.pixelWidth), Mathf.Max(64, sceneView.camera.pixelHeight));
                    File.WriteAllBytes(file, image.EncodeToPNG());
                    result["width"] = image.width;
                    result["height"] = image.height;
                    result["tiles"] = new JArray(new JObject
                    {
                        ["tile"] = "1-1", ["view"] = "scene_view",
                        ["projection"] = sceneView.camera.orthographic ? "orthographic" : "perspective",
                        ["transforms"] = new JArray(targets.Select(target => target.Path)),
                        ["bounds"] = new JObject { ["min"] = CapturePositionJson(bounds.min), ["max"] = CapturePositionJson(bounds.max) }
                    });
                }
                finally { if (image != null) UnityEngine.Object.DestroyImmediate(image); }
                AssetDatabase.Refresh();
                result["image_path"] = "Assets/KimodoGeneratedClips/TransformCaptures/" + Path.GetFileName(file);
                result["ok"] = true;
                return Ok(result);
            }
            else
            {
                lightStates = SuppressLightingForCapture();
                var directions = new[] { Vector3.back, Vector3.right, Vector3.up, new Vector3(1f, .75f, -1f) };
                var views = new[] { "front", "right", "top", "3d" };
                var sheet = new Texture2D(size * 2, size * 2, TextureFormat.RGBA32, false);
                try
                {
                    for (int index = 0; index < views.Length; index++)
                    {
                        Camera camera = CreateTransformCaptureCamera(bounds, directions[index], index != 3);
                        Texture2D image = null;
                        try
                        {
                            image = RenderCamera(camera, size, size, new Color(.12f, .12f, .12f, 1f));
                            sheet.SetPixels(index % 2 * size, (1 - index / 2) * size, size, size, image.GetPixels());
                        }
                        finally
                        {
                            if (image != null) UnityEngine.Object.DestroyImmediate(image);
                            UnityEngine.Object.DestroyImmediate(camera.gameObject);
                        }
                        descriptions.Add(new JObject
                        {
                            ["tile"] = $"{index / 2 + 1}-{index % 2 + 1}",
                            ["view"] = views[index],
                            ["projection"] = index == 3 ? "perspective" : "orthographic",
                            ["transforms"] = new JArray(targets.Select(target => target.Path)),
                            ["bounds"] = new JObject
                            {
                                ["min"] = new JArray(bounds.min.x, bounds.min.y, bounds.min.z),
                                ["max"] = new JArray(bounds.max.x, bounds.max.y, bounds.max.z)
                            }
                        });
                    }
                    DrawTransformCaptureTileBorders(sheet, size, 3, new Color(.72f, .72f, .72f, 1f));
                    sheet.Apply(false, false);
                    File.WriteAllBytes(file, sheet.EncodeToPNG());
                }
                finally { UnityEngine.Object.DestroyImmediate(sheet); }
            }
            AssetDatabase.Refresh();
            JObject response = new JObject
            {
                ["image_path"] = "Assets/KimodoGeneratedClips/TransformCaptures/" + Path.GetFileName(file),
                ["width"] = outputWidth,
                ["height"] = outputHeight,
                ["tile_size"] = sceneCameraMode ? JValue.CreateNull() : new JValue(size),
                ["tile_border_px"] = sceneCameraMode ? JValue.CreateNull() : new JValue(3),
                ["camera_mode"] = cameraMode,
                ["tiles"] = descriptions,
                ["source"] = source,
                ["targets"] = new JArray(targets.Select(target => new JObject
                {
                    ["key"] = target.Path,
                    ["world_position"] = CapturePositionJson(target.Transform.position)
                })),
                ["ok"] = true
            };
            if (poseAnimator != null && poseAnimator.isHuman)
            {
                var joints = new JObject();
                foreach (HumanBodyBones bone in new[] { HumanBodyBones.Hips, HumanBodyBones.RightHand,
                    HumanBodyBones.LeftHand, HumanBodyBones.RightFoot, HumanBodyBones.LeftFoot, HumanBodyBones.RightUpperArm })
                {
                    Transform joint = poseAnimator.GetBoneTransform(bone);
                    if (joint != null) joints[bone.ToString()] = CapturePositionJson(joint.position);
                }
                response["evaluated_joints_world"] = joints;
            }
            File.WriteAllText(Path.Combine(folder, fileName + ".json"), response.ToString(Newtonsoft.Json.Formatting.Indented));
            return Ok(response);
        }

        private static JArray CapturePositionJson(Vector3 position) => new JArray(position.x, position.y, position.z);

        private static JObject CaptureJointsJson(Animator animator)
        {
            var joints = new JObject();
            foreach (HumanBodyBones bone in new[] { HumanBodyBones.Hips, HumanBodyBones.RightHand,
                HumanBodyBones.LeftHand, HumanBodyBones.RightFoot, HumanBodyBones.LeftFoot, HumanBodyBones.RightUpperArm })
            {
                Transform joint = animator.GetBoneTransform(bone);
                if (joint != null) joints[bone.ToString()] = CapturePositionJson(joint.position);
            }
            return joints;
        }

        private static void CaptureTransformSceneView(SceneView sceneView)
        {
            if (sceneView != SceneView.lastActiveSceneView || sceneView?.camera?.targetTexture is not RenderTexture target ||
                !target.IsCreated()) return;
            foreach (KeyValuePair<string, TransformSceneCaptureRequest> entry in TransformSceneCaptureRequests.ToList())
            {
                string requestId = entry.Key;
                TransformSceneCaptureRequest request = entry.Value;
                if (request.Result != null || !string.IsNullOrEmpty(request.Error)) continue;
                RenderTexture previous = RenderTexture.active;
                Texture2D image = null;
                try
                {
                    image = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
                    RenderTexture.active = target;
                    image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
                    image.Apply(false, false);
                    File.WriteAllBytes(request.File, image.EncodeToPNG());
                    request.PendingResult["width"] = target.width;
                    request.PendingResult["height"] = target.height;
                    request.Result = request.PendingResult;
                }
                catch (Exception exception)
                {
                    request.Error = exception.Message;
                }
                finally
                {
                    RenderTexture.active = previous;
                    if (image != null) UnityEngine.Object.DestroyImmediate(image);
                    request.Cleanup?.Invoke();
                }
            }
            if (TransformSceneCaptureRequests.Values.All(request => request.Result != null || !string.IsNullOrEmpty(request.Error)))
                UnsubscribeAfterSceneViewRender(CaptureTransformSceneView);
        }

        private static void SubscribeAfterSceneViewRender(Action<SceneView> callback)
        {
            EventInfo completedEvent = typeof(SceneView).GetEvent("onGUIEnded", BindingFlags.Static | BindingFlags.NonPublic);
            if (completedEvent == null)
                throw new InvalidOperationException("This Unity version does not expose the SceneView render completion event.");
            completedEvent.RemoveEventHandler(null, callback);
            completedEvent.AddEventHandler(null, callback);
        }

        private static void UnsubscribeAfterSceneViewRender(Action<SceneView> callback)
        {
            typeof(SceneView).GetEvent("onGUIEnded", BindingFlags.Static | BindingFlags.NonPublic)?.RemoveEventHandler(null, callback);
        }

        private static Camera CreateTransformCaptureCamera(Bounds bounds, Vector3 direction, bool orthographic)
        {
            Camera camera = CreateAnalysisPictureCamera("Kimodo Transform Capture Camera");
            camera.cullingMask = -1; // Render all layers for transform capture
            camera.orthographic = orthographic;
            camera.nearClipPlane = .01f;
            camera.farClipPlane = 100f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.fieldOfView = 35f;
            Vector3 normalizedDirection = direction.sqrMagnitude > .0001f
                ? direction.normalized
                : new Vector3(1f, .75f, -1f).normalized;
            // Use the same camera-space AABB calculation as analysis pictures.
            // This keeps the perspective tile fitted to the actual projected
            // extents instead of the much looser world-axis bounds.
            CalculateTestViewExtents(
                bounds,
                normalizedDirection,
                out Vector3 viewCenter,
                out float horizontalHalf,
                out float verticalHalf,
                out float depthHalf);
            Vector3 up = Mathf.Abs(Vector3.Dot(normalizedDirection, Vector3.up)) > .95f
                ? Vector3.forward
                : Vector3.up;
            float fitDistance = orthographic
                ? Mathf.Max(.5f, depthHalf * 3.2f)
                : Mathf.Max(.5f, depthHalf +
                    Mathf.Max(horizontalHalf / Mathf.Max(.01f, camera.aspect), verticalHalf) /
                    Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * .5f) * 1.08f);
            camera.transform.position = viewCenter + normalizedDirection * fitDistance;
            camera.transform.LookAt(viewCenter, up);
            if (orthographic)
            {
                camera.orthographicSize = Mathf.Max(
                    .05f,
                    verticalHalf * 1.1f,
                    horizontalHalf * 1.1f / Mathf.Max(.01f, camera.aspect));
            }
            return camera;
        }

        private static void DrawTransformCaptureTileBorders(Texture2D sheet, int tileSize, int thickness, Color color)
        {
            if (sheet == null || tileSize <= 0 || thickness <= 0) return;
            int width = sheet.width;
            int height = sheet.height;
            Color[] pixels = sheet.GetPixels();
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    bool vertical = x < thickness || x >= width - thickness ||
                        (x >= tileSize - thickness / 2 && x < tileSize + (thickness + 1) / 2);
                    bool horizontal = y < thickness || y >= height - thickness ||
                        (y >= tileSize - thickness / 2 && y < tileSize + (thickness + 1) / 2);
                    if (vertical || horizontal) pixels[y * width + x] = color;
                }
            }
            sheet.SetPixels(pixels);
        }

        private static int[] ReadCameraResolution(JObject options, int fallback)
        {
            JArray value = options?["resolution"] as JArray;
            if (value == null || value.Count != 2)
                return new[] { fallback, fallback };
            return new[]
            {
                Mathf.Clamp(value[0].Value<int>(), 64, 4096),
                Mathf.Clamp(value[1].Value<int>(), 64, 4096)
            };
        }

        private static Color ReadCameraBackground(JObject options)
        {
            JArray value = options?["background"] as JArray;
            if (value == null || value.Count < 3)
                return new Color(.12f, .12f, .12f, 1f);
            return new Color(
                Mathf.Clamp01(value[0].Value<float>()),
                Mathf.Clamp01(value[1].Value<float>()),
                Mathf.Clamp01(value[2].Value<float>()),
                value.Count > 3 ? Mathf.Clamp01(value[3].Value<float>()) : 1f);
        }

        private static List<MaterialCaptureState> SuppressEmissionForCapture()
        {
            var states = new List<MaterialCaptureState>();
            var seen = new HashSet<Material>();
            foreach (Renderer renderer in Resources.FindObjectsOfTypeAll<Renderer>())
            {
                if (renderer == null || !renderer.gameObject.scene.IsValid()) continue;
                foreach (Material material in renderer.sharedMaterials.Where(item => item != null).Distinct())
                {
                    if (!seen.Add(material)) continue;
                    bool changed = false;
                    var state = new MaterialCaptureState { Material = material };
                    if (material.HasProperty("_EmissiveColor"))
                    {
                        state.HasEmissiveColor = true;
                        state.EmissiveColor = material.GetColor("_EmissiveColor");
                        material.SetColor("_EmissiveColor", Color.black);
                        changed = true;
                    }
                    if (material.HasProperty("_EmissionColor"))
                    {
                        state.HasEmissionColor = true;
                        state.EmissionColor = material.GetColor("_EmissionColor");
                        material.SetColor("_EmissionColor", Color.black);
                        changed = true;
                    }
                    if (material.HasProperty("_EmissiveIntensity"))
                    {
                        state.HasEmissiveIntensity = true;
                        state.EmissiveIntensity = material.GetFloat("_EmissiveIntensity");
                        material.SetFloat("_EmissiveIntensity", 0f);
                        changed = true;
                    }
                    if (material.HasProperty("_EmissionIntensity"))
                    {
                        state.HasEmissionIntensity = true;
                        state.EmissionIntensity = material.GetFloat("_EmissionIntensity");
                        material.SetFloat("_EmissionIntensity", 0f);
                        changed = true;
                    }
                    if (changed) states.Add(state);
                }
            }
            return states;
        }

        private static void RestoreEmissionAfterCapture(List<MaterialCaptureState> states)
        {
            if (states == null) return;
            foreach (MaterialCaptureState state in states)
            {
                if (state.Material == null) continue;
                if (state.HasEmissiveColor) state.Material.SetColor("_EmissiveColor", state.EmissiveColor);
                if (state.HasEmissionColor) state.Material.SetColor("_EmissionColor", state.EmissionColor);
                if (state.HasEmissiveIntensity) state.Material.SetFloat("_EmissiveIntensity", state.EmissiveIntensity);
                if (state.HasEmissionIntensity) state.Material.SetFloat("_EmissionIntensity", state.EmissionIntensity);
            }
        }

        private static List<LightCaptureState> SuppressLightingForCapture()
        {
            var states = new List<LightCaptureState>();
            foreach (Light light in Resources.FindObjectsOfTypeAll<Light>())
            {
                if (light == null || !light.gameObject.scene.IsValid() || !light.enabled) continue;
                states.Add(new LightCaptureState { Light = light, Intensity = light.intensity });
                // The demo scene's Sun is authored at 130000 lux. With HDRP
                // exposure disabled for evidence, that saturates white surfaces.
                light.intensity = Mathf.Min(light.intensity, 1f);
            }
            return states;
        }

        private static void RestoreLightingAfterCapture(List<LightCaptureState> states)
        {
            if (states == null) return;
            foreach (LightCaptureState state in states)
                if (state.Light != null) state.Light.intensity = state.Intensity;
        }

        private static void ApplySceneCameraOptions(Camera camera, JObject options, Bounds bounds, float aspect)
        {
            if (camera == null || options == null) return;
            if (options["field_of_view"] != null)
                camera.fieldOfView = Mathf.Clamp(options.Value<float>("field_of_view"), 1f, 179f);
            if (options["near_clip"] != null)
                camera.nearClipPlane = Mathf.Max(.001f, options.Value<float>("near_clip"));
            if (options["far_clip"] != null)
                camera.farClipPlane = Mathf.Max(camera.nearClipPlane + .01f, options.Value<float>("far_clip"));
            if (options["orthographic"] != null)
                camera.orthographic = options.Value<bool>("orthographic");
            if (options["orthographic_size"] != null)
                camera.orthographicSize = Mathf.Max(.01f, options.Value<float>("orthographic_size"));
            if (options["position"] is JArray position && position.Count == 3)
                camera.transform.position = new Vector3(position[0].Value<float>(), position[1].Value<float>(), position[2].Value<float>());
            if (options["rotation"] is JArray rotation && rotation.Count == 4)
                camera.transform.rotation = new Quaternion(rotation[0].Value<float>(), rotation[1].Value<float>(), rotation[2].Value<float>(), rotation[3].Value<float>());
            if (options.Value<bool?>("auto_frame") ?? true)
            {
                Vector3 direction = camera.transform.forward.sqrMagnitude > .0001f ? camera.transform.forward.normalized : Vector3.forward;
                CalculateTransformViewExtents(bounds, -direction, out Vector3 center, out float horizontalHalf, out float verticalHalf, out float depthHalf);
                float distance = camera.orthographic
                    ? Mathf.Max(.5f, depthHalf * 3f)
                    : Mathf.Max(.5f, depthHalf + Mathf.Max(horizontalHalf / Mathf.Max(.01f, aspect), verticalHalf) /
                        Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * .5f) * 1.15f);
                camera.transform.position = center - direction * distance;
                camera.transform.LookAt(center, Vector3.up);
                if (camera.orthographic)
                    camera.orthographicSize = Mathf.Max(.05f, verticalHalf * 1.15f, horizontalHalf * 1.15f / Mathf.Max(.01f, aspect));
            }
        }

        private static void CalculateTransformViewExtents(
            Bounds bounds,
            Vector3 direction,
            out Vector3 viewCenter,
            out float horizontalHalf,
            out float verticalHalf,
            out float depthHalf)
        {
            Vector3 up = Mathf.Abs(Vector3.Dot(direction, Vector3.up)) > .95f
                ? Vector3.forward
                : Vector3.up;
            Quaternion inverseView = Quaternion.Inverse(Quaternion.LookRotation(-direction, up));
            Vector3[] corners =
            {
                new Vector3(bounds.min.x, bounds.min.y, bounds.min.z),
                new Vector3(bounds.min.x, bounds.min.y, bounds.max.z),
                new Vector3(bounds.min.x, bounds.max.y, bounds.min.z),
                new Vector3(bounds.min.x, bounds.max.y, bounds.max.z),
                new Vector3(bounds.max.x, bounds.min.y, bounds.min.z),
                new Vector3(bounds.max.x, bounds.min.y, bounds.max.z),
                new Vector3(bounds.max.x, bounds.max.y, bounds.min.z),
                new Vector3(bounds.max.x, bounds.max.y, bounds.max.z)
            };
            float minHorizontal = float.PositiveInfinity;
            float maxHorizontal = float.NegativeInfinity;
            float minVertical = float.PositiveInfinity;
            float maxVertical = float.NegativeInfinity;
            float minDepth = float.PositiveInfinity;
            float maxDepth = float.NegativeInfinity;
            foreach (Vector3 corner in corners)
            {
                Vector3 local = inverseView * corner;
                minHorizontal = Mathf.Min(minHorizontal, local.x);
                maxHorizontal = Mathf.Max(maxHorizontal, local.x);
                minVertical = Mathf.Min(minVertical, local.y);
                maxVertical = Mathf.Max(maxVertical, local.y);
                minDepth = Mathf.Min(minDepth, local.z);
                maxDepth = Mathf.Max(maxDepth, local.z);
            }
            Vector3 localCenter = new Vector3(
                (minHorizontal + maxHorizontal) * .5f,
                (minVertical + maxVertical) * .5f,
                (minDepth + maxDepth) * .5f);
            viewCenter = Quaternion.Inverse(inverseView) * localCenter;
            horizontalHalf = (maxHorizontal - minHorizontal) * .5f;
            verticalHalf = (maxVertical - minVertical) * .5f;
            depthHalf = (maxDepth - minDepth) * .5f;
        }

        private static Bounds CalculateTransformBounds(Transform target, IEnumerable<Renderer> renderers)
        {
            Bounds bounds = new Bounds(target.position, Vector3.zero);
            bool hasBounds = false;
            foreach (Renderer renderer in renderers.Where(item => IsRelevantRenderer(item, target)))
            {
                if (renderer is SkinnedMeshRenderer skinned && skinned.sharedMesh != null)
                {
                    Mesh baked = new Mesh();
                    skinned.BakeMesh(baked);
                    Vector3[] vertices = baked.vertices;
                    BoneWeight[] weights = skinned.sharedMesh.boneWeights;
                    for (int i = 0; i < Mathf.Min(vertices.Length, weights.Length); i++)
                    {
                        BoneWeight weight = weights[i];
                        if ((weight.weight0 > .1f && BoneMatches(skinned, weight.boneIndex0, target)) ||
                            (weight.weight1 > .1f && BoneMatches(skinned, weight.boneIndex1, target)) ||
                            (weight.weight2 > .1f && BoneMatches(skinned, weight.boneIndex2, target)) ||
                            (weight.weight3 > .1f && BoneMatches(skinned, weight.boneIndex3, target)))
                        {
                            Vector3 world = skinned.transform.TransformPoint(vertices[i]);
                            if (!hasBounds) { bounds = new Bounds(world, Vector3.zero); hasBounds = true; } else bounds.Encapsulate(world);
                        }
                    }
                    UnityEngine.Object.DestroyImmediate(baked);
                }
                else if (renderer is MeshRenderer)
                {
                    if (renderer.bounds.size.sqrMagnitude < 0.000001f) continue;
                    if (!hasBounds) { bounds = renderer.bounds; hasBounds = true; } else bounds.Encapsulate(renderer.bounds);
                }
            }
            if (!hasBounds) throw new InvalidOperationException($"No renderable geometry found for '{GetSceneHierarchyPath(target)}'.");
            return bounds;
        }

        private static bool IsRelevantRenderer(Renderer renderer, Transform target)
        {
            if (renderer == null || target == null) return false;
            if (renderer.transform == target || renderer.transform.IsChildOf(target)) return true;
            // A scene may contain a white mannequin or capture duplicate that shares the
            // character's bones. It is not part of the requested character hierarchy.
            // Only use the bone fallback when the requested target is itself a bone/prop.
            bool targetIsCharacterRoot = target.GetComponent<Animator>() != null || target.GetComponentInChildren<Animator>(true) != null;
            return !targetIsCharacterRoot && renderer is SkinnedMeshRenderer skinned &&
                skinned.bones.Any(bone => bone != null && (bone == target || bone.IsChildOf(target)));
        }

        private static bool BoneMatches(SkinnedMeshRenderer renderer, int index, Transform target) =>
            index >= 0 && index < renderer.bones.Length && renderer.bones[index] != null && (renderer.bones[index] == target || renderer.bones[index].IsChildOf(target));

        private static Transform FindRelativeTransform(Transform root, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return root;
            return root.Find(path.Trim()) ?? (root.name == path.Trim() ? root : null);
        }

        private static Transform FindSceneTransform(string path)
        {
            string normalized = (path ?? string.Empty).Trim('/');
            return Resources.FindObjectsOfTypeAll<Transform>().FirstOrDefault(item => item.gameObject.scene == UnityEngine.SceneManagement.SceneManager.GetActiveScene() &&
                string.Equals(GetSceneHierarchyPath(item), normalized, StringComparison.Ordinal));
        }

        private static GameObject FindSceneObjectByPath(string path) =>
            Resources.FindObjectsOfTypeAll<GameObject>().FirstOrDefault(item => item.scene == UnityEngine.SceneManagement.SceneManager.GetActiveScene() &&
                string.Equals(GetSceneHierarchyPath(item.transform), path.Trim('/'), StringComparison.Ordinal));

        private static string GetSceneHierarchyPath(Transform transform) =>
            transform.parent == null ? transform.name : GetSceneHierarchyPath(transform.parent) + "/" + transform.name;

        private readonly struct CaptureTarget { public CaptureTarget(string path, Transform transform) { Path = path; Transform = transform; } public string Path { get; } public Transform Transform { get; } }

        private sealed class MaterialCaptureState
        {
            public Material Material;
            public bool HasEmissiveColor;
            public Color EmissiveColor;
            public bool HasEmissionColor;
            public Color EmissionColor;
            public bool HasEmissiveIntensity;
            public float EmissiveIntensity;
            public bool HasEmissionIntensity;
            public float EmissionIntensity;
        }

        private sealed class LightCaptureState
        {
            public Light Light;
            public float Intensity;
        }
    }
}
