using System;
using System.Collections.Generic;
using TimelineInject;
using UnityEngine;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal static class KimodoTimelineInOutConstraintAdapter
    {
        internal static bool TryBuildConstraintsJson(
            TimelineClip sourceClip,
            KimodoInOutConstraintMode mode,
            bool normalizeConstraintOrigin,
            int generationFrames,
            out string constraintsJson,
            out string error)
        {
            constraintsJson = string.Empty;
            error = string.Empty;

            if (!KimodoTimelineInOutConstraintContextUtility.TryResolve(
                    sourceClip,
                    out KimodoTimelineInOutConstraintContext context,
                    out error))
            {
                return false;
            }

            if (!KimodoTimelineConstraintMarkerSampler.TryBuildMarkerSamplesForExport(
                    context,
                    out List<KimodoMarkerSampleResult> manualSamples,
                    out error))
            {
                return false;
            }

            if (mode == KimodoInOutConstraintMode.Outside &&
                !string.IsNullOrWhiteSpace(context.PreviousClipWarning))
            {
                Debug.LogWarning($"[Kimodo][InOutConstraint] {context.PreviousClipWarning}");
            }

            if (mode == KimodoInOutConstraintMode.Outside &&
                !string.IsNullOrWhiteSpace(context.NextClipWarning))
            {
                Debug.LogWarning($"[Kimodo][InOutConstraint] {context.NextClipWarning}");
            }

            bool shouldNormalizeConstraintOrigin = mode switch
            {
                KimodoInOutConstraintMode.Inside => normalizeConstraintOrigin,
                KimodoInOutConstraintMode.Outside => normalizeConstraintOrigin && context.PreviousClip != null,
                _ => false
            };

            var request = new KimodoInOutConstraintRequest
            {
                Mode = mode,
                PreviousClip = context.PreviousClip,
                CurrentClip = context.CurrentClip,
                NextClip = context.NextClip,
                PreviousTimelineClip = context.PreviousTimelineClip,
                CurrentTimelineClip = context.SourceClip,
                NextTimelineClip = context.NextTimelineClip,
                SourceAvatar = context.SourceAvatar,
                ModelName = context.ModelName,
                GenerationFrames = KimodoInOutConstraintTimingUtility.ClampFrameCount(generationFrames),
                ExportClipStartSeconds = context.SourceClip.start,
                ExportClipDurationSeconds = context.SourceClip.duration,
                NormalizeConstraintOrigin = shouldNormalizeConstraintOrigin,
                ManualSamples = manualSamples
            };

            if (!KimodoInOutConstraintComposer.TryBuild(
                    request,
                    out KimodoInOutConstraintResult result,
                    out string warning,
                    out error))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(warning))
            {
                Debug.LogWarning($"[Kimodo][InOutConstraint] {warning}");
            }

            constraintsJson = result != null ? result.ConstraintsJson ?? string.Empty : string.Empty;
            return true;
        }

        internal static bool TryBuildBoundarySamplesForPreview(
            TimelineClip sourceClip,
            KimodoInOutConstraintMode mode,
            int generationFrames,
            out KimodoMarkerSampleResult beginBoundaryPose,
            out KimodoMarkerSampleResult endBoundaryPose,
            out string warning)
        {
            beginBoundaryPose = null;
            endBoundaryPose = null;
            warning = string.Empty;

            if (mode == KimodoInOutConstraintMode.None)
            {
                return true;
            }

            if (!KimodoTimelineInOutConstraintContextUtility.TryResolve(
                    sourceClip,
                    out KimodoTimelineInOutConstraintContext context,
                    out string error))
            {
                warning = error;
                return false;
            }

            if (mode == KimodoInOutConstraintMode.Outside &&
                context.PreviousClip == null &&
                context.NextClip == null)
            {
                warning = !string.IsNullOrWhiteSpace(context.PreviousClipWarning)
                    ? context.PreviousClipWarning
                    : (!string.IsNullOrWhiteSpace(context.NextClipWarning) ? context.NextClipWarning : "no neighboring clips found, skip boundary preview.");
                return true;
            }

            var request = new KimodoInOutConstraintRequest
            {
                Mode = mode,
                PreviousClip = context.PreviousClip,
                CurrentClip = context.CurrentClip,
                NextClip = context.NextClip,
                PreviousTimelineClip = context.PreviousTimelineClip,
                CurrentTimelineClip = context.SourceClip,
                NextTimelineClip = context.NextTimelineClip,
                SourceAvatar = context.SourceAvatar,
                ModelName = context.ModelName,
                GenerationFrames = KimodoInOutConstraintTimingUtility.ClampFrameCount(generationFrames),
                ExportClipStartSeconds = context.SourceClip.start,
                ExportClipDurationSeconds = context.SourceClip.duration,
                NormalizeConstraintOrigin = false,
            };

            if (!KimodoInOutConstraintComposer.TryBuild(
                    request,
                    out KimodoInOutConstraintResult result,
                    out string buildWarning,
                    out string buildError))
            {
                warning = buildError;
                return false;
            }

            beginBoundaryPose = result?.BeginSample;
            endBoundaryPose = result?.EndSample;

            warning = mode == KimodoInOutConstraintMode.Outside
                ? KimodoTimelineInOutConstraintContextUtility.FirstNonEmpty(
                    context.PreviousClipWarning,
                    context.NextClipWarning,
                    buildWarning)
                : buildWarning;
            return true;
        }
    }
}
