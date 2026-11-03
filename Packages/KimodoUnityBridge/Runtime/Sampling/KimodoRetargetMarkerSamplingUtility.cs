using System;
using System.Collections.Generic;
using KimodoUnityBridge;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge
{
    internal static class KimodoRetargetMarkerSamplingUtility
    {
        internal static bool TryResolveTargetAvatar(
            Avatar explicitTargetAvatar,
            out Avatar targetAvatar,
            out string error)
        {
            targetAvatar = null;
            error = string.Empty;
            if (KimodoRetargetCoreUtility.IsValidHumanoid(explicitTargetAvatar))
            {
                targetAvatar = explicitTargetAvatar;
                return true;
            }

            error = "An explicit target avatar is required; profile avatar fallback is disabled.";
            return false;
        }

        internal static bool TryBuildMarkerSampleResultFromBoneSample(
            BoneSample sample,
            RetargetSkeleton targetCache,
            string modelName,
            string markerType,
            double sampleTime,
            out KimodoMarkerSampleResult result,
            out string error)
        {
            result = null;
            error = string.Empty;
            if (sample == null || !sample.IsValid)
            {
                error = "Bone sample is invalid.";
                return false;
            }

            if (!KimodoRetargetAvatarUtility.ValidateRetargetSkeleton(targetCache, out error))
            {
                return false;
            }

            if (!KimodoRetargetSamplingUtility.TryApplyBoneSampleToRetargetSkeleton(sample, targetCache, out error))
            {
                return false;
            }

            result = CreateSampleShell(markerType, sampleTime);

            if (!KimodoRetargetSamplingUtility.TryCaptureSampleData(
                    targetCache,
                    out MuscleSample sampleData,
                    out error))
            {
                result = null;
                return false;
            }

            result.sampleData = sampleData;
            result.sampleValid = true;
            CaptureWorldTargets(targetCache, result);
            result.enabled = true;
            return true;
        }

        /// <summary>
        /// Captures complete scene-facing targets from the rebuilt skeleton.
        /// The caller establishes whether that Transform space is world or track.
        /// Root2D is an application mode; this method always retains the
        /// complete Hips position and rotation in rootOverride.
        /// Effector positions follow that Transform space; effector rotations
        /// are always bind-relative IK-goal deltas.
        /// </summary>
        internal static void CaptureWorldTargets(
            RetargetSkeleton cache,
            KimodoMarkerSampleResult result)
        {
            if (result == null) return;
            result.effectors ??= new KimodoConstraintEffectors();
            result.effectors.leftHand ??= KimodoRigidTransform.Identity;
            result.effectors.rightHand ??= KimodoRigidTransform.Identity;
            result.effectors.leftFoot ??= KimodoRigidTransform.Identity;
            result.effectors.rightFoot ??= KimodoRigidTransform.Identity;
            result.rootOverride ??= KimodoRigidTransform.Identity;
            result.enableMask ??= new KimodoConstraintMask();

            Vector3 position = Vector3.zero;
            Quaternion rotation = Quaternion.identity;
            bool rootValid = cache != null &&
                cache.GetBonePose(HumanBodyBones.Hips, out position, out rotation);
            if (!rootValid)
            {
                position = Vector3.zero;
                rotation = Quaternion.identity;
            }
            result.rootOverride.t = position;
            result.rootOverride.q = rotation;
            CaptureEffector(cache, HumanBodyBones.LeftHand, result.effectors.leftHand);
            CaptureEffector(cache, HumanBodyBones.RightHand, result.effectors.rightHand);
            CaptureEffector(cache, HumanBodyBones.LeftFoot, result.effectors.leftFoot);
            CaptureEffector(cache, HumanBodyBones.RightFoot, result.effectors.rightFoot);
        }

        private static void CaptureEffector(
            RetargetSkeleton cache,
            HumanBodyBones bone,
            KimodoRigidTransform target)
        {
            Vector3 position = Vector3.zero;
            Quaternion rotation = Quaternion.identity;
            bool valid = cache != null &&
                cache.GetBonePose(bone, out position, out rotation);
            if (valid)
            {
                target.t = position;
                target.space = KimodoConstraintSpace.World;
                target.enableRotation = false;
                // Hand goals use Unity's axis-corrected goal frame. Feet keep
                // the legacy bind-relative transport below.
                target.q = ResolveEffectorTransportRotation(cache, bone, rotation);
            }
            else
            {
                target.t = Vector3.zero;
                target.space = KimodoConstraintSpace.World;
                target.q = Quaternion.identity;
                target.enableRotation = false;
            }
        }

        internal static Quaternion ResolveEffectorTransportRotation(
            RetargetSkeleton cache,
            HumanBodyBones bone,
            Quaternion currentWorld)
        {
            if (cache == null)
            {
                return currentWorld;
            }

            if (bone == HumanBodyBones.LeftHand || bone == HumanBodyBones.RightHand)
            {
                Quaternion post = AvatarRuntimeAccess.GetAvatarPostRotationOrIdentity(cache.avatar, (int)bone);
                Quaternion goalOffset = bone == HumanBodyBones.LeftHand
                    ? new Quaternion(0.707107f, 0f, 0.707107f, 0f)
                    : new Quaternion(0f, 0.707107f, 0f, 0.707107f);
                // Native Unity: goal rotation = bone rotation * PostQ * hand offset.
                return (currentWorld * post * goalOffset).normalized;
            }

            if (!cache.GetBoneBindWorldRotation(bone, out Quaternion initialWorld))
            {
                return currentWorld;
            }

            // Feet retain the existing bind-relative transport protocol.
            return (currentWorld * Quaternion.Inverse(initialWorld)).normalized;
        }

        private static KimodoMarkerSampleResult CreateSampleShell(
            string markerType,
            double sampleTime)
        {
            string mode = (markerType ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-');
            if (mode == "left-hand" || mode == "right-hand" || mode == "left-foot" || mode == "right-foot")
                mode = "effector";
            mode = KimodoConstraintInternal.NormalizeMode(mode);
            return new KimodoMarkerSampleResult
            {
                sampleData = new MuscleSample(),
                constraintMode = mode,
                sampleTime = sampleTime,
                enableMask = new KimodoConstraintMask(),
                effectors = new KimodoConstraintEffectors(),
                rootOverride = KimodoRigidTransform.Identity
            };
        }
    }
}
