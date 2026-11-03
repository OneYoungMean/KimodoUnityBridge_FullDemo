using UnityEngine;

namespace KimodoBridge
{
    /// <summary>
    /// Disables an editor-only Session root during Play Mode and restores its
    /// original active state when the Editor returns to Edit Mode.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Kimodo/Session Runtime Guard")]
    internal sealed class KimodoDisableOnRuntime : MonoBehaviour
    {
        [SerializeField, HideInInspector] private bool activeBeforePlay;
        [SerializeField, HideInInspector] private bool hasCapturedPlayState;

        internal static KimodoDisableOnRuntime Ensure(GameObject root)
        {
            if (root == null) return null;
            return root.GetComponent<KimodoDisableOnRuntime>() ?? root.AddComponent<KimodoDisableOnRuntime>();
        }

        internal void DisableForPlay()
        {
            if (!hasCapturedPlayState)
            {
                activeBeforePlay = gameObject.activeSelf;
                hasCapturedPlayState = true;
            }

            if (gameObject.activeSelf)
            {
                gameObject.SetActive(false);
            }
        }

        internal void RestoreAfterPlay()
        {
            if (!hasCapturedPlayState) return;
            bool restoreActive = activeBeforePlay;
            hasCapturedPlayState = false;
            if (gameObject.activeSelf != restoreActive)
            {
                gameObject.SetActive(restoreActive);
            }
        }

        private void Awake()
        {
            if (Application.isPlaying) DisableForPlay();
        }

        private void OnEnable()
        {
            if (Application.isPlaying) DisableForPlay();
        }
    }
}
