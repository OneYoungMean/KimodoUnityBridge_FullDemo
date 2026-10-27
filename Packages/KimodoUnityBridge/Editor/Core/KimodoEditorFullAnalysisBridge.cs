using TimelineInject;

namespace KimodoBridge.Editor
{
    /// <summary>
    /// One-way bridge from the Inspector assembly to the command analysis implementation.
    /// The Editor assembly does not reference the Command assembly.
    /// </summary>
    internal static class KimodoEditorFullAnalysisBridge
    {
        internal delegate bool AnalyzeHandler(
            KimodoPlayableClip clip,
            out string analysisJson,
            out string error);

        private static AnalyzeHandler analyzeHandler;

        internal static void Register(AnalyzeHandler handler)
        {
            analyzeHandler = handler;
        }

        internal static bool TryAnalyze(
            KimodoPlayableClip clip,
            out string analysisJson,
            out string error)
        {
            analysisJson = string.Empty;
            if (analyzeHandler == null)
            {
                error = "Full animation analysis is not available until the Command editor integration is initialized.";
                return false;
            }

            return analyzeHandler(clip, out analysisJson, out error);
        }
    }
}
