using System.Collections.Generic;
using TimelineInject;
using UnityEngine;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal sealed class KimodoInOutConstraintRequest
    {
        public KimodoInOutConstraintMode Mode;
        public AnimationClip PreviousClip;
        public AnimationClip CurrentClip;
        public AnimationClip NextClip;
        public TimelineClip PreviousTimelineClip;
        public TimelineClip CurrentTimelineClip;
        public TimelineClip NextTimelineClip;
        public Avatar SourceAvatar;
        public string ModelName = KimodoPlayableClip.DefaultBridgeModelName;
        public int GenerationFrames = 1;
        public double ExportClipStartSeconds;
        public double? ExportClipDurationSeconds;
        public bool NormalizeConstraintOrigin;
        public bool IsLoop;
        public List<KimodoMarkerSampleResult> ManualSamples = new List<KimodoMarkerSampleResult>();
    }

    internal sealed class KimodoInOutConstraintResult
    {
        public KimodoMarkerSampleResult BeginSample;
        public KimodoMarkerSampleResult EndSample;
        public List<KimodoMarkerSampleResult> CombinedSamples = new List<KimodoMarkerSampleResult>();
        public string ConstraintsJson = string.Empty;
    }
}
