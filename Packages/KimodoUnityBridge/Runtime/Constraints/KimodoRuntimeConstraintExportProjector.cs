using System;
using System.Collections.Generic;
using System.Linq;
using KimodoUnityBridge;
using Newtonsoft.Json.Linq;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge
{
    /// <summary>
    /// Projects model-native constraint samples onto the protocol skeleton.
    /// Timeline character samples use ProjectSolvedMuscle after their
    /// Character FK/root/IK pass has completed.
    /// </summary>
    public static class KimodoRuntimeConstraintExportProjector
    {
        public static Func<KimodoMarkerSampleResult, KimodoConstraintProjectedPose> Create(
            string modelName)
        {
            string resolvedModelName = KimodoMotionModelProfiles.NormalizeName(modelName);
            return sample => ProjectProfileNative(sample, resolvedModelName);
        }

        private static KimodoConstraintProjectedPose ProjectProfileNative(
            KimodoMarkerSampleResult sample,
            string modelName)
        {
            if (sample == null || !sample.sampleValid)
            {
                throw new InvalidOperationException("Constraint sample is invalid.");
            }
            string mode = KimodoConstraintInternal.ResolveExecutionMode(sample);
            // Root2D is intrinsically root-only. Ignore stale effector bits
            // that may exist in older SampleResult data; the pose pipeline
            // applies the same mode gate before constructing the IK job.
            bool rootOnly = mode == "root2d"
                ? KimodoConstraintMask.IsActive(sample, "rootposition") && sample.rootOverride != null
                : false;
            if (!rootOnly && (sample?.sampleData == null || !sample.sampleData.IsValid))
            {
                throw new InvalidOperationException("Constraint MuscleSample is invalid.");
            }

            if (!TryBuildProfileCache(modelName, out RetargetSkeleton cache, out string error))
            {
                throw new InvalidOperationException($"Constraint pose projection failed: {error}");
            }

            try
            {
                float frameRate = KimodoMotionModelProfiles.ResolveGenerationFrameRate(modelName);
                if (!KimodoConstraintPosePipeline.TryApply(
                        sample,
                        frameRate,
                        cache,
                        out _,
                        out _,
                        out error))
                {
                    throw new InvalidOperationException($"Constraint pose projection failed: {error}");
                }

                return CaptureProfilePose(modelName, cache);
            }
            finally
            {
                cache.Dispose();
            }
        }

        internal static KimodoConstraintProjectedPose ProjectSolvedMuscle(
            MuscleSample solvedTrackPose,
            string modelName)
        {
            if (solvedTrackPose == null || !solvedTrackPose.IsValid)
            {
                throw new InvalidOperationException("Solved Character MuscleSample is invalid.");
            }

            string resolvedModelName = KimodoMotionModelProfiles.NormalizeName(modelName);
            if (!TryBuildProfileCache(resolvedModelName, out RetargetSkeleton cache, out string error))
            {
                throw new InvalidOperationException($"Constraint profile projection failed: {error}");
            }

            try
            {
                if (!KimodoRetargetSamplingUtility.TrySampleTargetFromSingleMuscleSample(
                        solvedTrackPose,
                        KimodoMotionModelProfiles.ResolveGenerationFrameRate(resolvedModelName),
                        cache,
                        out _,
                        out _,
                        out error))
                {
                    throw new InvalidOperationException($"Constraint profile retarget failed: {error}");
                }

                return CaptureProfilePose(resolvedModelName, cache);
            }
            finally
            {
                cache.Dispose();
            }
        }

        internal static bool TryBuildProfileCache(
            string modelName,
            out RetargetSkeleton cache,
            out string error)
        {
            cache = null;
            if (!KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                    modelName,
                    out Avatar avatar,
                    out error))
            {
                return false;
            }

            return KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(
                avatar,
                "KimodoRuntimeConstraintExportProfile",
                out cache,
                out error);
        }

        internal static KimodoConstraintProjectedPose CaptureProfilePose(
            string modelName,
            RetargetSkeleton cache)
        {
            if (!KimodoProfileSkeletonUtility.TryResolveProfileSkeleton(
                    modelName,
                    cache,
                    out string[] jointNames,
                    out _,
                    out Transform[] joints,
                    out string error))
            {
                throw new InvalidOperationException($"Constraint profile skeleton failed: {error}");
            }

            if (joints == null || joints.Length == 0 || joints[0] == null)
            {
                throw new InvalidOperationException("Constraint profile skeleton has no Hips joint after projection.");
            }

            var jointPositions = new Vector3[joints.Length];
            var jointRotations = new Quaternion[joints.Length];
            var localJointAngles = new List<Vector3>(joints.Length);
            for (int i = 0; i < joints.Length; i++)
            {
                Transform joint = joints[i];
                Quaternion rotation = i == 0 ? joint.rotation : joint.localRotation;
                jointPositions[i] = joint.position;
                jointRotations[i] = joint.rotation;
                localJointAngles.Add(KimodoConstraintRotationUtility.QuaternionToAxisAngleVector(rotation));
            }

            return new KimodoConstraintProjectedPose
            {
                profileRootPosition = joints[0].position,
                jointNames = jointNames,
                jointPositions = jointPositions,
                jointRotations = jointRotations,
                localJointAngles = localJointAngles,
            };
        }

    }

    /// <summary>Compares generated profile motion with the constraints used to generate it.</summary>
    public static class KimodoGenerationConstraintLossComparer
    {
        private static readonly (string Mask, string Joint, string Output)[] Effectors =
        {
            ("leftHand", "LeftHand", "left_hand"),
            ("rightHand", "RightHand", "right_hand"),
            ("leftFoot", "LeftFoot", "left_foot"),
            ("rightFoot", "RightFoot", "right_foot")
        };

        public static JObject Compare(
            KimodoRawMotionData motion,
            string modelName,
            IReadOnlyList<KimodoMarkerSampleResult> constraints,
            int trimStartFrame,
            float frameRate)
        {
            var frames = new JArray();
            var errors = new JArray();
            if (motion == null || constraints == null || constraints.Count == 0)
            {
                return new JObject
                {
                    ["status"] = "not_applicable",
                    ["metric"] = "position errors in meters; rotation errors in degrees",
                    ["compared_count"] = 0,
                    ["frames"] = frames
                };
            }

            RetargetSkeleton cache = null;
            try
            {
                if (!KimodoRuntimeConstraintExportProjector.TryBuildProfileCache(
                        modelName, out cache, out string cacheError))
                {
                    throw new InvalidOperationException(cacheError);
                }
                if (!KimodoRawMotionUtility.TryCreatePlaybackBinding(
                        motion, modelName, cache.skeletonRoot,
                        out KimodoRawMotionPlaybackBinding binding, out string bindingError))
                {
                    throw new InvalidOperationException(bindingError);
                }

                Func<KimodoMarkerSampleResult, KimodoConstraintProjectedPose> projectConstraint =
                    KimodoRuntimeConstraintExportProjector.Create(modelName);
                for (int i = 0; i < constraints.Count; i++)
                {
                    KimodoMarkerSampleResult sample = constraints[i];
                    if (sample == null || !sample.enabled || !sample.sampleValid) continue;
                    int runtimeFrame = KimodoFrameTimeUtility.SecondsToFrameIndex(
                        sample.sampleTime, frameRate > 0f ? frameRate : motion.FrameRate);
                    int outputFrame = runtimeFrame - Mathf.Max(0, trimStartFrame);
                    if (outputFrame < 0 || outputFrame >= motion.FrameCount) continue;

                    try
                    {
                        if (!KimodoRawMotionUtility.TryApplyFrame(
                                binding, outputFrame, out string frameError))
                        {
                            throw new InvalidOperationException(frameError);
                        }
                        KimodoConstraintProjectedPose actual =
                            KimodoRuntimeConstraintExportProjector.CaptureProfilePose(modelName, cache);
                        KimodoConstraintProjectedPose expected = projectConstraint(sample);
                        frames.Add(CompareFrame(sample, i, runtimeFrame, outputFrame, actual, expected));
                    }
                    catch (Exception error)
                    {
                        errors.Add(new JObject
                        {
                            ["frame"] = outputFrame,
                            ["constraint_index"] = i,
                            ["message"] = error.Message
                        });
                    }
                }
            }
            catch (Exception error)
            {
                return new JObject
                {
                    ["status"] = "unavailable",
                    ["metric"] = "position errors in meters; rotation errors in degrees",
                    ["compared_count"] = frames.Count,
                    ["frames"] = frames,
                    ["errors"] = new JArray(new JObject { ["message"] = error.Message })
                };
            }
            finally
            {
                cache?.Dispose();
            }

            var result = new JObject
            {
                ["status"] = errors.Count > 0 ? "partial" : frames.Count > 0 ? "ok" : "not_applicable",
                ["metric"] = "position errors in meters; rotation errors in degrees",
                ["compared_count"] = frames.Count,
                ["frames"] = frames
            };
            if (errors.Count > 0) result["errors"] = errors;
            return result;
        }

        private static JObject CompareFrame(
            KimodoMarkerSampleResult sample,
            int constraintIndex,
            int constraintFrame,
            int outputFrame,
            KimodoConstraintProjectedPose actual,
            KimodoConstraintProjectedPose expected)
        {
            string mode = KimodoConstraintInternal.ResolveExecutionMode(sample);
            bool fullBody = sample.composedFullBody || mode == "fullbody";
            bool root2D = sample.composedRoot2D || mode == "root2d";
            bool effector = sample.composedEffector || mode == "effector" ||
                mode == "left-hand" || mode == "right-hand" || mode == "left-foot" || mode == "right-foot";
            var result = new JObject
            {
                ["constraint_index"] = constraintIndex,
                ["constraint_type"] = mode,
                ["constraint_frame"] = constraintFrame,
                ["frame"] = outputFrame
            };

            if (fullBody || root2D)
            {
                Vector3 delta = actual.profileRootPosition - expected.profileRootPosition;
                result["root_position_error_m"] = fullBody
                    ? delta.magnitude
                    : new Vector2(delta.x, delta.z).magnitude;
            }
            if (root2D && KimodoConstraintMask.IsActive(sample, "rootheading"))
                result["root_heading_error_degrees"] = PlanarHeadingError(actual, expected);

            if (fullBody)
            {
                int count = Math.Min(actual.localJointAngles?.Count ?? 0, expected.localJointAngles?.Count ?? 0);
                var jointErrors = new List<float>(count);
                for (int i = 0; i < count; i++)
                {
                    Quaternion actualRotation = KimodoConstraintRotationUtility.AxisAngleVectorToQuaternion(actual.localJointAngles[i]);
                    Quaternion expectedRotation = KimodoConstraintRotationUtility.AxisAngleVectorToQuaternion(expected.localJointAngles[i]);
                    jointErrors.Add(Quaternion.Angle(actualRotation, expectedRotation));
                }
                if (jointErrors.Count > 0)
                {
                    result["joint_rotation_mean_error_degrees"] = jointErrors.Average();
                    result["joint_rotation_max_error_degrees"] = jointErrors.Max();
                }
            }
            if (effector)
            {
                var effectors = new JObject();
                foreach (var item in Effectors)
                {
                    if (!KimodoConstraintMask.IsActive(sample, item.Mask)) continue;
                    int expectedJoint = FindJoint(expected.jointNames, item.Joint);
                    int actualJoint = FindJoint(actual.jointNames, item.Joint);
                    if (expectedJoint < 0 || actualJoint < 0) continue;
                    KimodoRigidTransform target = item.Mask switch
                    {
                        "leftHand" => sample.effectors?.leftHand,
                        "rightHand" => sample.effectors?.rightHand,
                        "leftFoot" => sample.effectors?.leftFoot,
                        _ => sample.effectors?.rightFoot
                    };
                    if (target == null) continue;
                    KimodoRigidTransform worldTarget = KimodoConstraintSpaceUtility.ToWorld(
                        target, sample.rootOverride);
                    Vector3 positionDelta = actual.jointPositions[actualJoint] - worldTarget.position;
                    var effectorLoss = new JObject
                    {
                        ["position_error_m"] = positionDelta.magnitude
                    };
                    if (target?.enableRotation == true)
                    {
                        effectorLoss["rotation_error_degrees"] = Quaternion.Angle(
                            actual.jointRotations[actualJoint], expected.jointRotations[expectedJoint]);
                    }
                    effectors[item.Output] = effectorLoss;
                }
                if (effectors.HasValues) result["effectors"] = effectors;
            }
            return result;
        }

        private static float PlanarHeadingError(
            KimodoConstraintProjectedPose actual,
            KimodoConstraintProjectedPose expected)
        {
            if (actual.localJointAngles == null || actual.localJointAngles.Count == 0 ||
                expected.localJointAngles == null || expected.localJointAngles.Count == 0) return 0f;
            Quaternion actualRotation = KimodoConstraintRotationUtility.AxisAngleVectorToQuaternion(actual.localJointAngles[0]);
            Quaternion expectedRotation = KimodoConstraintRotationUtility.AxisAngleVectorToQuaternion(expected.localJointAngles[0]);
            Vector3 actualForward = actualRotation * Vector3.forward;
            Vector3 expectedForward = expectedRotation * Vector3.forward;
            return Vector2.Angle(
                new Vector2(actualForward.x, actualForward.z),
                new Vector2(expectedForward.x, expectedForward.z));
        }

        private static int FindJoint(string[] names, string expected)
        {
            if (names == null) return -1;
            for (int i = 0; i < names.Length; i++)
                if (string.Equals(names[i], expected, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }
    }
}
