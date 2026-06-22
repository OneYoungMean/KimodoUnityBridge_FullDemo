using System;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal sealed class KimodoAnimatorApplyService
    {
        internal sealed class TransitionApplyContext
        {
            public AnimatorController Controller;
            public AnimatorStateMachine StateMachine;
            public AnimatorState FromState;
            public AnimatorState ToState;
            public AnimatorStateTransition OriginalTransition;
            public AnimationClip GeneratedClip;
            public string NewStateName;
        }

        internal sealed class StateApplyContext
        {
            public AnimatorController Controller;
            public AnimatorState State;
            public AnimationClip GeneratedClip;
        }

        public bool TryApplyTransition(TransitionApplyContext context, out string error)
        {
            error = string.Empty;
            if (!ValidateTransitionContext(context, out error))
            {
                return false;
            }

            return TryApplyTransitionToController(context, context.Controller, out error);
        }

        public bool TryApplyState(StateApplyContext context, out string error)
        {
            error = string.Empty;
            if (context == null || context.Controller == null || context.State == null || context.GeneratedClip == null)
            {
                error = "State apply context is invalid.";
                return false;
            }

            try
            {
                Undo.RegisterCompleteObjectUndo(context.Controller, "Kimodo Apply State Clip");
                context.State.motion = context.GeneratedClip;
                EditorUtility.SetDirty(context.Controller);
                EditorUtility.SetDirty(context.State);
                return true;
            }
            catch (Exception ex)
            {
                error = $"State apply failed: {ex.Message}";
                return false;
            }
        }

        private static bool ValidateTransitionContext(TransitionApplyContext context, out string error)
        {
            error = string.Empty;
            if (context == null || context.Controller == null || context.StateMachine == null ||
                context.FromState == null || context.ToState == null || context.OriginalTransition == null ||
                context.GeneratedClip == null)
            {
                error = "Transition apply context is invalid.";
                return false;
            }

            return true;
        }

        private static bool TryApplyTransitionToController(
            TransitionApplyContext context,
            AnimatorController controllerToModify,
            out string error)
        {
            error = string.Empty;
            try
            {
                Undo.RegisterCompleteObjectUndo(controllerToModify, "Kimodo Apply Transition Insert");
                AnimatorStateMachine sm = context.StateMachine;
                AnimatorState from = context.FromState;
                AnimatorState to = context.ToState;
                AnimatorStateTransition original = context.OriginalTransition;

                string newStateName = KimodoAnimatorStateMachineUtility.EnsureUniqueStateName(sm, context.NewStateName);
                AnimatorState newState = sm.AddState(newStateName, KimodoAnimatorStateMachineUtility.ResolveInsertedStatePosition(sm, from));
                newState.motion = context.GeneratedClip;

                bool hasExitTime = original.hasExitTime;
                float exitTime = original.exitTime;
                AnimatorCondition[] conditions = original.conditions;

                for (int i = from.transitions.Length - 1; i >= 0; i--)
                {
                    if (from.transitions[i] == original)
                    {
                        from.RemoveTransition(from.transitions[i]);
                        break;
                    }
                }

                AnimatorStateTransition fromToNew = from.AddTransition(newState);
                fromToNew.hasExitTime = hasExitTime;
                fromToNew.exitTime = exitTime;
                fromToNew.hasFixedDuration = true;
                fromToNew.duration = 0f;
                fromToNew.offset = 0f;
                CopyConditions(fromToNew, conditions);

                AnimatorStateTransition newToTo = newState.AddTransition(to);
                newToTo.hasExitTime = true;
                newToTo.exitTime = 1f;
                newToTo.hasFixedDuration = true;
                newToTo.duration = 0f;
                newToTo.offset = 0f;

                EditorUtility.SetDirty(controllerToModify);
                EditorUtility.SetDirty(sm);
                EditorUtility.SetDirty(from);
                EditorUtility.SetDirty(newState);
                EditorUtility.SetDirty(to);
                return true;
            }
            catch (Exception ex)
            {
                error = $"Transition apply failed: {ex.Message}";
                return false;
            }
        }

        private static void CopyConditions(AnimatorStateTransition dst, AnimatorCondition[] conditions)
        {
            if (dst == null || conditions == null)
            {
                return;
            }

            for (int i = 0; i < conditions.Length; i++)
            {
                AnimatorCondition c = conditions[i];
                dst.AddCondition(c.mode, c.threshold, c.parameter);
            }
        }

    }
}
