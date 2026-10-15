---
name: kimodo-animation-loop-common
description: Shared execution, evidence, recognition, comparison, revision, and handoff contract for reusable Kimodo animation tasks.
---

# Common execution contract

This file is the active shared contract for the root Skill. It replaces the former four phase manuals with one loop and keeps the useful evidence rules from the historical recognition and comparison procedures. It coordinates Kimodo commands and Unity/project APIs as equal implementation routes for one animation task.

## Command entry and state

The public Editor entry is:

```csharp
string schema = KimodoUnityBridge.Command.command_dispatcher.GetCommandDefinitionsJson();
string result = KimodoUnityBridge.Command.command_dispatcher.Invoke(commandName, argumentsJson);
```

Plan the complete visible Unity result before choosing tools. Kimodo commands, Unity Editor APIs, Timeline APIs, AnimationClip/AnimationCurve APIs, runtime APIs, and project-local tools are peer routes in the same execution loop. Read the live schema or `kimodo_help` before choosing a Kimodo command or parameter. Use protocol command names, not internal C# handler names. With Unity CLI, execute the dispatcher in the connected Editor. Command frames use 60 FPS; use the documented Clip-relative or Timeline-global origin for each field. The command schema describes the Kimodo route; it is not an allowlist of all permitted implementation methods. For each subtask, record `subtask → route → output → acceptance check`. If an operation is better represented by a Unity or project API, use that API directly and record the route.

The active Unity scene, Timeline, and Kimodo Session are an editable working pipeline. The agent may switch context and extend an existing Timeline by inserting tracks, clips, bindings, or synchronized prop motion. Preserve existing tracks and clips by default; prefer adding a track or making a derived Timeline over rewriting or rebuilding existing content. Modify existing content only when required by the task, and preserve a recoverable original first. After editing, verify the active scene/session, track bindings, clip ranges, and synchronization in the intended Timeline.

At the start of a task, keep a compact loop state:

```text
intent and explicit requirements
scene character/object references
planned Clips and dependencies
subtask-to-route plan and produced assets/tracks
acceptance criteria and allowed semantic alternatives
confirmed Pose, root, effector, path, and object evidence
candidates: request_id, animation_id, seed, prompt, constraints, analysis_id
criterion results: verified, failed, or unknown
revision history and no-improvement count
```

Do not create another public state protocol or copy complete scene/analysis payloads. Use the persisted Session history and returned IDs to navigate candidates.

## Response and asynchronous rules

- Check top-level `ok` before reading domain fields.
- Command errors use `ok=false` and `error={code,message,details?}`.
- A successful poll can still have task `status` `failed` or `canceled`; report that terminal state and its error.
- Save the exact `request_id`; poll at a fixed interval until a terminal state or bounded timeout.
- Never duplicate a request that is still running. Cancel only when the task should stop.
- Treat warnings and non-blocking checks as evidence to inspect.
- Preserve completed Clips and use derived outputs for revisions.

## Intent-to-criteria loop

For each request:

1. Resolve participating characters and objects from the current scene. Identify the visible result, action order, contact points, duration, and ending state. State assumptions.
2. Split independent goals or target interactions into Clips. Keep one continuous semantic action together. Record dependencies between Clips.
3. Select only criteria relevant to the request. Criteria may be semantic, spatial, temporal, contact-related, structural, visual, or playback-related.
4. Gather the smallest evidence needed: `scene.context`, `pose_get`, `pose_test`, `transform_capture`, an existing Clip analysis, or a confirmed object Transform. Use `pose_test` to measure actual Humanoid joint positions in any sampled Pose and compare them with contact targets, including after effect-only generation. Generation-time pose/effect safety preflight runs only when one point constraint explicitly contains both `pose` and `effect`; effect-only constraints skip this check. Such preflight warnings remain non-blocking and do not replace measured pose results. Do not turn every observation into a constraint.
5. Construct a short semantic prompt and structured constraints. Exact root position, heading, Pose, hand/foot target, loop, duration, and continuation belong in Kimodo fields when supported; object curves, Timeline tracks, bindings, and other asset edits belong in the appropriate Unity or project API.
6. Generate candidates, poll each request, and retain the IDs and submitted settings.
7. Analyze each candidate under the criteria. Open returned images before visual claims and map each claim to its tile or structured field.
8. Compare candidates only when there are multiple candidates or the user requests a comparison. Use identical conditions and decide each criterion independently.
9. If a criterion fails with evidence, change only the relevant input and create a derived candidate. If evidence is missing, gather it or report unknown; do not retry blindly.
10. Finish when criteria pass, further candidates do not improve the target, or the remaining work is unavailable through both the relevant command and API routes, or requires human artistic judgment.

## Recognition contract

Recognition is a constrained evidence operation, not a source of new intent. The caller supplies semantic alternatives when action semantics must be distinguished. Never infer from a filename, Clip name, candidate order, ID, or saliency.

Use only relevant tasks from this set:

```text
visual: trajectory_shape, action_semantics, visual_motion_quality, contact_pattern
structured: loop_endpoint, keyframe_heading, trajectory_turn,
            trajectory_length, phase_track, endpoint_pose, motion_profile
```

Allowed visual values remain deliberately small:

```text
trajectory_shape: closed_loop | open_path | near_static | unclear
visual_motion_quality: smooth_appearance | visible_break | unclear
contact_pattern: alternating | left_dominant | right_dominant | unclear
```

For every task, return a value, confidence, and evidence. Return `unknown` or `unclear` when the image is unreadable, fields are absent, evidence conflicts, or the claim requires playback that was not observed. `smooth_appearance` describes an unbroken static drawing; it does not mean playback smoothness. Structured trajectory values come from named returned fields, never from pixel estimates.

When comparing action semantics, use only caller-provided alternatives and phase/pose/contact evidence. A single accidental contact or anchor frame does not prove a required phase. If alternatives cannot be separated, preserve `unknown`.

## Comparison contract

Analyze and render each candidate separately with the same picture request, resolution, and evidence procedure. Validate the candidate-to-image mapping before comparing. Compare only criteria declared for the task, such as semantic correctness, trajectory coherence, foot contacts, required phase coverage, required Pose coverage, expression strength, endpoint continuity, or a declared neighboring-Clip transition.

For each criterion, report `candidate_1`, `candidate_2`, `tie`, or `unknown`, with a short reason and evidence. Do not calculate an overall winner by summing scores, votes, magnitudes, displacement, contact counts, or selected frames. Prefer one candidate only when the target criterion and evidence make the difference decisive; otherwise report no reliable difference or insufficient evidence.

## Evidence boundaries

Report these separately:

- **Visual**: what the opened image visibly shows.
- **Structured**: what the command returned as a measured field.
- **Playback**: what was observed during actual playback or a supported runtime check.

Static pictures cannot establish velocity or acceleration smoothness, foot sliding, popping, or playback continuity. A missing criterion is `unknown`, not a failure. A failed command is different from an animation that generated but failed its acceptance criteria.

## Revision and human handoff

Use a targeted revision table internally:

```text
failed criterion → supporting evidence → changed input → new candidate → result
```

Do not regenerate passing dependencies. Stop automatic revision when the same criterion fails without improvement across the configured attempts, when the remaining defect is artistic frame-by-frame polish, or when it needs complex rigging or physics. A missing command alone is not a stop condition: first inspect the project/package and relevant Unity APIs and attempt an appropriate API-based route. Report the successful criteria, failed/unknown criteria, candidates tried, evidence paths, and the precise animator handoff.

For simple props, use Kimodo commands where exposed and Unity APIs where needed to author Transform curves, Timeline tracks, or hand attachment. Keep them tied to the character candidate through shared contact frames, offsets, Timeline range, and evidence IDs. Verify both character pose and object motion; if an API route is genuinely unavailable, mark that criterion unknown and explain the limitation.
