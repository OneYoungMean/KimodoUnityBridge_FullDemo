using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json.Linq;

namespace KimodoBridge.Editor
{
    internal sealed class KimodoDebugConstraintPass
    {
        public string Name;
        public string AddedConstraintsJson;
        public string CombinedConstraintsJson;
        public string ModelName;
        public int FrameRate;
        public int FrameCount;
        internal KimodoPlayableClip DebugClipTarget;
    }

    internal sealed class KimodoEditorGenerationResult
    {
        public string ConstraintsPath;
        public string Prompt;
        public int Seed;
        public string MotionJsonCompact;
        public string AnalysisJson;
        public string PostAnalysisJson;
        public string PostAnalysisError;
        public bool PostAnalysisSkipped;
        public JObject ConstraintLoss;
        public byte[] MotionBytes;
        public int StartFrame;
        public int EndFrameExclusive;
        public AnimationClip GeneratedClip;
        public AnimationClip RawBoneClip;
        public List<KimodoDebugConstraintPass> DebugConstraintPasses = new List<KimodoDebugConstraintPass>();
    }
}
