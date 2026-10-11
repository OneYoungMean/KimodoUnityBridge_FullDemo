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
        /// Selects one concrete protocol family for one canonical sample.
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
            if (!sample.sampleValid)
            {
                throw new InvalidOperationException("Constraint sample is invalid.");
            }

            string mode = ResolveExecutionMode(sample);
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
            else if (string.Equals(mode, "mix", StringComparison.OrdinalIgnoreCase))
            {
                // "mix" is a transient canonical-sample mode produced by
                // Editor pass composition. It is never a public command
                // family: emit typed rows here, then let the raw protocol
                // normalizer fold same-frame Root2D into the root-context row.
                bool hasFamilies = sample.composedFullBody || sample.composedRoot2D || sample.composedEffector;
                bool hasFullBody = sample.composedFullBody ||
                    (!hasFamilies && sample.sampleData?.IsValid == true &&
                     (sample.enableMask?.muscle == true || sample.enableMask?.rootTQ == true ||
                      sample.enableMask?.leftFootTQ == true || sample.enableMask?.rightFootTQ == true));
                bool hasRoot2D = sample.composedRoot2D ||
                    (!hasFamilies && sample.rootOverride != null &&
                     (sample.enableMask?.rootPosition == true || sample.enableMask?.rootHeading == true));
                bool hasEffector = sample.composedEffector || !hasFamilies;

                if (hasFullBody && sample.sampleData?.IsValid == true)
                    result.Add(new KimodoFullBodyConstraintInternal(sample, modelType, exportContext));
                if (hasRoot2D && sample.rootOverride != null)
                    result.Add(new KimodoRoot2DConstraintInternal(sample, modelType, exportContext));
                if (hasEffector)
                    AddEffectors(result, sample, modelType, exportContext);
            }
            else
            {
                if (string.Equals(mode, "left-hand", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(mode, "right-hand", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(mode, "left-foot", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(mode, "right-foot", StringComparison.OrdinalIgnoreCase))
                {
                    AddEffector(result, sample, modelType, exportContext, mode);
                }
                else if (string.Equals(mode, "effector", StringComparison.OrdinalIgnoreCase))
                {
                    AddEffectors(result, sample, modelType, exportContext);
                }
            }
            return result.ToArray();
        }

        internal static string ResolveExecutionMode(KimodoMarkerSampleResult sample)
        {
            string mode = NormalizeMode(sample?.constraintMode);
            return mode;
        }

        internal static KimodoConstraintInternal[] Build(
            KimodoMarkerSampleResult sample,
            KimodoConstraintRigType modelType,
            KimodoConstraintExportContext exportContext)
        {
            return GetConstraintInternal(sample, modelType, exportContext);
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

        private static void AddEffector(
            List<KimodoConstraintInternal> result,
            KimodoMarkerSampleResult sample,
            KimodoConstraintRigType modelType,
            KimodoConstraintExportContext exportContext,
            string type)
        {
            string channel = type.Replace("-", string.Empty);
            if (KimodoConstraintMask.IsActive(sample, channel))
            {
                result.Add(new KimodoEndEffectorConstraintInternal(
                    sample, modelType, exportContext, type));
            }
        }

        internal static string NormalizeMode(string value)
        {
            string mode = (value ?? string.Empty)
                .Trim().ToLowerInvariant().Replace('_', '-');
            if (mode == string.Empty || mode == "constraint") return "fullbody";
            // RootEffect is a public root-plus-effector input. Its execution
            // family is Effector; root support remains in the sample payload.
            if (mode == "root-effect") return "effector";
            return mode == "ik" ? "effector" : mode;
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
