using System;
using System.Collections.Generic;
using KimodoBridge;
using UnityEngine;
using UnityEngine.Rendering;

namespace KimodoUnityBridge.Command
{
    internal static partial class command_context
    {
        private static readonly Dictionary<string, AnalysisCacheRecord> AnalysisCache =
            new Dictionary<string, AnalysisCacheRecord>(StringComparer.OrdinalIgnoreCase);
        // Pose samples are expensive (they evaluate a Timeline/retarget
        // sampler). Keep one canonical sample array for the lifetime of an
        // analysis request so trajectory, endpoint checks and rendering all
        // consume the same data.
        private static readonly Dictionary<string, KimodoMarkerSampleResult[]> AnalysisPoseSamples =
            new Dictionary<string, KimodoMarkerSampleResult[]>(StringComparer.OrdinalIgnoreCase);
        private const float StationaryTrajectoryRange = .25f;
        private const int StationaryTrajectoryMinFrames = 10;
        private static readonly Color AnalysisStartFrameTint = new Color(.05f, .90f, .85f, 1f);
        private static readonly Color AnalysisEndFrameTint = new Color(.95f, .08f, .08f, 1f);
        private static readonly Color AnalysisKeyframeTint = new Color(1f, 1f, .02f, 1f);
        private static GameObject captureSessionRoot;

        private static Material CreatePipelineUnlitMaterial(Color color)
        {
            string pipelineName = GraphicsSettings.currentRenderPipeline?.GetType().FullName ?? string.Empty;
            bool isHdrp = pipelineName.IndexOf("HighDefinition", StringComparison.OrdinalIgnoreCase) >= 0 ||
                pipelineName.IndexOf("HDRP", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isUrp = pipelineName.IndexOf("Universal", StringComparison.OrdinalIgnoreCase) >= 0 ||
                pipelineName.IndexOf("URP", StringComparison.OrdinalIgnoreCase) >= 0;
            string shaderName = isHdrp
                ? "HDRP/Unlit"
                : isUrp
                    ? "Universal Render Pipeline/Unlit"
                    : "Sprites/Default";
            Shader shader = Shader.Find(shaderName) ??
                (!isHdrp && !isUrp ? Shader.Find("Standard") : null);
            shader ??= Shader.Find("Sprites/Default") ?? Shader.Find("Standard");
            if (shader == null)
            {
                throw new InvalidOperationException($"No shader is available for analysis evidence lines (preferred {shaderName}).");
            }

            var material = new Material(shader)
            {
                hideFlags = HideFlags.HideAndDontSave,
                color = color
            };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_UnlitColor")) material.SetColor("_UnlitColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            return material;
        }
    }
}
