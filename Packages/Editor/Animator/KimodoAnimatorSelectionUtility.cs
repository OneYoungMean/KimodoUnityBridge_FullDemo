using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class KimodoAnimatorSelectionUtility
    {
        internal static AnimatorController FindControllerForObject(UnityEngine.Object target)
        {
            if (target == null)
            {
                return null;
            }

            string path = AssetDatabase.GetAssetPath(target);
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            return AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
        }

        internal static AnimatorStateMachine FindStateMachineForState(AnimatorController controller, AnimatorState state)
        {
            if (controller == null || state == null)
            {
                return null;
            }

            for (int i = 0; i < controller.layers.Length; i++)
            {
                AnimatorStateMachine sm = controller.layers[i].stateMachine;
                if (TryFindStateMachineForStateRecursive(sm, state, out AnimatorStateMachine owner))
                {
                    return owner;
                }
            }

            return null;
        }

        internal static AnimatorStateMachine FindStateMachineForTransition(
            AnimatorController controller,
            AnimatorStateTransition transition,
            out AnimatorState fromState)
        {
            fromState = null;
            if (controller == null || transition == null)
            {
                return null;
            }

            for (int i = 0; i < controller.layers.Length; i++)
            {
                AnimatorStateMachine sm = controller.layers[i].stateMachine;
                if (TryFindStateMachineForTransitionRecursive(sm, transition, out AnimatorStateMachine owner, out fromState))
                {
                    return owner;
                }
            }

            return null;
        }

        private static bool TryFindStateMachineForStateRecursive(
            AnimatorStateMachine stateMachine,
            AnimatorState targetState,
            out AnimatorStateMachine owner)
        {
            owner = null;
            if (stateMachine == null || targetState == null)
            {
                return false;
            }

            ChildAnimatorState[] states = stateMachine.states;
            for (int i = 0; i < states.Length; i++)
            {
                if (states[i].state == targetState)
                {
                    owner = stateMachine;
                    return true;
                }
            }

            ChildAnimatorStateMachine[] childStateMachines = stateMachine.stateMachines;
            for (int i = 0; i < childStateMachines.Length; i++)
            {
                if (TryFindStateMachineForStateRecursive(childStateMachines[i].stateMachine, targetState, out owner))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryFindStateMachineForTransitionRecursive(
            AnimatorStateMachine stateMachine,
            AnimatorStateTransition transition,
            out AnimatorStateMachine owner,
            out AnimatorState fromState)
        {
            owner = null;
            fromState = null;
            if (stateMachine == null || transition == null)
            {
                return false;
            }

            ChildAnimatorState[] states = stateMachine.states;
            for (int i = 0; i < states.Length; i++)
            {
                AnimatorState state = states[i].state;
                if (state == null)
                {
                    continue;
                }

                AnimatorStateTransition[] transitions = state.transitions;
                for (int j = 0; j < transitions.Length; j++)
                {
                    if (transitions[j] == transition)
                    {
                        owner = stateMachine;
                        fromState = state;
                        return true;
                    }
                }
            }

            ChildAnimatorStateMachine[] childStateMachines = stateMachine.stateMachines;
            for (int i = 0; i < childStateMachines.Length; i++)
            {
                if (TryFindStateMachineForTransitionRecursive(
                        childStateMachines[i].stateMachine,
                        transition,
                        out owner,
                        out fromState))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
