# Kimodo Unity Bridge Known Issues

This is an informational record of observations from animation-agent tests. When a listed symptom appears, continue through the best available route and verify it with the relevant evidence. The entries are observations and optional workarounds. Revisit this file when the execution loop, Pose test, command schema, or Unity/API integration changes.

## Composite actions and pose/constraint selection

- A composite action can use one Clip when its beats share timing and acceptance evidence. Use separate Clips when approach and interaction need independent revision, continuation, or verification; choose the boundary from the task's dependencies and criteria.
- Choose between a FullBody Pose and end-effector constraints from the interaction goal and evidence. A verified starting Pose with an effector target can coordinate the reaching limb and the rest of the body; use the constraint composition that matches the intended motion.
- Resolve a starting Pose with `pose.get` and keep its source reference. For an action-specific start, use an evaluated Clip frame; for a neutral baseline, use the Avatar skeleton rest pose. Check foot height and contact when those criteria matter.
- For each revision, identify the affected beat, transfer the evaluated predecessor Pose when continuing motion, and verify the integrated Timeline and playback against the task criteria.

## Prop pickup handoff and visible verification

- Moving the tabletop prop itself with a Transform curve can be fragile when Timeline start-offset handling, Animator binding, or the prop's parent transform changes the effective curve. The saved curve may look plausible while the evaluated scene leaves the prop at its original parent origin or far from the hand. Inspect evaluated world transforms at the handoff frames and verify the relevant Timeline offset/binding behavior.
- For a simple visual pickup, a hand-attached visual proxy can be easier to control than moving the tabletop object across the scene: keep the original prop on the table until contact, then switch visibility to a matching proxy attached to the hand and align them at the handoff. This is a workflow option; use visibility states that show the intended copy at each beat and preserve deterministic Timeline scrubbing/playback.
- Confirm the actual binding or attachment mechanism, then sample the prop against the hand during the evaluated Timeline.
- Compare static samples, generation-analysis images, and Game View captures against reproducible samples of the actual scene. Confirm that the character and interaction are visible, and inspect camera and render state when an image is inconclusive.
- Frame the integrated scene around the intended character and target, then inspect the result in that view.
- After a repair, re-evaluate the relevant Timeline frames and inspect the visible handoff and ending in the intended scene view.
