using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using KimodoBridge;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using UnityEngine.Timeline;

namespace KimodoUnityBridge.Command
{
    internal static partial class command_context
    {
        private static JObject OpenTimelineFromSceneContext(string requestedPath)
        {
            string assetPath = ResolveTimelineAssetPath(requestedPath);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            TimelineAsset timeline = AssetDatabase.LoadAssetAtPath<TimelineAsset>(assetPath);
            if (timeline == null)
            {
                throw new InvalidOperationException($"Timeline asset '{requestedPath}' could not be loaded.");
            }

            ArchiveCurrentTimelineContext();

            Scene scene = SceneManager.GetActiveScene();
            string safeName = KimodoRuntimeUtility.SanitizeName(timeline.name, "Timeline");
            GameObject sessionRoot = new GameObject($"{KimodoRuntimeUtility.SessionRootPrefix}History_{safeName}")
            {
                layer = SessionCaptureLayer,
                hideFlags = HideFlags.DontSaveInEditor
            };
            KimodoDisableOnRuntime.Ensure(sessionRoot);
            GameObject directorObject = new GameObject($"{KimodoRuntimeUtility.SessionDirectorPrefix}History_{safeName}")
            {
                hideFlags = HideFlags.DontSaveInEditor
            };
            directorObject.transform.SetParent(sessionRoot.transform, false);
            PlayableDirector director = directorObject.AddComponent<PlayableDirector>();
            director.playableAsset = timeline;
            director.time = 0.0;
            director.enabled = true;

            var metadata = ScriptableObject.CreateInstance<KimodoCommandSessionMetadata>();
            metadata.hideFlags = HideFlags.HideAndDontSave;
            metadata.sessionId = Guid.NewGuid().ToString("D");
            metadata.sessionName = $"History_{safeName}";
            metadata.scenePath = scene.IsValid() ? scene.path : string.Empty;
            metadata.schemaVersion = "history-open";

            string sessionName = metadata.sessionName;
            lock (TimelineSessionsLock)
            {
                for (int suffix = 1; TimelineSessions.ContainsKey(sessionName); suffix++)
                {
                    sessionName = $"{metadata.sessionName}_{suffix}";
                }
            }
            var session = new TimelineSessionRecord(
                Guid.Parse(metadata.sessionId), sessionName, director, timeline, assetPath,
                isAutomatic: false, metadata, sessionRoot);

            var result = new JObject
            {
                ["timeline_asset"] = timeline.name,
                ["timeline_asset_path"] = assetPath,
                ["director"] = GetSceneHierarchyPath(directorObject),
                ["bound_characters"] = new JArray(),
                ["warnings"] = new JArray()
            };
            JArray boundCharacters = (JArray)result["bound_characters"];
            JArray warnings = (JArray)result["warnings"];

            Animator[] sceneAnimators = Resources.FindObjectsOfTypeAll<Animator>()
                .Where(animator => animator != null && animator.gameObject != null &&
                    animator.gameObject.scene == scene && !EditorUtility.IsPersistent(animator) &&
                    !IsSessionObject(animator.gameObject))
                .GroupBy(animator => animator.transform.root.gameObject)
                .Select(group => group.First())
                .ToArray();

            foreach (AnimationTrack track in timeline.GetRootTracks().OfType<AnimationTrack>())
            {
                string trackName = track.name?.Trim() ?? string.Empty;
                Animator match = sceneAnimators.FirstOrDefault(animator =>
                    string.Equals(animator.transform.root.name, trackName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(animator.gameObject.name, trackName, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    string warning = $"Timeline track '{track.name}' has no same-name scene character; Timeline was opened without a binding.";
                    Debug.LogWarning($"[Kimodo] {warning}");
                    warnings.Add(warning);
                    continue;
                }

                director.SetGenericBinding(track, match);
                AnimationTrack poseTrack = track.GetChildTracks().OfType<AnimationTrack>().FirstOrDefault() ?? track;
                string characterRef = GetObjectReference(match.transform.root.gameObject);
                session.Characters.Add(new TimelineCharacterRecord(
                    characterRef, match.transform.root.gameObject, match, match.avatar, track, poseTrack, string.Empty));
                boundCharacters.Add(new JObject
                {
                    ["track"] = track.name,
                    ["character"] = match.transform.root.name,
                    ["scene_object"] = GetSceneHierarchyPath(match.transform.root.gameObject),
                    ["animator"] = GetSceneHierarchyPath(match.gameObject)
                });
            }

            lock (TimelineSessionsLock)
            {
                TimelineSessions[sessionName] = session;
            }
            currentTimelineSession = session;
            firstTimelineSession = session;
            timelineSessionsRestored = true;
            ActivateTimelineSession(session);
            OpenTimelineWindow(session.Director);
            return result;
        }

        private static string ResolveTimelineAssetPath(string requestedPath)
        {
            string path = requestedPath?.Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException("timeline_path is required when opening a Timeline.");
            }

            path = path.Replace('\\', '/');
            string projectRoot = Directory.GetParent(Application.dataPath).FullName.Replace('\\', '/').TrimEnd('/');
            if (Path.IsPathRooted(path))
            {
                string absolute = Path.GetFullPath(path).Replace('\\', '/');
                if (!absolute.StartsWith(projectRoot + "/", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("timeline_path must point to an asset inside the active Unity project.");
                }
                path = absolute.Substring(projectRoot.Length + 1);
            }

            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("timeline_path must be an Assets/... or Packages/... Unity asset path.");
            }
            return path;
        }

        private static void ArchiveCurrentTimelineContext()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (currentTimelineSession != null)
            {
                CancelTimelineSessionGenerations(currentTimelineSession, "Session context switched to a historical Timeline.");
            }
            GameObject[] roots = GetSceneSessionRoots(scene);
            if (roots.Length == 0 && currentTimelineSession?.SessionRoot != null)
            {
                roots = new[] { currentTimelineSession.SessionRoot };
            }

            if (roots.Length == 0)
            {
                currentTimelineSession = null;
                firstTimelineSession = null;
                lock (TimelineSessionsLock) TimelineSessions.Clear();
                return;
            }

            GameObject archive = scene.GetRootGameObjects()
                .FirstOrDefault(root => root != null && root.name == "KimodoSessionArchive");
            if (archive == null)
            {
                archive = new GameObject("KimodoSessionArchive") { hideFlags = HideFlags.DontSaveInEditor };
            }
            foreach (GameObject root in roots)
            {
                if (root == null || root == archive) continue;
                root.SetActive(false);
                root.name = $"Archive_{root.name}";
                root.transform.SetParent(archive.transform, true);
            }

            currentTimelineSession = null;
            firstTimelineSession = null;
            lock (TimelineSessionsLock) TimelineSessions.Clear();
        }

        private static void OpenTimelineWindow(PlayableDirector director)
        {
            if (director == null || director.playableAsset is not TimelineAsset)
            {
                return;
            }

            TimelineEditorWindow window = TimelineEditor.GetOrCreateWindow();
            window.Focus();
            EditorApplication.delayCall += () =>
            {
                if (window == null || director == null || director.playableAsset is not TimelineAsset)
                {
                    return;
                }
                window.SetTimeline(director);
                window.locked = true;
                window.Focus();
                TimelineEditor.Refresh(RefreshReason.SceneNeedsUpdate | RefreshReason.WindowNeedsRedraw);
            };
        }
    }
}
