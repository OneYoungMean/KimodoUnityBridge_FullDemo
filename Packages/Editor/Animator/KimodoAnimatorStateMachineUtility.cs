using UnityEditor.Animations;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class KimodoAnimatorStateMachineUtility
    {
        private static readonly Vector3 InsertedStateOffset = new Vector3(0f, 80f, 0f);

        internal static Vector3 ResolveInsertedStatePosition(AnimatorStateMachine stateMachine, AnimatorState fromState)
        {
            if (stateMachine == null)
            {
                return InsertedStateOffset;
            }

            ChildAnimatorState[] states = stateMachine.states;
            Vector3 basePosition = ResolveStatePosition(states, fromState);
            Vector3 candidate = basePosition + InsertedStateOffset;
            for (int i = 0; i < states.Length; i++)
            {
                AnimatorState state = states[i].state;
                if (state == null || ReferenceEquals(state, fromState))
                {
                    continue;
                }

                if (Vector3.Distance(states[i].position, candidate) < 1f)
                {
                    candidate += InsertedStateOffset;
                    i = -1;
                }
            }

            return candidate;
        }

        internal static string EnsureUniqueStateName(AnimatorStateMachine sm, string preferred)
        {
            string baseName = string.IsNullOrWhiteSpace(preferred) ? "KimodoInsert" : preferred.Trim();
            string name = baseName;
            int suffix = 1;
            while (FindStateByName(sm, name) != null)
            {
                name = $"{baseName}_{suffix++}";
            }
            return name;
        }

        private static Vector3 ResolveStatePosition(ChildAnimatorState[] states, AnimatorState targetState)
        {
            if (states == null || targetState == null)
            {
                return Vector3.zero;
            }

            for (int i = 0; i < states.Length; i++)
            {
                if (ReferenceEquals(states[i].state, targetState))
                {
                    return states[i].position;
                }
            }

            return Vector3.zero;
        }

        private static AnimatorState FindStateByName(AnimatorStateMachine sm, string stateName)
        {
            if (sm == null || string.IsNullOrWhiteSpace(stateName))
            {
                return null;
            }

            ChildAnimatorState[] states = sm.states;
            for (int i = 0; i < states.Length; i++)
            {
                AnimatorState s = states[i].state;
                if (s != null && string.Equals(s.name, stateName, System.StringComparison.Ordinal))
                {
                    return s;
                }
            }

            ChildAnimatorStateMachine[] childStateMachines = sm.stateMachines;
            for (int i = 0; i < childStateMachines.Length; i++)
            {
                AnimatorState found = FindStateByName(childStateMachines[i].stateMachine, stateName);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }
    }
}
