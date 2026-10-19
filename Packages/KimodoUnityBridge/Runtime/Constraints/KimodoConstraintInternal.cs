using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace KimodoBridge
{
    /// <summary>
    /// Internal protocol constraint created from one canonical SampleResult.
    /// Avatar/retarget details stay behind this boundary; callers only handle
    /// the returned constraint objects and their JSON.
    /// </summary>
    internal abstract class KimodoConstraintInternal
    {
        protected readonly KimodoMarkerSampleResult Sample;
        protected readonly KimodoConstraintExportContext ExportContext;
        protected readonly KimodoConstraintRigType ModelType;

        protected KimodoConstraintInternal(
            KimodoMarkerSampleResult sample,
            KimodoConstraintRigType modelType,
            KimodoConstraintExportContext exportContext)
        {
            Sample = sample;
            ModelType = modelType;
            ExportContext = exportContext;
        }

        internal abstract KimodoConstraintJson ToJsonObject(
            double clipStartSeconds,
            double? clipDurationSeconds,
            double exportFps);

        internal string ToJson(
            double clipStartSeconds = 0.0,
            double? clipDurationSeconds = null,
            double exportFps = 30.0)
        {
            KimodoConstraintJson value = ToJsonObject(
                clipStartSeconds,
                clipDurationSeconds,
                exportFps);
            return value == null
                ? string.Empty
                : JsonConvert.SerializeObject(
                    value,
                    Formatting.Indented,
                    new JsonSerializerSettings
                    {
                        NullValueHandling = NullValueHandling.Ignore
                    });
        }

        /// <summary>
        /// Selects the concrete execution family for a canonical sample.
        /// </summary>
        internal static KimodoConstraintInternal[] GetConstraintInternal(
            KimodoMarkerSampleResult sample,
            KimodoConstraintRigType modelType,
            KimodoConstraintExportContext exportContext)
        {
            if (sample == null || !sample.enabled)
            {
                return Array.Empty<KimodoConstraintInternal>();
            }
            string mode = ValidateSample(sample);
            var result = new List<KimodoConstraintInternal>(4);
            if (string.Equals(mode, "root2d", StringComparison.OrdinalIgnoreCase))
            {
                if (sample.rootOverride != null)
                {
                    result.Add(new KimodoRoot2DConstraintInternal(sample, modelType, exportContext));
                }
            }
            else if (string.Equals(mode, "fullbody", StringComparison.OrdinalIgnoreCase))
            {
                if (sample.sampleData?.IsValid == true)
                {
                    result.Add(new KimodoFullBodyConstraintInternal(sample, modelType, exportContext));
                }
            }
            else if (mode == "effector")
            {
                AddEffectors(result, sample, modelType, exportContext);
            }
            return result.ToArray();
        }

        internal static string ValidateSample(KimodoMarkerSampleResult sample)
        {
            if (sample == null) throw new InvalidOperationException("Constraint sample is null.");
            if (!sample.sampleValid)
            {
                throw new InvalidOperationException("Constraint sample is invalid.");
            }

            string mode = NormalizeMode(sample.constraintMode);
            if (sample.rootOverride == null)
                throw new InvalidOperationException("Constraint sample requires an explicit root override.");
            if (mode == "fullbody" && sample.sampleData?.IsValid != true)
                throw new InvalidOperationException("FullBody constraint requires a valid pose payload.");
            if (mode == "effector" && sample.enableMask?.AnyEndEffector != true)
                throw new InvalidOperationException("Effector constraint requires at least one enabled end effector.");
            return mode;
        }

        internal static string ResolveExecutionMode(KimodoMarkerSampleResult sample)
        {
            string mode = NormalizeMode(sample?.constraintMode);
            return mode;
        }

        private static void AddEffectors(
            List<KimodoConstraintInternal> result,
            KimodoMarkerSampleResult sample,
            KimodoConstraintRigType modelType,
            KimodoConstraintExportContext exportContext)
        {
            if (KimodoConstraintMask.IsActive(sample, "lefthand"))
                result.Add(new KimodoEndEffectorConstraintInternal(sample, modelType, exportContext, "left-hand"));
            if (KimodoConstraintMask.IsActive(sample, "righthand"))
                result.Add(new KimodoEndEffectorConstraintInternal(sample, modelType, exportContext, "right-hand"));
            if (KimodoConstraintMask.IsActive(sample, "leftfoot"))
                result.Add(new KimodoEndEffectorConstraintInternal(sample, modelType, exportContext, "left-foot"));
            if (KimodoConstraintMask.IsActive(sample, "rightfoot"))
                result.Add(new KimodoEndEffectorConstraintInternal(sample, modelType, exportContext, "right-foot"));
        }

        internal static string NormalizeMode(string value)
        {
            string mode = (value ?? string.Empty)
                .Trim().ToLowerInvariant().Replace('_', '-');
            switch (mode)
            {
                case "fullbody": return "fullbody";
                case "root2d": return "root2d";
                case "effector": return "effector";
                default: throw new InvalidOperationException(
                    $"Unsupported constraint mode '{value}'. Use root2d, fullbody, or effector.");
            }
        }
    }

    internal sealed class KimodoFullBodyConstraintInternal : KimodoConstraintInternal
    {
        internal KimodoFullBodyConstraintInternal(
            KimodoMarkerSampleResult sample,
            KimodoConstraintRigType modelType,
            KimodoConstraintExportContext exportContext)
            : base(sample, modelType, exportContext) { }

        internal override KimodoConstraintJson ToJsonObject(
            double clipStartSeconds,
            double? clipDurationSeconds,
            double exportFps)
        {
            return KimodoConstraintJsonExporter.BuildFullBodyInternal(
                Sample, ExportContext, clipStartSeconds, clipDurationSeconds, exportFps);
        }
    }

    internal sealed class KimodoRoot2DConstraintInternal : KimodoConstraintInternal
    {
        internal KimodoRoot2DConstraintInternal(
            KimodoMarkerSampleResult sample,
            KimodoConstraintRigType modelType,
            KimodoConstraintExportContext exportContext)
            : base(sample, modelType, exportContext) { }

        internal override KimodoConstraintJson ToJsonObject(
            double clipStartSeconds,
            double? clipDurationSeconds,
            double exportFps)
        {
            return KimodoConstraintJsonExporter.BuildRoot2DInternal(
                Sample, ExportContext, clipStartSeconds, clipDurationSeconds, exportFps);
        }
    }

    internal sealed class KimodoEndEffectorConstraintInternal : KimodoConstraintInternal
    {
        private readonly string jointType;

        internal KimodoEndEffectorConstraintInternal(
            KimodoMarkerSampleResult sample,
            KimodoConstraintRigType modelType,
            KimodoConstraintExportContext exportContext,
            string jointType)
            : base(sample, modelType, exportContext)
        {
            this.jointType = jointType;
        }

        internal override KimodoConstraintJson ToJsonObject(
            double clipStartSeconds,
            double? clipDurationSeconds,
            double exportFps)
        {
            return KimodoConstraintJsonExporter.BuildEndEffectorInternal(
                Sample, ExportContext, jointType, clipStartSeconds, clipDurationSeconds, exportFps);
        }
    }
}
