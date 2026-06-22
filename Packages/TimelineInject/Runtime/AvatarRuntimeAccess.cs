using UnityEngine;

namespace TimelineInject
{
    public static class AvatarRuntimeAccess
    {
        public static Quaternion GetAvatarPostRotationOrIdentity(Avatar avatar, int humanId)
        {
            if (avatar == null)
            {
                return Quaternion.identity;
            }
            return avatar.GetPostRotation(humanId);
        }



        public static float GetAvatarAxisLengthOrZero(Avatar avatar, int humanId)
        {
            if (avatar == null)
            {
                return 0f;
            }
            return avatar.GetAxisLength(humanId);
        }

        public static string GetSkeletonBoneParentNameOrEmpty(SkeletonBone bone)
        {
            return string.IsNullOrWhiteSpace(bone.parentName) ? string.Empty : bone.parentName;
        }
    }
}
