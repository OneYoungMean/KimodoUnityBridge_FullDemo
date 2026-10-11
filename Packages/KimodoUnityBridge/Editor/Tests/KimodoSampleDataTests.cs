using System;
using System.Collections.Generic;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using TimelineInject;
using UnityEngine;
using KimodoUnityBridge;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoSampleDataTests
    {
        [Test, Category("BridgeRegression"), Ignore("Legacy Mix execution mapping was removed.")]
        public void LegacyMix_ResolvesToOneHighestPriorityFamily()
        {
            KimodoMarkerSampleResult sample = CreateFullBody(1, 0.5f, true);
            sample.constraintMode = "mix";
            sample.enableMask.rootPosition = true;
            sample.enableMask.rootHeading = true;
            sample.validMask.rootPosition = true;
            sample.validMask.rootHeading = true;
            EnableAllEffectors(sample);
            CollectionAssert.AreEqual(new[] { "fullbody" }, ResolveTypes(sample));

            sample.enableMask.muscle = false;
            sample.validMask.muscle = true;
            CollectionAssert.AreEqual(new[] { "left-hand", "right-hand", "left-foot", "right-foot" }, ResolveTypes(sample));

            sample.enableMask.leftHand = false;
            sample.enableMask.rightHand = false;
            sample.enableMask.leftFoot = false;
            sample.enableMask.rightFoot = false;
            CollectionAssert.AreEqual(new[] { "root2d" }, ResolveTypes(sample));
        }

        [Test, Category("BridgeRegression")]
        public void Composer_MergesDifferentChannelsAtSameFrame()
        {
            KimodoMarkerSampleResult fullBody = CreateFullBody(1, 0.5f, true);
            KimodoMarkerSampleResult root2d = CreateFullBody(2, 0.5f, true);
            fullBody.constraintMode = "fullbody";
            root2d.constraintMode = "root2d";
            root2d.enableMask = KimodoConstraintMask.ForType("root2d");
            root2d.rootOverride = new KimodoRigidTransform();
            root2d.rootOverride.t = new UnityEngine.Vector3(4f, 0f, 5f);
            root2d.validMask = KimodoConstraintMask.ForType("root2d");
            var composed = KimodoConstraintSampleComposer.ComposeCanonicalSamples(
                new[] { fullBody, root2d }, 30.0);
            Assert.That(composed, Has.Count.EqualTo(1));
            Assert.That(composed[0].constraintMode, Is.EqualTo("mix"));
            Assert.That(composed[0].enableMask.muscle, Is.True);
            Assert.That(composed[0].enableMask.rootPosition, Is.True);
        }

        [Test, Category("BridgeRegression")]
        public void Exporter_EmitsTypedRowsForSameFrameRoot2DAndFullBody()
        {
            KimodoMarkerSampleResult fullBody = CreateFullBody(1, 0.5f, true);
            fullBody.constraintMode = "fullbody";
            fullBody.sampleTime = 1.0 / 30.0;

            KimodoMarkerSampleResult root2d = CreateFullBody(2, 0.5f, true);
            root2d.constraintMode = "root2d";
            root2d.enableMask = KimodoConstraintMask.ForType("root2d");
            root2d.rootOverride = new KimodoRigidTransform
            {
                t = new Vector3(4f, 7f, 5f),
                q = Quaternion.Euler(0f, 90f, 0f)
            };
            root2d.sampleTime = fullBody.sampleTime;

            string json = KimodoConstraintJsonExporter.ToConstraintsJson(
                new[] { fullBody, root2d },
                new KimodoConstraintExportContext
                {
                    projectedPoseProjector = sample =>
                    {
                        KimodoConstraintProjectedPose projected = CreateProjectedTestPose(sample);
                        projected.jointRotations[0] = Quaternion.Euler(10f, 90f, 5f);
                        projected.localJointAngles[0] =
                            KimodoConstraintRotationUtility.QuaternionToAxisAngleVector(
                                projected.jointRotations[0]);
                        // Model the solved FullBody pose retaining its own
                        // root height while the Root2D request carries a
                        // deliberately different Y value.
                        projected.profileRootPosition = new Vector3(
                            sample.rootOverride.t.x, 0.25f, sample.rootOverride.t.z);
                        return projected;
                    }
                },
                exportFps: 30.0);
            string serialized = new KimodoConstraintPayload { json = json }.Serialize(
                KimodoMotionModelProfiles.DefaultModelName,
                new List<byte[]>());
            JArray rows = JArray.Parse(serialized);
            Assert.That(rows, Has.Count.EqualTo(1));
            Assert.That(rows[0]["type"].Value<string>(), Is.EqualTo("fullbody"));
            Assert.That(rows[0]["root_positions"][0][0].Value<float>(), Is.EqualTo(-4f));
            Assert.That(rows[0]["root_positions"][0][1].Value<float>(), Is.EqualTo(0.25f));
            Assert.That(rows[0]["root_positions"][0][2].Value<float>(), Is.EqualTo(5f));
            Assert.That(rows[0]["smooth_root_2d"][0][0].Value<float>(), Is.EqualTo(-4f));
            Assert.That(rows[0]["smooth_root_2d"][0][1].Value<float>(), Is.EqualTo(5f));
            Vector3 protocolRoot = new Vector3(
                rows[0]["local_joints_rot"][0][0][0].Value<float>(),
                rows[0]["local_joints_rot"][0][0][1].Value<float>(),
                rows[0]["local_joints_rot"][0][0][2].Value<float>());
            Quaternion kimodoRoot = KimodoConstraintRotationUtility.AxisAngleVectorToQuaternion(protocolRoot);
            Quaternion unityRoot = new Quaternion(
                kimodoRoot.x, -kimodoRoot.y, -kimodoRoot.z, kimodoRoot.w);
            Assert.That(
                Mathf.Abs(Mathf.DeltaAngle(90f, KimodoMotionMath.ResolvePlanarYawDegrees(unityRoot))),
                Is.LessThan(1e-2f));
        }

        [Test, Category("BridgeRegression")]
        public void SampleValid_BlocksOuterConstraintConstruction()
        {
            KimodoMarkerSampleResult invalid = CreateFullBody(1, 0.5f, true);
            invalid.sampleValid = false;
            invalid.validMask = KimodoConstraintMask.ForType("fullbody");

            Assert.Throws<InvalidOperationException>(() => KimodoConstraintInternal.GetConstraintInternal(
                invalid, KimodoConstraintRigType.Soma77, null));
            Assert.Throws<InvalidOperationException>(() =>
                KimodoConstraintSampleComposer.ComposeCanonicalSamples(new[] { invalid }, 30.0));
        }

        [Test, Category("BridgeRegression")]
        public void SampleValid_TrueDoesNotDependOnLegacyChannelValidity()
        {
            KimodoMarkerSampleResult sample = CreateFullBody(1, 0.5f, true);
            sample.constraintMode = "fullbody";
            sample.sampleValid = true;
            sample.enableMask.muscle = true;
            sample.validMask.muscle = false;

            CollectionAssert.AreEqual(new[] { "fullbody" }, ResolveTypes(sample));
        }

        [Test, Category("BridgeRegression")]
        public void CommandConstraintFrames_RejectDuplicatesAndPrioritizeRootPathCollision()
        {
            var duplicatePoints = new JArray
            {
                new JObject { ["frame"] = 2, ["type"] = "root2d" },
                new JObject { ["frame"] = 2, ["type"] = "fullbody" }
            };
            StringAssert.Contains("undefined", KimodoUnityBridge.Command.command_context
                .ValidateUniqueConstraintFrames(duplicatePoints, 10));

            var rootPathWinsFrameCheck = new JArray
            {
                new JObject { ["frame"] = 0, ["root_path"] = new JObject() },
                new JObject { ["frame"] = 0, ["type"] = "root2d" }
            };
            StringAssert.Contains("root_path has priority", KimodoUnityBridge.Command.command_context
                .ValidateUniqueConstraintFrames(rootPathWinsFrameCheck, 10));

            var pointBeforeRootPath = new JArray
            {
                new JObject { ["frame"] = 0, ["type"] = "root2d" },
                new JObject { ["frame"] = 0, ["root_path"] = new JObject() }
            };
            StringAssert.Contains("root_path has priority", KimodoUnityBridge.Command.command_context
                .ValidateUniqueConstraintFrames(pointBeforeRootPath, 10));

            var endpointCollision = new JArray
            {
                new JObject { ["frame"] = 9, ["type"] = "root2d" },
                new JObject { ["frame"] = 0, ["root_path"] = new JObject() }
            };
            StringAssert.Contains("root_path has priority", KimodoUnityBridge.Command.command_context
                .ValidateUniqueConstraintFrames(endpointCollision, 10));
        }

        // Archived legacy expectation: Mix with no declared channels no longer
        // controls execution through the old mask fallback.
        [Test, Category("BridgeRegression"), Ignore("Legacy Mix mask fallback; move to archive with the destructive upgrade.")]
        public void MixDefaults_DoNotEnableUndeclaredChannels()
        {
            Assert.That(KimodoConstraintMask.ForType("mix").IsEmpty, Is.True);
            var sample = new KimodoMarkerSampleResult { constraintMode = "mix" };
            EnableAllEffectors(sample);
            sample.enableMask = KimodoConstraintMask.ForType("mix");
            Assert.That(ResolveTypes(sample), Is.Empty);
        }

        [Test, Category("BridgeRegression"), Ignore("Legacy Mix writeback path was removed.")]
        public void MixAutoSampleWriteback_PreservesIntentAndZeroTargets()
        {
            var marker = ScriptableObject.CreateInstance<KimodoConstraintMarker>();
            try
            {
                marker.ConstraintMode = KimodoConstraintMode.Mix;
                marker.autoSample = true;
                marker.SampleData.enableMask = new KimodoConstraintMask { leftHand = true };
                marker.SampleData.effectors.leftHand.t = Vector3.one;
                var sampled = CreateFullBody(1, 0f, true);
                EnableAllEffectors(sampled);
                Assert.That(KimodoMarkerSamplingEditorUtility.TryWriteConstraintMarkerSample(
                    marker, sampled, out string error), Is.True, error);
                Assert.That(marker.SampleData.constraintMode, Is.EqualTo("mix"));
                Assert.That(marker.SampleData.enableMask.muscle, Is.False);
                Assert.That(marker.SampleData.enableMask.rightHand, Is.False);
                Assert.That(marker.SampleData.enableMask.leftFoot, Is.False);
                Assert.That(marker.SampleData.enableMask.rightFoot, Is.False);
                Assert.That(marker.SampleData.effectors.leftHand.t, Is.EqualTo(Vector3.zero));
                CollectionAssert.AreEqual(new[] { "left-hand" }, ResolveTypes(marker.SampleData));
            }
            finally { UnityEngine.Object.DestroyImmediate(marker); }
        }

        // Archived legacy expectation: same-frame samples are now rejected at the outer boundary.
        [TestCase(true), TestCase(false), Category("BridgeRegression"), Ignore("Legacy Mix writeback/composer semantics; move to archive with the destructive upgrade.")]
        public void MixManualWriteback_PreservesExplicitEffectorSwitch(bool enabled)
        {
            var marker = ScriptableObject.CreateInstance<KimodoConstraintMarker>();
            try
            {
                marker.ConstraintMode = KimodoConstraintMode.Mix;
                marker.autoSample = false;
                marker.SampleData.enableMask = new KimodoConstraintMask { leftHand = !enabled };
                marker.SampleData.validMask = new KimodoConstraintMask { leftHand = true };
                var edited = marker.SampleData.Clone();
                edited.enableMask.leftHand = enabled;
                Assert.That(KimodoMarkerSamplingEditorUtility.TryWriteConstraintMarkerSample(
                    marker, edited, out string error), Is.True, error);
                CollectionAssert.AreEqual(enabled ? new[] { "left-hand" } : Array.Empty<string>(),
                    ResolveTypes(marker.SampleData));
                var composed = KimodoConstraintSampleComposer.ComposeCanonicalSamples(
                    new[] { edited, marker.SampleData }, 30.0);
                CollectionAssert.AreEqual(enabled ? new[] { "left-hand" } : Array.Empty<string>(),
                    ResolveTypes(composed[0]));
            }
            finally { UnityEngine.Object.DestroyImmediate(marker); }
        }

        [Test]
        public void SampleDataLayout_Uses70ValuesAndRoundTripsTransforms()
        {
            float[] data = KimodoSampleDataLayout.CreateBuffer();
            Assert.That(data, Has.Length.EqualTo(70));
            KimodoSampleDataLayout.SetTransform(
                data,
                KimodoSampleDataLayout.RootTqOffset,
                new Vector3(1f, 2f, 3f),
                Quaternion.Euler(0f, 45f, 0f));

            KimodoSampleDataLayout.GetTransform(
                data,
                KimodoSampleDataLayout.RootTqOffset,
                out Vector3 position,
                out Quaternion rotation);

            Assert.That(position, Is.EqualTo(new Vector3(1f, 2f, 3f)));
            Assert.That(Quaternion.Angle(rotation, Quaternion.Euler(0f, 45f, 0f)), Is.LessThan(1e-4f));
        }

        // Archived legacy expectation: same-frame channel fallback is no longer valid.
        [Test, Ignore("Legacy same-frame composer fallback; move to archive with the destructive upgrade.")]
        public void Composer_LastCreatedInvalidChannelWinsWithoutFallback()
        {
            KimodoMarkerSampleResult first = CreateFullBody(1, 0.25f, true);
            KimodoMarkerSampleResult lastInvalid = CreateFullBody(2, 9f, false);

            var composed = KimodoConstraintSampleComposer.ComposeCanonicalSamples(
                new[] { first, lastInvalid },
                60.0);

            Assert.That(composed, Has.Count.EqualTo(1));
            Assert.That(composed[0].enableMask.muscle, Is.False);
            Assert.That(composed[0].sampleData.data[KimodoSampleDataLayout.BodyMuscleOffset], Is.EqualTo(0f));
        }

        // Archived legacy expectation: per-channel validMask controlled activation.
        [Test, Ignore("Legacy per-channel validity semantics; sampleValid now gates the complete sample.")]
        public void MissingValidMask_DoesNotFallBackToEnableMask()
        {
            var sample = new KimodoMarkerSampleResult
            {
                enableMask = new KimodoConstraintMask { muscle = true },
                validMask = null,
                constraintMode = "fullbody"
            };
            Assert.That(KimodoConstraintMask.FromSample(sample).muscle, Is.False);
            Assert.That(ResolveTypes(sample), Is.Empty);
        }

        [Test]
        public void AutoSample_UsesMarkerEnableIntentAndSampleValidity()
        {
            KimodoConstraintMarker marker = ScriptableObject.CreateInstance<KimodoConstraintMarker>();
            try
            {
                marker.autoSample = true;
                marker.ConstraintMode = KimodoConstraintMode.FullBody;
                marker.SampleData.enableMask = new KimodoConstraintMask
                {
                    muscle = true,
                    rootPosition = true,
                    rootHeading = true
                };
                var sampled = new KimodoMarkerSampleResult
                {
                    sampleData = new MuscleSample(),
                    enableMask = new KimodoConstraintMask(),
                    validMask = new KimodoConstraintMask
                    {
                        muscle = true,
                        rootPosition = true,
                        rootHeading = true,
                        leftHand = true
                    }
                };

                KimodoMarkerSampleResult normalized =
                    KimodoMarkerSamplingUtility.NormalizeConstraintMarkerSample(marker, sampled);

                Assert.That(normalized.enableMask.muscle, Is.True);
                Assert.That(normalized.enableMask.rootPosition, Is.True);
                Assert.That(normalized.enableMask.rootHeading, Is.True);
                Assert.That(normalized.enableMask.leftHand, Is.False);
                Assert.That(normalized.validMask.leftHand, Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(marker);
            }
        }

        [Test]
        public void AutoSample_KeepsNewEffectorTargetsInLocalSpace()
        {
            KimodoConstraintMarker marker = ScriptableObject.CreateInstance<KimodoConstraintMarker>();
            try
            {
                marker.autoSample = true;
                marker.ConstraintMode = KimodoConstraintMode.Effector;
                KimodoRigidTransform authoredRoot = marker.SampleData.rootOverride;
                authoredRoot.position = new Vector3(2f, 1f, -3f);
                authoredRoot.rotation = Quaternion.Euler(0f, 40f, 0f);
                marker.SampleData.rootOverride = authoredRoot;

                var sampled = new KimodoMarkerSampleResult
                {
                    sampleValid = true,
                    constraintMode = "effector",
                    rootOverride = new KimodoRigidTransform
                    {
                        position = authoredRoot.position,
                        rotation = authoredRoot.rotation
                    },
                    effectors = new KimodoConstraintEffectors
                    {
                        leftHand = new KimodoRigidTransform
                        {
                            position = new Vector3(3f, 2f, -1f),
                            rotation = Quaternion.Euler(10f, 20f, 30f),
                            space = KimodoConstraintSpace.World
                        }
                    }
                };
                Vector3 sampledWorldPosition = sampled.effectors.leftHand.position;
                Quaternion sampledWorldRotation = sampled.effectors.leftHand.rotation;

                Assert.That(KimodoMarkerSamplingEditorUtility.TryWriteConstraintMarkerSample(
                    marker, sampled, out string error), Is.True, error);

                KimodoRigidTransform stored = marker.SampleData.effectors.leftHand;
                Assert.That(stored.space, Is.EqualTo(KimodoConstraintSpace.Local));
                Assert.That(Vector3.Distance(
                    KimodoConstraintSpaceUtility.ToWorldPosition(stored.position, stored.space,
                        marker.SampleData.rootOverride), sampledWorldPosition), Is.LessThan(0.001f));
                Assert.That(Quaternion.Angle(
                    KimodoConstraintSpaceUtility.ToWorldRotation(stored.rotation, stored.space,
                        marker.SampleData.rootOverride), sampledWorldRotation), Is.LessThan(0.001f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(marker);
            }
        }

        [Test, Ignore("Archived legacy carry serialization behavior; carry option was removed.")]
        public void AutoSample_PreservesCarryEffectorsWithRootIntent()
        {
            KimodoConstraintMarker marker = ScriptableObject.CreateInstance<KimodoConstraintMarker>();
            try
            {
                marker.autoSample = true;
                marker.ConstraintMode = KimodoConstraintMode.FullBody;
                marker.SampleData.carryEffectorsWithRoot = true;

                var sampled = new KimodoMarkerSampleResult
                {
                    constraintMode = "fullbody",
                    carryEffectorsWithRoot = false,
                    sampleData = new MuscleSample(),
                    enableMask = new KimodoConstraintMask(),
                    validMask = new KimodoConstraintMask { muscle = true }
                };

                KimodoMarkerSampleResult normalized =
                    KimodoMarkerSamplingUtility.NormalizeConstraintMarkerSample(marker, sampled);

                Assert.That(normalized.carryEffectorsWithRoot, Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(marker);
            }
        }

        [Test]
        public void EffectorSpace_LocalWorldRoundTripPreservesPose()
        {
            KimodoRigidTransform root = new KimodoRigidTransform
            {
                position = new Vector3(3f, 1f, -2f),
                rotation = Quaternion.Euler(0f, 45f, 0f)
            };
            KimodoRigidTransform value = new KimodoRigidTransform
            {
                position = new Vector3(1f, 2f, 3f),
                rotation = Quaternion.Euler(10f, 20f, 30f),
                enableRotation = true,
                space = KimodoConstraintSpace.Local
            };
            Vector3 originalPosition = value.position;
            Quaternion originalRotation = value.rotation;
            KimodoConstraintSpaceUtility.ConvertSpace(value, root, KimodoConstraintSpace.World);
            Vector3 worldPosition = value.position;
            Quaternion worldRotation = value.rotation;
            KimodoConstraintSpaceUtility.ConvertSpace(value, root, KimodoConstraintSpace.Local);
            Assert.That(Vector3.Distance(value.position, originalPosition), Is.LessThan(0.001f));
            Assert.That(Quaternion.Angle(value.rotation, originalRotation), Is.LessThan(0.001f));
            Assert.That(Vector3.Distance(KimodoConstraintSpaceUtility.ToWorldPosition(value.position, value.space, root), worldPosition), Is.LessThan(0.001f));
            Assert.That(Quaternion.Angle(KimodoConstraintSpaceUtility.ToWorldRotation(value.rotation, value.space, root), worldRotation), Is.LessThan(0.001f));
        }

        [Test]
        public void RootEditDoesNotModifyEffectorPayload()
        {
            KimodoMarkerSampleResult sample = new KimodoMarkerSampleResult
            {
                rootOverride = new KimodoRigidTransform { position = Vector3.zero, rotation = Quaternion.identity },
                effectors = new KimodoConstraintEffectors
                {
                    leftHand = new KimodoRigidTransform { position = new Vector3(1f, 2f, 3f), rotation = Quaternion.Euler(1f, 2f, 3f) }
                }
            };
            Vector3 position = sample.effectors.leftHand.position;
            Quaternion rotation = sample.effectors.leftHand.rotation;
            sample.rootOverride.position = new Vector3(4f, 0f, 5f);
            sample.rootOverride.rotation = Quaternion.Euler(0f, 90f, 0f);
            Assert.That(sample.effectors.leftHand.position, Is.EqualTo(position));
            Assert.That(Quaternion.Angle(sample.effectors.leftHand.rotation, rotation), Is.LessThan(0.001f));
        }

        [Test]
        public void CommandAndUiCanonicalSamplesPreserveConcreteIntent()
        {
            var commandSample = CreateFullBody(1, 0.75f, true);
            commandSample.constraintMode = "effector";
            commandSample.rootOverride = new KimodoUnityBridge.KimodoRigidTransform
            {
                t = new Vector3(1f, 2f, 3f),
                q = Quaternion.Euler(0f, 35f, 0f)
            };
            commandSample.effectors.leftHand.t = new Vector3(2f, 3f, 4f);
            commandSample.enableMask = new KimodoConstraintMask
            {
                rootHeading = true,
                leftHand = true
            };
            commandSample.validMask = new KimodoConstraintMask
            {
                rootPosition = true,
                rootHeading = true,
                leftHand = true
            };

            var marker = ScriptableObject.CreateInstance<KimodoConstraintMarker>();
            try
            {
                marker.autoSample = false;
                marker.ConstraintMode = KimodoConstraintMode.Effector;
                Assert.That(
                    KimodoMarkerSamplingEditorUtility.TryWriteConstraintMarkerSample(
                        marker, commandSample.Clone(), out string error),
                    Is.True, error);

                KimodoMarkerSampleResult uiSample = marker.SampleData;
                KimodoMarkerSampleResult commandCanonical =
                    KimodoConstraintSampleComposer.ResolveUnifiedSample(commandSample);

                Assert.That(uiSample.sampleValid, Is.EqualTo(commandCanonical.sampleValid));
                Assert.That(uiSample.constraintMode, Is.EqualTo(commandCanonical.constraintMode));
                Assert.That(uiSample.enableMask.leftHand, Is.EqualTo(commandCanonical.enableMask.leftHand));
                Assert.That(uiSample.enableMask.rootHeading, Is.EqualTo(commandCanonical.enableMask.rootHeading));
                Assert.That(uiSample.effectors.leftHand.t, Is.EqualTo(commandCanonical.effectors.leftHand.t));
                Assert.That(uiSample.rootOverride.t, Is.EqualTo(commandCanonical.rootOverride.t));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(marker);
            }
        }

        [Test]
        public void NewConstraintMarker_DefaultsOnlyItsFullBodyIntent()
        {
            KimodoConstraintMarker marker = ScriptableObject.CreateInstance<KimodoConstraintMarker>();
            try
            {
                Assert.That(marker.SampleData.sampleValid, Is.True);
                Assert.That(marker.SampleData.enableMask.muscle, Is.False);
                Assert.That(marker.SampleData.enableMask.rootPosition, Is.False);
                Assert.That(marker.SampleData.enableMask.rootHeading, Is.True);
                Assert.That(marker.SampleData.enableMask.AnyEndEffector, Is.False);
                Assert.That(marker.SampleData.effectors.leftHand.space, Is.EqualTo(KimodoConstraintSpace.Local));
                Assert.That(marker.SampleData.effectors.rightHand.space, Is.EqualTo(KimodoConstraintSpace.Local));
                Assert.That(marker.SampleData.effectors.leftFoot.space, Is.EqualTo(KimodoConstraintSpace.Local));
                Assert.That(marker.SampleData.effectors.rightFoot.space, Is.EqualTo(KimodoConstraintSpace.Local));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(marker);
            }
        }

        [Test]
        public void ConstraintWriteback_PreservesDraggedRoot2DOverride()
        {
            KimodoConstraintMarker marker = ScriptableObject.CreateInstance<KimodoConstraintMarker>();
            try
            {
                marker.autoSample = false;
                marker.ConstraintMode = KimodoConstraintMode.Root2D;
                marker.SampleData.rootOverride = new KimodoUnityBridge.KimodoRigidTransform
                {
                    position = new Vector3(1f, 2f, 3f),
                    rotation = Quaternion.Euler(0f, 15f, 0f)
                };
                marker.SampleData.enableMask.rootPosition = true;
                marker.SampleData.validMask.rootPosition = true;

                KimodoMarkerSampleResult dragged = marker.SampleData.Clone();
                dragged.rootOverride.position = new Vector3(8f, 9f, 10f);
                dragged.rootOverride.rotation = Quaternion.Euler(10f, 25f, 30f);

                Assert.That(
                    KimodoBridge.Editor.KimodoMarkerSamplingEditorUtility.TryWriteConstraintMarkerSample(
                        marker, dragged, out string error),
                    Is.True,
                    error);
                Assert.That(marker.SampleData.rootOverride.position, Is.EqualTo(new Vector3(8f, 9f, 10f)));
                Assert.That(
                    Quaternion.Angle(marker.SampleData.rootOverride.rotation, Quaternion.Euler(10f, 25f, 30f)),
                    Is.LessThan(1e-4f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(marker);
            }
        }

        // Archived legacy expectation: Mix emitted multiple protocol families.
        [Test, Ignore("Legacy Mix multi-family emission; move to archive with the destructive upgrade.")]
        public void Internal_MixPreservesAllExplicitChannelFamilies()
        {
            KimodoMarkerSampleResult sample = CreateFullBody(1, 0.5f, true);
            sample.constraintMode = "mix";
            sample.enableMask.rootPosition = true;
            sample.enableMask.rootHeading = true;
            sample.validMask.rootPosition = true;
            sample.validMask.rootHeading = true;
            EnableAllEffectors(sample);
            sample.rootOverride.t = new Vector3(3f, 0f, 4f);
            sample = KimodoConstraintSampleComposer.ComposeCanonicalSamples(
                new[] { sample }, 30.0)[0];

            CollectionAssert.AreEqual(
                new[] { "fullbody", "root2d", "left-hand", "right-hand", "left-foot", "right-foot" },
                ResolveTypes(sample));
            Assert.That(sample.enableMask.leftHand, Is.True);
            Assert.That(sample.enableMask.rightHand, Is.True);
            Assert.That(sample.enableMask.leftFoot, Is.True);
            Assert.That(sample.enableMask.rightFoot, Is.True);
        }

        // Archived legacy expectation: Mix split root and effectors.
        [Test, Ignore("Legacy Mix family splitting; move to archive with the destructive upgrade.")]
        public void Internal_MixEmitsRootAndAllExplicitEffectorsInFixedOrder()
        {
            KimodoMarkerSampleResult sample = CreateFullBody(1, 0.5f, true);
            sample.constraintMode = "mix";
            sample.validMask.muscle = false;
            sample.enableMask.rootPosition = true;
            sample.validMask.rootPosition = true;
            EnableAllEffectors(sample);

            CollectionAssert.AreEqual(
                new[] { "root2d", "left-hand", "right-hand", "left-foot", "right-foot" },
                ResolveTypes(sample));
        }

        // Archived legacy expectation: per-channel validity selected a fallback family.
        [Test, Ignore("Legacy Mix validity fallback; move to archive with the destructive upgrade.")]
        public void Internal_MixFallsThroughToRoot2DWhenHigherFamiliesAreInvalid()
        {
            KimodoMarkerSampleResult sample = CreateFullBody(1, 0.5f, true);
            sample.constraintMode = "mix";
            sample.validMask.muscle = false;
            sample.enableMask.rootPosition = true;
            sample.validMask.rootPosition = true;

            CollectionAssert.AreEqual(new[] { "root2d" }, ResolveTypes(sample));
        }

        // Archived legacy expectation: per-channel validMask controlled execution.
        [Test, Ignore("Legacy per-channel validity semantics; sampleValid now gates the complete sample.")]
        public void Internal_ValidMaskIsNotOverriddenByPayloadHeuristics()
        {
            KimodoMarkerSampleResult sample = CreateFullBody(1, 0.5f, true);
            sample.constraintMode = "effector";
            sample.enableMask.leftHand = true;
            sample.validMask.leftHand = true;
            sample.effectors.leftHand.t = new Vector3(float.NaN, float.PositiveInfinity, 0f);

            CollectionAssert.AreEqual(new[] { "left-hand" }, ResolveTypes(sample));

            sample.validMask.leftHand = false;
            Assert.That(ResolveTypes(sample), Is.Empty);
        }

        // Archived legacy expectation: same-frame support composition is now blocked.
        [Test, Ignore("Legacy same-frame Mix composition; move to archive with the destructive upgrade.")]
        public void Composer_KeepsEffectorSupportPoseValidWithoutEnablingFullBody()
        {
            KimodoMarkerSampleResult effector = CreateFullBody(1, 0.75f, true);
            effector.constraintMode = "left-hand";
            effector.enableMask.leftHand = true;
            effector.validMask.leftHand = true;
            var root = new KimodoMarkerSampleResult
            {
                constraintMode = "root2d",
                creationOrder = 2,
                enableMask = new KimodoConstraintMask { rootPosition = true },
                validMask = new KimodoConstraintMask { rootPosition = true }
            };

            KimodoMarkerSampleResult composed = KimodoConstraintSampleComposer.ComposeCanonicalSamples(
                new[] { effector, root }, 30.0)[0];

            Assert.That(composed.enableMask.muscle, Is.False);
            Assert.That(composed.validMask.muscle, Is.True);
            Assert.That(
                composed.sampleData.data[KimodoSampleDataLayout.BodyMuscleOffset],
                Is.EqualTo(0.75f));
            CollectionAssert.AreEqual(new[] { "root2d", "left-hand" }, ResolveTypes(composed));
        }

        // Archived legacy expectation: root position enableMask controlled Root2D activation.
        [Test, Ignore("Legacy root enableMask gating; root payload now controls Root2D activation.")]
        public void Internal_ModeLimitsFamiliesAndRoot2DRequiresPosition()
        {
            KimodoMarkerSampleResult sample = CreateFullBody(1, 0.5f, true);
            sample.enableMask.rootPosition = true;
            sample.validMask.rootPosition = true;
            sample.enableMask.leftHand = true;
            sample.validMask.leftHand = true;

            sample.constraintMode = "effector";
            CollectionAssert.AreEqual(new[] { "left-hand" }, ResolveTypes(sample));

            sample.constraintMode = "ik";
            CollectionAssert.AreEqual(new[] { "left-hand" }, ResolveTypes(sample));

            sample.constraintMode = "root2d";
            CollectionAssert.AreEqual(new[] { "root2d" }, ResolveTypes(sample));

            sample.enableMask.rootPosition = false;
            sample.validMask.rootPosition = false;
            sample.enableMask.rootHeading = true;
            sample.validMask.rootHeading = true;
            Assert.That(ResolveTypes(sample), Is.Empty);

            KimodoMarkerSampleResult defaultMode = CreateFullBody(1, 0.5f, true);
            defaultMode.constraintMode = string.Empty;
            CollectionAssert.AreEqual(new[] { "fullbody" }, ResolveTypes(defaultMode));
        }

        [Test]
        public void Root2DMode_IgnoresStaleEffectorMasks()
        {
            var sample = new KimodoMarkerSampleResult
            {
                constraintMode = "root2d",
                rootOverride = KimodoUnityBridge.KimodoRigidTransform.Identity,
                enableMask = new KimodoConstraintMask
                {
                    rootPosition = true,
                    leftHand = true,
                    rightHand = true,
                    leftFoot = true,
                    rightFoot = true
                },
                validMask = new KimodoConstraintMask
                {
                    rootPosition = true,
                    leftHand = true,
                    rightHand = true,
                    leftFoot = true,
                    rightFoot = true
                }
            };

            Assert.That(KimodoConstraintPosePipeline.IsRootOnlySample(sample), Is.True);
        }

        private static KimodoMarkerSampleResult CreateFullBody(
            long creationOrder,
            float firstMuscle,
            bool valid)
        {
            float[] data = KimodoSampleDataLayout.CreateBuffer();
            data[KimodoSampleDataLayout.BodyMuscleOffset] = firstMuscle;
            return new KimodoMarkerSampleResult
            {
                sampleData = KimodoSampleDataLayout.FromBuffer(data),
                enableMask = new KimodoConstraintMask
                {
                    muscle = valid,
                    rootTQ = valid,
                    leftFootTQ = valid,
                    rightFootTQ = valid
                },
                constraintMode = "fullbody",
                validMask = new KimodoConstraintMask
                {
                    muscle = valid,
                    rootTQ = valid,
                    leftFootTQ = valid,
                    rightFootTQ = valid
                },
                sampleTime = 0,
                creationOrder = creationOrder,
                enabled = true
            };
        }

        private static void EnableAllEffectors(KimodoMarkerSampleResult sample)
        {
            sample.enableMask.leftHand = sample.validMask.leftHand = true;
            sample.enableMask.rightHand = sample.validMask.rightHand = true;
            sample.enableMask.leftFoot = sample.validMask.leftFoot = true;
            sample.enableMask.rightFoot = sample.validMask.rightFoot = true;
        }

        private static string[] ResolveTypes(KimodoMarkerSampleResult sample)
        {
            var context = new KimodoConstraintExportContext
            {
                projectedPoseProjector = sample => CreateProjectedTestPose(sample)
            };
            KimodoConstraintInternal[] internals = KimodoConstraintInternal.GetConstraintInternal(
                sample,
                KimodoConstraintRigType.Unknown,
                context);
            return Array.ConvertAll(
                internals,
                item => item.ToJsonObject(0.0, null, 30.0).type);
        }

        private static KimodoConstraintProjectedPose CreateProjectedTestPose(
            KimodoMarkerSampleResult sample)
        {
            return new KimodoConstraintProjectedPose
            {
                profileRootPosition = sample.rootOverride?.t ?? Vector3.zero,
                jointNames = new[] { "Hips", "LeftHand", "RightHand", "LeftFoot", "RightFoot" },
                jointPositions = new[]
                {
                    sample.rootOverride?.t ?? Vector3.zero,
                    sample.effectors?.leftHand?.t ?? Vector3.zero,
                    sample.effectors?.rightHand?.t ?? Vector3.zero,
                    sample.effectors?.leftFoot?.t ?? Vector3.zero,
                    sample.effectors?.rightFoot?.t ?? Vector3.zero
                },
                jointRotations = new[]
                {
                    Quaternion.identity,
                    Quaternion.identity,
                    Quaternion.identity,
                    Quaternion.identity,
                    Quaternion.identity
                },
                localJointAngles = new List<Vector3> { Vector3.zero }
            };
        }
    }
}
