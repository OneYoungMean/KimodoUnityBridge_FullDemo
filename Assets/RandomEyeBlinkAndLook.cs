using UnityEngine;

namespace UnityChan
{
    public sealed class RandomEyeBlinkAndLook : MonoBehaviour
    {
        [Header("Auto Resolve")]
        [SerializeField] private Animator targetAnimator;
        [SerializeField] private string headNodeName = "Head_Def";
        [SerializeField] private string leftBlinkBlendShapeName = "Head_EyeCloseB_L";
        [SerializeField] private string rightBlinkBlendShapeName = "Head_EyeCloseB_R";

        [Header("Blink")]
        [SerializeField] private bool enableBlink = true;
        [SerializeField] private Vector2 blinkIntervalRange = new Vector2(2.0f, 5.0f);
        [SerializeField] private Vector2 blinkCloseDurationRange = new Vector2(0.04f, 0.09f);
        [SerializeField] private Vector2 blinkOpenDurationRange = new Vector2(0.05f, 0.12f);
        [SerializeField][Range(0f, 100f)] private float blinkWeight = 100f;

        [Header("Eye Look")]
        [SerializeField] private bool enableEyeLook = true;
        [SerializeField] private string leftEyeBoneName = "eye_L_old";
        [SerializeField] private string rightEyeBoneName = "eye_R_old";
        [SerializeField] private Vector2 eyeMoveIntervalRange = new Vector2(1.5f, 4.5f);
        [SerializeField] private Vector2 eyeHoldDurationRange = new Vector2(0.2f, 1.0f);
        [SerializeField][Range(0f, 20f)] private float maxYawOffset = 20f;
        [SerializeField][Range(0f, 1f)] private float quickReturnChance = 0.5f;
        [SerializeField] private Vector2 quickReturnDelayRange = new Vector2(0.04f, 0.18f);
        [SerializeField] private float eyeMoveSpeed = 18f;

        private SkinnedMeshRenderer headRenderer;
        private int leftBlinkBlendShapeIndex = -1;
        private int rightBlinkBlendShapeIndex = -1;
        private Transform leftEyeBone;
        private Transform rightEyeBone;
        private Quaternion leftEyeBaseLocalRotation;
        private Quaternion rightEyeBaseLocalRotation;
        private float blinkWeightCurrent;
        private float blinkWeightVelocity;
        private float nextBlinkTime;
        private float blinkPhaseEndTime;
        private BlinkPhase blinkPhase = BlinkPhase.Idle;
        private float nextEyeMoveTime;
        private float eyeReturnTime = -1f;
        private float currentEyeYaw;
        private float targetEyeYaw;

        private enum BlinkPhase
        {
            Idle,
            Closing,
            Opening
        }

        private void Awake()
        {
            AutoResolveReferences();
            ScheduleNextBlink();
            ScheduleNextEyeMove();
        }

        private void OnValidate()
        {
            blinkIntervalRange = SanitizeRange(blinkIntervalRange, 0.01f);
            blinkCloseDurationRange = SanitizeRange(blinkCloseDurationRange, 0.01f);
            blinkOpenDurationRange = SanitizeRange(blinkOpenDurationRange, 0.01f);
            eyeMoveIntervalRange = SanitizeRange(eyeMoveIntervalRange, 0.01f);
            eyeHoldDurationRange = SanitizeRange(eyeHoldDurationRange, 0.01f);
            quickReturnDelayRange = SanitizeRange(quickReturnDelayRange, 0.01f);
            eyeMoveSpeed = Mathf.Max(0.01f, eyeMoveSpeed);
            maxYawOffset = Mathf.Clamp(maxYawOffset, 0f, 20f);
            quickReturnChance = Mathf.Clamp01(quickReturnChance);
            blinkWeight = Mathf.Clamp(blinkWeight, 0f, 100f);
        }

        private void LateUpdate()
        {
            if (enableBlink)
            {
                UpdateBlink();
            }
            else
            {
                ApplyBlinkWeight(0f);
            }

            if (enableEyeLook)
            {
                UpdateEyeLook();
            }
            else
            {
                targetEyeYaw = 0f;
                currentEyeYaw = Mathf.MoveTowards(currentEyeYaw, 0f, eyeMoveSpeed * 60f * Time.deltaTime);
                ApplyEyeYaw(currentEyeYaw);
            }
        }

        private void AutoResolveReferences()
        {
            if (targetAnimator == null)
            {
                targetAnimator = GetComponentInChildren<Animator>();
            }

            Transform headNode = FindChildRecursive(transform, headNodeName);
            headRenderer = headNode != null ? headNode.GetComponent<SkinnedMeshRenderer>() : null;
            if (headRenderer != null && headRenderer.sharedMesh != null)
            {
                leftBlinkBlendShapeIndex = headRenderer.sharedMesh.GetBlendShapeIndex(leftBlinkBlendShapeName);
                rightBlinkBlendShapeIndex = headRenderer.sharedMesh.GetBlendShapeIndex(rightBlinkBlendShapeName);
            }
            else
            {
                leftBlinkBlendShapeIndex = -1;
                rightBlinkBlendShapeIndex = -1;
            }

            leftEyeBone = ResolveEyeBone(leftEyeBoneName, HumanBodyBones.LeftEye);
            rightEyeBone = ResolveEyeBone(rightEyeBoneName, HumanBodyBones.RightEye);
            leftEyeBaseLocalRotation = leftEyeBone != null ? leftEyeBone.localRotation : Quaternion.identity;
            rightEyeBaseLocalRotation = rightEyeBone != null ? rightEyeBone.localRotation : Quaternion.identity;
        }

        private void UpdateBlink()
        {
            float now = Time.time;
            if (blinkPhase == BlinkPhase.Idle && now >= nextBlinkTime)
            {
                blinkPhase = BlinkPhase.Closing;
                blinkPhaseEndTime = now + RandomRange(blinkCloseDurationRange);
            }

            if (blinkPhase == BlinkPhase.Closing)
            {
                blinkWeightCurrent = Mathf.SmoothDamp(blinkWeightCurrent, blinkWeight, ref blinkWeightVelocity, 0.02f);
                if (now >= blinkPhaseEndTime)
                {
                    blinkPhase = BlinkPhase.Opening;
                    blinkPhaseEndTime = now + RandomRange(blinkOpenDurationRange);
                }
            }
            else if (blinkPhase == BlinkPhase.Opening)
            {
                blinkWeightCurrent = Mathf.SmoothDamp(blinkWeightCurrent, 0f, ref blinkWeightVelocity, 0.025f);
                if (now >= blinkPhaseEndTime && blinkWeightCurrent <= 0.5f)
                {
                    blinkPhase = BlinkPhase.Idle;
                    blinkWeightCurrent = 0f;
                    blinkWeightVelocity = 0f;
                    ScheduleNextBlink();
                }
            }

            ApplyBlinkWeight(blinkWeightCurrent);
        }

        private void UpdateEyeLook()
        {
            float now = Time.time;
            if (now >= nextEyeMoveTime)
            {
                targetEyeYaw = Random.Range(-maxYawOffset, maxYawOffset);
                nextEyeMoveTime = now + RandomRange(eyeMoveIntervalRange);
                if (Random.value <= quickReturnChance)
                {
                    eyeReturnTime = now + RandomRange(quickReturnDelayRange);
                }
                else
                {
                    eyeReturnTime = now + RandomRange(eyeHoldDurationRange);
                }
            }

            if (eyeReturnTime > 0f && now >= eyeReturnTime)
            {
                targetEyeYaw = 0f;
                eyeReturnTime = -1f;
            }

            currentEyeYaw = Mathf.MoveTowards(currentEyeYaw, targetEyeYaw, eyeMoveSpeed * Time.deltaTime);
            ApplyEyeYaw(currentEyeYaw);
        }

        private void ApplyBlinkWeight(float weight)
        {
            if (headRenderer == null)
            {
                return;
            }

            float clamped = Mathf.Clamp(weight, 0f, 100f);
            if (leftBlinkBlendShapeIndex >= 0)
            {
                headRenderer.SetBlendShapeWeight(leftBlinkBlendShapeIndex, clamped);
            }

            if (rightBlinkBlendShapeIndex >= 0)
            {
                headRenderer.SetBlendShapeWeight(rightBlinkBlendShapeIndex, clamped);
            }
        }

        private void ApplyEyeYaw(float yaw)
        {
            Quaternion yawRotation = Quaternion.Euler(0f, yaw, 0f);
            if (leftEyeBone != null)
            {
                leftEyeBone.localRotation = leftEyeBaseLocalRotation * yawRotation;
            }

            if (rightEyeBone != null)
            {
                rightEyeBone.localRotation = rightEyeBaseLocalRotation * yawRotation;
            }
        }

        private Transform ResolveEyeBone(string fallbackBoneName, HumanBodyBones humanoidBone)
        {
            if (targetAnimator != null)
            {
                Transform humanoidTransform = targetAnimator.GetBoneTransform(humanoidBone);
                if (humanoidTransform != null)
                {
                    return humanoidTransform;
                }
            }

            return FindChildRecursive(transform, fallbackBoneName);
        }

        private void ScheduleNextBlink()
        {
            nextBlinkTime = Time.time + RandomRange(blinkIntervalRange);
        }

        private void ScheduleNextEyeMove()
        {
            nextEyeMoveTime = Time.time + RandomRange(eyeMoveIntervalRange);
        }

        private static float RandomRange(Vector2 range)
        {
            return Random.Range(range.x, range.y);
        }

        private static Vector2 SanitizeRange(Vector2 range, float minValue)
        {
            float min = Mathf.Max(minValue, Mathf.Min(range.x, range.y));
            float max = Mathf.Max(min, Mathf.Max(range.x, range.y));
            return new Vector2(min, max);
        }

        private static Transform FindChildRecursive(Transform root, string childName)
        {
            if (root == null || string.IsNullOrWhiteSpace(childName))
            {
                return null;
            }

            if (string.Equals(root.name, childName, System.StringComparison.Ordinal))
            {
                return root;
            }

            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                Transform found = FindChildRecursive(child, childName);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }
    }
}
