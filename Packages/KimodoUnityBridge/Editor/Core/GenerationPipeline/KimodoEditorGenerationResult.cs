using UnityEngine;
using Newtonsoft.Json.Linq;

namespace KimodoBridge.Editor
{
    internal sealed class KimodoEditorGenerationResult
    {
        public string ConstraintsPath;
        public string Prompt;
        public int Seed;
        public string MotionJsonCompact;
        public string AnalysisJson;
        public JObject ConstraintLoss;
        public byte[] MotionBytes;
        public int StartFrame;
        public int EndFrameExclusive;
        public AnimationClip GeneratedClip;
        public AnimationClip RawBoneClip;
    }
}
