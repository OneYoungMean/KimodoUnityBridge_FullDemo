using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace KimodoUnityBridge.Command
{
    internal static partial class command_context
    {
        private sealed class SceneCaptureRequest
        {
            public SceneView View;
            public string File;
            public string Error;
            public int Width;
            public int Height;
            public string DrawMode;
            public bool SceneLighting;
            public bool ShowSkybox;
            public bool ShowGizmos;
            public bool AutoFramed;
            public string Projection;
            public JArray Targets;
            public SceneAnalysisLayout Layout;
            public bool AwaitingSetup;
            public double Deadline;
        }

        private static readonly Dictionary<string, SceneCaptureRequest> SceneCaptureRequests =
            new Dictionary<string, SceneCaptureRequest>(StringComparer.Ordinal);

        public static string SceneCapture(string argumentsJson) => Execute(argumentsJson, arguments =>
        {
            SceneView sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
                throw new InvalidOperationException("No active Scene View is available.");
            bool analysisLayout = arguments["clips"] != null;
            if (analysisLayout && arguments["targets"] != null)
                throw new InvalidOperationException("clips and targets cannot be used together.");
            JObject viewArguments = arguments;
            if (analysisLayout)
            {
                JObject layoutCamera = arguments["camera"] as JObject != null
                    ? (JObject)arguments["camera"].DeepClone()
                    : new JObject();
                if (layoutCamera["scene_lighting"] == null) layoutCamera["scene_lighting"] = false;
                layoutCamera["auto_frame"] = false;
                viewArguments = new JObject { ["camera"] = layoutCamera };
            }
            JObject framing = ConfigureSceneView(sceneView, viewArguments);
            string folder = Path.Combine(Application.dataPath, "KimodoGeneratedClips", "SceneCaptures");
            Directory.CreateDirectory(folder);
            string requestId = Guid.NewGuid().ToString("N");
            string file = Path.Combine(folder, "scene_capture_" + requestId + ".png");
            SceneAnalysisLayout layout = null;
            if (analysisLayout)
            {
                layout = CreateSceneAnalysisLayout(arguments);
            }
            try
            {
                int width = sceneView.camera.pixelWidth;
                int height = sceneView.camera.pixelHeight;
                if (layout != null)
                {
                    for (int index = 0; index < layout.Tiles.Count; index++)
                    {
                        layout.ShowNext(sceneView);
                        Texture2D tile = SceneViewCaptureBridge.Render(sceneView, Mathf.Max(64, width), Mathf.Max(64, height));
                        bool complete;
                        try { complete = layout.AddTile(tile); }
                        finally { UnityEngine.Object.DestroyImmediate(tile); }
                        if (complete) break;
                    }
                    File.WriteAllBytes(file, layout.Canvas.EncodeToPNG());
                    layout.Metadata["image_path"] = "Assets/KimodoGeneratedClips/SceneCaptures/" + Path.GetFileName(file);
                    AssetDatabase.Refresh();
                    return Ok(new JObject
                    {
                        ["image_path"] = layout.Metadata.Value<string>("image_path"),
                        ["width"] = layout.Canvas.width, ["height"] = layout.Canvas.height,
                        ["capture_type"] = "scene_view_internal", ["auto_framed"] = true,
                        ["projection"] = "orthographic", ["pictures"] = layout.Metadata.DeepClone(),
                        ["clip"] = layout.Clip.DeepClone(), ["tile_count"] = layout.Tiles.Count, ["ok"] = true
                    });
                }
                Texture2D image = SceneViewCaptureBridge.Render(sceneView, Mathf.Max(64, width), Mathf.Max(64, height));
                try { File.WriteAllBytes(file, image.EncodeToPNG()); }
                finally { UnityEngine.Object.DestroyImmediate(image); }
                AssetDatabase.Refresh();
                return Ok(new JObject
                {
                    ["image_path"] = "Assets/KimodoGeneratedClips/SceneCaptures/" + Path.GetFileName(file),
                    ["width"] = width, ["height"] = height,
                    ["capture_type"] = "scene_view_internal", ["auto_framed"] = framing.Value<bool>("auto_framed"),
                    ["projection"] = framing.Value<string>("projection"), ["targets"] = framing["targets"], ["ok"] = true
                });
            }
            finally { layout?.Dispose(); }
        });

        // Shared by capture.clip and animation.analyze. The analysis command
        // supplies an already analyzed subject so this path does not run a
        // second analysis or install the legacy off-screen camera renderer.
        private static JObject RenderSceneAnalysisCapture(
            TimelineSessionRecord session,
            AnalysisSubject subject,
            int requestedResolution,
            bool includeScene)
        {
            SceneView sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
                throw new InvalidOperationException("No active Scene View is available.");

            int outputWidth = Mathf.Max(64, requestedResolution);
            // Match capture.clip defaults so analyze does not inherit stale
            // grid, gizmo, skybox, or SceneView-lighting state from the editor.
            ConfigureSceneView(sceneView, new JObject
            {
                ["camera"] = new JObject
                {
                    ["scene_lighting"] = false,
                    ["show_skybox"] = false,
                    ["show_gizmos"] = false,
                    ["show_grid"] = false
                }
            });
            SceneAnalysisLayout layout = CreateSceneAnalysisLayout(session, subject, outputWidth, includeScene);
            string folder = Path.Combine(Application.dataPath, "KimodoGeneratedClips", "SceneCaptures");
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, "scene_capture_" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                int renderWidth = Mathf.Max(64, sceneView.camera.pixelWidth);
                int renderHeight = Mathf.Max(64, sceneView.camera.pixelHeight);
                for (int index = 0; index < layout.Tiles.Count; index++)
                {
                    layout.ShowNext(sceneView);
                    Texture2D tile = SceneViewCaptureBridge.Render(sceneView, renderWidth, renderHeight);
                    bool complete;
                    try { complete = layout.AddTile(tile); }
                    finally { UnityEngine.Object.DestroyImmediate(tile); }
                    if (complete) break;
                }

                File.WriteAllBytes(file, layout.Canvas.EncodeToPNG());
                AssetDatabase.Refresh();
                layout.Metadata["image_path"] = "Assets/KimodoGeneratedClips/SceneCaptures/" + Path.GetFileName(file);
                return new JObject
                {
                    ["image_path"] = layout.Metadata.Value<string>("image_path"),
                    ["width"] = layout.Canvas.width,
                    ["height"] = layout.Canvas.height,
                    ["capture_type"] = "scene_view_internal",
                    ["auto_framed"] = true,
                    ["projection"] = "orthographic",
                    ["pictures"] = layout.Metadata.DeepClone(),
                    ["clip"] = layout.Clip.DeepClone(),
                    ["tile_count"] = layout.Tiles.Count,
                    ["ok"] = true
                };
            }
            finally { layout.Dispose(); }
        }

        public static string SceneCaptureGet(string argumentsJson) => Execute(argumentsJson, arguments =>
        {
            string requestId = RequiredStringValue(arguments, "request_id");
            if (!SceneCaptureRequests.TryGetValue(requestId, out SceneCaptureRequest request))
                throw new InvalidOperationException($"Unknown scene capture request '{requestId}'.");
            CheckSceneCaptureRequests();
            if (!string.IsNullOrEmpty(request.Error))
                return Ok(new JObject { ["request_id"] = requestId, ["status"] = "failed", ["error_message"] = request.Error, ["ok"] = true });
            if (string.IsNullOrEmpty(request.File) || !File.Exists(request.File))
                return Ok(new JObject { ["request_id"] = requestId, ["status"] = "running", ["completed_tiles"] = request.Layout?.Index ?? 0, ["tile_count"] = request.Layout?.Tiles.Count ?? 1, ["ok"] = true });
            AssetDatabase.Refresh();
            var result = new JObject
            {
                ["request_id"] = requestId,
                ["status"] = "completed",
                ["image_path"] = "Assets/KimodoGeneratedClips/SceneCaptures/" + Path.GetFileName(request.File),
                ["width"] = request.Width,
                ["height"] = request.Height,
                ["capture_type"] = "scene_view_target",
                ["draw_mode"] = request.DrawMode,
                ["scene_lighting"] = request.SceneLighting,
                ["show_skybox"] = request.ShowSkybox,
                ["show_gizmos"] = request.ShowGizmos,
                ["auto_framed"] = request.AutoFramed,
                ["projection"] = request.Projection,
                ["targets"] = request.Targets?.DeepClone() ?? new JArray(),
                ["tile_count"] = request.Layout?.Tiles.Count ?? 1,
                ["ok"] = true
            };
            if (request.Layout != null)
            {
                result["pictures"] = request.Layout.Metadata.DeepClone();
                result["clip"] = request.Layout.Clip.DeepClone();
            }
            return Ok(result);
        });

        private static JObject ConfigureSceneView(SceneView sceneView, JObject arguments)
        {
            JObject cameraOptions = arguments["camera"] as JObject ?? new JObject();
            JArray targetValues = arguments["targets"] as JArray;
            bool autoFrame = cameraOptions.Value<bool?>("auto_frame") ?? false;
            var targetPaths = targetValues?.Values<string>()
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.Trim('/'))
                .Distinct(StringComparer.Ordinal)
                .ToList() ?? new List<string>();

            if (autoFrame)
            {
                if (targetPaths.Count == 0)
                    throw new InvalidOperationException("camera.auto_frame requires at least one target hierarchy path.");

                Bounds bounds = default;
                bool hasBounds = false;
                foreach (string path in targetPaths)
                {
                    Transform target = FindSceneTransform(path);
                    if (target == null)
                        throw new InvalidOperationException($"Scene target path '{path}' was not found.");
                    Bounds targetBounds = CalculateSceneTargetBounds(target);
                    if (!hasBounds) { bounds = targetBounds; hasBounds = true; }
                    else bounds.Encapsulate(targetBounds);
                }

                Vector3 direction = ReadSceneVector(
                    cameraOptions["direction"],
                    "camera.direction",
                    new Vector3(1f, .75f, -1f));
                if (direction.sqrMagnitude < .0001f)
                    throw new InvalidOperationException("camera.direction must not be zero.");
                bool orthographic = cameraOptions.Value<bool?>("orthographic") ?? true;
                float padding = Mathf.Max(.01f, cameraOptions.Value<float?>("padding") ?? 1.1f);
                FrameSceneView(sceneView, bounds, direction, orthographic, padding);
            }

            if (cameraOptions["near_clip"] != null || cameraOptions["far_clip"] != null)
            {
                var settings = sceneView.cameraSettings;
                settings.nearClip = cameraOptions.Value<float?>("near_clip") ?? settings.nearClip;
                settings.farClip = cameraOptions.Value<float?>("far_clip") ?? settings.farClip;
                if (settings.farClip <= settings.nearClip)
                    throw new InvalidOperationException("camera.far_clip must be greater than camera.near_clip.");
                settings.dynamicClip = false;
                sceneView.cameraSettings = settings;
            }

            sceneView.drawGizmos = cameraOptions.Value<bool?>("show_gizmos") ?? false;
            sceneView.showGrid = cameraOptions.Value<bool?>("show_grid") ?? false;
            sceneView.sceneLighting = cameraOptions.Value<bool?>("scene_lighting") ?? true;
            sceneView.sceneViewState.showSkybox = cameraOptions.Value<bool?>("show_skybox") ?? false;
            return new JObject
            {
                ["auto_framed"] = autoFrame,
                ["projection"] = sceneView.orthographic ? "orthographic" : "perspective",
                ["targets"] = new JArray(targetPaths)
            };
        }

        private static void FrameSceneView(SceneView view, Bounds bounds, Vector3 direction, bool orthographic, float padding, float outputAspect = 0)
        {
            direction.Normalize();
            CalculateTestViewExtents(bounds, direction, out Vector3 center, out float horizontal, out float vertical, out float depth);
            float aspect = view.camera != null ? Mathf.Max(.01f, view.camera.aspect) : 1f;
            if (outputAspect <= 0) outputAspect = aspect;
            // Frame the central crop without stretching the SceneView render into the panel.
            float halfHeight = Mathf.Max(.05f, vertical * padding, horizontal * padding / outputAspect) * Mathf.Max(1f, outputAspect / aspect);
            float halfFov = view.cameraSettings.fieldOfView * Mathf.Deg2Rad * .5f;
            float size = orthographic ? halfHeight * Mathf.Min(1f, aspect)
                : (depth + halfHeight * Mathf.Min(1f, aspect) / Mathf.Tan(halfFov)) * Mathf.Sin(halfFov);
            Vector3 up = Mathf.Abs(Vector3.Dot(direction, Vector3.up)) > .95f ? Vector3.forward : Vector3.up;
            Quaternion rotation = Quaternion.LookRotation(-direction, up);
            view.orthographic = orthographic;
            view.LookAtDirect(center, rotation, size);
            view.size = size;

            // SceneView normally copies pivot/rotation/size into its camera during
            // the next GUI repaint. The synchronous capture path renders the same
            // camera before that repaint, so apply the final state directly to the
            // real SceneView camera while preserving the SceneView itself.
            Camera camera = view.camera;
            if (camera != null)
            {
                camera.orthographic = orthographic;
                if (orthographic) camera.orthographicSize = size;
                float distance = Mathf.Max(10f, view.cameraSettings.nearClip + size + 1f);
                camera.transform.SetPositionAndRotation(center - rotation * Vector3.forward * distance, rotation);
                camera.ResetProjectionMatrix();
            }
        }

        private static void CheckSceneCaptureRequests()
        {
            foreach (SceneCaptureRequest request in SceneCaptureRequests.Values)
            {
                if (request.Width > 0 || !string.IsNullOrEmpty(request.Error)) continue;
                if (request.View == null) request.Error = "The requested Scene View was closed before rendering.";
                else if (request.AwaitingSetup)
                {
                    try
                    {
                        request.Layout.ShowNext(request.View);
                        request.Deadline = EditorApplication.timeSinceStartup + 60;
                        request.AwaitingSetup = false;
                        request.View.Repaint();
                    }
                    catch (Exception exception) { request.Error = exception.Message; request.Layout?.Dispose(); }
                }
                else if (EditorApplication.timeSinceStartup > request.Deadline) request.Error = "Timed out waiting for a Scene View repaint.";
                if (!string.IsNullOrEmpty(request.Error)) request.Layout?.Dispose();
            }
            if (SceneCaptureRequests.Values.All(request => request.Width > 0 || !string.IsNullOrEmpty(request.Error)))
            {
                RenderPipelineManager.endContextRendering -= CaptureContext;
                Camera.onPostRender -= CaptureCamera;
                EditorApplication.update -= CheckSceneCaptureRequests;
            }
        }

        private static void StopSceneCaptures()
        {
            foreach (SceneCaptureRequest request in SceneCaptureRequests.Values) request.Layout?.Dispose();
        }

        private static Vector3 ReadSceneVector(JToken token, string name, Vector3 fallback)
        {
            if (token == null) return fallback;
            if (!(token is JArray values) || values.Count != 3)
                throw new InvalidOperationException($"{name} must be an array of three numbers.");
            return new Vector3(values[0].Value<float>(), values[1].Value<float>(), values[2].Value<float>());
        }

        private static Bounds CalculateSceneTargetBounds(Transform target)
        {
            bool hasBounds = false;
            Bounds bounds = new Bounds(target.position, Vector3.zero);
            foreach (Renderer renderer in target.GetComponentsInChildren<Renderer>(true))
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy || renderer.forceRenderingOff || renderer.bounds.size.sqrMagnitude < .000001f) continue;
                if (!hasBounds) { bounds = renderer.bounds; hasBounds = true; }
                else bounds.Encapsulate(renderer.bounds);
            }
            if (!hasBounds)
                throw new InvalidOperationException($"No renderable geometry found for '{GetSceneHierarchyPath(target)}'.");
            return bounds;
        }

        private static void CaptureContext(ScriptableRenderContext context, List<Camera> cameras)
        {
            foreach (Camera camera in cameras) CaptureCamera(camera);
        }

        private static void CaptureCamera(Camera camera)
        {
            SceneView sceneView = SceneCaptureRequests.Values.FirstOrDefault(request => request.Width == 0 && string.IsNullOrEmpty(request.Error) && request.View != null && request.View.camera == camera)?.View;
            if (sceneView == null) return;
            if (camera.targetTexture is not RenderTexture target || !target.IsCreated()) return;
            foreach (SceneCaptureRequest request in SceneCaptureRequests.Values)
            {
                if (request.View != sceneView || !string.IsNullOrEmpty(request.Error) || request.Width > 0 || request.AwaitingSetup) continue;
                RenderTexture previous = RenderTexture.active;
                Texture2D image = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
                try
                {
                    RenderTexture.active = target;
                    image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
                    image.Apply(false, false);
                    bool completed = request.Layout == null || request.Layout.AddTile(image);
                    if (completed)
                    {
                        Texture2D output = request.Layout?.Canvas ?? image;
                        File.WriteAllBytes(request.File, output.EncodeToPNG());
                        if (request.Layout != null) request.Layout.Metadata["image_path"] = "Assets/KimodoGeneratedClips/SceneCaptures/" + Path.GetFileName(request.File);
                        request.Width = output.width;
                        request.Height = output.height;
                    }
                    else
                    {
                        request.AwaitingSetup = true;
                    }
                    request.DrawMode = sceneView.cameraMode.drawMode.ToString();
                    request.SceneLighting = sceneView.sceneLighting;
                    request.ShowSkybox = sceneView.sceneViewState.showSkybox;
                    request.ShowGizmos = sceneView.drawGizmos;
                }
                catch (Exception exception) { request.Error = exception.Message; }
                finally
                {
                    RenderTexture.active = previous;
                    UnityEngine.Object.DestroyImmediate(image);
                    if (request.Width > 0 || !string.IsNullOrEmpty(request.Error)) request.Layout?.Dispose();
                }
            }
            if (SceneCaptureRequests.Values.All(request => request.Width > 0 || !string.IsNullOrEmpty(request.Error)))
                CheckSceneCaptureRequests();
        }
    }
}
