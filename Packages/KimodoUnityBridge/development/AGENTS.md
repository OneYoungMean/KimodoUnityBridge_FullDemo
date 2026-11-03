# Repository instructions

## Scope

These rules apply to repository maintenance. Animation task execution is defined by [tools/common.md](../tools/common.md); command details are defined by [Command/help.json](../Command/help.json).

## Repository rules

- Preserve unrelated worktree changes.
- Modify only files required by the requested task.
- Code edits and code-level removals may be made directly; they do not need to be archived. When removing one or more files, move each file and its associated Unity `.meta` file, if present, to the nearest `archive/` directory first.
- Do not commit or push unless the user explicitly asks.
- Keep runtime compatibility code and attribution intact unless removal is explicitly requested.

## Documentation ownership

- `../README.md` / `../README.zh-CN.md`: human-facing package entry and installation overview.
- `../SKILL.md`: purpose, installation, and documentation navigation.
- `../tools/common.md`: scope, task routing, tool coordination, the active reusable animation loop, shared command/evidence contract, and recognition/comparison rules. Superseded execution guides are retained under `../archive/` and are not active instructions.
- `../Command/help.json`: generated command and parameter schema; keep it aligned with the dispatcher.
- `DEVELOPMENT.md`: temporary development snapshot, not an execution contract.
- `README.md`: development and handoff navigation page.
- `plan.md`: documentation and compatibility maintenance plan.

## Maintenance checks

- For command changes, verify the live schema, `kimodo.help`, and the smallest relevant Unity/editor check.
- For documentation changes, run a static terminology/command audit and `git diff --check`.
- Report static, build, runtime, image, scene, and playback evidence separately.
