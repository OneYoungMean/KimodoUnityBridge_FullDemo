using System;
using KimodoUnityBridge;
using TimelineInject;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace KimodoBridge
{
    /// <summary>
    /// Canonical constraint pose path shared by preview and protocol
    /// projection. Root overrides are world-space; local effector targets are
    /// normalized against that root at this boundary. The single playable
    /// graph evaluates the authored MuscleSample, root override and IK
    /// together. Timeline generation converts the resulting world pose back
    /// to track space before writing the character clip.
    /// </summary>
    internal static class KimodoConstraintPosePipeline
    {
        internal static bool TryApplyCanonical(
            KimodoMarkerSampleResult sample,
            float frameRate,
            RetargetSkeleton cache,
            out BoneSample boneSample,
            out MuscleSample muscleSample,
            out string error,
            bool fullBodyRootSemantics = false)
        {
            boneSample = null;
            muscleSample = null;
            error = string.Empty;
            if (sample == null)
            {
                error = "Constraint pose input is null.";
                return false;
            }

            KimodoMarkerSampleResult canonical;
            try
            {
                KimodoConstraintInternal.ValidateSample(sample);
                canonical = sample.Clone();
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            if (canonical == null)
            {
                error = "Constraint pose input is empty.";
                return false;
            }

            // Preview callers may apply the complete Root2D root transform.

            return TryApply(
                canonical,
                frameRate,
                cache,
                out boneSample,
                out muscleSample,
                out error,
                fullBodyRootSemantics);
        }

        internal static bool TryApply(
            KimodoMarkerSampleResult sample,
            float frameRate,
            RetargetSkeleton cache,
            out BoneSample boneSample,
            out MuscleSample muscleSample,
            out string error,
            bool fullBodyRootSemantics = false)
        {
            return TryApply(
                sample,
                frameRate,
                cache,
                Vector3.zero,
                Quaternion.identity,
                out boneSample,
                out muscleSample,
                out error,
                fullBodyRootSemantics);
        }

        internal static bool TryApply(
            KimodoMarkerSampleResult sample,
            float frameRate,
            RetargetSkeleton cache,
            Vector3 unusedTrackPosition,
            Quaternion unusedTrackRotation,
            out BoneSample boneSample,
            out MuscleSample muscleSample,
            out string error,
            bool fullBodyRootSemantics = false)
        {
            boneSample = null;
            muscleSample = null;
            error = string.Empty;

            if (sample == null || cache == null || !sample.sampleValid)
            {
                error = sample != null && !sample.sampleValid
                    ? "Constraint sample is invalid."
                    : "Constraint pose input is null.";
                return false;
            }

            // Every command constraint is evaluated from its authored support
            // pose. The concrete family decides which authored channels the
            // solver applies.
            if (!KimodoConstraintIkSolver.TryApply(sample, frameRate, cache, out error, fullBodyRootSemantics))
            {
                return false;
            }

            boneSample = KimodoRetargetSamplingUtility.CaptureBoneSample(cache);
            if (!KimodoRetargetSamplingUtility.TryCaptureMuscleSample(
                    cache,
                    out muscleSample,
                    out error))
            {
                return false;
            }

            return true;
        }

        internal static bool IsRootOnlySample(KimodoMarkerSampleResult sample)
        {
            if (sample == null || !KimodoConstraintMask.IsActive(sample, "rootposition"))
            {
                return false;
            }

            string mode = KimodoConstraintInternal.ResolveExecutionMode(sample);
            if (mode == "root2d")
            {
                return sample.rootOverride != null;
            }
            return false;
        }

    }

    /// <summary>
    /// Humanoid IK job whose targets are copied value data from SampleResult.
    /// No scene Transform or external rig is read by the job.
    /// </summary>
    internal static class KimodoConstraintIkSolver
    {
        private struct SolveJob : IAnimationJob
        {
            public bool solveLeftHand;
            public bool solveRightHand;
            public bool solveLeftFoot;
            public bool solveRightFoot;
            public bool rotateLeftHand, rotateRightHand, rotateLeftFoot, rotateRightFoot;
            public Vector3 leftHandPosition;
            public Quaternion leftHandRotation;
            public Vector3 rightHandPosition;
            public Quaternion rightHandRotation;
            public Vector3 leftFootPosition;
            public Quaternion leftFootRotation;
            public Vector3 rightFootPosition;
            public Quaternion rightFootRotation;
            public bool applyRoot;
            public bool applyRootPosition;
            public bool rootPlanar;
            public bool rootHeading;
            public bool footIKEnabled;
            public bool compensateLeftFoot;
            public bool compensateRightFoot;
            public Vector3 rootPosition;
            public Quaternion rootRotation;
            public Vector3 compensatedLeftFootPosition;
            public Quaternion compensatedLeftFootRotation;
            public Vector3 compensatedRightFootPosition;
            public Quaternion compensatedRightFootRotation;
            public TransformStreamHandle hips;

            public void ProcessRootMotion(AnimationStream stream)
            {
                // Root overrides are applied after humanoid IK in ProcessAnimation.
                // AnimationHumanStream.bodyPosition/bodyRotation written here are
                // overwritten by the subsequent SolveIK pass.
            }

            public void ProcessAnimation(AnimationStream stream)
            {
                if (!stream.isHumanStream)
                {
                    return;
                }

                AnimationHumanStream human = stream.AsHuman();
                // Root and effector values come from the same SampleResult.
                // Apply the root first so SolveIK consumes the final body pose
                // and does not receive a second root delta afterward.
                if (applyRoot)
                {
                    ApplyRoot(stream);
                }

                ApplyGoal(human, AvatarIKGoal.LeftHand, solveLeftHand,
                    leftHandPosition, leftHandRotation, rotateLeftHand);
                ApplyGoal(human, AvatarIKGoal.RightHand, solveRightHand,
                    rightHandPosition, rightHandRotation, rotateRightHand);
                ApplyGoal(human, AvatarIKGoal.LeftFoot, solveLeftFoot || compensateLeftFoot,
                    compensateLeftFoot ? compensatedLeftFootPosition : leftFootPosition,
                    compensateLeftFoot ? compensatedLeftFootRotation : leftFootRotation,
                    compensateLeftFoot || rotateLeftFoot);
                ApplyGoal(human, AvatarIKGoal.RightFoot, solveRightFoot || compensateRightFoot,
                    compensateRightFoot ? compensatedRightFootPosition : rightFootPosition,
                    compensateRightFoot ? compensatedRightFootRotation : rightFootRotation,
                    compensateRightFoot || rotateRightFoot);

                if (solveLeftHand || solveRightHand || solveLeftFoot || solveRightFoot ||
                    compensateLeftFoot || compensateRightFoot)
                {
                    human.SolveIK();
                }
            }

            private void ApplyRoot(AnimationStream stream)
            {
                AnimationHumanStream human = stream.AsHuman();
                Vector3 currentHipsPosition = hips.GetPosition(stream);
                Quaternion currentHipsRotation = hips.GetRotation(stream).normalized;
                Vector3 leftFootPosePosition = Vector3.zero;
                Quaternion leftFootPoseRotation = Quaternion.identity;
                Vector3 rightFootPosePosition = Vector3.zero;
                Quaternion rightFootPoseRotation = Quaternion.identity;
                if (footIKEnabled && (!solveLeftFoot || !solveRightFoot))
                {
                    if (!solveLeftFoot)
                    {
                        leftFootPosePosition = human.GetGoalPositionFromPose(AvatarIKGoal.LeftFoot);
                        leftFootPoseRotation = human.GetGoalRotationFromPose(AvatarIKGoal.LeftFoot);
                    }
                    if (!solveRightFoot)
                    {
                        rightFootPosePosition = human.GetGoalPositionFromPose(AvatarIKGoal.RightFoot);
                        rightFootPoseRotation = human.GetGoalRotationFromPose(AvatarIKGoal.RightFoot);
                    }
                }
                Vector3 targetPosition = applyRootPosition ? rootPosition : currentHipsPosition;
                Quaternion targetRotation = rootRotation.normalized;
                if (rootPlanar)
                {
                    if (applyRootPosition)
                    {
                        targetPosition = KimodoMotionMath.ApplyPlanarPosition(
                            currentHipsPosition,
                            targetPosition);
                    }
                    targetRotation = rootHeading
                        ? KimodoMotionMath.ApplyPlanarHeading(currentHipsRotation, targetRotation)
                        : currentHipsRotation;
                }
                else if (!rootHeading)
                {
                    targetRotation = currentHipsRotation;
                }

                Quaternion deltaRotation = rootHeading
                    ? (targetRotation * Quaternion.Inverse(currentHipsRotation)).normalized
                    : Quaternion.identity;
                Vector3 hipDelta = targetPosition - currentHipsPosition;
                // AnimationHumanStream.bodyPosition is world-space COM, unlike
                // the human-scale-normalized HumanPose.bodyPosition used by
                // MuscleSample. Rotate its offset about Hips and translate in
                // world units so the requested Hips position stays fixed.
                human.bodyPosition = targetPosition +
                    deltaRotation * (human.bodyPosition - currentHipsPosition);
                human.bodyRotation = (deltaRotation * human.bodyRotation).normalized;

                bool rootMoved = hipDelta.sqrMagnitude > 1e-10f ||
                    Quaternion.Angle(deltaRotation, Quaternion.identity) > 1e-4f;
                if (footIKEnabled && rootMoved)
                {
                    if (!solveLeftFoot)
                    {
                        compensateLeftFoot = true;
                        compensatedLeftFootPosition = targetPosition +
                            deltaRotation * (leftFootPosePosition - currentHipsPosition);
                        compensatedLeftFootRotation =
                            (deltaRotation * leftFootPoseRotation).normalized;
                    }
                    if (!solveRightFoot)
                    {
                        compensateRightFoot = true;
                        compensatedRightFootPosition = targetPosition +
                            deltaRotation * (rightFootPosePosition - currentHipsPosition);
                        compensatedRightFootRotation =
                            (deltaRotation * rightFootPoseRotation).normalized;
                    }
                }
            }

            private static void ApplyGoal(
                AnimationHumanStream human,
                AvatarIKGoal goal,
                bool enabled,
                Vector3 position,
                Quaternion rotation,
                bool enableRotation)
            {
                human.SetGoalWeightPosition(goal, enabled ? 1f : 0f);
                human.SetGoalWeightRotation(goal, enabled && enableRotation ? 1f : 0f);
                if (!enabled)
                {
                    return;
                }

                human.SetGoalPosition(goal, position);
                if (enableRotation) human.SetGoalRotation(goal, rotation);
            }
        }
        internal static bool TryApply(
            KimodoMarkerSampleResult sample,
            float frameRate,
            RetargetSkeleton cache,
            out string error,
            bool fullBodyRootSemantics = false)
        {
            error = string.Empty;
            if (!TryBuildJob(sample, cache, out SolveJob job, out bool any, out error, fullBodyRootSemantics) || !any)
            {
                return string.IsNullOrEmpty(error);
            }

            if (sample?.sampleData == null || !sample.sampleData.IsValid)
            {
                error = "Constraint IK requires a valid MuscleSample payload.";
                return false;
            }

            if (!KimodoRetargetSamplingUtility.TryCreateTransientMuscleClip(
                    new[] { sample.sampleData },
                    frameRate,
                    out AnimationClip clip,
                    out error))
            {
                return false;
            }

        
            PlayableGraph graph = default;
            Avatar originalAvatar = null;
            bool restoreAvatar = false;
            BoneSample solved = null;
            try
            {
                if (!KimodoRetargetClipSamplingUtility.TryConfigureAnimatorForClipSampling(
                        cache,
                        KimodoRetargetClipSamplingUtility.ClipSamplingMode.Humanoid,
                        out originalAvatar,
                        out restoreAvatar,
                        out error))
                {
                    return false;
                }
                graph = PlayableGraph.Create("KimodoConstraintIkGraph");
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                job.footIKEnabled = true;
                AnimationClipPlayable clipPlayable =
                    KimodoRetargetClipSamplingUtility.CreateClipPlayable(graph, clip);
                Playable sourcePlayable = AnimationOffsetPlayableAccess.CreateMotionXToDeltaAndConnect(
                    graph,
                    clipPlayable);
                AnimationScriptPlayable ikPlayable = AnimationScriptPlayable.Create(
                    graph,
                    job,
                    1);
                graph.Connect(sourcePlayable, 0, ikPlayable, 0);
                ikPlayable.SetInputWeight(0, 1f);
                AnimationPlayableOutput output = AnimationPlayableOutput.Create(
                    graph,
                    "KimodoConstraintIkOutput",
                    cache.animator);
                output.SetSourcePlayable(ikPlayable);
                clipPlayable.SetTime(0f);
                graph.Play();
                graph.Evaluate(0f);

                solved = KimodoRetargetSamplingUtility.CaptureBoneSample(cache);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                if (graph.IsValid())
                {
                    graph.Destroy();
                }
                if (restoreAvatar)
                {
                    KimodoRetargetClipSamplingUtility.RestoreAnimatorAfterClipSampling(
                        cache,
                        originalAvatar);
                }
                UnityEngine.Object.DestroyImmediate(clip);
               
            }

            return solved != null &&
                KimodoRetargetSamplingUtility.TryApplyBoneSampleToRetargetSkeleton(
                    solved,
                    cache,
                    out error);
        }

        private static bool TryBuildJob(
            KimodoMarkerSampleResult sample,
            RetargetSkeleton cache,
            out SolveJob job,
            out bool any,
            out string error,
            bool fullBodyRootSemantics = false)
        {
            job = default;
            any = false;
            error = string.Empty;
            if (sample == null || cache == null)
            {
                return true;
            }

            // Root2D is a root-only constraint. Older/authored samples can
            // still carry stale effector payloads or masks after their mode
            // changes, so do not let those values accidentally turn Root2D
            // into an IK solve. Other modes retain their explicit effector
            // channel semantics.
            bool calculateEffectors =
                KimodoConstraintInternal.ResolveExecutionMode(sample) != "root2d";
            job.rotateLeftHand = sample.effectors?.leftHand?.enableRotation == true;
            job.rotateRightHand = sample.effectors?.rightHand?.enableRotation == true;
            job.rotateLeftFoot = sample.effectors?.leftFoot?.enableRotation == true;
            job.rotateRightFoot = sample.effectors?.rightFoot?.enableRotation == true;
            if (calculateEffectors)
            {
                any |= job.solveLeftHand = KimodoConstraintMask.IsActive(sample, "lefthand");
                any |= job.solveRightHand = KimodoConstraintMask.IsActive(sample, "righthand");
                any |= job.solveLeftFoot = KimodoConstraintMask.IsActive(sample, "leftfoot");
                any |= job.solveRightFoot = KimodoConstraintMask.IsActive(sample, "rightfoot");
            }

            bool applyRootPosition = KimodoConstraintMask.IsActive(sample, "rootposition");
            bool applyRootHeading = KimodoConstraintMask.IsActive(sample, "rootheading");
            if ((applyRootPosition || applyRootHeading) && sample.rootOverride != null)
            {
                job.applyRoot = true;
                job.applyRootPosition = applyRootPosition;
                job.rootPlanar = !fullBodyRootSemantics &&
                    KimodoConstraintInternal.ResolveExecutionMode(sample) == "root2d";
                job.rootHeading = KimodoConstraintMask.IsActive(sample, "rootheading");
                job.rootPosition = sample.rootOverride.t;
                job.rootRotation = sample.rootOverride.q.normalized;
                Transform hips = cache.animator != null
                    ? cache.animator.GetBoneTransform(HumanBodyBones.Hips)
                    : null;
                if (hips == null)
                {
                    error = "Constraint root override requires a Hips transform.";
                    return false;
                }
                job.hips = cache.animator.BindStreamTransform(hips);
                any = true;
            }

            if (!TryResolveTarget(sample.effectors?.leftHand, sample.rootOverride, job.solveLeftHand,
                    HumanBodyBones.LeftHand, cache, out job.leftHandPosition, out job.leftHandRotation, out error) ||
                !TryResolveTarget(sample.effectors?.rightHand, sample.rootOverride, job.solveRightHand,
                    HumanBodyBones.RightHand, cache, out job.rightHandPosition, out job.rightHandRotation, out error) ||
                !TryResolveTarget(sample.effectors?.leftFoot, sample.rootOverride, job.solveLeftFoot,
                    HumanBodyBones.LeftFoot, cache, out job.leftFootPosition, out job.leftFootRotation, out error) ||
                !TryResolveTarget(sample.effectors?.rightFoot, sample.rootOverride, job.solveRightFoot,
                    HumanBodyBones.RightFoot, cache, out job.rightFootPosition, out job.rightFootRotation, out error))
            {
                return false;
            }
            return true;
        }

        private static bool TryResolveTarget(
            KimodoRigidTransform value,
            KimodoRigidTransform root,
            bool enabled,
            HumanBodyBones bone,
            RetargetSkeleton cache,
            out Vector3 position,
            out Quaternion rotation,
            out string error)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            error = string.Empty;
            if (!enabled)
            {
                return true;
            }
            if (value == null)
            {
                error = $"Constraint effector '{bone}' is enabled but has no value.";
                return false;
            }
            // Local effector coordinates are relative to the canonical sample
            // root override. Resolve that boundary exactly once.
            KimodoRigidTransform worldValue = KimodoConstraintSpaceUtility.ToWorld(value, root);
            position = worldValue.t;
            if (bone == HumanBodyBones.LeftHand || bone == HumanBodyBones.RightHand)
            {
                // Hand q is the Unity public IK-goal rotation produced by
                // boneWorld * Avatar.GetPostRotation * handGoalOffset.
                rotation = value.enableRotation ? worldValue.q.normalized : Quaternion.identity;
            }
            else
            {
                // Foot effectors retain their existing transport protocol.
                rotation = value.enableRotation ? worldValue.q : Quaternion.identity;
            }
            return true;
        }

    }
}
