using System;
using System.Collections.Generic;
using TimelineInject;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal static class KimodoInOutConstraintComposer
    {
        private const double AutoBeginAnchorWindowSeconds = 1.0;

        internal static bool TryBuild(
            KimodoInOutConstraintRequest request,
            out KimodoInOutConstraintResult result,
            out string warning,
            out string error)
        {
            result = null;
            warning = string.Empty;
            error = string.Empty;

            if (request == null)
            {
                error = "InOut constraint request is null.";
                return false;
            }

            var built = new KimodoInOutConstraintResult();

            if (!KimodoInOutConstraintTools.TrySampleBoundaries(
                    request,
                    out List<KimodoMarkerSampleResult> beginSamples,
                    out List<KimodoMarkerSampleResult> endSamples,
                    out warning,
                    out error))
            {
                return false;
            }

            if (beginSamples.Count > 0)
            {
                // Keep the boundary first so the previous Timeline frame wins same-frame conflicts.
                // ARDY still promotes its single In sample to a ClipConstraint.
                built.CombinedSamples.AddRange(beginSamples);
                built.BeginBoundarySample = beginSamples[0];
            }

            AppendSamples(request.ManualSamples, built.CombinedSamples);

            if (endSamples.Count > 0 &&
                KimodoInOutConstraintTools.ClampFrameCount(request.GenerationFrames) > 1)
            {
                built.CombinedSamples.AddRange(endSamples);
            }

            if (!KimodoGenerationWarningCheckService.TryValidateRootMotionConstraints(
                    built.CombinedSamples,
                    KimodoMotionModelProfiles.ResolveGenerationFrameRate(request.ModelName),
                    out error))
            {
                error = $"Boundary constraints violate root motion limits: {error}";
                return false;
            }

            double normalizationAnchorWindowSeconds = request.AutoBeginAnchor
                ? AutoBeginAnchorWindowSeconds
                : double.PositiveInfinity;
            KimodoMarkerSampleResult autoBegin = null;
            if (request.AutoBeginAnchor &&
                !KimodoConstraintNormalizationUtility.HasNormalizationAnchor(
                    built.CombinedSamples,
                    normalizationAnchorWindowSeconds) &&
                !TryBuildAutoBeginConstraint(request, out autoBegin, out error))
            {
                return false;
            }
            else if (autoBegin != null)
            {
                var withAutoBegin = new List<KimodoMarkerSampleResult>(built.CombinedSamples.Count + 1)
                {
                    autoBegin
                };
                withAutoBegin.AddRange(built.CombinedSamples);
                if (KimodoGenerationWarningCheckService.TryValidateRootMotionConstraints(
                        withAutoBegin,
                        KimodoMotionModelProfiles.ResolveGenerationFrameRate(request.ModelName),
                        out _))
                {
                    built.CombinedSamples.Insert(0, autoBegin);
                    built.HasSyntheticAutoBeginConstraint = true;
                }
            }

            float generationFrameRate = KimodoMotionModelProfiles.ResolveGenerationFrameRate(request.ModelName);
            double clipDurationSeconds = KimodoInOutConstraintTools.ResolveConstraintClipDurationSeconds(
                request.GenerationFrames,
                generationFrameRate);
            Func<KimodoMarkerSampleResult, KimodoConstraintProjectedPose> projectedPoseProjector =
                request.TimelineContext?.Animator != null
                    ? KimodoConstraintExportProjector.Create(request.TimelineContext)
                    : KimodoConstraintExportProjector.CreateProfileNative(request.ModelName);
            built.ConstraintsJson = KimodoConstraintJsonExporter.ToConstraintsJson(
                built.CombinedSamples,
                new KimodoConstraintExportContext
                {
                    // Lightweight editor/tests can construct a track-only
                    // constraint request. Real Timeline generation uses the
                    // bound Character projector above.
                    projectedPoseProjector = projectedPoseProjector
                },
                clipStartSeconds: 0.0,
                clipDurationSeconds: clipDurationSeconds,
                exportFps: generationFrameRate);

            result = built;
            return true;
        }

        internal static void AppendSamples(
            IReadOnlyList<KimodoMarkerSampleResult> source,
            List<KimodoMarkerSampleResult> destination)
        {
            if (source == null || destination == null)
            {
                return;
            }

            for (int i = 0; i < source.Count; i++)
            {
                KimodoMarkerSampleResult sample = source[i];
                if (sample != null)
                {
                    destination.Add(sample.Clone());
                }
            }
        }

        private static bool TryBuildAutoBeginConstraint(
            KimodoInOutConstraintRequest request,
            out KimodoMarkerSampleResult sample,
            out string error)
        {
            sample = null;
            error = string.Empty;
            if (request?.TimelineContext == null)
            {
                error = "Auto Begin constraint requires a Timeline context.";
                return false;
            }

            KimodoTimelineInOutConstraintContext context = request.TimelineContext;
            if (context.Animator == null)
            {
                error = "Auto Begin FullBody constraint requires the bound Character Animator.";
                return false;
            }

            if (!KimodoConstraintPoseUtility.TryCreateRestPoseSample(context, out sample, out error))
                return false;

            sample.constraintMode = "fullbody";
            sample.sampleTime = 0.0;
            sample.enableMask = KimodoConstraintMask.ForType("fullbody");
            sample.sampleValid = true;
            sample.enabled = true;
            return true;
        }

    }
}
