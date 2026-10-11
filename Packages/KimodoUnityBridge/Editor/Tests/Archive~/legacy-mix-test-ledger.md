# Archived legacy Mix test expectations

These assertions describe the retired behavior where same-frame samples were
composed channel-by-channel or one `mix` sample expanded into several output
families. They remain annotated with NUnit `Ignore` in
`KimodoSampleDataTests.cs` so the history is visible while they no longer act as
the behavior oracle.

- `MixManualWriteback_PreservesExplicitEffectorSwitch`: its final composer call
  supplies two same-frame entries; same-frame constraints are now rejected.
- `Composer_LastCreatedInvalidChannelWinsWithoutFallback`: expects same-frame
  channel composition and last-created fallback selection.
- `Internal_MixPreservesAllExplicitChannelFamilies`: expects one Mix sample to
  emit FullBody, Root2D, and all enabled effectors at once.
- `Internal_MixEmitsRootAndAllExplicitEffectorsInFixedOrder`: expects Mix to
  split root and effector families into several outputs.
- `Internal_MixFallsThroughToRoot2DWhenHigherFamiliesAreInvalid`: expects
  validity-based fallback between constraint families.
- `Composer_KeepsEffectorSupportPoseValidWithoutEnablingFullBody`: expects two
  same-frame family samples to merge into one Mix sample.
- `CarryEffectorsWithRoot_FullBodyPromotesFootGoalsForSolveIk`: expects a
  FullBody carry operation to move foot targets while leaving another valid
  target untouched; the canonical carry rule now applies the root delta to
  every valid target payload and does not use enable bits as a carry gate.
- `MixDefaults_DoNotEnableUndeclaredChannels`: depends on the retired Mix
  mask fallback.
- `MixManualWriteback_PreservesExplicitEffectorSwitch`: composes two samples
  from one frame and therefore depends on the retired same-frame merge.
- `Composer_LastCreatedInvalidChannelWinsWithoutFallback`: depends on last
  writer and per-channel validity fallback.
- `MissingValidMask_DoesNotFallBackToEnableMask`: asserts the retired
  channel-level validity gate.
- `Internal_MixPreservesAllExplicitChannelFamilies` and
  `Internal_MixEmitsRootAndAllExplicitEffectorsInFixedOrder`: expect one Mix
  sample to emit several protocol families.
- `Internal_MixFallsThroughToRoot2DWhenHigherFamiliesAreInvalid` and
  `Internal_ValidMaskIsNotOverriddenByPayloadHeuristics`: expect validity-mask
  family fallback.
- `Composer_KeepsEffectorSupportPoseValidWithoutEnablingFullBody`: expects
  same-frame support composition.
- `Internal_ModeLimitsFamiliesAndRoot2DRequiresPosition`: expects root
  position enableMask gating.
- `TimelineProjection_SolvesCharacterBeforeRemovingTrackOffset`: retained as
  an independent projection diagnostic; it is not an oracle for Mix, carry, or
  the canonical constraint protocol.

The archive is documentation-only and is outside Unity's test assembly. New
regression tests for single-family classification and outer frame rejection
live in the active test file.

- `KimodoConstraintProtocolNormalizerTests.PayloadSerialization_FoldsRootOverrideAndPreservesOtherFrames`: expected a same-frame Root2D row to be folded into a FullBody row and its heading rewritten. Same-frame multi-constraint input is now undefined and rejected at the command boundary, so this is retained only as historical protocol evidence.
