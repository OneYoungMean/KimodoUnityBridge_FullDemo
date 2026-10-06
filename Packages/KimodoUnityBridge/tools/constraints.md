---
name: kimodo-constraints
description: Public constraint types normalized to the canonical internal mix representation.
---

生成命令允许三种 point constraint 类型：`fullbody`、`root2d`（语义上的 root）和 `root_effect`。解析后它们统一转换为内部 `constraintMode: "mix"`，由同一套 pipeline/exporter 处理。

```json
{
  "frame": 90,
  "type": "root_effect",
  "pose": { "track": "Character.Poses", "index": 0 },
  "root2d": { "position": [2.4, 5.0], "heading": [0, 1] },
  "effect": { "right_hand": { "position": [2.55, 0.95, 5.35] } }
}
```

- `fullbody` 必须提供 `pose`，完整支撑姿态会转换成 mix 的 muscle/root 通道。
- `root2d` 需要 `root2d.position` 和 `root2d.heading`，`pose` 可选。
- `root_effect` 需要 root2d 和至少一个 `effect` 手/脚目标，`pose` 可选。

转换只改变约束模式和显式 channel mask，不会清空或重建 `rootTQ`、`leftFootTQ`、`rightFootTQ`，也不会覆盖未提供的支撑 Pose 通道。Root path 展开后的内部样本同样使用 mix。
