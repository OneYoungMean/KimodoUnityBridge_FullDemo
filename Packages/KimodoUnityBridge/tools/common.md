---
name: kimodo-animation-loop-common
description: Shared execution, evidence, recognition, comparison, revision, and handoff contract for reusable Kimodo animation tasks.
---

# Common execution contract

[简体中文](common_zh.md)

This file is the active shared contract for the root Skill. It replaces the former four phase manuals with one loop and keeps the useful evidence rules from the historical recognition and comparison procedures. It coordinates Kimodo commands and Unity/project APIs as equal implementation routes for one animation task.

## Scope and task routing

Work on Humanoid Clips, generation Poses, and simple Transform-driven props such as doors, balls, and weapons. Animator Controllers, state machines, gameplay scripting, complex rigs, quadrupeds, vehicles, and physics authoring are outside this workflow. Final frame-by-frame acting polish may require an animator.

Choose a primary route from the requested outcome; combine routes for dependent subtasks:

| Request | Route |
|---|---|
| Inspect or compare existing animation | Resolve and evaluate the existing Clips; skip generation and source edits. |
| Generate a new animation | Define criteria, generate candidates, then evaluate. |
| Improve, continue, or retarget an existing Clip | Inspect the source, choose an edit or generation route, and compare a derived output against the source. |
| Animate character/prop interaction | Coordinate character motion, prop motion, contact events, and Timeline integration; verify the assembled result. |
| Ambiguous intent | Inspect available context first; ask only about unresolved choices that materially change the result. |

## Command entry and state

The public Editor entry is:

```csharp
string schema = KimodoUnityBridge.Command.command_dispatcher.GetCommandDefinitionsJson();
string result = KimodoUnityBridge.Command.command_dispatcher.Invoke(commandName, argumentsJson);
```

Plan the complete visible Unity result before choosing tools. Kimodo commands, Unity Editor APIs, Timeline APIs, AnimationClip/AnimationCurve APIs, runtime APIs, and project-local tools are peer routes in the same execution loop. Respect any user-required route or tool restriction; API success does not validate a Kimodo command path that was bypassed. Read the live schema or `kimodo.help` before choosing a Kimodo command or parameter. Use protocol command names, not internal C# handler names. With Unity CLI, execute the dispatcher in the connected Editor. Command frames use 60 FPS; use the documented Clip-relative or Timeline-global origin for each field. For each subtask, record `subtask → route → output → acceptance check`. If an operation is better represented by an allowed Unity or project API, use that API directly and record the route.

[`Command/help.json`](../Command/help.json) is the checked-in reference, not a substitute for the connected Editor's live schema.

Resolve character, Clip, Timeline, and Session references through `scene.context` and the relevant command results. Use returned handles rather than guessing from display names. Asset paths are valid only for fields that accept them; they do not substitute for a required scene handle or `{track,index}` reference.

The active Unity scene, Timeline, and Kimodo Session are an editable working pipeline. The agent may switch context and extend an existing Timeline by inserting tracks, clips, bindings, or synchronized prop motion. Preserve existing tracks and clips by default; prefer adding a track or making a derived Timeline over rewriting or rebuilding existing content. Modify existing content only when required by the task, and preserve a recoverable original first. After editing, verify the active scene/session, track bindings, clip ranges, and synchronization in the intended Timeline.

When removal is requested, move the affected files and associated Unity `.meta` files into the nearest archive directory and check affected references.

Coordinate-space contract:

- Public command positions and targets (`root`, `effector`, and `pose.set`) are expressed in Unity World space.
- Point constraint `root.rotation` and `effector.*.rotation` accept Euler degrees `[x,y,z]`. Each transform requires position and/or rotation; omitted channels retain sampled values. The command selects Root2D from root, Effector from root + effector, and FullBody from pose with optional root/effector (IK solved). Empty input and effector alone are invalid; duplicate command frames are rejected.
- `animation.analysis.root_trajectory` is returned in World space. The analyzed Clip begins at frame zero, so its trajectory's zero origin is the World reference used by the command. When `animation.generate` consumes its `root_path`, generation resolves that World path once against the target character's World root position and planar heading.
- Local joint rotations and camera-specific fields keep their documented spaces; the World-space rule applies to position and target fields.

At the start of a task, keep a compact loop state:

```text
task route and current check
intent, explicit requirements, assumptions, and unknowns
project/scene/session, character/object references, and relevant tool versions
planned Clips and dependencies
subtask-to-route plan and produced assets/tracks
acceptance criteria and allowed semantic alternatives
confirmed Pose, root, effector, path, and object evidence
candidates: request_id, animation_id, seed, prompt, constraints, analysis_id
criterion results: verified, failed, or unknown
revision history, best candidate, attempt budget, and no-improvement count
capability evidence and unresolved choices per subtask
```

Do not create another public state protocol or copy complete scene/analysis payloads. Use the persisted Session history and returned IDs to navigate candidates.

## Route and check contract

Use the task routing above. Read-only discovery, including schema lookup and scene inspection, may precede route confirmation. Before generation or edits, name the target object or Clip, expected output, relevant criteria, and dependencies. Default to preserving the original and creating a derived output; apply existing user authorization without asking again.

For each subtask, distinguish the implementation route from its capability evidence:

| Capability | Required basis |
|---|---|
| Current Kimodo command | Present in the live schema; check current scene and runtime prerequisites. |
| Unity or project API route | Inspect the relevant API and integration, then verify the produced result in the current project. |
| Planned capability | A proposed command or integration; do not invoke it or claim it is implemented. |
| Unavailable route | The relevant command and API routes have been checked and cannot perform the operation in this environment. |

Schema presence proves an interface exists, not that its output meets the task. Keep criterion results (`verified`, `failed`, `unknown`) and an animator handoff separate from capability availability.

Use re-enterable checks, skipping work irrelevant to the route:

| Check | Evidence needed before dependent work |
|---|---|
| Intent and scope | Requested result, explicit limits, consequential choices, and harmless assumptions. |
| References and scene state | Intended project/scene, character and Avatar where required, source Clip, Timeline/session, and relevant prop transforms, pivots, and bounds. |
| Constraints and criteria | Confirmed coordinate/frame bases, interaction events, applicable tolerances, and a feasible check for each required result. |
| Execution | Submitted inputs and IDs, terminal result or API-produced assets, and the actual evaluated Timeline bindings/ranges. Inspection-only tasks reuse existing Clips. |
| Verification | Candidate-matched measurements/images and playback when needed; changes trigger checks on affected dependencies. |

Resolve missing facts through available context first. Ask only if unresolved alternatives would materially change the requested result; hold dependent generation/edits until answered and continue independent work. Missing acceptance evidence remains `unknown`; it cannot be bypassed by treating the task as passed. No extra confirmation is needed merely to enter a check.

## Response and asynchronous rules

- Check top-level `ok` before reading domain fields.
- Command errors use `ok=false` and `error={code,message,details?}`.
- A successful poll can still have task `status` `failed` or `canceled`; report that terminal state and its error.
- Save the exact `request_id`; poll at a fixed interval until a terminal state or bounded timeout.
- Apply this to any asynchronous command returning a pending `request_id`, including `server.install` and `animation.generate`. A polling timeout leaves the task pending, not failed or canceled; retain the ID for resumption.
- Never duplicate a request that is still running. Cancel only when the task should stop.
- Treat warnings and non-blocking checks as evidence to inspect.
- Preserve completed Clips and use derived outputs for revisions.

## Intent-to-criteria loop

Apply only the work required by the selected route; inspection/comparison does not generate or edit source animation:

1. Resolve participating characters and objects from the current scene. Identify the visible result, action order, contact points, duration, and ending state. State assumptions.
2. Identify independently testable action beats. Split into Clips when local revision and continuation needs justify it; different beats do not automatically require different Clips. Record dependencies between character Clips, prop motion, and Timeline bindings.
3. Select relevant criteria, distinguishing required constraints from quality preferences. Criteria may be semantic, spatial, temporal, contact-related, structural, visual, or playback-related. State their observable checks and tolerances where needed; do not invent a universal score or pass threshold.
4. Gather the smallest evidence needed: `scene.context`, `pose.get`, `pose.analysis`, an existing Clip analysis, or a confirmed object Transform. Before generation, pass the proposed point `constraints` unchanged to `pose.analysis` and inspect each captured pose; set `combine=true` to render them together as opaque poses in one SceneView image. The command reports measured world-space joints and distances from effector targets. Its `camera` object is shared with `animation.analysis`; SceneView lighting, skybox, gizmos, and grid keep their current settings unless explicitly supplied. Generation-time pose/effector safety preflight runs only when one point constraint explicitly contains both `pose` and `effector`; root + effector constraints skip this check; effector alone is invalid because it has no explicit root context. Such preflight warnings remain non-blocking and do not replace measured pose results.
5. When a task requires a new or revised action Pose, prefer a Clip-derived source: generate or select a preliminary Clip, materialize the intended frame with `pose.get`, and use its `{track,index}` reference inside the point constraints passed to `pose.analysis`. Inspect the captured pose before reusing the same constraint for generation. Use Avatar rest pose only as the default initialization when no action-context Pose exists, or when the user explicitly requests a neutral baseline. Do not use rest pose as a substitute for a bend, reach, contact, or other performance-specific Pose.
6. For generation, construct a short semantic prompt and supported structured constraints from confirmed scene evidence or explicit requirements. Exact root position, heading, Pose, hand/foot target, loop, duration, and continuation belong in Kimodo fields when supported; object curves, Timeline tracks, bindings, and other asset edits belong in the appropriate Unity or project API. Inspect a `pose.get` result before reusing its `{track,index}`; an unchecked scene T-pose or a Clip name is not a confirmed continuation source. Generate dependent Clips in order from their evaluated predecessor endings. When a constraint has no explicit Pose, its initialization may use Avatar rest pose, but that default must be reported separately from a Clip-derived action Pose.
7. Generate or edit candidates only when required, poll asynchronous requests, and retain the IDs and submitted settings. For API outputs without Kimodo IDs, retain the actual asset/track references. Inspection routes use the existing output as their subject.
8. Evaluate relevant hard requirements, then structure, scene interaction, and playback/quality criteria as applicable. Use `animation.analysis` for Humanoid Clip evaluation; use evaluated scene measurements and appropriate APIs for prop/Timeline checks. Open returned images before visual claims. Validate the evidence-to-output mapping below before accepting any criterion.
9. Compare candidates only when there are multiple candidates or the user requests a comparison. Use identical conditions and decide each criterion independently.
10. If a criterion fails with evidence, change only the relevant input and create a derived candidate. If evidence is missing, gather it or report unknown; do not retry blindly.
11. Apply the stopping and handoff rules below. Never repeat an identical request without new evidence or a changed input.

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

For each acceptance claim, retain a compact provenance record:

```text
evaluated candidate/source asset and request/animation/analysis IDs when supplied
project, scene, Session and intended Timeline with clip/track bindings
sample frame or interval, frame origin, and analysis/render settings
relevant character/prop transforms, offsets, and source asset state
returned evidence path or measurement field, criterion result, and limitation
```

Use existing IDs and Session history rather than duplicating payloads. When the tool does not return an ID, cite its actual asset, frame, and evidence path. Before reusing evidence, verify that the evaluated source and relevant scene/Timeline state still match. Evidence for an unchanged dependency may be reused; a changed character Clip invalidates dependent prop-follow samples, and changed bindings, offsets, or prop state invalidate affected interaction evidence. Re-run those checks after integration. A screenshot of another candidate, old Timeline, or incorrect camera framing cannot establish acceptance.

Report these separately:

- **Visual**: what the opened image visibly shows.
- **Structured**: what the command returned as a measured field.
- **Playback**: what was observed during actual playback or a supported runtime check.

Static pictures cannot establish velocity or acceleration smoothness, foot sliding, popping, or playback continuity. A supported temporal measurement may establish a specific numerical property within its sampling limits; it does not imply that full playback was observed. A missing criterion is `unknown`, not a failure. A sampled joint distance proves only that sampled relation, not finger grip, collision clearance, or contact throughout an interval.

Attribute failures before choosing a revision: invalid references/prerequisites or request/protocol errors are toolchain failures; a completed character result that violates its criteria is a generation-quality failure; wrong prop motion, bindings, offsets, or synchronization are integration failures; missing, stale, or inconclusive observations are evidence gaps. More than one category may apply. Repeating generation does not fix a protocol error or missing evidence.

## Revision and human handoff

Use a targeted revision table internally:

```text
failed criterion → supporting evidence → changed input → new candidate → result
```

Before a generation/revision loop, record a bounded candidate or time budget using the user's limits when supplied. As a local default, allow one initial candidate and up to two targeted revisions of the same failing criterion. This is a cost-control default, not a quality threshold; choose a different finite budget when the task and existing authorization justify it. Count prior attempts and shared candidates, and do not reset the budget by renaming a criterion or restarting the loop.

Stop revising when the required criteria pass, the budget is exhausted, two consecutive targeted attempts bring no meaningful improvement under comparable evidence, no allowed route can perform the required operation after the relevant checks, or the remaining defect needs human artistic judgment. Define improvement against the declared criterion; changing a Seed or receiving `completed` is not evidence of improvement. Missing evidence calls for inspection, not a speculative generation retry.

Keep the best candidate that satisfies hard requirements and reject regressions on previously passing requirements. If none satisfies them, preserve the best partial output and report its failed/unknown criteria instead of claiming acceptance. Do not regenerate unrelated passing dependencies; recheck any outputs actually affected by a revision. A missing command alone is not a stop condition: inspect the relevant package and API route within the user's tool restrictions before reporting a tooling limitation. If required tooling cannot be obtained, stop dependent execution, report the issue, and wait for the user's repair rather than substituting a subagent.

For simple props, use Kimodo commands where exposed and Unity APIs where needed to author Transform curves, Timeline tracks, or hand attachment. Keep them tied to the character candidate through shared contact frames, offsets, Timeline range, and evidence IDs. Verify both character pose and object motion; if an API route is genuinely unavailable, mark that criterion unknown and explain the limitation.

## Result report

Name the selected Clip/assets and intended Timeline. Report required criteria as `verified`, `failed`, or `unknown`, each with its matching evidence and relevant limitation. Distinguish request acceptance, task terminal status, character-Clip acceptance, and whole-interaction acceptance. A required unknown prevents a claim of complete verification.

Summarize targeted attempts, best result, failure category, and the actual stop reason. If work remains, identify the affected interval, body part, curve, binding, or missing evidence and the precise next action. A toolchain repair or evidence gap need not be an animator handoff; reserve that handoff for work that actually needs an animator.

Consult [`known-issues.md`](known-issues.md) only when a relevant symptom appears. It records observations and optional workarounds, not mandatory steps or stopping rules. Archived manuals are historical reference rather than active instructions.
