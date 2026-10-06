using TimelineInject;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Playables;
using System;

namespace KimodoBridge
{
    public static class KimodoRuntimeUtility
    {
        // Scene object classification is shared by command and editor code. Keep
        // these tokens in Runtime so every caller applies the same protocol.
        public const string SessionRootPrefix = "KimodoSession_";
        public const string SessionDirectorPrefix = "Kimodo_CommandSession_";
        public const string ArchiveRootName = "__KimodoArchive";
        public const string ArchivedObjectPrefix = "Archived_";

        public static bool IsArchivedObject(GameObject gameObject)
        {
            Transform current = gameObject != null ? gameObject.transform : null;
            while (current != null)
            {
                if (string.Equals(current.name, ArchiveRootName, StringComparison.Ordinal) ||
                    current.name.StartsWith(ArchivedObjectPrefix, StringComparison.Ordinal))
                {
                    return true;
                }

                current = current.parent;
            }

            return false;
        }

        public static bool IsContextObject(GameObject gameObject)
        {
            Transform current = gameObject != null ? gameObject.transform : null;
            while (current != null)
            {
                if (current.name.StartsWith(SessionRootPrefix, StringComparison.Ordinal) ||
                    current.name.StartsWith(SessionDirectorPrefix, StringComparison.Ordinal))
                {
                    return true;
                }

                current = current.parent;
            }

            return false;
        }

        public static bool IsArchivedSessionName(string name)
        {
            return !string.IsNullOrEmpty(name) &&
                (string.Equals(name, ArchiveRootName, StringComparison.Ordinal) ||
                 name.StartsWith(ArchivedObjectPrefix, StringComparison.Ordinal));
        }

        public static bool IsContextSessionName(string name)
        {
            return !string.IsNullOrEmpty(name) &&
                (name.StartsWith(SessionRootPrefix, StringComparison.Ordinal) ||
                 name.StartsWith(SessionDirectorPrefix, StringComparison.Ordinal));
        }

        public static bool IsUsableSceneDirector(PlayableDirector director, Scene scene)
        {
            return director != null && director.gameObject != null &&
                scene.IsValid() && director.gameObject.scene == scene &&
                !IsArchivedObject(director.gameObject);
        }

        public static string SanitizeName(string input, string defaultName = "joint")
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return string.IsNullOrWhiteSpace(defaultName) ? "joint" : defaultName;
            }

            return input.Replace("/", "_").Replace("\\", "_").Replace(":", "_");
        }

        public static Vector3 QuaternionToAxisAngleVector(Quaternion q)
        {
            return KimodoConstraintRotationUtility.QuaternionToAxisAngleVector(q);
        }
    }

    public static class KimodoUnityObjectIdUtility
    {
        public static string StableKey(UnityEngine.Object value)
        {
            if (value == null) return "0";
#if UNITY_6000_0_OR_NEWER
            return value.GetEntityId().ToString();
#else
            return value.GetInstanceID().ToString();
#endif
        }

        public static ulong GetSceneHandle(Scene scene)
        {
#if UNITY_6000_0_OR_NEWER
            return scene.handle.GetRawData();
#else
            return scene.handle >= 0 ? (ulong)scene.handle : 0UL;
#endif
        }

        public static string NameKey(UnityEngine.Object value)
        {
            return value != null ? value.name ?? string.Empty : string.Empty;
        }

        public static int IdHash(UnityEngine.Object value)
        {
            if (value == null)
            {
                return 0;
            }

#if UNITY_6000_0_OR_NEWER
            return value.GetEntityId().GetHashCode();
#else
            return value.GetInstanceID();
#endif
        }

        public static int NameHash(UnityEngine.Object value)
        {
            return StringComparer.Ordinal.GetHashCode(NameKey(value));
        }
    }
}
