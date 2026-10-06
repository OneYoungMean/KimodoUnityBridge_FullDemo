---
name: kimodo-pose
description: Create and edit External Pose slots for explicit Humanoid pose constraints.
---

# Pose tool / Pose 工具

负责 `pose_get`、`pose_set` 以及按需拆分使用的 `pose_set_root_transform` 和 `pose_set_muscle`。

## Pose loop / Pose 闭环

Analysis candidates are not generation inputs. They first pass through the
implementation's pose conversion or materialization step, then inspection and
explicit selection:

```text
AnalysisCandidate -> PoseCandidate -> inspect -> confirmed|disabled
                  -> ConfirmedPoseSet -> generation constraints
```

Pose sources may be combined. A source can provide a whole pose, a body region,
or a contact relationship. Record its role and scope instead of copying an
entire source pose implicitly:

```json
{
  "pose": { "track": "<returned track>", "index": 0 },
  "role": "start|contact|impact|end|loop_seam",
  "scope": ["fullbody", "left_hand", "right_hand", "root2d"],
  "status": "candidate|confirmed|disabled",
  "evidence": ["tile:<id>"]
}
```

Adding, editing, or disabling a Pose creates a derived PoseSet for the next
iteration. It does not mutate the previous Clip. Only `confirmed` Pose
references may be passed to generation, and only when the request explicitly
asks to use them.

`transform_capture` 接受 `pose_get` 返回的 `pose: {track,index}`。在 `transforms`
中使用特殊 key `@pose` 表示该 Pose 的角色，`@pose/<相对骨骼路径>` 表示该角色局部；
同一数组中加入桌子、苹果等场景层级路径即可在真实世界坐标下合拍。返回四视图
`image_path`、Pose 来源和 `evaluated_joints_world`，截图不会移动原角色，渲染可见性会恢复。
该命令不接受 `frames`；要检查动画指定帧，先 `pose_get`，再用返回的 Pose 引用截图。

```json
{
  "pose": { "track": "<pose_get 返回的 track>", "index": 0 },
  "transforms": ["@pose", "Desk", "Desk/Apple"],
  "resolution": 768
}
```

- 仅对当前场景上下文中明确指定的 Humanoid Clip 采样。
- 只复用运行时返回的 `{track,index}` Pose 引用。
- 编辑命令都创建派生 Pose，不覆盖已存在的 Pose。
- Pose 引用只有在用户明确要求约束时才传入 generation；不能把分析选帧自动当作约束。

`pose_get` 支持三种互斥的时间寻址方式：

约束如何使用 `pose_get` 返回的 `{track,index}`，见 [`constraints.md`](constraints.md)。
`root2d` 与 `root_effect` 的 Pose 可省略；`fullbody` 需要显式 Pose。写入末端目标前，
提交生成前由统一的非阻断 generate check 检查其可达性。

```json
{ "source": { "character": "<character>", "timeline_time_seconds": 1.5 } }
```

上面按角色的 Timeline 全局时间采样。也可以指定目标 Clip，并使用 Clip 在 Timeline 上的局部时间：

```json
{ "source": { "character": "<character>", "clip": "<clip>", "clip_time_seconds": 0.5 } }
```

当角色已经绑定到当前场景的 Animator/Timeline，但没有可用的源 Clip 时，使用场景时间直接采样：

```json
{ "source": { "character": "<character>", "scene_time_seconds": 0.0 } }
```

三种模式不能混用。`scene_time_seconds` 会从当前场景上下文的真实角色、Animator 和
Timeline 采样完整支撑姿态，即使当前角色没有 Timeline Clip，也会返回可供后续约束
引用的 `{track,index}`。返回的 `timeline_frame_60` 是由全局秒值派生的 60 FPS 对齐字段；
命令会按 Clip 在 Timeline 中的实际播放范围采样。

生成约束时，只将已确认的 `{track,index}` 传入请求。三种约束的完整协议见
[`constraints.md`](constraints.md)。

`pose_set` 可在一次调用中同时更新 root 和 muscles，至少提供其中一项。输出至少包含新的 `{track,index}`、来源角色/Clip/全局帧，以及未验证项目。

`pose_set` 也支持 `effector` 对象，用于更新末端执行器目标。键名为 `left_hand`、`right_hand`、`left_foot`、`right_foot`，每个值至少包含一个 `position: [x,y,z]` 或 `rotation: [x,y,z,w]`；只提交的通道会覆盖原值，并标记对应的有效性。例如：

```json
{
  "pose": { "track": "Character Pose", "index": 12 },
  "effector": {
    "left_hand": { "position": [0.2, 1.1, 0.4] },
    "right_foot": { "rotation": [0, 0, 0, 1] }
  }
}
```
