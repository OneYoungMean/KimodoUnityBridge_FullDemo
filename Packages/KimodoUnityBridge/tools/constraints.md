---
name: kimodo-constraints
description: Public constraint types resolved to one canonical concrete constraint family.
---

生成命令允许三种 point constraint 类型：`fullbody`、`root2d`（语义上的 root）和 `root_effect`。命令输入中的每个 point constraint 先解析为 canonical sample；同一帧的 Editor/runtime pass 会在底层按 channel 合成为一个 SampleResult，之后 raw constraint 序列化再把 Root2D root 通道折叠进同帧 FullBody/EndEffector 行。

命令边界仍然可以拒绝用户显式重复的 point frame 或 root_path 碰撞，这是输入诊断规则，不代表底层 SampleResult 或 raw constraint 不能复合。内部自动采样、Timeline pass 和 authored constraint 允许同帧叠加；同一 channel 的覆盖按 creation order 的最后值生效。

```json
{
  "frame": 90,
  "type": "root_effect",
  "pose": { "track": "Character.Poses", "index": 0 },
  "root2d": { "position": [2.4, 5.0], "heading": [0, 1] },
  "effect": { "right_hand": { "position": [2.55, 0.95, 5.35] } }
}
```

- `fullbody` 必须提供 `pose`，完整支撑姿态归类为 FullBody。
- `root2d` 需要 `root2d.position` 和 `root2d.heading`，`pose` 可选。
- `root_effect` 需要 root2d 和至少一个 `effect` 手/脚目标，`pose` 可选，归类为带 root support 的 Effector。

转换只改变约束模式和显式 channel mask，不会清空或重建 `rootTQ`、`leftFootTQ`、`rightFootTQ`，也不会覆盖未提供的支撑 Pose 通道。Root path 展开后的内部样本归类为 Root2D。
