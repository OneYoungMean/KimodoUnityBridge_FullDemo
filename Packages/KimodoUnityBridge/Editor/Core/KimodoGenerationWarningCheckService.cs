using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using KimodoBridge;
using KimodoUnityBridge;
using UnityEngine;

namespace KimodoBridge.Editor
{
    /// <summary>
    /// Shared generation preflight checks for every animation generator.
    /// </summary>
    public static class KimodoGenerationWarningCheckService
    {
        public const float MaxSpeedMetersPerSecond = 7f;
        public const float MaxBlockingSpeedMetersPerSecond = 10f;
        public const float MaxAccelerationMetersPerSecondSquared = 10f;
        public const float MaxBlockingAccelerationMetersPerSecondSquared = 15f;
        private const float PositionToleranceMeters = 0.05f;
        private const float HeadingToleranceDegrees = 5f;
        private const float HeadingJumpWarningDegrees = 45f;
        private const float RootYJumpWarningMeters = 0.25f;
        private const string RootMotionLimitSuggestion = " Try splitting the action into multiple animation generations. For each following clip, use an In constraint (constraints[].inout.in) referencing the preceding clip.";

        public static JObject Build(
            JObject arguments,
            int durationFrames,
            float frameRate,
            string characterName = null,
            string modelName = null,
            IReadOnlyList<KimodoMarkerSampleResult> generatedSamples = null)
        {
            try
            {
                JObject rootTrajectory = BuildRootTrajectory(arguments, durationFrames, frameRate, generatedSamples);
                JObject overlap = BuildConstraintOverlap(arguments, frameRate, generatedSamples);
                JObject effectors = BuildEffectorChecks(
                    arguments, characterName, modelName, frameRate, generatedSamples);
                bool blocking = rootTrajectory.Value<float>("max_speed_mps") > MaxBlockingSpeedMetersPerSecond
                    || rootTrajectory.Value<float>("max_acceleration_mps2") > MaxBlockingAccelerationMetersPerSecondSquared;
                bool warning = HasWarning(rootTrajectory) || HasWarning(overlap) || HasWarning(effectors);
                return new JObject
                {
                    ["blocking"] = blocking,
                    ["overall"] = blocking ? "error" : warning ? "warning" : "ok",
                    ["root_trajectory"] = rootTrajectory,
                    ["constraint_overlap"] = overlap,
                    ["effectors"] = effectors
                };
            }
            catch (Exception error)
            {
                return new JObject
                {
                    ["blocking"] = false,
                    ["overall"] = "warning",
                    ["status"] = "check_error",
                    ["message"] = error.Message
                };
            }
        }

        private sealed class RootObservation
        {
            public int Frame;
            public string Source;
            public Vector3 Position;
            public bool HasY;
            public Vector2 Heading;
            public bool HasHeading;
        }

        private static JObject BuildRootTrajectory(
            JObject arguments,
            int durationFrames,
            float frameRate,
            IReadOnlyList<KimodoMarkerSampleResult> generatedSamples)
        {
            List<RootObservation> observations = CollectRootObservations(arguments, frameRate, generatedSamples);
            observations = observations.OrderBy(item => item.Frame).ThenBy(item => item.Source, StringComparer.Ordinal).ToList();
            JArray issues = new JArray();
            float maxSpeed = 0f;
            float maxAcceleration = 0f;
            float maxHeadingJump = 0f;
            float maxYJump = 0f;
            float previousSpeed = 0f;
            RootObservation previous = observations.Count > 0 ? observations[0] : null;
            int compared = 0;

            for (int i = 1; i < observations.Count; i++)
            {
                RootObservation current = observations[i];
                if (previous.Frame == current.Frame)
                    continue;
                float dt = Mathf.Max(1f / Mathf.Max(1f, frameRate), (current.Frame - previous.Frame) / Mathf.Max(1f, frameRate));
                Vector2 previousXZ = new Vector2(previous.Position.x, previous.Position.z);
                Vector2 currentXZ = new Vector2(current.Position.x, current.Position.z);
                float planarDistance = Vector2.Distance(previousXZ, currentXZ);
                float distance = previous.HasY && current.HasY
                    ? Vector3.Distance(previous.Position, current.Position)
                    : planarDistance;
                float speed = distance / dt;
                float acceleration = compared == 0 ? speed / dt : Mathf.Abs(speed - previousSpeed) / dt;
                maxSpeed = Mathf.Max(maxSpeed, speed);
                maxAcceleration = Mathf.Max(maxAcceleration, acceleration);
                if (previous.HasHeading && current.HasHeading)
                    maxHeadingJump = Mathf.Max(maxHeadingJump, Vector2.Angle(previous.Heading, current.Heading));
                if (previous.HasY && current.HasY)
                    maxYJump = Mathf.Max(maxYJump, Mathf.Abs(current.Position.y - previous.Position.y));
                float headingJump = previous.HasHeading && current.HasHeading
                    ? Vector2.Angle(previous.Heading, current.Heading)
                    : 0f;
                float rootYJump = previous.HasY && current.HasY
                    ? Mathf.Abs(current.Position.y - previous.Position.y)
                    : 0f;

                bool sourceChanged = !string.Equals(previous.Source, current.Source, StringComparison.OrdinalIgnoreCase);
                if (speed > MaxSpeedMetersPerSecond)
                    AddIssue(issues, "speed_limit", current.Frame, $"Root speed {speed:F3} m/s exceeds {MaxSpeedMetersPerSecond:F1} m/s." + RootMotionLimitSuggestion, previous.Source, current.Source);
                if (speed > MaxBlockingSpeedMetersPerSecond)
                    AddIssue(issues, "speed_hard_limit", current.Frame, $"Root speed {speed:F3} m/s exceeds the hard limit of {MaxBlockingSpeedMetersPerSecond:F1} m/s." + RootMotionLimitSuggestion, previous.Source, current.Source);
                if (acceleration > MaxAccelerationMetersPerSecondSquared)
                    AddIssue(issues, sourceChanged ? "constraint_jump" : "acceleration_limit", current.Frame, $"Root acceleration {acceleration:F3} m/s² exceeds {MaxAccelerationMetersPerSecondSquared:F1} m/s²." + RootMotionLimitSuggestion, previous.Source, current.Source);
                if (acceleration > MaxBlockingAccelerationMetersPerSecondSquared)
                    AddIssue(issues, "acceleration_hard_limit", current.Frame, $"Root acceleration {acceleration:F3} m/s² exceeds the hard limit of {MaxBlockingAccelerationMetersPerSecondSquared:F1} m/s²." + RootMotionLimitSuggestion, previous.Source, current.Source);
                if (sourceChanged && (speed > MaxSpeedMetersPerSecond || acceleration > MaxAccelerationMetersPerSecondSquared))
                    AddIssue(issues, "constraint_jump", current.Frame, "Adjacent constraints introduce a large root trajectory jump.", previous.Source, current.Source);
                if (headingJump > HeadingJumpWarningDegrees)
                    AddIssue(issues, "heading_jump", current.Frame, $"Root heading changes by {headingJump:F1} degrees.", previous.Source, current.Source);
                if (rootYJump > RootYJumpWarningMeters)
                    AddIssue(issues, "root_y_jump", current.Frame, $"FullBody root Y changes by {rootYJump:F3} m.", previous.Source, current.Source);

                previousSpeed = speed;
                compared++;
                previous = current;
            }

            string status = observations.Count == 0 ? "not_applicable" : issues.Count == 0 ? "ok" : "warning";
            return new JObject
            {
                ["status"] = status,
                ["sample_count"] = observations.Count,
                ["compared_pairs"] = compared,
                ["max_speed_mps"] = maxSpeed,
                ["max_acceleration_mps2"] = maxAcceleration,
                ["max_heading_jump_degrees"] = maxHeadingJump,
                ["max_root_y_jump_m"] = maxYJump,
                ["issues"] = issues
            };
        }

        private static JObject BuildConstraintOverlap(
            JObject arguments,
            float frameRate,
            IReadOnlyList<KimodoMarkerSampleResult> generatedSamples)
        {
            List<RootObservation> observations = CollectRootObservations(arguments, frameRate, generatedSamples);
            JArray issues = new JArray();
            foreach (IGrouping<int, RootObservation> group in observations.GroupBy(item => item.Frame).Where(item => item.Count() > 1))
            {
                List<RootObservation> entries = group.ToList();
                for (int i = 0; i < entries.Count; i++)
                {
                    for (int j = i + 1; j < entries.Count; j++)
                    {
                        RootObservation left = entries[i];
                        RootObservation right = entries[j];
                        Vector2 leftXZ = new Vector2(left.Position.x, left.Position.z);
                        Vector2 rightXZ = new Vector2(right.Position.x, right.Position.z);
                        float positionDelta = Vector2.Distance(leftXZ, rightXZ);
                        float yDelta = left.HasY && right.HasY ? Mathf.Abs(left.Position.y - right.Position.y) : 0f;
                        float headingDelta = left.HasHeading && right.HasHeading ? Vector2.Angle(left.Heading, right.Heading) : 0f;
                        if (positionDelta > PositionToleranceMeters ||
                            yDelta > PositionToleranceMeters ||
                            headingDelta > HeadingToleranceDegrees)
                        {
                            JObject issue = new JObject
                            {
                                ["code"] = "root_description_conflict",
                                ["frame"] = group.Key,
                                ["left_source"] = left.Source,
                                ["right_source"] = right.Source,
                                ["position_delta_xz_m"] = positionDelta,
                                ["root_y_delta_m"] = yDelta,
                                ["heading_delta_degrees"] = headingDelta,
                                ["message"] = "Overlapping constraints describe different root position or heading."
                            };
                            issues.Add(issue);
                        }
                    }
                }
            }

            return new JObject
            {
                ["status"] = observations.Count == 0 ? "not_applicable" : issues.Count == 0 ? "ok" : "warning",
                ["overlap_frame_count"] = observations.GroupBy(item => item.Frame).Count(item => item.Count() > 1),
                ["issues"] = issues
            };
        }

        private static JObject BuildEffectorChecks(
            JObject arguments,
            string characterName,
            string modelName,
            float frameRate,
            IReadOnlyList<KimodoMarkerSampleResult> generatedSamples)
        {
            JArray checks = new JArray();
            JArray constraints = arguments?["constraints"] as JArray;
            Dictionary<int, KimodoMarkerSampleResult> samplesByFrame = (generatedSamples ?? Array.Empty<KimodoMarkerSampleResult>())
                .Where(item => item != null)
                .GroupBy(item => Mathf.RoundToInt((float)(item.sampleTime * Mathf.Max(1f, frameRate))))
                .ToDictionary(group => group.Key, group => group.Last());

            foreach (JObject item in constraints?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                int frame = item.Value<int?>("frame") ?? 0;
                if (item["effector"] is JObject effect)
                {
                    foreach (JProperty target in effect?.Properties() ?? Enumerable.Empty<JProperty>())
                    {
                        JObject payload = target.Value as JObject;
                        if (payload == null || (!HasPosition(payload) && payload["rotation"] == null)) continue;
                        JObject check = new JObject
                        {
                            ["frame"] = frame,
                            ["effector"] = target.Name,
                            ["source"] = "effector",
                            ["status"] = "not_verified"
                        };
                        checks.Add(check);
                    }
                }

                if (item["pose"] != null && item["root"] == null &&
                    samplesByFrame.TryGetValue(frame, out KimodoMarkerSampleResult sample))
                {
                    AddSampleEffectorCheck(checks, frame, "left_hand", sample.sampleValid, sample.effectors?.leftHand);
                    AddSampleEffectorCheck(checks, frame, "right_hand", sample.sampleValid, sample.effectors?.rightHand);
                    AddSampleEffectorCheck(checks, frame, "left_foot", sample.sampleValid, sample.effectors?.leftFoot);
                    AddSampleEffectorCheck(checks, frame, "right_foot", sample.sampleValid, sample.effectors?.rightFoot);
                }
            }

            int warningCount = checks.OfType<JObject>().Count(item => string.Equals(item.Value<string>("status"), "warning", StringComparison.OrdinalIgnoreCase) || string.Equals(item.Value<string>("status"), "not_verified", StringComparison.OrdinalIgnoreCase));
            return new JObject
            {
                ["status"] = checks.Count == 0 ? "not_applicable" : warningCount == 0 ? "ok" : "warning",
                ["checked_count"] = checks.Count,
                ["warning_count"] = warningCount,
                ["checks"] = checks
            };
        }

        private static void AddSampleEffectorCheck(JArray checks, int frame, string name, bool valid, KimodoRigidTransform target)
        {
            if (!valid || target == null) return;
            checks.Add(new JObject
            {
                ["frame"] = frame,
                ["effector"] = name,
                ["source"] = "fullbody",
                ["status"] = "not_verified",
                ["target_position"] = ToArray(target.t)
            });
        }

        private static List<RootObservation> CollectRootObservations(
            JObject arguments,
            float frameRate,
            IReadOnlyList<KimodoMarkerSampleResult> generatedSamples)
        {
            var observations = new List<RootObservation>();
            JArray constraints = arguments?["constraints"] as JArray;
            foreach (JObject item in constraints?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                int frame = item.Value<int?>("frame") ?? 0;
                JObject rootInput = item["root"] as JObject;
                KimodoMarkerSampleResult sample = generatedSamples?.LastOrDefault(candidate =>
                    candidate != null && candidate.sampleValid && candidate.rootOverride != null &&
                    Mathf.RoundToInt((float)(candidate.sampleTime * Mathf.Max(1f, frameRate))) == frame);
                if (rootInput == null && item["pose"] == null) continue;
                if (sample != null)
                {
                    Vector3 forward = sample.rootOverride.q * Vector3.forward;
                    observations.Add(new RootObservation
                    {
                        Frame = frame, Source = rootInput != null ? "root" : "pose",
                        Position = sample.rootOverride.t, HasY = true,
                        Heading = new Vector2(forward.x, forward.z), HasHeading = true
                    });
                }
                else if (rootInput != null && HasPosition(rootInput))
                {
                    JArray p = (JArray)rootInput["position"];
                    JArray r = rootInput["rotation"] as JArray;
                    Vector3 forward = r?.Count == 3
                        ? Quaternion.Euler(r[0].Value<float>(), r[1].Value<float>(), r[2].Value<float>()) * Vector3.forward
                        : Vector3.forward;
                    observations.Add(new RootObservation
                    {
                        Frame = frame, Source = "root", HasY = true,
                        Position = new Vector3(p[0].Value<float>(), p[1].Value<float>(), p[2].Value<float>()),
                        Heading = new Vector2(forward.x, forward.z), HasHeading = r?.Count == 3
                    });
                }
            }
            return observations;
        }

        private static bool HasPosition(JObject value)
        {
            return value?["position"] is JArray array && array.Count >= 3;
        }

        private static bool HasWarning(JObject value)
        {
            return string.Equals(value?.Value<string>("status"), "warning", StringComparison.OrdinalIgnoreCase);
        }

        private static void AddIssue(JArray issues, string code, int frame, string message, string previousSource, string currentSource)
        {
            issues.Add(new JObject
            {
                ["code"] = code,
                ["frame"] = frame,
                ["message"] = message,
                ["previous_source"] = previousSource,
                ["current_source"] = currentSource
            });
        }

        private static JArray ToArray(Vector3 value) => new JArray(value.x, value.y, value.z);
    }
}





