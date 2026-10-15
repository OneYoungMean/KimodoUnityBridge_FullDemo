---
name: kimodo-unity-bridge
description: Coordinate reusable Unity animation tasks across Kimodo commands, Unity APIs, Timeline assets, scene context, pose constraints, generation, analysis, comparison, and targeted revision.
---

# KimodoUnityBridge

Use this Skill when the user wants to create, continue, retarget, inspect, compare, or improve animation in a Unity project with KimodoUnityBridge installed. Read [`tools/common.md`](tools/common.md) before executing a task. Read the live command schema or `kimodo_help` before choosing parameters; [`Command/help.json`](Command/help.json) is the checked-in reference, not a substitute for the connected Editor schema.

## Scope

The reusable workflow covers Humanoid character animation and simple Transform-driven scene objects such as doors, balls, weapons, and props. It can generate derived character Clips, sample and edit generation Poses, apply root and end-effector constraints, retarget Humanoid Clips, inspect evidence, and compare candidates. Complex rigs, quadrupeds, non-humanoid creatures, vehicles, physics authoring, and final frame-by-frame acting polish require a different tool or an animator.

The command surface is a convenient route, not the boundary of what the agent may do. When a required operation is absent from the live command schema, inspect the project and use appropriate available Unity APIs (for example, Editor scripting, AnimationClip/AnimationCurve, Timeline, or runtime APIs) to complete it. Verify API behavior and preserve generated or source assets before modifying them. Hand off only when the operation is unavailable or the remaining task requires human artistic judgment; do not label work unsupported merely because no Kimodo command exists.

Treat Unity scene, Timeline, and Kimodo Session context as an editable working pipeline. The agent may select or switch context, and may extend an existing Timeline by inserting tracks, clips, bindings, or synchronized prop motion. Preserve existing tracks and clips by default; prefer adding a track or creating a derived Timeline over rewriting or rebuilding existing content. Modify existing content only when the task requires it, and preserve a recoverable original before doing so. After edits, verify the active scene/session, track bindings, clip ranges, and synchronization in the intended Timeline.

## Tool coordination

Plan the complete visible Unity result first, then assign each subtask to the most suitable route. Kimodo commands, Unity Editor APIs, Timeline APIs, AnimationClip/AnimationCurve APIs, runtime APIs, and project-local tools are peers in one workflow. Do not treat command invocation as the goal, and do not wait for a command failure before considering an API that is already appropriate for the task.

Use Kimodo commands for semantic scene discovery, Humanoid pose and effector constraints, character generation, retargeting, analysis, comparison, and Kimodo candidate/session references. Use Unity or project APIs for scene and asset edits that are more naturally represented there, such as sampling actual bone positions, creating or editing prop Transform curves, inserting Timeline tracks, binding objects, and checking multi-object synchronization. When a task crosses both routes, pass the same object references, contact frames, offsets, clip ranges, and evidence through the loop.

Before execution, make a small routing plan such as `subtask → route → output → acceptance check`. For a character picking up an object, this normally includes a character route, a pose-test route, an object-motion route, and a Timeline-integration route. The plan is not rigid: choose a different route when the live project exposes a better one.

## One execution loop

Treat every request as a bounded loop, not as a fixed list of phases:

```text
understand intent and scene
→ split the performance into independently testable Clips
→ define observable acceptance criteria
→ gather only the evidence needed for constraints
→ generate one or more candidates
→ analyze and recognize relevant evidence
→ compare candidates when comparison is useful
→ revise only the failed or uncertain criterion
→ preserve the best derived output, or hand off to an animator
```

The loop may revisit any earlier decision when new evidence changes the action, timing, target, or acceptance criteria. Do not regenerate unrelated Clips that already pass. A completed Clip is preserved; revisions produce derived outputs.

### Intent and planning

Identify the participating characters and objects, the visible result, action order, contact or interaction points, duration, and ending state. Separate facts supplied by the user from assumptions. Split actions when they have different immediate goals or target interactions; keep one continuous performance together. Ask only when two reasonable interpretations would produce materially different motion, target, timing, or Clip boundaries.

Choose criteria that can be checked for this task. Typical criteria are action semantics, required phase order, root position and heading, contact or effector reach, endpoint continuity, trajectory shape, foot contacts, loop closure, and object-relative motion. Do not invent a universal score or threshold when the task has not supplied one.

### Constraints and generation

Keep the motion prompt short and semantic. Put exact positions, headings, Poses, hand or foot targets, loop settings, Clip windows, and confirmed continuation data in structured command fields. Use only scene evidence or explicit requirements for exact constraints. Resolve and inspect a `pose_get` result before using its `{track,index}` reference. Distinguish Timeline-global frames from generated-Clip-relative frames.

Generate dependent Clips in order. When a later Clip must continue from an earlier one, finish and evaluate the earlier Clip first, then use its confirmed ending as the next input. For unstable generation, vary the Seed or a targeted constraint and retain every candidate's `request_id`, `animation_id`, Seed, prompt, constraints, and analysis IDs.

### Evidence, recognition, and comparison

Run `animation_analyze` for each candidate that needs evaluation and open its returned picture before making a visual claim. Use the fixed recognition tasks in `tools/common.md`; only run tasks that answer the current acceptance criteria. Compare candidates under identical analysis and rendering conditions. Decide criterion by criterion; never add scores, votes, frame counts, displacement, or contact counts into an invented total.

Separate visual observations, structured measurements, and playback observations. Static analysis cannot prove playback continuity, acceleration, speed smoothness, foot sliding, popping, or a final acting judgment. Mark missing or conflicting evidence as unknown.

### Revision and handoff

When evidence supports a defect, state the failed criterion, change only the relevant prompt, Pose, root, effector, continuation, or object curve input, then generate and evaluate a derived candidate under the same criterion. Stop when the required criteria pass, when further candidates show no meaningful improvement, or when the remaining issue requires frame-by-frame acting, complex binding, physics, or an unavailable API. The final report names the selected output, criteria and evidence, remaining unknowns, attempts, and the exact human follow-up.

## Command and API routes

Use the available Unity project tools through the route selected for each subtask. For Kimodo operations, use the Unity CLI connected to the Editor and invoke the public `KimodoUnityBridge.Command.command_dispatcher`. `kimodo_install_server` and `kimodo_generate_animation` are asynchronous: save the exact `request_id` and poll with `kimodo_get_generation` until a terminal state. Check top-level `ok`, then task `status`; warnings are evidence to inspect, not proof of success or failure.

Use `scene.context` for authoritative character, Clip, Timeline, and Session references. Use `pose_get`, `pose_test`, `pose_set`, `kimodo_retarget_animation`, `animation_analyze`, and `transform_capture` only with handles returned by the active context or the relevant command. Use `pose_test` after `pose_get` to measure actual Humanoid joint world positions and optionally compare contact targets; generation warnings are non-blocking preflight diagnostics and do not replace this measurement. Treat an asset path as metadata, not a scene Clip handle. Do not infer references from display names when a command has returned an unambiguous handle.

Kimodo commands are one part of the implementation, not a required wrapper around every operation. Use Unity APIs even when a command exists if the API gives the necessary precision or integration. Keep both routes in one loop: share contact frames, offsets, Timeline ranges, and evidence IDs; inspect the result in the scene; and distinguish character-Clip success from whole-interaction success. Before concluding an operation is impossible, inspect the installed package and relevant Unity APIs. Report a genuine API/tooling limitation only after that check.
