using UnityEngine;

namespace UnityChan
{
    public sealed class RandomCameraBreathingMotion : MonoBehaviour
    {
        [Header("Rotation")]
        [SerializeField] private Vector2 pitchAngleRange = new Vector2(-1.2f, 1.2f);
        [SerializeField] private Vector2 yawAngleRange = new Vector2(-1.5f, 1.5f);
        [SerializeField] private Vector2 rollAngleRange = new Vector2(-0.6f, 0.6f);
        [SerializeField] private float rotationBlendSpeed = 1.8f;

        [Header("Timing")]
        [SerializeField] private Vector2 retargetIntervalRange = new Vector2(1.2f, 3.2f);
        [SerializeField] private bool useSineBreathing = true;
        [SerializeField] private float breathingFrequency = 0.35f;
        [SerializeField] private float breathingAmplitude = 0.35f;

        private Quaternion baseLocalRotation;
        private Vector3 currentOffsetEuler;
        private Vector3 targetOffsetEuler;
        private float nextRetargetTime;
        private float breathingSeed;

        private void Awake()
        {
            baseLocalRotation = transform.localRotation;
            breathingSeed = Random.Range(0f, 1000f);
            RetargetOffset();
        }

        private void OnValidate()
        {
            pitchAngleRange = SanitizeRange(pitchAngleRange);
            yawAngleRange = SanitizeRange(yawAngleRange);
            rollAngleRange = SanitizeRange(rollAngleRange);
            retargetIntervalRange = SanitizeRange(retargetIntervalRange, 0.01f);
            rotationBlendSpeed = Mathf.Max(0.01f, rotationBlendSpeed);
            breathingFrequency = Mathf.Max(0f, breathingFrequency);
            breathingAmplitude = Mathf.Max(0f, breathingAmplitude);
        }

        private void LateUpdate()
        {
            if (Time.time >= nextRetargetTime)
            {
                RetargetOffset();
            }

            currentOffsetEuler = Vector3.Lerp(
                currentOffsetEuler,
                targetOffsetEuler,
                1f - Mathf.Exp(-rotationBlendSpeed * Time.deltaTime));

            Vector3 breathingOffset = Vector3.zero;
            if (useSineBreathing && breathingAmplitude > 0f)
            {
                float t = (Time.time + breathingSeed) * Mathf.Max(0f, breathingFrequency) * Mathf.PI * 2f;
                breathingOffset.x = Mathf.Sin(t) * breathingAmplitude;
                breathingOffset.y = Mathf.Sin(t * 0.73f + 0.9f) * breathingAmplitude * 0.6f;
                breathingOffset.z = Mathf.Sin(t * 0.51f + 1.7f) * breathingAmplitude * 0.45f;
            }

            Vector3 finalEuler = currentOffsetEuler + breathingOffset;
            transform.localRotation = baseLocalRotation * Quaternion.Euler(finalEuler);
        }

        private void RetargetOffset()
        {
            targetOffsetEuler = new Vector3(
                Random.Range(pitchAngleRange.x, pitchAngleRange.y),
                Random.Range(yawAngleRange.x, yawAngleRange.y),
                Random.Range(rollAngleRange.x, rollAngleRange.y));
            nextRetargetTime = Time.time + Random.Range(retargetIntervalRange.x, retargetIntervalRange.y);
        }

        private static Vector2 SanitizeRange(Vector2 range, float minValue = float.NegativeInfinity)
        {
            float min = Mathf.Max(minValue, Mathf.Min(range.x, range.y));
            float max = Mathf.Max(min, Mathf.Max(range.x, range.y));
            return new Vector2(min, max);
        }
    }
}
