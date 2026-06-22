using TimelineInject;
using UnityEngine;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal static class KimodoInOutConstraintClipSampler
    {
        private const string FullBodyConstraintType = "fullbody";

        internal static bool TrySampleBoundaryPair(
            KimodoInOutConstraintRequest request,
            out KimodoMarkerSampleResult beginSample,
            out KimodoMarkerSampleResult endSample,
            out string warning,
            out string error)
        {
            beginSample = null;
            endSample = null;
            warning = string.Empty;
            error = string.Empty;

            if (request == null)
            {
                error = "InOut constraint request is null.";
                return false;
            }

            if (request.Mode == KimodoInOutConstraintMode.None)
            {
                return true;
            }

            if (!KimodoRetargetCoreUtility.IsValidHumanoid(request.SourceAvatar))
            {
                error = "Source avatar is null/invalid/non-humanoid.";
                return false;
            }

            switch (request.Mode)
            {
                case KimodoInOutConstraintMode.Inside:
                    return TrySampleInsideBoundaryPair(request, out beginSample, out endSample, out error);

                case KimodoInOutConstraintMode.Outside:
                    return TrySampleOutsideBoundaryPair(request, out beginSample, out endSample, out warning, out error);

                default:
                    error = $"Unsupported InOut constraint mode: {request.Mode}.";
                    return false;
            }
        }

        private static bool TrySampleInsideBoundaryPair(
            KimodoInOutConstraintRequest request,
            out KimodoMarkerSampleResult beginSample,
            out KimodoMarkerSampleResult endSample,
            out string error)
        {
            beginSample = null;
            endSample = null;
            error = string.Empty;

            if (request.CurrentClip == null)
            {
                error = "Inside mode requires CurrentClip.";
                return false;
            }

            int generatedFrameCount = KimodoInOutConstraintTimingUtility.ClampFrameCount(request.GenerationFrames);
            double exportClipStartSeconds = request.ExportClipStartSeconds;
            double endConstraintTime = KimodoInOutConstraintTimingUtility.ResolveConstraintEndSampleTimeSeconds(generatedFrameCount);
            if (!TrySampleBoundaryPose(
                    request.CurrentClip,
                    request.CurrentTimelineClip,
                    request.SourceAvatar,
                    request.ModelName,
                    0.0,
                    exportClipStartSeconds,
                    out beginSample,
                    out error))
            {
                return false;
            }

            if (!TrySampleBoundaryPose(
                    request.CurrentClip,
                    request.CurrentTimelineClip,
                    request.SourceAvatar,
                    request.ModelName,
                    1.0,
                    exportClipStartSeconds + endConstraintTime,
                    out endSample,
                    out error))
            {
                return false;
            }

            if (request.IsLoop)
            {
                KimodoInOutConstraintSamplePostProcessor.CopyPoseAxes(beginSample, endSample);
            }

            return true;
        }

        private static bool TrySampleOutsideBoundaryPair(
            KimodoInOutConstraintRequest request,
            out KimodoMarkerSampleResult beginSample,
            out KimodoMarkerSampleResult endSample,
            out string warning,
            out string error)
        {
            beginSample = null;
            endSample = null;
            warning = string.Empty;
            error = string.Empty;

            int generatedFrameCount = KimodoInOutConstraintTimingUtility.ClampFrameCount(request.GenerationFrames);
            double exportClipStartSeconds = request.ExportClipStartSeconds;
            double endConstraintTime = KimodoInOutConstraintTimingUtility.ResolveConstraintEndSampleTimeSeconds(generatedFrameCount);
            bool hasAnyBoundary = false;

            if (request.PreviousClip != null)
            {
                if (!TrySampleBoundaryPose(
                        request.PreviousClip,
                        request.PreviousTimelineClip,
                        request.SourceAvatar,
                        request.ModelName,
                        1.0,
                        exportClipStartSeconds,
                        out beginSample,
                        out error))
                {
                    return false;
                }

                hasAnyBoundary = true;
            }

            if (request.NextClip != null)
            {
                if (!TrySampleBoundaryPose(
                        request.NextClip,
                        request.NextTimelineClip,
                        request.SourceAvatar,
                        request.ModelName,
                        0.0,
                        exportClipStartSeconds + endConstraintTime,
                        out endSample,
                        out error))
                {
                    return false;
                }

                hasAnyBoundary = true;
            }

            if (!hasAnyBoundary)
            {
                warning = "Outside mode has no previous or next clip; boundary samples are skipped.";
            }

            return true;
        }

        private static bool TrySampleBoundaryPose(
            AnimationClip sourceClip,
            TimelineClip timelineClip,
            Avatar sourceAvatar,
            string modelName,
            double normalizedTime,
            double exportedSampleTime,
            out KimodoMarkerSampleResult sample,
            out string error)
        {
            sample = null;
            error = string.Empty;

            if (sourceClip == null)
            {
                error = "Boundary source clip is null.";
                return false;
            }

            double clipSampleTime = KimodoInOutConstraintTimingUtility.ResolveTimelineClipSampleTime(
                timelineClip,
                sourceClip,
                normalizedTime);
            if (!KimodoRetargetToolsEditor.TrySampleMarkerForClip(
                    sourceClip,
                    FullBodyConstraintType,
                    clipSampleTime,
                    sourceAvatar,
                    null,
                    null,
                    KimodoPlayableClip.NormalizeBridgeModelName(modelName),
                    forceRefresh: false,
                    out KimodoMarkerSampleResult sampledPose,
                    out error))
            {
                return false;
            }

            sampledPose.constraintType = FullBodyConstraintType;
            sampledPose.sampleTime = exportedSampleTime;
            sample = sampledPose;
            return true;
        }
    }
}
