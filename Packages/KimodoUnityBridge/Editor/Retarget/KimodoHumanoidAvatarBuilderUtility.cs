using System;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class KimodoHumanoidAvatarBuilderUtility
    {
        internal static bool TryLoadImporterAvatar(GameObject gameObject, out Avatar avatar, out string modelImporterPath)
        {
            return AvatarSetupToolExtension.TryLoadImporterAvatar(gameObject, out avatar, out modelImporterPath);
        }

        internal static Avatar GenerateHumanoidAvatar(
            GameObject sourceRoot,
            out string error)
        {
            error = string.Empty;
            if (sourceRoot == null)
            {
                error = "Avatar root object is null.";
                return null;
            }

            try
            {
                GameObject rootObject = sourceRoot;
                if (sourceRoot.TryGetComponent(out Animator animator) && animator.avatarRoot != null)
                {
                    rootObject = animator.avatarRoot.gameObject;
                }

                return AvatarSetupToolExtension.AutoGenerateHumanoidAvatarFromModelOrThrow(rootObject, forceReimport: true);
            }
            catch (Exception e)
            {
                error = $"GenerateHumanoidAvatar failed: {e.Message}";
                return null;
            }
        }
    }
}
