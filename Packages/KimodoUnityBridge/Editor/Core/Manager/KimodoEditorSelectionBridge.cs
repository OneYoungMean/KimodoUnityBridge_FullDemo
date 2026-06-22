using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    public readonly struct KimodoSelectedPlayableClipInfo
    {
        public KimodoSelectedPlayableClipInfo(int clipInstanceId, string prompt)
        {
            ClipInstanceId = clipInstanceId;
            Prompt = prompt ?? string.Empty;
        }

        public int ClipInstanceId { get; }

        public string Prompt { get; }

        public bool IsValid => ClipInstanceId != 0;

        public string TargetKey => IsValid ? "clip:" + ClipInstanceId : "clip:null";
    }

    public static class KimodoEditorSelectionBridge
    {
        public static bool TryGetSelectedPlayableClip(out KimodoSelectedPlayableClipInfo info)
        {
            info = default;

            TimelineClip[] selectedClips = TimelineEditor.selectedClips;
            if (selectedClips != null)
            {
                for (int i = 0; i < selectedClips.Length; i++)
                {
                    if (selectedClips[i]?.asset is KimodoPlayableClip playableFromTimeline)
                    {
                        info = new KimodoSelectedPlayableClipInfo(
                            playableFromTimeline.GetInstanceID(),
                            playableFromTimeline.motionPrompt);
                        return true;
                    }
                }
            }

            if (Selection.activeObject is KimodoPlayableClip selectedAsset)
            {
                info = new KimodoSelectedPlayableClipInfo(selectedAsset.GetInstanceID(), selectedAsset.motionPrompt);
                return true;
            }

            return false;
        }
    }
}
