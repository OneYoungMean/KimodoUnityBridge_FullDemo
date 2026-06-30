using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace KimodoBridge
{
    [AddComponentMenu("Kimodo/CLI Ability")]
    public sealed class CLIAbility : MonoBehaviour
    {
        public sealed class CameraPictureResult
        {
            public bool Success { get; set; }
            public string Error { get; set; }
            public string Path { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public byte[] PngBytes { get; set; }
        }

        public static CLIAbility Instance { get; private set; }

        [Header("Scene References")]
        public KimodoRuntimeMotionDriver motionDriver;
        public KimodoCliMotionRoutePlanner motionRoutePlanner;
        public Camera visionCamera;
        public Transform characterRoot;
        public CharacterController characterController;

        [Header("Sensing")]
        public float senseRadius = 1f;
        public float senseHeight = 2f;
        public float senseDistance = 2f;
        public LayerMask senseLayer = ~0;

        [Header("Measurement")]
        public float measureMaxDistance = 20f;
        public LayerMask measureLayer = ~0;

        [Header("Arrival Check")]
        public float arrivalThreshold = 1.5f;
        public string goalTag = "Goal";

        private readonly List<ControllerColliderHit> _ccHits = new();

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            if (motionDriver == null) motionDriver = GetComponent<KimodoRuntimeMotionDriver>();
            if (motionRoutePlanner == null) motionRoutePlanner = GetComponent<KimodoCliMotionRoutePlanner>();
        }

        void OnDestroy() { if (Instance == this) Instance = null; }

        // ================================================================
        // 1. Animation
        // ================================================================

        public string Animate(string prompt, float localX, float localZ)
        {
            if (motionRoutePlanner == null)
            {
                return Log("Animate", "Error: motionRoutePlanner is not assigned");
            }

            string result = motionRoutePlanner.Animate(prompt, localX, localZ);
            return Log("Animate", result);
        }

        public string AnimateRoute(string prompt, IList<Vector2> localWaypoints)
        {
            if (motionRoutePlanner == null)
            {
                return Log("AnimateRoute", "Error: motionRoutePlanner is not assigned");
            }

            string result = motionRoutePlanner.AnimateRoute(prompt, localWaypoints);
            return Log("AnimateRoute", result);
        }

        public string Animate(string prompt)
        {
            if (motionDriver == null)
            {
                return Log("Animate", "Error: motionDriver is not assigned");
            }

            motionDriver.SetAnimationPrompt(prompt);
            return Log("Animate", $"Animation configured: prompt = \"{prompt}\" (no displacement)");
        }

        public string SetAnimationDuration(float seconds)
        {
            if (motionDriver == null)
            {
                return Log("SetAnimationDuration", "Error: motionDriver is not assigned");
            }

            float clamped = Mathf.Clamp(seconds, 1f, 10f);
            motionDriver.SetAnimationDurationSeconds(clamped);
            return Log("SetAnimationDuration", $"Animation duration: {clamped:F1}s (range 1-10s)");
        }

        public string GetAnimationState()
        {
            if (motionDriver == null)
            {
                return Log("GetAnimationState", "Error: motionDriver is not assigned");
            }

            string prompt = motionDriver.GetAnimationPrompt(out bool isIdle);
            string routeState = motionRoutePlanner == null
                ? "Route: planner is not assigned"
                : $"RouteActive: {motionRoutePlanner.RouteActive}, PendingWaypoints: {motionRoutePlanner.PendingWaypointCount}";
            return Log("GetAnimationState", $"=== Animation State ===\n  Prompt: {prompt}\n  IsIdle: {isIdle}\n  Status: {(isIdle ? "Idle (ready for next command)" : "Busy (generating/playing)")}\n  {routeState}");
        }

        // ================================================================
        // 2. Sensing
        // ================================================================

        public string SenseControllerHits()
        {
            if (characterController == null)
                return Log("SenseControllerHits", "Error: characterController is not assigned");
            if (_ccHits.Count == 0) return Log("SenseControllerHits", "CharacterController collisions: none");

            var root = ResolveRoot();
            var sb = new StringBuilder();
            sb.AppendLine($"=== CharacterController Collisions ({_ccHits.Count}) ===");
            for (int i = 0; i < _ccHits.Count; i++)
            {
                var hit = _ccHits[i];
                var offset = hit.gameObject.transform.position - root.position;
                sb.AppendLine($"  [{i}] {hit.gameObject.name}");
                sb.AppendLine($"      Distance={offset.magnitude:F2}m");
                sb.AppendLine($"      Normal=({hit.normal.x:F2}, {hit.normal.y:F2}, {hit.normal.z:F2})");
            }
            return Log("SenseControllerHits", sb.ToString());
        }

        public string QueryObject(string objectName)
        {
            var go = GameObject.Find(objectName);
            if (go == null)
            {
                var root = ResolveRoot();
                var child = root.Find(objectName);
                if (child != null) go = child.gameObject;
            }
            if (go == null) return Log("QueryObject", $"Object not found: {objectName}");

            var root2 = ResolveRoot();
            var worldPos = go.transform.position;
            var localPos = root2.InverseTransformPoint(worldPos);
            var dist = Vector3.Distance(root2.position, worldPos);
            var angle = Vector3.SignedAngle(root2.forward, (worldPos - root2.position).normalized, Vector3.up);
            return Log("QueryObject", $"Object '{objectName}':\n  Local Position: ({localPos.x:F2}, {localPos.y:F2}, {localPos.z:F2}) [x=right, y=up, z=forward]\n  Distance: {dist:F2}m, Direction: {angle:F0}° ({DescribeDirection(angle)})");
        }

        // ================================================================
        // 3. Vision - capture and save a screenshot
        // ================================================================

        public CameraPictureResult GetCameraPicture()
        {
            if (visionCamera == null)
            {
                const string error = "Error: visionCamera is not assigned";
                Log("GetCameraPicture", error);
                return new CameraPictureResult
                {
                    Success = false,
                    Error = error
                };
            }

            const int width = 128;
            const int height = 128;

            var rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            var originalTarget = visionCamera.targetTexture;

            visionCamera.targetTexture = rt;
            visionCamera.Render();

            RenderTexture.active = rt;
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();

            visionCamera.targetTexture = originalTarget;
            RenderTexture.active = null;
            RenderTexture.ReleaseTemporary(rt);

            var pngBytes = tex.EncodeToPNG();
            Object.Destroy(tex);

            string dir = Path.Combine(Path.GetDirectoryName(Application.dataPath), "screenshots");
            Directory.CreateDirectory(dir);
            string filename = $"CameraPicture_{System.DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.png";
            string savePath = Path.Combine(dir, filename);
            File.WriteAllBytes(savePath, pngBytes);

            Debug.Log($"[CLIAbility] GetCameraPicture: saved to {savePath}");
            return new CameraPictureResult
            {
                Success = true,
                Path = savePath,
                Width = width,
                Height = height,
                PngBytes = pngBytes
            };
        }

        // ================================================================
        // 4. Measurement
        // ================================================================

        public string MeasureFromCamera(float screenX = 0.5f, float screenY = 0.5f)
        {
            if (visionCamera == null) return Log("MeasureFromCamera", "Error: visionCamera is not assigned");
            var ray = visionCamera.ViewportPointToRay(new Vector3(screenX, screenY, 0f));
            var root = ResolveRoot();
            if (Physics.Raycast(ray, out var hit, measureMaxDistance, measureLayer))
            {
                var distToChar = Vector3.Distance(root.position, hit.point);
                return Log("MeasureFromCamera", $"=== Measurement ({screenX:F2}, {screenY:F2}) ===\n  Hit: {hit.collider.gameObject.name}\n  Distance To Camera: {hit.distance:F2}m\n  Distance To Character: {distToChar:F2}m\n  Height: {hit.collider.bounds.size.y:F2}m");
            }
            return Log("MeasureFromCamera", $"=== Measurement ({screenX:F2}, {screenY:F2}) ===\n  No hit (>{measureMaxDistance}m)");
        }

        // ================================================================
        // 5. Success Check
        // ================================================================

        public string CheckArrival()
        {
            var root = ResolveRoot();
            var center = root.position;
            var goalObjs = GameObject.FindGameObjectsWithTag(goalTag);
            if (goalObjs == null || goalObjs.Length == 0)
                return Log("CheckArrival", "NotArrived");

            float minDist = float.MaxValue;
            foreach (var go in goalObjs)
            {
                var d = Vector3.Distance(center, go.transform.position);
                if (d < minDist) minDist = d;
            }

            return Log("CheckArrival", minDist <= arrivalThreshold ? "Arrived" : "NotArrived");
        }

        // Summary

        public string GetEnvironmentSummary()
        {
            var sb = new StringBuilder();
            sb.AppendLine("╔══════════ Environment Summary ══════════╗");
            sb.AppendLine(GetAnimationState());
            sb.AppendLine(SenseControllerHits());
            sb.AppendLine(MeasureFromCamera());
            sb.AppendLine(CheckArrival());
            sb.AppendLine("╚══════════════════════════════╝");
            return Log("GetEnvironmentSummary", sb.ToString());
        }

        // Internal Helpers

        private Transform ResolveRoot()
        {
            if (characterRoot != null) return characterRoot;
            if (motionDriver != null) return motionDriver.transform;
            return transform;
        }

        private static string Log(string method, string result)
        {
            Debug.Log($"[CLIAbility] {method}: {result}");
            return result;
        }

        private static string DescribeDirection(float angle)
        {
            float a = Mathf.Abs(angle);
            if (a < 22.5f) return "front";
            if (angle is >= 22.5f and < 67.5f) return "front-right";
            if (angle is >= 67.5f and < 112.5f) return "right";
            if (angle is >= 112.5f and < 157.5f) return "back-right";
            if (a >= 157.5f) return "back";
            if (angle is <= -22.5f and > -67.5f) return "front-left";
            if (angle is <= -67.5f and > -112.5f) return "left";
            if (angle is <= -112.5f and > -157.5f) return "back-left";
            return "unknown";
        }

        private void OnControllerColliderHit(ControllerColliderHit hit)
        {
            _ccHits.Add(hit);
            if (_ccHits.Count > 20) _ccHits.RemoveAt(0);
        }
    }
}
