using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace KimodoUnityBridge.Command
{
    internal static partial class command_context
    {
        private static JObject RenderSceneAnalysisCapture(
            TimelineSessionRecord session,
            AnalysisSubject subject,
            int requestedResolution,
            JObject cameraOptions)
        {
            SceneView sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null || sceneView.camera == null)
                throw new InvalidOperationException("No active Scene View camera is available.");

            cameraOptions = cameraOptions ?? new JObject();
            int outputWidth = Mathf.Max(64, requestedResolution);
            ConfigureSceneView(sceneView, cameraOptions);
            SceneAnalysisLayout layout = CreateSceneAnalysisLayout(session, subject, outputWidth, cameraOptions);
            string folder = Path.Combine(Application.dataPath, "KimodoGeneratedClips", "SceneCaptures");
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, "scene_capture_" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                int renderWidth;
                int renderHeight;
                ResolveSceneViewResolution(cameraOptions, sceneView, out renderWidth, out renderHeight);
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
                    ["pictures"] = layout.Metadata.DeepClone(),
                    ["clip"] = layout.Clip.DeepClone(),
                    ["tile_count"] = layout.Tiles.Count,
                    ["ok"] = true
                };
            }
            finally
            {
                layout.Dispose();
            }
        }

        private static void ConfigureSceneView(SceneView view, JObject cameraOptions)
        {
            if (view == null || view.camera == null)
                throw new InvalidOperationException("No active Scene View camera is available.");

            cameraOptions = cameraOptions ?? new JObject();
            Camera camera = view.camera;
            if (cameraOptions["near_clip"] != null || cameraOptions["far_clip"] != null)
            {
                var settings = view.cameraSettings;
                settings.nearClip = ReadPositiveCameraValue(cameraOptions["near_clip"], "camera.near_clip", settings.nearClip);
                settings.farClip = ReadPositiveCameraValue(cameraOptions["far_clip"], "camera.far_clip", settings.farClip);
                if (settings.farClip <= settings.nearClip)
                    throw new InvalidOperationException("camera.far_clip must be greater than camera.near_clip.");
                settings.dynamicClip = false;
                view.cameraSettings = settings;
            }

            if (cameraOptions["show_gizmos"] != null)
                view.drawGizmos = cameraOptions.Value<bool>("show_gizmos");
            if (cameraOptions["show_grid"] != null)
                view.showGrid = cameraOptions.Value<bool>("show_grid");
            if (cameraOptions["scene_lighting"] != null)
                view.sceneLighting = cameraOptions.Value<bool>("scene_lighting");
            if (cameraOptions["show_skybox"] != null)
                view.sceneViewState.showSkybox = cameraOptions.Value<bool>("show_skybox");
            if (cameraOptions["orthographic"] != null)
                view.orthographic = cameraOptions.Value<bool>("orthographic");
            camera.ResetProjectionMatrix();
        }

        private static float ReadPositiveCameraValue(JToken value, string name, float fallback)
        {
            if (value == null) return fallback;
            if (value.Type != JTokenType.Integer && value.Type != JTokenType.Float)
                throw new InvalidOperationException(name + " must be a positive number.");
            float result = value.Value<float>();
            if (float.IsNaN(result) || float.IsInfinity(result) || result <= 0f)
                throw new InvalidOperationException(name + " must be a positive finite number.");
            return result;
        }

        private static void ResolveSceneViewResolution(
            JObject cameraOptions,
            SceneView view,
            out int width,
            out int height)
        {
            JArray requested = cameraOptions?["resolution"] as JArray;
            if (requested == null)
            {
                width = Mathf.Max(64, view.camera.pixelWidth);
                height = Mathf.Max(64, view.camera.pixelHeight);
                return;
            }
            if (requested.Count != 2)
                throw new InvalidOperationException("camera.resolution must be [width,height].");

            width = ReadSceneResolutionValue(requested[0], "camera.resolution[0]");
            height = ReadSceneResolutionValue(requested[1], "camera.resolution[1]");
        }

        private static int ReadSceneResolutionValue(JToken value, string name)
        {
            if (value == null || value.Type != JTokenType.Integer)
                throw new InvalidOperationException(name + " must be an integer from 64 through 4096.");
            int result = value.Value<int>();
            if (result < 64 || result > 4096)
                throw new InvalidOperationException(name + " must be an integer from 64 through 4096.");
            return result;
        }

        private static void FrameSceneView(
            SceneView view,
            Bounds bounds,
            Vector3 direction,
            JObject cameraOptions,
            float outputAspect = 0f)
        {
            ConfigureSceneView(view, cameraOptions);
            if (direction.sqrMagnitude < .0001f)
                throw new InvalidOperationException("camera.direction must not be zero.");
            direction.Normalize();
            CalculateSceneViewExtents(bounds, direction, out Vector3 center, out float horizontal, out float vertical, out float depth);

            Camera camera = view.camera;
            float cameraAspect = camera != null ? Mathf.Max(.01f, camera.aspect) : 1f;
            if (outputAspect <= 0f) outputAspect = cameraAspect;
            bool orthographic = cameraOptions?.Value<bool?>("orthographic") ?? true;
            float padding = cameraOptions?.Value<float?>("padding") ?? 1.1f;
            if (float.IsNaN(padding) || float.IsInfinity(padding) || padding < .01f)
                throw new InvalidOperationException("camera.padding must be a finite number of at least 0.01.");

            float halfHeight = Mathf.Max(.05f, vertical * padding, horizontal * padding / outputAspect)
                * Mathf.Max(1f, outputAspect / cameraAspect);
            float halfFov = camera.fieldOfView * Mathf.Deg2Rad * .5f;
            float size = orthographic
                ? halfHeight * Mathf.Min(1f, cameraAspect)
                : (depth + halfHeight * Mathf.Min(1f, cameraAspect) / Mathf.Tan(halfFov)) * Mathf.Sin(halfFov);
            Vector3 up = Mathf.Abs(Vector3.Dot(direction, Vector3.up)) > .95f ? Vector3.forward : Vector3.up;
            Quaternion rotation = Quaternion.LookRotation(-direction, up);
            view.orthographic = orthographic;
            view.LookAtDirect(center, rotation, size);
            view.size = size;

            float cameraDistance = Mathf.Max(10f, view.cameraSettings.nearClip + size + 1f);
            camera.orthographic = orthographic;
            if (orthographic) camera.orthographicSize = size;
            camera.transform.SetPositionAndRotation(center - rotation * Vector3.forward * cameraDistance, rotation);
            camera.ResetProjectionMatrix();
        }

        private static Vector3 ReadSceneVector(JToken token, string name, Vector3 fallback)
        {
            if (token == null) return fallback;
            if (!(token is JArray values) || values.Count != 3)
                throw new InvalidOperationException(name + " must be an array of three numbers.");
            for (int index = 0; index < values.Count; index++)
            {
                if (values[index].Type != JTokenType.Integer && values[index].Type != JTokenType.Float)
                    throw new InvalidOperationException(name + " must contain finite numbers.");
                float component = values[index].Value<float>();
                if (float.IsNaN(component) || float.IsInfinity(component))
                    throw new InvalidOperationException(name + " must contain finite numbers.");
            }
            return new Vector3(values[0].Value<float>(), values[1].Value<float>(), values[2].Value<float>());
        }

        private static Bounds CalculateSceneTargetBounds(Transform target)
        {
            bool hasBounds = false;
            Bounds bounds = new Bounds(target.position, Vector3.zero);
            foreach (Renderer renderer in target.GetComponentsInChildren<Renderer>(true))
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy ||
                    renderer.forceRenderingOff || renderer.bounds.size.sqrMagnitude < .000001f)
                    continue;
                if (!hasBounds) { bounds = renderer.bounds; hasBounds = true; }
                else bounds.Encapsulate(renderer.bounds);
            }
            if (!hasBounds)
                throw new InvalidOperationException("No renderable geometry found for '" + target.name + "'.");
            return bounds;
        }
    }
}
