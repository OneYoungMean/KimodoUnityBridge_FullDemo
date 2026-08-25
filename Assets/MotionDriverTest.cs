using System;
using System.Collections.Generic;
using UnityEngine;

namespace KimodoBridge
{
    [AddComponentMenu("Kimodo/Runtime Motion Driver Test")]
    public sealed class MotionDriverTest : MonoBehaviour
    {
        [Header("Prompt")]
        [SerializeField] private string prompt = "a person waves hello";

        [Header("Duration")]
        [SerializeField] private float durationSeconds = 5f;

        [Header("Root2D")]
        [SerializeField] private float rootWorldX;
        [SerializeField] private float rootWorldZ = 1f;
        [SerializeField] private float rootHeadingX = 0f;
        [SerializeField] private float rootHeadingZ = 1f;
        [SerializeField] private float constraintTimeSeconds = 1f;

        [Header("Mouse Root2D Constraint")]
        [SerializeField] private Camera clickCamera;
        [SerializeField] private LayerMask clickMask = ~0;
        [SerializeField][Min(0.01f)] private float clickRayDistance = 1000f;
        [SerializeField][Min(0f)] private float clickTargetRadius = 1f;
        [SerializeField][Min(0.01f)] private float clickMaxSpeedMetersPerSecond = 1.25f;
        [SerializeField][Min(0.01f)] private float clickMaxAccelerationMetersPerSecond2 = 1.5f;
        [SerializeField][Min(0f)] private float clickArrivalThresholdMeters = 0.1f;
        [SerializeField] private bool clickIncludeHeading = true;
        [SerializeField][Min(0.01f)] private float clickTargetMarkerDiameter = 0.2f;

        [Header("End Effector")]
        [SerializeField] private float targetX;
        [SerializeField] private float targetY = 1f;
        [SerializeField] private float targetZ = 1f;
        [SerializeField] private float effectorTimeSeconds = 1f;

        private Rect windowRect = new Rect(16f, 16f, 420f, 720f);
        private Vector2 scroll;
        private string lastResult = "Ready.";
        private readonly List<KimodoRuntimeMotionDriver> resolvedDrivers =
            new List<KimodoRuntimeMotionDriver>();
        private readonly List<ClickTargetAssignment> clickTargets =
            new List<ClickTargetAssignment>();

        private sealed class ClickTargetAssignment
        {
            public KimodoRuntimeMotionDriver driver;
            public Vector3 position;
            public GameObject marker;
        }

        private void OnGUI()
        {
            windowRect = GUI.Window(KimodoUnityObjectIdUtility.IdHash(this), windowRect, DrawWindow, "Motion Driver Test");
        }

        private void Update()
        {
            IReadOnlyList<KimodoRuntimeMotionDriver> targets = ResolveDrivers();
            if (targets.Count == 0)
            {
                return;
            }

            RemoveTargetMarkersOnArrival();
            if (!Input.GetMouseButtonDown(0))
            {
                return;
            }

            Vector2 guiMousePosition = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            if (windowRect.Contains(guiMousePosition))
            {
                return;
            }

            Camera rayCamera = clickCamera != null ? clickCamera : Camera.main;
            if (rayCamera == null ||
                !Physics.Raycast(
                    rayCamera.ScreenPointToRay(Input.mousePosition),
                    out RaycastHit hit,
                    clickRayDistance,
                    clickMask,
                    QueryTriggerInteraction.Ignore))
            {
                lastResult = "Mouse constraint failed: no camera or surface hit.";
                return;
            }

            List<Vector3> targetPoints = BuildSymmetricCircleTargets(
                hit.point,
                clickTargetRadius,
                ResolveCameraPlanarAxis(rayCamera),
                targets.Count);
            Shuffle(targetPoints);
            ClearTargetMarkers();
            for (int i = 0; i < targets.Count; i++)
            {
                KimodoRuntimeMotionDriver target = targets[i];
                Vector3 targetPoint = targetPoints[i];
                Vector2? worldHeading = clickIncludeHeading
                    ? ResolveCameraFacingHeading(rayCamera, targetPoint)
                    : (Vector2?)null;
                target.SetRoot2DTarget(
                    targetPoint.x,
                    targetPoint.z,
                    clickMaxSpeedMetersPerSecond,
                    clickMaxAccelerationMetersPerSecond2,
                    clickArrivalThresholdMeters,
                    includeHeading: clickIncludeHeading,
                    worldHeading: worldHeading);
                target.ApplyStagedConstraints();
                ShowTargetMarker(target, targetPoint, i);
            }
            lastResult =
                $"Mouse Root2DTarget center=({hit.point.x:0.###}, {hit.point.z:0.###}), radius={clickTargetRadius:0.###}, targets={targets.Count}, maxSpeed={clickMaxSpeedMetersPerSecond:0.###}, maxAcceleration={clickMaxAccelerationMetersPerSecond2:0.###}.";
        }

        private static Vector3 ResolveCameraPlanarAxis(Camera rayCamera)
        {
            Vector3 axis = Vector3.ProjectOnPlane(rayCamera.transform.forward, Vector3.up);
            if (axis.sqrMagnitude < 1e-6f)
            {
                axis = Vector3.ProjectOnPlane(rayCamera.transform.up, Vector3.up);
            }
            return axis.sqrMagnitude < 1e-6f ? Vector3.forward : axis.normalized;
        }

        private static Vector2 ResolveCameraFacingHeading(Camera rayCamera, Vector3 targetPoint)
        {
            Vector3 toCamera = rayCamera.transform.position - targetPoint;
            toCamera.y = 0f;
            if (toCamera.sqrMagnitude < 1e-6f)
            {
                Vector3 fallback = -ResolveCameraPlanarAxis(rayCamera);
                return new Vector2(fallback.x, fallback.z).normalized;
            }

            toCamera.Normalize();
            return new Vector2(toCamera.x, toCamera.z);
        }

        private static List<Vector3> BuildSymmetricCircleTargets(
            Vector3 center,
            float radius,
            Vector3 cameraPlanarAxis,
            int count)
        {
            var points = new List<Vector3>(Mathf.Max(0, count));
            if (count <= 0)
            {
                return points;
            }

            float targetRadius = Mathf.Max(0f, radius);
            Vector3 axis = cameraPlanarAxis.sqrMagnitude < 1e-6f
                ? Vector3.forward
                : cameraPlanarAxis.normalized;
            Vector3 right = Vector3.Cross(Vector3.up, axis).normalized;
            for (int i = 0; i < count / 2; i++)
            {
                float angle = UnityEngine.Random.Range(0f, Mathf.PI);
                Vector3 alongAxis = axis * Mathf.Cos(angle);
                Vector3 acrossAxis = right * Mathf.Sin(angle);
                points.Add(center + targetRadius * (alongAxis + acrossAxis));
                points.Add(center + targetRadius * (alongAxis - acrossAxis));
            }

            if ((count & 1) != 0)
            {
                float direction = UnityEngine.Random.value < 0.5f ? -1f : 1f;
                points.Add(center + targetRadius * axis * direction);
            }

            return points;
        }

        private static void Shuffle(List<Vector3> points)
        {
            for (int i = 0; i < points.Count - 1; i++)
            {
                int swapIndex = UnityEngine.Random.Range(i, points.Count);
                (points[i], points[swapIndex]) = (points[swapIndex], points[i]);
            }
        }

        private void ShowTargetMarker(KimodoRuntimeMotionDriver target, Vector3 targetPosition, int index)
        {
            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = $"Root2D Constraint {index + 1}";
            if (marker.TryGetComponent(out Collider markerCollider))
            {
                markerCollider.enabled = false;
            }
            if (marker.TryGetComponent(out Renderer markerRenderer))
            {
                markerRenderer.material.color = Color.green;
            }

            float diameter = Mathf.Max(0.01f, clickTargetMarkerDiameter);
            marker.transform.localScale = Vector3.one * diameter;
            marker.transform.position = targetPosition;
            clickTargets.Add(new ClickTargetAssignment
            {
                driver = target,
                position = targetPosition,
                marker = marker,
            });
        }

        private void RemoveTargetMarkersOnArrival()
        {
            float thresholdSquared = clickArrivalThresholdMeters * clickArrivalThresholdMeters;
            for (int i = clickTargets.Count - 1; i >= 0; i--)
            {
                ClickTargetAssignment assignment = clickTargets[i];
                if (assignment.driver == null)
                {
                    Destroy(assignment.marker);
                    clickTargets.RemoveAt(i);
                    continue;
                }

                Vector3 delta = assignment.driver.GetPosition() - assignment.position;
                delta.y = 0f;
                if (delta.sqrMagnitude > thresholdSquared)
                {
                    continue;
                }

                Destroy(assignment.marker);
                clickTargets.RemoveAt(i);
            }
        }

        private void ClearTargetMarkers()
        {
            for (int i = 0; i < clickTargets.Count; i++)
            {
                Destroy(clickTargets[i].marker);
            }
            clickTargets.Clear();
        }

        private void OnDestroy()
        {
            ClearTargetMarkers();
        }

        private void DrawWindow(int windowId)
        {
            IReadOnlyList<KimodoRuntimeMotionDriver> targets = ResolveDrivers();
            if (targets.Count == 0)
            {
                GUILayout.Label("No Motion Driver was found in the scene.");
                GUI.DragWindow(new Rect(0f, 0f, windowRect.width, 24f));
                return;
            }

            scroll = GUILayout.BeginScrollView(scroll, false, true);

            DrawStatusSection();
            DrawPromptSection();
            DrawDurationSection();
            DrawRootSection();
            DrawEffectorSection();

            GUILayout.Space(8f);
            GUILayout.Label("Last Result");
            GUILayout.TextArea(lastResult, GUILayout.MinHeight(80f));

            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0f, 0f, windowRect.width, 24f));
        }

        private void DrawStatusSection()
        {
            KimodoRuntimeMotionDriver primaryDriver = ResolveDrivers()[0];
            GUILayout.Label("Status");
            bool isIdle;
            string currentPrompt = primaryDriver.GetCurrentPrompt(out isIdle);
            Vector3 position = primaryDriver.GetPosition();

            GUILayout.Label($"Drivers: {resolvedDrivers.Count} (status shows first)");
            GUILayout.Label($"Running: {primaryDriver.IsRunning}");
            GUILayout.Label($"Prompt: {currentPrompt}");
            GUILayout.Label($"IsIdle: {isIdle}");
            GUILayout.Label($"Position: ({position.x:0.###}, {position.y:0.###}, {position.z:0.###})");
            GUILayout.Label($"Duration: {primaryDriver.GetAnimationDurationSeconds():0.###}s");
            GUILayout.Label($"Status: {primaryDriver.StatusMessage}");

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Read Prompt"))
            {
                lastResult = $"Prompt={currentPrompt}, IsIdle={isIdle}";
            }

            if (GUILayout.Button("Read Position"))
            {
                lastResult = $"Position=({position.x:0.###}, {position.y:0.###}, {position.z:0.###})";
            }

            GUILayout.EndHorizontal();

            if (GUILayout.Button("Reset Motion"))
            {
                ForEachDriver(target => _ = target.ResetMotionAsync());
                lastResult = $"ResetMotionAsync called for {resolvedDrivers.Count} drivers.";
            }
        }

        private void DrawPromptSection()
        {
            GUILayout.Space(8f);
            GUILayout.Label("Prompt");
            prompt = DrawTextField("Text", prompt);

            if (GUILayout.Button("Set Prompt"))
            {
                ForEachDriver(target => target.SetAnimationPrompt(prompt));
                lastResult = $"SetAnimationPrompt(\"{prompt}\") -> {resolvedDrivers.Count} drivers";
            }
        }

        private void DrawDurationSection()
        {
            GUILayout.Space(8f);
            GUILayout.Label("Duration");
            durationSeconds = DrawFloatField("Seconds", durationSeconds);

            if (GUILayout.Button("Set Duration"))
            {
                ForEachDriver(target => target.SetAnimationDurationSeconds(durationSeconds));
                lastResult = $"SetAnimationDurationSeconds({durationSeconds:0.###}) -> {resolvedDrivers.Count} drivers";
            }
        }

        private void DrawRootSection()
        {
            GUILayout.Space(8f);
            GUILayout.Label("Root2D");
            clickTargetRadius = Mathf.Max(
                0f,
                DrawFloatField("Formation Radius", clickTargetRadius));
            clickMaxSpeedMetersPerSecond = Mathf.Max(
                0.01f,
                DrawFloatField("Target Max Speed", clickMaxSpeedMetersPerSecond));
            clickMaxAccelerationMetersPerSecond2 = Mathf.Max(
                0.01f,
                DrawFloatField("Target Max Accel", clickMaxAccelerationMetersPerSecond2));
            clickArrivalThresholdMeters = Mathf.Max(
                0f,
                DrawFloatField("Arrival Threshold", clickArrivalThresholdMeters));
            clickIncludeHeading = GUILayout.Toggle(clickIncludeHeading, "Mouse Target Heading Faces Camera");
            clickTargetMarkerDiameter = Mathf.Max(
                0.01f,
                DrawFloatField("Constraint Marker Size", clickTargetMarkerDiameter));
            GUILayout.Label("Click outside this window to submit a world Root2D constraint.");
            constraintTimeSeconds = DrawFloatField("Constraint Time", constraintTimeSeconds);
            rootWorldX = DrawFloatField("World X", rootWorldX);
            rootWorldZ = DrawFloatField("World Z", rootWorldZ);
            rootHeadingX = DrawFloatField("Heading X", rootHeadingX);
            rootHeadingZ = DrawFloatField("Heading Z", rootHeadingZ);

            if (GUILayout.Button("Queue World Root2D"))
            {
                ForEachDriver(target =>
                {
                    target.SetRoot2D(rootWorldX, rootWorldZ, constraintTimeSeconds);
                    target.ApplyStagedConstraints();
                });
                lastResult =
                    $"SetRoot2D({rootWorldX:0.###}, {rootWorldZ:0.###}, duration={constraintTimeSeconds:0.###})";
            }

            if (GUILayout.Button("Queue World Root+Heading"))
            {
                ForEachDriver(target => target.SetRoot2D(
                    rootWorldX,
                    rootWorldZ,
                    rootHeadingX,
                    rootHeadingZ,
                    constraintTimeSeconds));
                ForEachDriver(target => target.ApplyStagedConstraints());
                lastResult =
                    $"SetRoot2D({rootWorldX:0.###}, {rootWorldZ:0.###}, {rootHeadingX:0.###}, {rootHeadingZ:0.###}, duration={constraintTimeSeconds:0.###})";
            }

        }

        private void DrawEffectorSection()
        {
            GUILayout.Space(8f);
            GUILayout.Label("End Effector");
            effectorTimeSeconds = DrawFloatField("Effector Time", effectorTimeSeconds);
            targetX = DrawFloatField("Target X", targetX);
            targetY = DrawFloatField("Target Y", targetY);
            targetZ = DrawFloatField("Target Z", targetZ);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Left Hand"))
            {
                ForEachDriver(target => target.SetLeftHandConstraint(targetX, targetY, targetZ, effectorTimeSeconds));
                lastResult = $"SetLeftHandConstraint({targetX:0.###}, {targetY:0.###}, {targetZ:0.###}, {effectorTimeSeconds:0.###})";
            }

            if (GUILayout.Button("Right Hand"))
            {
                ForEachDriver(target => target.SetRightHandConstraint(targetX, targetY, targetZ, effectorTimeSeconds));
                lastResult = $"SetRightHandConstraint({targetX:0.###}, {targetY:0.###}, {targetZ:0.###}, {effectorTimeSeconds:0.###})";
            }

            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Left Foot"))
            {
                ForEachDriver(target => target.SetLeftFootConstraint(targetX, targetY, targetZ, effectorTimeSeconds));
                lastResult = $"SetLeftFootConstraint({targetX:0.###}, {targetY:0.###}, {targetZ:0.###}, {effectorTimeSeconds:0.###})";
            }

            if (GUILayout.Button("Right Foot"))
            {
                ForEachDriver(target => target.SetRightFootConstraint(targetX, targetY, targetZ, effectorTimeSeconds));
                lastResult = $"SetRightFootConstraint({targetX:0.###}, {targetY:0.###}, {targetZ:0.###}, {effectorTimeSeconds:0.###})";
            }

            GUILayout.EndHorizontal();
        }

        private IReadOnlyList<KimodoRuntimeMotionDriver> ResolveDrivers()
        {
            resolvedDrivers.Clear();
            KimodoRuntimeMotionDriver[] sceneDrivers =
                UnityEngine.Object.FindObjectsByType<KimodoRuntimeMotionDriver>(
                    FindObjectsInactive.Exclude,
                    FindObjectsSortMode.None);
            for (int i = 0; i < sceneDrivers.Length; i++)
            {
                KimodoRuntimeMotionDriver target = sceneDrivers[i];
                if (target != null && target.isActiveAndEnabled)
                {
                    resolvedDrivers.Add(target);
                }
            }
            return resolvedDrivers;
        }

        private void ForEachDriver(Action<KimodoRuntimeMotionDriver> action)
        {
            IReadOnlyList<KimodoRuntimeMotionDriver> targets = ResolveDrivers();
            for (int i = 0; i < targets.Count; i++)
            {
                action(targets[i]);
            }
        }

        private static string DrawTextField(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(120f));
            string result = GUILayout.TextField(value ?? string.Empty);
            GUILayout.EndHorizontal();
            return result;
        }

        private static float DrawFloatField(string label, float value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(120f));
            string next = GUILayout.TextField(value.ToString("0.###"));
            GUILayout.EndHorizontal();

            if (float.TryParse(next, out float parsed))
            {
                return parsed;
            }

            return value;
        }
    }
}
