using System;
using UnityEngine;

namespace KimodoUnityBridge
{
    public enum KimodoConstraintSpace
    {
        Local = 0,
        World = 1
    }

    /// <summary>Minimal position/rotation value with no hierarchy or IK semantics.</summary>
    [Serializable]
    public sealed class KimodoRigidTransform
    {
        public Vector3 position;
        public Quaternion rotation;
        public bool enableRotation;
        // Effector targets may be authored relative to rootOverride. Root
        // transforms themselves are always world-space and ignore this value.
        public KimodoConstraintSpace space = KimodoConstraintSpace.World;

        public Vector3 t
        {
            get => position;
            set => position = value;
        }

        public Quaternion q
        {
            get => rotation;
            set => rotation = value;
        }

        public static KimodoRigidTransform Identity => new KimodoRigidTransform
        {
            position = Vector3.zero,
            rotation = Quaternion.identity,
            enableRotation = false,
            space = KimodoConstraintSpace.World
        };

        public KimodoRigidTransform Clone() => new KimodoRigidTransform
        {
            position = position,
            rotation = rotation,
            enableRotation = enableRotation,
            space = space
        };
    }

    public static class KimodoConstraintSpaceUtility
    {
        public static Vector3 ToWorldPosition(Vector3 value, KimodoConstraintSpace space, KimodoRigidTransform root)
        {
            if (space != KimodoConstraintSpace.Local || root == null) return value;
            return root.position + root.rotation * value;
        }

        public static Quaternion ToWorldRotation(Quaternion value, KimodoConstraintSpace space, KimodoRigidTransform root)
        {
            if (space != KimodoConstraintSpace.Local || root == null) return value;
            return (root.rotation * value).normalized;
        }

        public static KimodoRigidTransform ToWorld(KimodoRigidTransform value, KimodoRigidTransform root)
        {
            if (value == null) return KimodoRigidTransform.Identity;
            KimodoRigidTransform result = value.Clone();
            result.position = ToWorldPosition(value.position, value.space, root);
            result.rotation = ToWorldRotation(value.rotation, value.space, root);
            result.space = KimodoConstraintSpace.World;
            return result;
        }

        public static void ConvertSpace(KimodoRigidTransform value, KimodoRigidTransform root, KimodoConstraintSpace target)
        {
            if (value == null || value.space == target) return;
            KimodoRigidTransform world = ToWorld(value, root);
            if (target == KimodoConstraintSpace.Local && root != null)
            {
                value.position = Quaternion.Inverse(root.rotation) * (world.position - root.position);
                value.rotation = (Quaternion.Inverse(root.rotation) * world.rotation).normalized;
            }
            else
            {
                value.position = world.position;
                value.rotation = world.rotation;
            }
            value.space = target;
        }
    }
}
