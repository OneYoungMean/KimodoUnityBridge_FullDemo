---
name: kimodo-scene-context
description: Resolve and inspect the automatic context shared by Kimodo commands.
---

# Scene context / 场景上下文

Kimodo commands resolve the active Unity scene through one automatically managed temporary
session shared by Timeline evaluation, generation, analysis, and Pose sampling. There is
no separate public context command.

- Scene characters are resolved from the active scene and the selected/open `Animator` when available.
- Protocol character entries use `role: "scene"`; their nested `ghost` entry is an internal evaluation clone and is never a valid character handle.
- Commands reject Ghost hierarchy paths. Pass the scene character `name` or `object_ref` instead.
- Existing clips remain immutable; generation, analysis, retargeting, and corrections append derived assets.
- Returned character and animation names, `{track,index}` pose references, and project-relative paths are the only handles passed between commands.
- Use the character and animation references returned by the command that resolved them;
  all subsequent commands use the same session context.
- Scene changes, Editor reloads, and Play Mode transitions can cancel active generation requests and must be reported.

`session.clear` resets the active scene context; saved Timeline and animation assets are retained. It takes no session-name argument. Context GameObjects are moved under the inactive `__KimodoArchive` scene object, and the next command creates exactly one new active context when needed.

Kimodo rejects every command when more than one non-archived `KimodoSession_*` context exists in a scene. Call `session.clear` to archive the duplicates before retrying.
