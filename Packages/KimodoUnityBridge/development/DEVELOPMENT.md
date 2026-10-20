# 开发备忘录 / Development memo

> Temporary development snapshot; use [tools/common.md](../tools/common.md) and the command catalog for execution details.
>
> 当前文件只是临时开发快照。日常任务的执行规则位于 [tools/common.md](../tools/common.md)，命令定义位于 [Command/help.json](../Command/help.json)。

## Current command surface (主要命令面)

The maintained public commands are:

- Startup and discovery: `server.install`, `kimodo.help` (`server.install` starts an asynchronous project-local installation task and returns `install:<guid>`; poll it with `generation.get`)
- Scene inputs: commands resolve the active scene and return explicit asset/evidence references
- Async tasks: `animation.generate` returns a generation `request_id`; `generation.get` polls either task type; `generation.cancel` cancels generation only
- Analysis/evidence: `animation.analysis`, `pose.analysis`, `pose.fromvideo`, `pose.frompicture`
- Pose editing: `pose.get`, `pose.set`
- Asset output: `animation.retarget`

## Current boundaries

- Commands resolve a scene Humanoid Animator or renderable Mesh directly. A Mesh-only character rejects Humanoid clips, while a Humanoid target retargets a generic clip before appending it.
- Completed Clips are immutable. Corrections, retargets, and generated variants append new Clips rather than replacing an existing Clip.
- `animation.analysis` accepts one explicit scene Clip and returns structured motion analysis plus a fixed 20-tile SceneView image. Its `camera` object is shared with synchronous `pose.analysis`; both use the active SceneView camera and return image data inline with no request ID. `pose.analysis` previews the same point constraints accepted by `animation.generate`, separately by default or together as opaque poses with `combine=true`.
- Point constraints use explicit `root`, `pose`, and `effector` inputs through `animation.generate.constraints`; the command infers Root2D, FullBody, or Effector after sampling. Pose plus effector is exported as IK-solved FullBody. See [the current constraint contract](plan.md#当前-constraint-契约). Humanoid analysis stores and returns a reusable Root Path consumed through `root_path`; Path Override can regenerate with the same seed from absolute Unity-yaw begin/end angles, preserve the baseline path length, and coexist with loop generation. An absolute heading override runs last at 30-frame intervals, replacing Root2D headings without changing the composed Root XZ positions; when Path Override is active, it rewrites that Root2D record instead of appending a duplicate. Generation and Pose sampling require a valid Humanoid Avatar; Mesh-only characters are analysis-only. There is no standalone root-transform application command.
- `pose.get` creates a new External Pose marker for a Humanoid character. Use its `{track,index}` reference inside a point constraint, preview that constraint with `pose.analysis`, then edit the marker with `pose.set` if needed.

## Active documentation work

- Keep `../Command/help.json` aligned with the dispatcher, and keep concrete command details out of `../SKILL.md` and the capability tools under `../tools/`.
- Keep raw object lookup details confined to `../Command/help.json` and the single entry sentence in `../SKILL.md`.

## Verification items

- Run a Unity Editor compile/import check after documentation and command-surface changes.
- Validate representative generation, analysis image opening, External Pose/Path editing, and immutable-Clip append behavior in the maintained project. Scene changes and Editor reloads may cancel active generations. Keep Foot IK/Raycast or other unexposed capabilities documented as boundaries unless a public command and project asset prove them.
