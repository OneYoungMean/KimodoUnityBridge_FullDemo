using UnityEditor;
using UnityEngine;

namespace KimodoBridge.Editor
{
    [FilePath("ProjectSettings/KimodoPlayableClipGenerationSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    internal sealed class KimodoPlayableClipGenerationSettings : ScriptableSingleton<KimodoPlayableClipGenerationSettings>
    {
        internal const int MinGeneratedClipsLimit = 1;
        internal const int MaxGeneratedClipsLimit = 1000;
        internal const int DefaultGeneratedClipsLimit = 400;
        internal const float MinGenerationTimeoutSeconds = 10f;
        internal const float DefaultGenerationTimeoutSeconds = 600f;
        private const string KeepCpuForceEditorPrefsKey = "KimodoBridge.KeepCpuForceExperimental";

        [SerializeField] private int maxGeneratedClips = DefaultGeneratedClipsLimit;
        [SerializeField] private string localModelsPath = string.Empty;
        [SerializeField] private float generationTimeoutSeconds = DefaultGenerationTimeoutSeconds;
        [SerializeField] private bool floatingUiEnabled = true;
        [SerializeField] private bool keepCpuForceExperimental;
        [SerializeField, HideInInspector] private bool advancedCurveFilterFoldout = true;

        internal int MaxGeneratedClips
        {
            get => Mathf.Clamp(maxGeneratedClips, MinGeneratedClipsLimit, MaxGeneratedClipsLimit);
            set => maxGeneratedClips = Mathf.Clamp(value, MinGeneratedClipsLimit, MaxGeneratedClipsLimit);
        }

        internal string LocalModelsPath
        {
            get => localModelsPath ?? string.Empty;
            set => localModelsPath = value ?? string.Empty;
        }

        internal bool AdvancedCurveFilterFoldout
        {
            get => advancedCurveFilterFoldout;
            set => advancedCurveFilterFoldout = value;
        }

        internal bool FloatingUiEnabled
        {
            get => floatingUiEnabled;
            set => floatingUiEnabled = value;
        }

        internal bool KeepCpuForceExperimental
        {
            get => keepCpuForceExperimental || EditorPrefs.GetBool(KeepCpuForceEditorPrefsKey, false);
            set
            {
                keepCpuForceExperimental = value;
                EditorPrefs.SetBool(KeepCpuForceEditorPrefsKey, value);
            }
        }

        internal float GenerationTimeoutSeconds
        {
            get => Mathf.Max(MinGenerationTimeoutSeconds, generationTimeoutSeconds);
            set => generationTimeoutSeconds = Mathf.Max(MinGenerationTimeoutSeconds, value);
        }

        internal void SaveSettings()
        {
            bool effectiveKeepCpuForce = KeepCpuForceExperimental;
            maxGeneratedClips = Mathf.Clamp(maxGeneratedClips, MinGeneratedClipsLimit, MaxGeneratedClipsLimit);
            localModelsPath = localModelsPath ?? string.Empty;
            generationTimeoutSeconds = Mathf.Max(MinGenerationTimeoutSeconds, generationTimeoutSeconds);
            keepCpuForceExperimental = effectiveKeepCpuForce;
            EditorPrefs.SetBool(KeepCpuForceEditorPrefsKey, effectiveKeepCpuForce);
            Save(true);
        }
    }
}


