using System;
using UnityEngine;

namespace KimodoBridge
{
    public static class KimodoProfileSkeletonUtility
    {
        public static bool TryResolveProfileSkeleton(
            string modelName,
            SkeletonCache cache,
            out string[] jointNames,
            out int[] parentIndices,
            out Transform[] jointTransforms,
            out string error)
        {
            error = string.Empty;
            jointTransforms = Array.Empty<Transform>();
            KimodoRigProfileDatabase.ResolveProfile(modelName, out _, out jointNames, out parentIndices);
            if (jointNames == null || jointNames.Length == 0)
            {
                error = $"Profile joint layout not found for '{modelName}'.";
                return false;
            }

            if (!KimodoRetargetAvatarUtility.ValidateRetargetCache(cache, out error))
            {
                return false;
            }

            jointTransforms = new Transform[jointNames.Length];
            for (int i = 0; i < jointNames.Length; i++)
            {
                string jointName = jointNames[i];
                if (string.IsNullOrWhiteSpace(jointName))
                {
                    error = $"Profile joint at index {i} is empty.";
                    return false;
                }

                if (!KimodoRetargetAvatarUtility.TryGetUniqueCachedTransformByName(cache, jointName, out jointTransforms[i], out bool ambiguous))
                {
                    error = ambiguous
                        ? $"Profile joint '{jointName}' matches multiple transforms under '{cache.skeletonRoot.name}'."
                        : $"Profile joint '{jointName}' was not found under '{cache.skeletonRoot.name}'.";
                    jointTransforms = Array.Empty<Transform>();
                    return false;
                }
            }

            return true;
        }

        public static bool TryResolveProfileSkeleton(
            string modelName,
            Transform root,
            out string[] jointNames,
            out int[] parentIndices,
            out Transform[] jointTransforms,
            out string error)
        {
            error = string.Empty;
            jointTransforms = Array.Empty<Transform>();
            KimodoRigProfileDatabase.ResolveProfile(modelName, out _, out jointNames, out parentIndices);
            if (jointNames == null || jointNames.Length == 0)
            {
                error = $"Profile joint layout not found for '{modelName}'.";
                return false;
            }

            if (root == null)
            {
                error = "Skeleton root is null.";
                return false;
            }

            jointTransforms = new Transform[jointNames.Length];
            for (int i = 0; i < jointNames.Length; i++)
            {
                string jointName = jointNames[i];
                if (string.IsNullOrWhiteSpace(jointName))
                {
                    error = $"Profile joint at index {i} is empty.";
                    return false;
                }

                if (!KimodoRetargetAvatarUtility.TryFindUniqueTransformByName(root, jointName, out jointTransforms[i], out bool ambiguous))
                {
                    error = ambiguous
                        ? $"Profile joint '{jointName}' matches multiple transforms under '{root.name}'."
                        : $"Profile joint '{jointName}' was not found under '{root.name}'.";
                    jointTransforms = Array.Empty<Transform>();
                    return false;
                }
            }

            return true;
        }
    }
}
