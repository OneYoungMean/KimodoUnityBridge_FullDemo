using System;
using UnityEngine;
using UnityEngine.Timeline;

namespace KimodoBridge
{
    /// <summary>
    /// Passive debug metadata placed at the timeline time of one raw constraint.
    /// It is deliberately separate from KimodoConstraintMarker, so it cannot
    /// participate in generation or constraint sampling.
    /// </summary>
    [Serializable]
    public sealed class KimodoDebugConstraintJsonMarker : Marker
    {
        [SerializeField] private string passName = string.Empty;
        [SerializeField, TextArea(3, 16)] private string addedConstraintsJson = string.Empty;
        [SerializeField, TextArea(3, 16)] private string combinedConstraintsJson = string.Empty;
        [SerializeField] private string modelName = string.Empty;
        [SerializeField] private int frameRate;
        [SerializeField] private int frameCount;

        public string PassName => passName;
        public string AddedConstraintsJson => addedConstraintsJson;
        public string CombinedConstraintsJson => combinedConstraintsJson;
        public string ModelName => modelName;
        public int FrameRate => frameRate;
        public int FrameCount => frameCount;

        public void SetData(
            string passNameValue,
            string addedJson,
            string combinedJson,
            string modelNameValue,
            int frameRateValue,
            int frameCountValue)
        {
            passName = passNameValue ?? string.Empty;
            addedConstraintsJson = addedJson ?? string.Empty;
            combinedConstraintsJson = combinedJson ?? string.Empty;
            modelName = modelNameValue ?? string.Empty;
            frameRate = frameRateValue;
            frameCount = frameCountValue;
        }
    }
}
