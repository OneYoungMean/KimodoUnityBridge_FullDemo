using System;
using System.Collections.Generic;
using System.Linq;
using KimodoBridge;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    /// <summary>Resolves a scene director for a Timeline asset using one shared priority policy.</summary>
    internal static class KimodoDirectorResolver
    {
        internal static bool TryResolve(
            TimelineAsset timelineAsset,
            out PlayableDirector director,
            out string error)
        {
            director = null;
            error = string.Empty;
            if (timelineAsset == null)
            {
                error = "TimelineAsset is null.";
                return false;
            }

            Scene scene = SceneManager.GetActiveScene();
            PlayableDirector inspected = TimelineEditor.inspectedDirector;
            if (KimodoRuntimeUtility.IsUsableSceneDirector(inspected, scene) &&
                inspected.playableAsset == timelineAsset)
            {
                director = inspected;
                return true;
            }

            List<PlayableDirector> candidates = Resources.FindObjectsOfTypeAll<PlayableDirector>()
                .Where(item => KimodoRuntimeUtility.IsUsableSceneDirector(item, scene) &&
                               item.playableAsset == timelineAsset)
                .GroupBy(item => KimodoUnityObjectIdUtility.IdHash(item))
                .Select(group => group.First())
                .ToList();

            PlayableDirector selected = TryResolveSelectedDirector(candidates);
            if (selected != null)
            {
                director = selected;
                return true;
            }

            PlayableDirector context = candidates.FirstOrDefault(IsContextDirector);
            if (context != null)
            {
                director = context;
                return true;
            }

            if (candidates.Count == 1)
            {
                director = candidates[0];
                return true;
            }

            if (candidates.Count == 0)
            {
                error = "No usable scene PlayableDirector references this TimelineAsset.";
            }
            else
            {
                error = $"Multiple usable scene PlayableDirectors reference '{timelineAsset.name}'. Select the intended Director or open it in Timeline.";
            }

            return false;
        }

        private static bool IsContextDirector(PlayableDirector director)
        {
            return director != null && KimodoRuntimeUtility.IsContextObject(director.gameObject);
        }

        private static PlayableDirector TryResolveSelectedDirector(IReadOnlyList<PlayableDirector> candidates)
        {
            GameObject selectedObject = Selection.activeGameObject;
            if (selectedObject == null) return null;

            PlayableDirector selected = selectedObject.GetComponent<PlayableDirector>() ??
                selectedObject.GetComponentInParent<PlayableDirector>();
            return selected != null && candidates.Contains(selected) ? selected : null;
        }
    }
}
