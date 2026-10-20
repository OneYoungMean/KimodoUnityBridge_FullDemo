using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KimodoUnityBridge;
using KimodoBridge;
using TimelineInject;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

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
        private static readonly Dictionary<string, PrecomputedAnalysis> PrecomputedAnalyses =
            new Dictionary<string, PrecomputedAnalysis>(StringComparer.OrdinalIgnoreCase);

        private sealed class PrecomputedAnalysis
        {
            public JObject Analysis;
            public KimodoMarkerSampleResult[] Samples;
        }

        private const float StationaryTrajectoryRange = .25f;
        private const int StationaryTrajectoryMinFrames = 10;
        private static readonly Color AnalysisStartFrameTint = new Color(.05f, .90f, .85f, 1f);
        private static readonly Color AnalysisEndFrameTint = new Color(.95f, .08f, .08f, 1f);
        private static readonly Color AnalysisKeyframeTint = new Color(1f, 1f, .02f, 1f);
        private static GameObject captureSessionRoot;
    }
}
