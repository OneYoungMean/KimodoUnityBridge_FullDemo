using UnityEditor.Timeline;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal static class KimodoTimelineClipResolver
    {
        public static TimelineClip FindTimelineClipForAsset(PlayableAsset asset)
        {
            if (asset == null || TimelineEditor.inspectedAsset == null)
            {
                return null;
            }

            foreach (TimelineClip selectedClip in TimelineEditor.selectedClips)
            {
                if (selectedClip.asset == asset)
                {
                    return selectedClip;
                }
            }

            foreach (TrackAsset track in TimelineEditor.inspectedAsset.GetOutputTracks())
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
