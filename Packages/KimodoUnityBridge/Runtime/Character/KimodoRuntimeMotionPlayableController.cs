#if KIMODO_RUNTIME_USE_PLAYABLE_GRAPH
using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace KimodoBridge
{
    internal sealed class KimodoRuntimeMotionPlayableController : IDisposable
    {
        private PlayableGraph graph;
        private AnimationScriptPlayable scriptPlayable;
        private AnimationPlayableOutput output;
        private NativeArray<float> muscles;
        private NativeArray<MuscleHandle> muscleHandles;
        private RuntimeMotionJob jobData;
        private Animator animator;

        public bool IsReady => animator != null && graph.IsValid() && scriptPlayable.IsValid();

        public bool EnsureInitialized(Animator targetAnimator, out string error)
        {
            error = string.Empty;
            if (targetAnimator == null)
            {
                error = "Target animator is null.";
                return false;
            }

            if (IsReady && ReferenceEquals(animator, targetAnimator))
            {
                return true;
            }

            Dispose();

            try
            {
                animator = targetAnimator;
                muscles = new NativeArray<float>(HumanTrait.MuscleCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                muscleHandles = new NativeArray<MuscleHandle>(MuscleHandle.muscleHandleCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                MuscleHandle[] muscleHandleArray = new MuscleHandle[MuscleHandle.muscleHandleCount];
                MuscleHandle.GetMuscleHandles(muscleHandleArray);
                int handleCount = Mathf.Min(muscleHandles.Length, muscleHandleArray != null ? muscleHandleArray.Length : 0);
                for (int i = 0; i < handleCount; i++)
                {
                    muscleHandles[i] = muscleHandleArray[i];
                }
                jobData = new RuntimeMotionJob
                {
                    muscles = muscles,
                    muscleHandles = muscleHandles
                };

                graph = PlayableGraph.Create("KimodoRuntimeMotionDriverGraph");
                graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
                scriptPlayable = AnimationScriptPlayable.Create(graph, jobData);
                scriptPlayable.SetProcessInputs(false);
                output = AnimationPlayableOutput.Create(graph, "KimodoRuntimeMotionDriverOutput", targetAnimator);
                output.SetSourcePlayable(scriptPlayable);
                graph.Play();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Dispose();
                return false;
            }
        }

        public void SetFrame(KimodoRuntimeMotionFrame frame)
        {
            if (!IsReady)
            {
                return;
            }

            int muscleCount = muscles.IsCreated ? muscles.Length : 0;
            int sourceCount = frame.muscles != null ? frame.muscles.Length : 0;
            int count = Mathf.Min(muscleCount, sourceCount);
            for (int i = 0; i < count; i++)
            {
                muscles[i] = frame.muscles[i];
            }

            for (int i = count; i < muscleCount; i++)
            {
                muscles[i] = 0f;
            }

            jobData.hasPose = frame.hasPose;
            jobData.bodyPosition = frame.bodyPosition;
            jobData.bodyRotation = frame.bodyRotation;
            jobData.leftFootGoalPosition = frame.leftFootGoalPosition;
            jobData.leftFootGoalRotation = frame.leftFootGoalRotation;
            jobData.rightFootGoalPosition = frame.rightFootGoalPosition;
            jobData.rightFootGoalRotation = frame.rightFootGoalRotation;
            jobData.leftFootPositionWeight = frame.leftFootPositionWeight;
            jobData.leftFootRotationWeight = frame.leftFootRotationWeight;
            jobData.rightFootPositionWeight = frame.rightFootPositionWeight;
            jobData.rightFootRotationWeight = frame.rightFootRotationWeight;
            jobData.applyFootIk = frame.applyFootIk;
            scriptPlayable.SetJobData(jobData);
        }

        public void Dispose()
        {
            if (graph.IsValid())
            {
                graph.Destroy();
            }

            if (muscles.IsCreated)
            {
                muscles.Dispose();
            }

            if (muscleHandles.IsCreated)
            {
                muscleHandles.Dispose();
            }

            animator = null;
            scriptPlayable = default;
            output = default;
            graph = default;
            jobData = default;
        }
    }

    internal struct KimodoRuntimeMotionFrame
    {
        public bool hasPose;
        public Vector3 bodyPosition;
        public Quaternion bodyRotation;
        public float[] muscles;
        public bool applyFootIk;
        public Vector3 leftFootGoalPosition;
        public Quaternion leftFootGoalRotation;
        public float leftFootPositionWeight;
        public float leftFootRotationWeight;
        public Vector3 rightFootGoalPosition;
        public Quaternion rightFootGoalRotation;
        public float rightFootPositionWeight;
        public float rightFootRotationWeight;
    }

    internal struct RuntimeMotionJob : IAnimationJob
    {
        public bool hasPose;
        public Vector3 bodyPosition;
        public Quaternion bodyRotation;
        public NativeArray<float> muscles;
        public NativeArray<MuscleHandle> muscleHandles;
        public bool applyFootIk;
        public Vector3 leftFootGoalPosition;
        public Quaternion leftFootGoalRotation;
        public float leftFootPositionWeight;
        public float leftFootRotationWeight;
        public Vector3 rightFootGoalPosition;
        public Quaternion rightFootGoalRotation;
        public float rightFootPositionWeight;
        public float rightFootRotationWeight;

        public void ProcessRootMotion(AnimationStream stream)
        {
        }

        public void ProcessAnimation(AnimationStream stream)
        {
            if (!hasPose || !stream.isHumanStream)
            {
                return;
            }

            AnimationHumanStream human = stream.AsHuman();
            human.bodyPosition = bodyPosition;
            human.bodyRotation = bodyRotation;

            int muscleCount = muscles.IsCreated && muscleHandles.IsCreated
                ? Mathf.Min(muscles.Length, muscleHandles.Length)
                : 0;
            for (int i = 0; i < muscleCount; i++)
            {
                human.SetMuscle(muscleHandles[i], muscles[i]);
            }

            if (applyFootIk)
            {
                ApplyGoal(
                    human,
                    AvatarIKGoal.LeftFoot,
                    leftFootGoalPosition,
                    leftFootGoalRotation,
                    leftFootPositionWeight,
                    leftFootRotationWeight);
                ApplyGoal(
                    human,
                    AvatarIKGoal.RightFoot,
                    rightFootGoalPosition,
                    rightFootGoalRotation,
                    rightFootPositionWeight,
                    rightFootRotationWeight);
                human.SolveIK();
            }
        }

        private static void ApplyGoal(
            AnimationHumanStream human,
            AvatarIKGoal goal,
            Vector3 position,
            Quaternion rotation,
            float positionWeight,
            float rotationWeight)
        {
            if (positionWeight <= 0f && rotationWeight <= 0f)
            {
                return;
            }

            human.SetGoalPosition(goal, position);
            human.SetGoalRotation(goal, rotation);
            human.SetGoalWeightPosition(goal, Mathf.Clamp01(positionWeight));
            human.SetGoalWeightRotation(goal, Mathf.Clamp01(rotationWeight));
        }
    }
}
#endif
