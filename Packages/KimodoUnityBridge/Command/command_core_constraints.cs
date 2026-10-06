using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KimodoUnityBridge;
using KimodoBridge;
using KimodoBridge.Editor;
using TimelineInject;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;

namespace KimodoUnityBridge.Command

{
    internal static partial class command_context
    {
        private static List<KimodoMarkerSampleResult> BuildPoseConstraints(
            JObject arguments,
            string modelName,
            Avatar targetAvatar,
            int frameCount,
            float frameRate,
            int durationFrames,
            GameObject sceneCharacterRoot,
            TimelineCharacterRecord sourceCharacter,
            out List<string> poseWarnings)
        {
            poseWarnings = new List<string>();
            if (arguments?["constraints"] == null)
            {
                return new List<KimodoMarkerSampleResult>();
            }
            if (arguments["constraints"] is not JArray constraints)
            {
                throw new InvalidOperationException("constraints must be an array.");
            }
            var samples = new List<KimodoMarkerSampleResult>(constraints.Count * 3);
            var pathSamples = new List<KimodoMarkerSampleResult>();
            RetargetSkeleton targetCache = null;
            TimelineSessionRecord session = RequireCurrentTimelineSession();
            double originalSessionTime = session.Director.time;
            try
            {
                if (constraints.Count > 0 &&
                    !KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(
                        targetAvatar,
                        "KimodoCommandPoseConstraints",
                        out targetCache,
                        out string cacheError))
                {
                    throw new InvalidOperationException($"Build pose constraint target failed: {cacheError}");
                }

                float targetRootHeight = 0f;
                if (targetCache != null &&
                    targetCache.GetBonePose(HumanBodyBones.Hips, out Vector3 initialHipsPosition, out _))
                {
                    targetRootHeight = initialHipsPosition.y;
                }

                var pointFrames = new HashSet<int>();
                var pathFrames = new HashSet<int>();
                var occupiedPathFrames = new HashSet<int>();
                string frameError = ValidateUniqueConstraintFrames(constraints, durationFrames);
                if (!string.IsNullOrEmpty(frameError)) throw new InvalidOperationException(frameError);
                foreach (JObject item in constraints.OfType<JObject>())
                    if (item.Property("inout") == null && item["root_path"] == null)
                        pointFrames.Add(RequiredNonNegativeFrame(item, "frame"));
                for (int i = 0; i < constraints.Count; i++)
                {
                    if (constraints[i] is JObject item && item["root_path"] is JObject rootPath)
                    {
                        int startFrame = item.Value<int?>("frame") ?? 0;
                        if (startFrame < 0 || startFrame >= durationFrames)
                        {
                            throw new InvalidOperationException(
                                $"constraints[{i}].frame must be within [0,{durationFrames}).");
                        }
                        PoseReference reference = RequirePoseReference(rootPath["path"] as JObject);
                        if (!pathFrames.Add(startFrame))
                            throw new InvalidOperationException($"constraints[{i}].root_path reuses frame {startFrame}; command constraints are not merged.");
                        pathSamples.AddRange(BuildRootPathConstraintsSparse(
                            RequirePathMarker(reference).PathData,
                            i,
                            startFrame,
                            durationFrames,
                            targetRootHeight,
                            targetCache != null ? Mathf.Max(1e-6f, targetCache.humanScale) : 1f,
                            pointFrames,
                            occupiedPathFrames));
                    }
                }
                for (int i = 0; i < constraints.Count; i++)
                {
                    if (constraints[i] is not JObject constraint)
                    {
                        throw new InvalidOperationException($"constraints[{i}] must be an object.");
                    }
                    if (constraint.Property("inout") != null) continue;
                    if (constraint["root_path"] is JObject)
                    {
                        if (constraint.Properties().Any(property =>
                            property.Name != "frame" && property.Name != "root_path"))
                        {
                            throw new InvalidOperationException(
                                $"constraints[{i}] root_path cannot be combined with point constraint fields.");
                        }
                        continue;
                    }
                    int relativeFrame = RequiredNonNegativeFrame(constraint, "frame");
                    if (relativeFrame >= durationFrames)
                    {
                        throw new InvalidOperationException($"constraints[{i}].frame must be within [0,{durationFrames}).");
                    }
                    if (pathFrames.Contains(relativeFrame))
                        throw new InvalidOperationException($"constraints[{i}].frame {relativeFrame} collides with root_path; command constraints are not merged.");
                    double at = relativeFrame / SessionFrameRate;
                    string type = constraint.Value<string>("type")?.Trim().ToLowerInvariant();
                    if (type != "fullbody" && type != "root2d" && type != "root_effect")
                        throw new InvalidOperationException($"constraints[{i}].type must be fullbody, root2d, or root_effect.");
                    ValidatePointConstraintShape(constraint, type, i);
                    JObject canonical = constraint.DeepClone() as JObject;
                    KimodoMarkerSampleResult sample = BuildMixConstraint(
                        canonical, targetCache, modelName, frameRate, at, i,
                        sceneCharacterRoot, sourceCharacter, poseWarnings);
                    samples.Add(sample);
                }
                // Root paths remain independent samples. No command-side row is
                // merged with a point constraint or overwritten by frame order.
                samples.AddRange(pathSamples);
            }
            finally
            {
                session.Director.time = originalSessionTime;
                session.Director.Evaluate();
                targetCache?.Dispose();
            }
            return samples;
        }

        internal static string ValidateUniqueConstraintFrames(JArray constraints, int durationFrames)
        {
            if (constraints == null) return string.Empty;
            var frames = new HashSet<int>();
            for (int i = 0; i < constraints.Count; i++)
            {
                if (constraints[i] is not JObject item || item.Property("inout") != null || item["root_path"] is JObject)
                    continue;
                int frame = RequiredNonNegativeFrame(item, "frame");
                if (frame >= durationFrames)
                    return $"constraints[{i}].frame must be within [0,{durationFrames}).";
                if (!frames.Add(frame))
                    return $"constraints[{i}].frame {frame} duplicates another point constraint; command constraints are not merged.";
            }
            return string.Empty;
        }

        private static KimodoMarkerSampleResult BuildRootEffectConstraint(
            JObject value,
            RetargetSkeleton targetCache,
            float frameRate,
            double sampleTime,
            int constraintIndex,
            GameObject sceneCharacterRoot,
            TimelineCharacterRecord sourceCharacter,
            List<string> poseWarnings)
        {
            if (value?["root2d"] is not JObject root2D)
            {
                throw new InvalidOperationException(
                    $"constraints[{constraintIndex}].root_effect requires root2d.");
            }
            KimodoMarkerSampleResult result = BuildRoot2DConstraint(
                value, sampleTime, constraintIndex, targetCache, sceneCharacterRoot, sourceCharacter, poseWarnings);
            result.constraintMode = "mix";
            result.enableMask = new KimodoConstraintMask
            {
                rootPosition = true,
                rootHeading = true
            };
            // Keep the complete support sample validity captured by the
            // source pose.  RootEffect adds explicit root/effect channels; it
            // must not discard sampled muscle, rootTQ, or footTQ validity.
            result.validMask ??= new KimodoConstraintMask();
            result.validMask.rootPosition = true;
            result.validMask.rootHeading = true;
            // Keep every sampled effector in the support payload. Explicit
            // RootEffect targets below replace only the named channels.
            result.effectors ??= new KimodoConstraintEffectors();

            string[] names = { "left_hand", "right_hand", "left_foot", "right_foot" };
            string[] types = { "left-hand", "right-hand", "left-foot", "right-foot" };
            bool anyEffector = false;
            JObject effect = value["effect"] as JObject;
            if (effect == null)
            {
                throw new InvalidOperationException($"constraints[{constraintIndex}].root_effect requires effect.");
            }
            for (int i = 0; i < names.Length; i++)
            {
                if (effect[names[i]] is not JObject effectorValue)
                {
                    continue;
                }

                KimodoMarkerSampleResult effector = BuildEffectorTargetConstraint(
                    effectorValue, types[i], sampleTime, constraintIndex);
                CopyEffector(result, effector, types[i]);
                anyEffector = true;
            }

            if (!anyEffector)
            {
                throw new InvalidOperationException(
                    $"constraints[{constraintIndex}].root_effect requires at least one end effector.");
            }

            // Validate the complete RootEffect sample from one unchanged support
            // pose. Solving each effector independently on the same skeleton would
            // make later targets depend on earlier solver mutations.
            if (!KimodoConstraintPosePipeline.TryApply(
                    result, frameRate, targetCache, out _, out _, out string validationError))
            {
                poseWarnings?.Add(
                    $"constraints[{constraintIndex}].root_effect preflight warning: {validationError}");
            }
            else
            {
                try
                {
                    ValidateSolvedRootEffectors(result, targetCache, effect, constraintIndex);
                }
                catch (InvalidOperationException warning)
                {
                    poseWarnings?.Add(warning.Message);
                }
            }

            result.enabled = true;
            return result;
        }

        private static void ValidateSolvedRootEffectors(
            KimodoMarkerSampleResult result,
            RetargetSkeleton targetCache,
            JObject effect,
            int constraintIndex)
        {
            string[] names = { "left_hand", "right_hand", "left_foot", "right_foot" };
            HumanBodyBones[] bones =
            {
                HumanBodyBones.LeftHand,
                HumanBodyBones.RightHand,
                HumanBodyBones.LeftFoot,
                HumanBodyBones.RightFoot
            };
            for (int i = 0; i < names.Length; i++)
            {
                if (effect[names[i]] is not JObject)
                {
                    continue;
                }
                if (!targetCache.GetBonePose(bones[i], out Vector3 solvedPosition, out Quaternion solvedRotation))
                {
                    throw new InvalidOperationException(
                        $"constraints[{constraintIndex}].effect.{names[i]} could not be read after IK preflight.");
                }

                KimodoRigidTransform target = names[i] switch
                {
                    "left_hand" => result.effectors.leftHand,
                    "right_hand" => result.effectors.rightHand,
                    "left_foot" => result.effectors.leftFoot,
                    "right_foot" => result.effectors.rightFoot,
                    _ => KimodoRigidTransform.Identity
                };
                float positionError = Vector3.Distance(solvedPosition, target.t);
                float rotationError = target.enableRotation
                    ? Quaternion.Angle(solvedRotation, target.q)
                    : 0f;
                if (positionError > 0.03f || rotationError > 15f)
                {
                    throw new InvalidOperationException(
                        $"constraints[{constraintIndex}].effect.{names[i]} is unreachable " +
                        $"(position_error={positionError.ToString("0.####", CultureInfo.InvariantCulture)}, " +
                        $"rotation_error_degrees={rotationError.ToString("0.##", CultureInfo.InvariantCulture)}).");
                }
            }
        }

        private static KimodoMarkerSampleResult BuildMixConstraint(
            JObject value,
            RetargetSkeleton targetCache,
            string modelName,
            float frameRate,
            double sampleTime,
            int constraintIndex,
            GameObject sceneCharacterRoot,
            TimelineCharacterRecord sourceCharacter,
            List<string> poseWarnings)
        {
            string type = value?.Value<string>("type")?.Trim().ToLowerInvariant();
            KimodoMarkerSampleResult result;
            switch (type)
            {
                case "fullbody":
                    result = BuildReferencedPoseConstraint(
                        value, "fullbody", targetCache, modelName, frameRate, sampleTime,
                        constraintIndex, sceneCharacterRoot, sourceCharacter, poseWarnings);
                    break;
                case "root2d":
                    result = BuildRoot2DConstraint(
                        value, sampleTime, constraintIndex, targetCache, sceneCharacterRoot, sourceCharacter, poseWarnings);
                    break;
                case "root_effect":
                    result = BuildRootEffectConstraint(
                        value, targetCache, frameRate, sampleTime, constraintIndex,
                        sceneCharacterRoot, sourceCharacter, poseWarnings);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"constraints[{constraintIndex}].type must be fullbody, root2d, or root_effect.");
            }

            // Every command point is stored as one canonical mix sample. The
            // complete sampled support payload remains intact; only the
            // explicit channels selected by the command are enabled.
            result.constraintMode = "mix";
            result.sampleTime = sampleTime;
            // Build from a complete support sample first, then expose only the
            // channels authored by this command as the mix intent. The support
            // payload remains available for later edits and inspection.
            result.enableMask = BuildMixEnableMask(type, result);
            result.validMask ??= new KimodoConstraintMask();
            result.effectors ??= new KimodoConstraintEffectors();
            result.enabled = true;
            return result;
        }

        private static KimodoConstraintMask BuildMixEnableMask(
            string type,
            KimodoMarkerSampleResult sample)
        {
            var mask = new KimodoConstraintMask();
            switch (type)
            {
                case "fullbody":
                    return KimodoConstraintMask.ForType("fullbody");
                case "root2d":
                    mask.rootPosition = true;
                    mask.rootHeading = true;
                    break;
                case "root_effect":
                    mask.rootPosition = true;
                    mask.rootHeading = true;
                    mask.leftHand = sample?.enableMask?.leftHand == true;
                    mask.rightHand = sample?.enableMask?.rightHand == true;
                    mask.leftFoot = sample?.enableMask?.leftFoot == true;
                    mask.rightFoot = sample?.enableMask?.rightFoot == true;
                    break;
            }
            return mask;
        }

        private static void ValidatePointConstraintShape(JObject constraint, string type, int index)
        {
            var allowed = new HashSet<string>(StringComparer.Ordinal)
            {
                "frame", "type"
            };
            if (type == "fullbody") allowed.Add("pose");
            if (type == "root2d")
            {
                allowed.Add("pose");
                allowed.Add("root2d");
            }
            if (type == "root_effect")
            {
                allowed.Add("pose");
                allowed.Add("root2d");
                allowed.Add("effect");
            }

            string unexpected = constraint.Properties()
                .Select(property => property.Name)
                .FirstOrDefault(name => !allowed.Contains(name));
            if (unexpected != null)
            {
                throw new InvalidOperationException(
                    $"constraints[{index}] field '{unexpected}' is not valid for type '{type}'.");
            }

            if (constraint["pose"] != null && constraint["pose"] is not JObject)
                throw new InvalidOperationException($"constraints[{index}].pose must be an object.");
            if (type == "fullbody" && constraint["pose"] is not JObject)
                throw new InvalidOperationException($"constraints[{index}].pose is required for fullbody.");
            if (type == "root2d" && constraint["root2d"] is not JObject)
                throw new InvalidOperationException($"constraints[{index}].root2d is required for root2d.");
            if (type == "root_effect")
            {
                if (constraint["root2d"] is not JObject)
                    throw new InvalidOperationException($"constraints[{index}].root2d is required for root_effect.");
                if (constraint["effect"] is not JObject)
                    throw new InvalidOperationException($"constraints[{index}].effect is required for root_effect.");
            }
        }

        private static void CopyEffector(
            KimodoMarkerSampleResult destination,
            KimodoMarkerSampleResult source,
            string type)
        {
            switch (type)
            {
                case "left-hand":
                    destination.effectors.leftHand = source.effectors.leftHand?.Clone() ?? KimodoRigidTransform.Identity;
                    destination.enableMask.leftHand = true;
                    destination.validMask.leftHand = true;
                    break;
                case "right-hand":
                    destination.effectors.rightHand = source.effectors.rightHand?.Clone() ?? KimodoRigidTransform.Identity;
                    destination.enableMask.rightHand = true;
                    destination.validMask.rightHand = true;
                    break;
                case "left-foot":
                    destination.effectors.leftFoot = source.effectors.leftFoot?.Clone() ?? KimodoRigidTransform.Identity;
                    destination.enableMask.leftFoot = true;
                    destination.validMask.leftFoot = true;
                    break;
                case "right-foot":
                    destination.effectors.rightFoot = source.effectors.rightFoot?.Clone() ?? KimodoRigidTransform.Identity;
                    destination.enableMask.rightFoot = true;
                    destination.validMask.rightFoot = true;
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported root_effect effector '{type}'.");
            }
        }

        private static KimodoMarkerSampleResult BuildEffectorTargetConstraint(
            JObject value,
            string type,
            double sampleTime,
            int constraintIndex)
        {
            if (value?["position"] is not JArray position)
                throw new InvalidOperationException($"constraints[{constraintIndex}].effect.{type.Replace('-', '_')}.position is required.");
            Vector3 targetPosition = ReadVector3(position, $"constraints[{constraintIndex}].effect.{type}.position");
            Quaternion targetRotation = Quaternion.identity;
            bool hasRotation = value["rotation"] != null;
            if (hasRotation)
                targetRotation = ReadQuaternion(value["rotation"] as JArray, $"constraints[{constraintIndex}].effect.{type}.rotation");

            KimodoMarkerSampleResult result = new KimodoMarkerSampleResult
            {
                constraintMode = type,
                sampleTime = sampleTime,
                enableMask = KimodoConstraintMask.ForType(type),
                validMask = KimodoConstraintMask.ForType(type),
                effectors = new KimodoConstraintEffectors(),
                enabled = true
            };
            KimodoRigidTransform target = KimodoRigidTransform.Identity;
            target.t = targetPosition;
            target.q = hasRotation ? targetRotation : Quaternion.identity;
            target.enableRotation = hasRotation;
            switch (type)
            {
                case "left-hand": result.effectors.leftHand = target; break;
                case "right-hand": result.effectors.rightHand = target; break;
                case "left-foot": result.effectors.leftFoot = target; break;
                case "right-foot": result.effectors.rightFoot = target; break;
                default: throw new InvalidOperationException($"Unsupported effect type '{type}'.");
            }
            return result;
        }

        private static IEnumerable<KimodoMarkerSampleResult> BuildRootPathConstraintsSparse(
            KimodoRootPathData path,
            int constraintIndex,
            int startFrame,
            int durationFrames,
            float targetRootHeight,
            float targetHumanScale,
            ISet<int> explicitPointFrames,
            ISet<int> occupiedPathFrames)
        {
            List<KimodoRootPathKnot> knots = path?.knots;
            if (path == null || path.length < 0f || knots == null || knots.Count < 1 || knots.Any(knot => knot == null))
            {
                throw new InvalidOperationException(
                    $"constraints[{constraintIndex}].root_path references invalid path data.");
            }
            float sourceLength = EstimatePathLength(knots);
            bool hasStoredHeadings = knots.All(knot => knot.hasHeading);
            if (sourceLength <= 1e-6f && (path.length > 1e-6f || !hasStoredHeadings))
            {
                throw new InvalidOperationException(
                    $"constraints[{constraintIndex}].root_path has zero source length without reusable headings.");
            }
            float sourceHumanScale = path.sourceHumanScale > 1e-6f ? path.sourceHumanScale : 1f;
            float retargetScale = Mathf.Max(1e-6f, targetHumanScale) / sourceHumanScale;
            float scale = sourceLength > 1e-6f
                ? path.length / sourceLength * retargetScale
                : retargetScale;
            int endFrame = durationFrames - 1;
            var sampleFrames = new SortedSet<int> { startFrame, endFrame };
            foreach (int frame in sampleFrames)
            {
                if (frame < startFrame || frame > endFrame)
                {
                    continue;
                }
                if (!occupiedPathFrames.Add(frame))
                {
                    throw new InvalidOperationException("root_path frame ranges cannot overlap.");
                }
            }

            var result = new List<KimodoMarkerSampleResult>(sampleFrames.Count);
            foreach (int frame in sampleFrames)
            {
                if (frame < startFrame || frame > endFrame)
                {
                    continue;
                }
                float progress = endFrame <= startFrame ? 0f : (frame - startFrame) / (float)(endFrame - startFrame);
                float pathTime = path.inverse ? 1f - progress : progress;
                EvaluatePath(knots, pathTime, out Vector2 position, out Vector2 tangent);
                position *= scale;
                if (hasStoredHeadings)
                {
                    tangent = EvaluatePathHeading(knots, pathTime);
                }
                if (path.inverse) tangent = -tangent;
                if (tangent.sqrMagnitude <= 1e-8f)
                {
                    throw new InvalidOperationException(
                        $"constraints[{constraintIndex}].root_path has a zero heading at frame {frame}.");
                }
                if (explicitPointFrames != null && explicitPointFrames.Contains(frame))
                    throw new InvalidOperationException($"constraints[{constraintIndex}].root_path collides with point frame {frame}; command constraints are not merged.");
                result.Add(CreateRootOverrideSample(
                    frame,
                    // Root paths constrain the planar trajectory and heading.
                    // Vertical placement remains the canonical hips height;
                    // path data never carries a separate delta-Y channel.
                    new Vector3(position.x, targetRootHeight, position.y),
                    new Vector3(tangent.x, 0f, tangent.y).normalized));
            }
            return result;
        }

        private static Vector2 EvaluatePathHeading(IReadOnlyList<KimodoRootPathKnot> knots, float time)
        {
            if (knots.Count == 1)
            {
                return knots[0].heading.sqrMagnitude > 1e-8f ? knots[0].heading.normalized : Vector2.up;
            }
            float scaled = Mathf.Clamp01(time) * (knots.Count - 1);
            int segment = Mathf.Min(Mathf.FloorToInt(scaled), knots.Count - 2);
            float t = segment == knots.Count - 2 && time >= 1f ? 1f : scaled - segment;
            Vector2 heading = Vector2.Lerp(knots[segment].heading, knots[segment + 1].heading, t);
            return heading.sqrMagnitude > 1e-8f ? heading.normalized : Vector2.up;
        }

        private static float EstimatePathLength(IReadOnlyList<KimodoRootPathKnot> knots)
        {
            if (knots.Count < 2) return 0f;
            const int samplesPerSegment = 16;
            float length = 0f;
            EvaluatePath(knots, 0f, out Vector2 previous, out _);
            int sampleCount = (knots.Count - 1) * samplesPerSegment;
            for (int i = 1; i <= sampleCount; i++)
            {
                EvaluatePath(knots, i / (float)sampleCount, out Vector2 position, out _);
                length += Vector2.Distance(previous, position);
                previous = position;
            }
            return length;
        }

        private static void EvaluatePath(
            IReadOnlyList<KimodoRootPathKnot> knots,
            float time,
            out Vector2 position,
            out Vector2 tangent)
        {
            if (knots.Count == 1)
            {
                position = knots[0].position;
                tangent = knots[0].hasHeading ? knots[0].heading : Vector2.up;
                return;
            }
            float scaled = Mathf.Clamp01(time) * (knots.Count - 1);
            int segment = Mathf.Min(Mathf.FloorToInt(scaled), knots.Count - 2);
            float t = segment == knots.Count - 2 && time >= 1f ? 1f : scaled - segment;
            KimodoRootPathKnot first = knots[segment];
            KimodoRootPathKnot second = knots[segment + 1];
            Vector2 chord = second.position - first.position;
            Vector2 p0 = first.position;
            Vector2 p1 = p0 + (first.hasTangentOut ? first.tangentOut : chord / 3f);
            Vector2 p3 = second.position;
            Vector2 p2 = p3 + (second.hasTangentIn ? second.tangentIn : -chord / 3f);
            position = EvaluateBezier(p0, p1, p2, p3, t);
            tangent = EvaluateBezierTangent(p0, p1, p2, p3, t);
            if (tangent.sqrMagnitude <= 1e-8f) tangent = chord;
        }

        private static KimodoMarkerSampleResult CreateRootOverrideSample(
            int frame,
            Vector3 position,
            Vector3 heading)
        {
            return new KimodoMarkerSampleResult
            {
                constraintMode = "mix",
                sampleTime = frame / SessionFrameRate,
                rootOverride = new KimodoRigidTransform
                {
                    t = position,
                    q = Quaternion.LookRotation(heading, Vector3.up)
                },
                enableMask = new KimodoConstraintMask { rootPosition = true, rootHeading = true },
                validMask = new KimodoConstraintMask { rootPosition = true, rootHeading = true }
            };
        }

        private static Vector2 EvaluateBezier(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
        {
            float oneMinus = 1f - t;
            return oneMinus * oneMinus * oneMinus * p0 +
                3f * oneMinus * oneMinus * t * p1 +
                3f * oneMinus * t * t * p2 +
                t * t * t * p3;
        }

        private static Vector2 EvaluateBezierTangent(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
        {
            float oneMinus = 1f - t;
            return 3f * oneMinus * oneMinus * (p1 - p0) +
                6f * oneMinus * t * (p2 - p1) +
                3f * t * t * (p3 - p2);
        }

        private static KimodoMarkerSampleResult BuildRoot2DConstraint(
            JObject value,
            double sampleTime,
            int constraintIndex,
            RetargetSkeleton targetCache,
            GameObject sceneCharacterRoot,
            TimelineCharacterRecord sourceCharacter,
            List<string> poseWarnings)
        {
            KimodoMarkerSampleResult poseSample = ResolveConstraintPose(
                value, "root2d", targetCache, sceneCharacterRoot, sourceCharacter, poseWarnings, constraintIndex, sampleTime);
            Vector3 rootPosition = Vector3.zero;
            Quaternion rootRotation = Quaternion.identity;
            if (poseSample != null)
            {
                GetRootTransform(poseSample, out rootPosition, out rootRotation);
            }
            JObject root2D = value?["root2d"] as JObject;
            bool hasPosition = root2D?["position"] != null;
            bool hasHeading = root2D?["heading"] != null;
            bool hasRootPose = poseSample != null;
            if (hasPosition != hasHeading)
            {
                throw new InvalidOperationException($"constraints[{constraintIndex}].root2d requires position and heading together.");
            }
            if (hasPosition)
            {
                Vector2 position = RequiredVector2(root2D, "position");
                Vector2 heading = RequiredVector2(root2D, "heading");
                if (heading.sqrMagnitude <= 1e-8f)
                {
                    throw new InvalidOperationException($"constraints[{constraintIndex}].root2d.heading must be non-zero.");
                }
                rootPosition = KimodoMotionMath.ApplyPlanarPosition(
                    rootPosition,
                    new Vector3(position.x, 0f, position.y));
                rootRotation = KimodoMotionMath.ApplyPlanarHeading(
                    rootRotation,
                    Quaternion.LookRotation(new Vector3(heading.x, 0f, heading.y), Vector3.up));
            }
            // A Root2D marker still needs the complete support pose for the
            // editor preview. The Root2D mode controls export/solve channels;
            // it must not discard the sampled muscles, feet, or root height.
            // This is the same shape produced by the Timeline right-click
            // marker path: sampled values are retained, while only the
            // authored enable mask is reduced to Root2D channels.
            KimodoMarkerSampleResult result = poseSample?.Clone() ?? new KimodoMarkerSampleResult();
            result.constraintMode = "mix";
            result.sampleTime = sampleTime;
            result.rootOverride = new KimodoRigidTransform
            {
                t = rootPosition,
                q = rootRotation
            };
            result.enableMask = KimodoConstraintMask.ForType("root2d");
            result.validMask ??= new KimodoConstraintMask();
            if (hasPosition || hasRootPose)
            {
                result.validMask.rootPosition = true;
                result.validMask.rootHeading = true;
            }
            result.enabled = true;
            return result;
        }

        private static KimodoMarkerSampleResult BuildReferencedPoseConstraint(
            JObject value,
            string constraintType,
            RetargetSkeleton targetCache,
            string modelName,
            float frameRate,
            double sampleTime,
            int constraintIndex,
            GameObject sceneCharacterRoot,
            TimelineCharacterRecord sourceCharacter,
            List<string> poseWarnings)
        {
            KimodoMarkerSampleResult sourceResult = ResolveConstraintPose(
                value,
                constraintType.Replace('-', '_'),
                targetCache,
                sceneCharacterRoot,
                sourceCharacter,
                poseWarnings,
                constraintIndex,
                sampleTime);
            return BuildModelNativeConstraintSample(
                sourceResult, constraintType, targetCache, modelName, frameRate, sampleTime);
        }

        private static KimodoMarkerSampleResult ResolveConstraintPose(
            JObject value,
            string constraintType,
            RetargetSkeleton targetCache,
            GameObject sceneCharacterRoot,
            TimelineCharacterRecord sourceCharacter,
            List<string> poseWarnings,
            int constraintIndex,
            double sampleTime)
        {
            if (value?["pose"] is JObject poseReference)
            {
                return ReadPoseSample(
                    RequirePoseReference(poseReference),
                    $"constraints[{constraintIndex}].{constraintType}");
            }

            if (sourceCharacter != null)
            {
                // A command point without an explicit Pose is still created
                // from a complete scene-time support sample. This is the same
                // sampling boundary used by pose_get; the later mix mask only
                // expresses the channels authored by this command.
                return CaptureSampleResult(
                    RequireCurrentTimelineSession(),
                    sourceCharacter,
                    sampleTime);
            }

            if (!TryCreateAvatarRestPoseSample(
                targetCache,
                sceneCharacterRoot,
                    alignToSceneRoot: sceneCharacterRoot != null,
                    out KimodoMarkerSampleResult restPose,
                    out string error))
            {
                throw new InvalidOperationException(
                    $"constraints[{constraintIndex}].{constraintType} omitted pose and Avatar rest pose could not be created: {error}");
            }

            poseWarnings?.Add(
                $"constraints[{constraintIndex}].{constraintType} omitted pose; used Avatar skeleton rest pose.");
            return restPose;
        }

        internal static bool TryCreateAvatarRestPoseSample(
            RetargetSkeleton targetCache,
            GameObject sceneRoot,
            bool alignToSceneRoot,
            out KimodoMarkerSampleResult result,
            out string error)
        {
            result = null;
            error = string.Empty;
            if (!KimodoRetargetAvatarUtility.ValidateRetargetSkeleton(targetCache, out error))
            {
                return false;
            }

            KimodoRetargetClipSamplingUtility.ResetRetargetSkeletonPose(targetCache);
            if (alignToSceneRoot && sceneRoot != null && targetCache.root != null)
            {
                targetCache.root.transform.SetPositionAndRotation(
                    sceneRoot.transform.position,
                    sceneRoot.transform.rotation);
                targetCache.root.transform.localScale = sceneRoot.transform.lossyScale;
                targetCache.animator.Rebind();
                targetCache.animator.Update(0f);
            }

            if (!KimodoRetargetSamplingUtility.TryCaptureSampleData(
                    targetCache,
                    out MuscleSample sampleData,
                    out KimodoConstraintMask validMask,
                    out error) ||
                sampleData == null || !sampleData.IsValid)
            {
                if (string.IsNullOrEmpty(error)) error = "Avatar rest pose sampling returned no valid MuscleSample.";
                return false;
            }

            result = new KimodoMarkerSampleResult
            {
                sampleData = sampleData,
                constraintMode = "fullbody",
                enabled = true,
                validMask = validMask ?? new KimodoConstraintMask(),
                enableMask = KimodoConstraintMask.ForType("fullbody"),
                effectors = new KimodoConstraintEffectors()
            };
            KimodoRetargetMarkerSamplingUtility.CaptureWorldTargets(targetCache, result);
            return true;
        }

        private static KimodoMarkerSampleResult BuildModelNativeConstraintSample(
            KimodoMarkerSampleResult sourceResult,
            string constraintType,
            RetargetSkeleton modelSkeleton,
            string modelName,
            float frameRate,
            double sampleTime)
        {
            if (sourceResult?.sampleData == null || !sourceResult.sampleData.IsValid)
            {
                throw new InvalidOperationException("Pose constraint has no valid 70-value sampleData payload.");
            }
            KimodoMarkerSampleResult solveInput = sourceResult.Clone();
            ConfigureConstraintIntent(solveInput, constraintType, sampleTime);
            if (!KimodoConstraintPosePipeline.TryApply(
                    solveInput, frameRate, modelSkeleton,
                    out BoneSample boneSample, out MuscleSample targetMuscleSample, out string retargetError))
            {
                throw new InvalidOperationException($"Retarget pose constraint failed: {retargetError}");
            }
            if (!KimodoRetargetMarkerSamplingUtility.TryBuildMarkerSampleResultFromBoneSample(
                    boneSample, modelSkeleton, modelName, constraintType, sampleTime,
                    out KimodoMarkerSampleResult converted, out string convertError))
            {
                throw new InvalidOperationException($"Convert pose constraint failed: {convertError}");
            }
            converted.sampleData = targetMuscleSample?.Clone() ?? new MuscleSample();
            // Preserve the complete sampled support root through retargeting.
            // It remains available for inspection and later edits even when
            // this command does not enable the root channel.
            if (solveInput.rootOverride != null)
            {
                converted.rootOverride = solveInput.rootOverride.Clone();
                converted.validMask.rootPosition = solveInput.validMask?.rootPosition == true;
                converted.validMask.rootHeading = solveInput.validMask?.rootHeading == true;
            }
            ConfigureConstraintIntent(converted, constraintType, sampleTime);
            return converted;
        }

        private static void ConfigureConstraintIntent(
            KimodoMarkerSampleResult sample,
            string constraintType,
            double sampleTime)
        {
            string type = (constraintType ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-');
            KimodoConstraintMask valid = KimodoConstraintMask.FromSample(sample);
            if (type == "fullbody" && !valid.muscle)
            {
                throw new InvalidOperationException("FullBody pose constraint requires valid muscle data.");
            }
            bool effectorValid = type switch
            {
                "left-hand" => valid.leftHand,
                "right-hand" => valid.rightHand,
                "left-foot" => valid.leftFoot,
                "right-foot" => valid.rightFoot,
                "fullbody" => true,
                _ => false
            };
            if (!effectorValid)
            {
                throw new InvalidOperationException($"Pose constraint '{type}' has no valid target data.");
            }
            sample.constraintMode = type;
            sample.enableMask = KimodoConstraintMask.ForType(type);
            sample.sampleTime = sampleTime;
            sample.enabled = true;
        }

        private static JObject Route(string intent, string command, string next = null)
        {
            var route = new JObject { ["intent"] = intent, ["command"] = command };
            if (!string.IsNullOrWhiteSpace(next)) route["next"] = next;
            return route;
        }

        private static bool TrySampleDirectSkeletonConstraint(
            TimelineCharacterRecord source,
            RetargetSkeleton targetCache,
            string modelName,
            string constraintType,
            double sampleTime,
            out KimodoMarkerSampleResult sample,
            out string error)
        {
            sample = null;
            error = string.Empty;
            if (source?.Root == null || !KimodoRetargetAvatarUtility.ValidateRetargetSkeleton(targetCache, out error))
            {
                return false;
            }

            Transform[] allSourceTransforms = source.Root.GetComponentsInChildren<Transform>(true);
            Transform[] rootCandidates = allSourceTransforms
                .Where(transform => string.Equals(
                    transform.name, targetCache.canonicalRootBoneName, StringComparison.Ordinal))
                .ToArray();
            Transform sourceSkeletonRoot = rootCandidates.Length == 1 ? rootCandidates[0] : null;
            if (sourceSkeletonRoot == null)
            {
                error = $"incompatible_skeleton: source must contain one unambiguous '{targetCache.canonicalRootBoneName}' root bone";
                return false;
            }
            Transform[] sourceTransforms = sourceSkeletonRoot.GetComponentsInChildren<Transform>(true);
            var sourceNames = sourceTransforms.ToDictionary(transform => transform, transform => transform.name);
            var sourceByPath = sourceTransforms.ToDictionary(
                transform => KimodoRetargetAvatarUtility.CalculateTransformPath(
                    transform, sourceSkeletonRoot, targetCache.canonicalRootBoneName, sourceNames),
                transform => transform,
                StringComparer.Ordinal);
            var targetPaths = new HashSet<string>(targetCache.bonePaths, StringComparer.Ordinal);
            string missing = sourceByPath.Keys.FirstOrDefault(path => !targetPaths.Contains(path));
            if (missing != null)
            {
                error = $"incompatible_skeleton: target skeleton is missing source bone path '{missing}'";
                return false;
            }

            KimodoRetargetClipSamplingUtility.ResetRetargetSkeletonPose(targetCache);
            for (int i = 0; i < targetCache.bonePaths.Length; i++)
            {
                if (sourceByPath.TryGetValue(targetCache.bonePaths[i], out Transform sourceTransform) &&
                    targetCache.boneTransforms[i] != null)
                {
                    targetCache.boneTransforms[i].localPosition = sourceTransform.localPosition;
                    targetCache.boneTransforms[i].localRotation = sourceTransform.localRotation;
                }
            }
            BoneSample targetSample = KimodoRetargetSamplingUtility.CaptureBoneSample(targetCache);
            if (!KimodoRetargetMarkerSamplingUtility.TryBuildMarkerSampleResultFromBoneSample(
                    targetSample, targetCache, modelName, constraintType, sampleTime, out sample, out error))
            {
                return false;
            }
            return true;
        }

        internal static List<double> ResolvePoseConstraintTimes(
            int poseCount,
            int frameCount,
            float frameRate,
            IReadOnlyList<double> suppliedTimes)
        {
            if (poseCount < 0)
            {
                throw new InvalidOperationException("pose count cannot be negative.");
            }
            if (suppliedTimes != null)
            {
                if (suppliedTimes.Count != poseCount)
                {
                    throw new InvalidOperationException("times count must match pose_refs count.");
                }
                return new List<double>(suppliedTimes);
            }

            var times = new List<double>(poseCount);
            if (poseCount == 0)
            {
                return times;
            }
            double endTime = KimodoInOutConstraintTools.ResolveConstraintEndSampleTimeSeconds(frameCount, frameRate);
            for (int i = 0; i < poseCount; i++)
            {
                times.Add(poseCount == 1 ? 0.0 : endTime * i / (poseCount - 1));
            }
            return times;
        }

        internal static List<string> ResolvePoseConstraintTypes(
            int poseCount,
            IReadOnlyList<string> suppliedTypes)
        {
            if (suppliedTypes != null && suppliedTypes.Count != poseCount)
            {
                throw new InvalidOperationException("constraint_types count must match pose_refs count.");
            }

            var types = new List<string>(poseCount);
            for (int i = 0; i < poseCount; i++)
            {
                string type = suppliedTypes == null ? "mix" : suppliedTypes[i]?.Trim().ToLowerInvariant();
                if (type != "mix")
                    throw new InvalidOperationException($"constraint_types[{i}] must be mix; legacy fullbody/root2d types were removed.");
                types.Add(type);
            }
            return types;
        }

        private static List<double> ParsePoseTimes(JToken token, int poseCount)
        {
            if (token == null)
            {
                return null;
            }
            if (token is not JArray values || values.Count != poseCount)
            {
                throw new InvalidOperationException("times count must match pose_refs count.");
            }

            var times = new List<double>(values.Count);
            for (int i = 0; i < values.Count; i++)
            {
                if ((values[i].Type != JTokenType.Float && values[i].Type != JTokenType.Integer) ||
                    double.IsNaN(values[i].Value<double>()) ||
                    double.IsInfinity(values[i].Value<double>()))
                {
                    throw new InvalidOperationException($"times[{i}] must be a finite number.");
                }
                times.Add(values[i].Value<double>());
            }
            return times;
        }

        private static List<string> ParsePoseConstraintTypes(JToken token, int poseCount)
        {
            if (token == null)
            {
                return ResolvePoseConstraintTypes(poseCount, null);
            }
            if (token is not JArray values || values.Count != poseCount)
            {
                throw new InvalidOperationException("constraint_types count must match pose_refs count.");
            }
            return ResolvePoseConstraintTypes(
                poseCount,
                values.Select((value, index) => value.Type == JTokenType.String
                    ? value.Value<string>()
                    : throw new InvalidOperationException($"constraint_types[{index}] must be a string.")).ToArray());
        }

        private static bool TrySamplePoseConstraint(
            ResolvedCharacter pose,
            RetargetSkeleton targetCache,
            string modelName,
            string constraintType,
            double sampleTime,
            out KimodoMarkerSampleResult sample,
            out string error)
        {
            sample = null;
            error = string.Empty;
            if (pose.Animator == null || !KimodoRetargetAvatarUtility.ValidateRetargetSkeleton(targetCache, out error))
            {
                return false;
            }

            try
            {
                var humanPose = new HumanPose();
                using (var poseHandler = new HumanPoseHandler(pose.Avatar, pose.Animator.transform))
                {
                    poseHandler.GetHumanPose(ref humanPose);
                }
                KimodoRetargetClipWriter.EnsureHumanPoseMuscles(ref humanPose);
                KimodoRetargetClipSamplingUtility.ResetRetargetSkeletonPose(targetCache);
                targetCache.poseHandler.SetHumanPose(ref humanPose);
                BoneSample targetSample = KimodoRetargetSamplingUtility.CaptureBoneSample(targetCache);
                if (!KimodoRetargetMarkerSamplingUtility.TryBuildMarkerSampleResultFromBoneSample(
                        targetSample,
                        targetCache,
                        modelName,
                        constraintType,
                        sampleTime,
                        out sample,
                        out error))
                {
                    return false;
                }

                if (!KimodoRetargetSamplingUtility.TryCaptureMuscleSample(
                        targetCache,
                        out MuscleSample evaluatedSample,
                        out error))
                {
                    return false;
                }
                sample.sampleData = evaluatedSample;
                sample.enableMask ??= new KimodoConstraintMask();
                sample.enableMask.muscle = true;
                sample.enableMask.rootTQ = true;
                sample.enableMask.leftFootTQ = true;
                sample.enableMask.rightFootTQ = true;
                sample.validMask ??= new KimodoConstraintMask();
                sample.validMask.muscle = true;
                sample.validMask.rootTQ = true;
                sample.validMask.leftFootTQ = true;
                sample.validMask.rightFootTQ = true;

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

    }
}
