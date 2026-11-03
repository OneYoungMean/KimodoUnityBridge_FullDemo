using UnityEditor;
using UnityEditor.Timeline;
using KimodoBridge;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal static class KimodoTimelineClipResolver
    {
        public static TimelineClip FindTimelineClipForAsset(PlayableAsset asset)
        {
            if (asset == null)
            {
                return null;
            }

            if (TimelineEditor.inspectedAsset != null)
            {
                foreach (TimelineClip selectedClip in TimelineEditor.selectedClips)
                {
                    if (selectedClip.asset == asset)
                    {
                        return selectedClip;
                    }
                }

                TimelineClip inspected = FindInTimeline(TimelineEditor.inspectedAsset, asset);
                if (inspected != null)
                {
                    return inspected;
                }
            }

            string assetPath = AssetDatabase.GetAssetPath(asset);
            TimelineAsset owningTimeline = string.IsNullOrWhiteSpace(assetPath)
                ? null
                : AssetDatabase.LoadAssetAtPath<TimelineAsset>(assetPath);
            TimelineClip owningClip = FindInTimeline(owningTimeline, asset);
            if (owningClip != null)
            {
                return owningClip;
            }

            TimelineAsset inspectedTimeline = TimelineEditor.inspectedAsset;
            if (inspectedTimeline != null && KimodoDirectorResolver.TryResolve(
                    inspectedTimeline, out PlayableDirector resolvedDirector, out _))
            {
                owningClip = FindInTimeline(resolvedDirector.playableAsset as TimelineAsset, asset);
                if (owningClip != null)
                {
                    return owningClip;
                }
            }

            PlayableDirector[] directors = UnityEngine.Resources.FindObjectsOfTypeAll<PlayableDirector>();
            for (int i = 0; i < directors.Length; i++)
            {
                PlayableDirector director = directors[i];
                if (!KimodoRuntimeUtility.IsUsableSceneDirector(director, UnityEngine.SceneManagement.SceneManager.GetActiveScene()) ||
                    director.playableAsset == null)
                {
                    continue;
                }

                owningClip = FindInTimeline(director.playableAsset as TimelineAsset, asset);
                if (owningClip != null) return owningClip;
            }

            return null;
        }

        private static TimelineClip FindInTimeline(TimelineAsset timelineAsset, PlayableAsset asset)
        {
            if (timelineAsset == null || asset == null)
            {
                return null;
            }

            foreach (TrackAsset track in timelineAsset.GetOutputTracks())
            {
                foreach (TimelineClip timelineClip in track.GetClips())
                {
                    if (timelineClip.asset == asset)
                    {
                        return timelineClip;
                    }
                }
            }

            return null;
        }
    }
}
