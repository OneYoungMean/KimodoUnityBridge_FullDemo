# KimodoUnityBridge 文档与兼容性维护计划

## 目标

让维护文档只描述可验证的产品能力、命令边界、工作流和证据限制；命令参数集中维护在命令目录中。

## 维护依据

1. `Command/command_dispatcher.cs` 暴露的入口。
2. `GetCommandDefinitionsJson()` 与 `Command/help.json` 的命令定义。
3. `kimodo_help`、运行时返回值、状态和错误 envelope。
4. 当前 Unity 项目和包内样例资产的实际结果。

文档不应复制猜测性的完整参数结构，也不应把验证脚本或临时工具作为产品依赖。

## 文档职责

| 文档 | 职责 |
|---|---|
| `development/AGENTS.md` | 仓库协作规则、文档所有权和验证要求 |
| `../README.md` | 人类用户的安装与入门说明 |
| `../README.zh-CN.md` | `README.md` 的简体中文版本 |
| `../SKILL.md` | 安装门槛、任务入口、能力工具编排和公共执行规则 |
| `../Command/help.json` | 维护命令、参数、必选关系和嵌套 schema |
| `../tools/common.md` | 场景上下文、证据、状态和报告公共规则 |
| `../tools/scene-context.md` | 场景输入与证据边界 |
| `../tools/recognition.md` | 语义动作识别工作流和证据决策 |
| `../tools/comparison.md` | 动画质量比较工作流和证据决策 |
| `../tools/generation.md` | 动画生成、验证、约束和派生修正工作流 |
| `../tools/pose.md` | External Pose 创建与编辑 |
| `DEVELOPMENT.md` | 临时开发快照，不是执行契约 |
| `README.md` | 面向维护者的文档导航页 |

## 当前产品边界

- 自动场景上下文和 `animation_analyze` 支持可渲染 Mesh 的分析路径。
- 生成和 Pose 采样仍要求有效 Humanoid Avatar。
- 已完成 Clip 不可变；修正、Retarget 和生成都会追加派生 Clip。
- 静态图片不能单独证明播放连续性、滑步、跳变、加速度或速度连续性。
- Foot IK、Raycast 或其他外部工作流只有在公开命令和项目资产实际支持时才能写成已实现能力。

## 兼容性维护

- 保留 `root2DOverride` 等旧序列化字段的兼容层，直到旧资产迁移和发布支持窗口完成。
- 保留旧 Spline 对象迁移逻辑，直到真实旧资产完成迁移回归。
- 保留 QuickServer 旧 `.venv` 检测与迁移，直到升级和回滚路径验证完成。
- 新代码优先使用 canonical 字段和实时 schema；兼容别名不得继续扩散。

## 暂缓 TODO：Analysis / Pose 协议整理

- [ ] 在 `animation_analyze` 大修完成后，重新生成并校验 `Command/help.json`，删除已废弃的 `clips[].role` 输入和输出残留。
- [ ] 删除 `pose_get` 的 `timeline_time_seconds` 协议，只保留 `clip + clip_time_seconds` 的 Clip-local 采样；同步更新 schema、帮助、示例和测试。
- [ ] 评估并移除 `BuildEffectiveAnalysisOptions` 对 `keyframe_count`、`keyframes.max_count`、`keyframes.enabled` 的兼容清理；破坏性升级后应由 schema 直接拒绝旧字段。
- [ ] 区分并保留仍有实际用途的 Timeline segment `role`、Marker `sourceRole` 与 analysis 输入字段，禁止全局关键字替换。
- [ ] 完成上述协议调整后，重新验证拾取苹果生成流程；在此之前，拾取苹果任务使用当前稳定的 generation 命令链，不依赖未完成的 Analysis 协议重构。

## Constraint 重构计划（分批实施）

### 目标和范围

- 修正 Mix constraint 将多个语义混在一起、Runtime/Editor 再拆分或分别解释的行为；Mix 只作为 command 层的输入收集与归类过程，不作为最终 Runtime/Editor constraint。
- 保留现有 `KimodoMarkerSampleResult` 作为 canonical sample，不以更换底层数据类型为目标。
- 先保留现有公共 command 输入形状和可用的序列化结构；不把本次工作扩展为无关 API/schema 改名。
- command 与 UI 使用同一套采样、状态写入和主动解算入口；最终归类为 FullBody、Effector 或 Root2D。
- 三类 constraint 共享采样和解算准备过程；保留编辑操作和 UI 交互本身，不把它们强行并入公共求解函数。
- `KimodoMarkerSampleResult.sampleValid` 作为 canonical sample 的外层准入标志，取代 `validMask` 的执行语义；它不应被解释为四个可选 effector 都必须显式启用/提供目标。sampleValid 为 false 时在最外层直接阻断，进入采样、求解、预览和导出内部后按已通过校验的有效 sample 处理，不再做逐通道 validity fallback。各约束通道是否参与仍由明确的约束类型、`allowHeading` 意图和 effector enabled 位决定。`enableMask` 的有效语义只保留四个 effector enabled 位；它只决定 raw constraint 是否包含 effector，不决定 preview 绘制。
- carry 对有效 effector target 持续应用刚体变换，不因 effector enabled 位关闭而停止变换，也不改写 enabled 意图；raw constraint 仍按 enabled 位选择 effector。
- 在行为路径和测试稳定后，再执行破坏性升级并将明确废弃的实现、测试、fixture 和文档移动到 archive。不得直接删除；不得覆盖无关工作树改动。

### 实施批次

1. **移除 Mix 的 Runtime/Editor 执行语义并在 command 边界归类。** command 在外层先校验每帧只能有一个 constraint；同一帧出现多个 point constraint 属于未定义行为，直接报错阻断，不进入 Mix 合并。单个 command constraint 内部仍按规则归类：存在 `overridePose` 时为 FullBody；否则有 enabled effector 时为 Effector；否则为 Root2D。Effector 与 Root2D 同时存在时保留 root support，但最终类型为 Effector；FullBody 携带 effector 数据时仍只输出 FullBody。Runtime、Editor、preview 和 exporter 不再把 Mix 当最终 constraint，也不从一个 Mix 样本拆出多条具体 constraint。此批优先切断执行分支，不更换 `KimodoMarkerSampleResult`。
2. **统一 command 与 UI 的主动采样/解算入口。** 扩展现有 constraint 内部方法，使 command 可以请求采样、写入 canonical sample 并主动触发一次解算；UI 操作也调用该共同入口。FullBody + effectors 与 command `overridePose` 均写入相同 sample 状态后解算一次。保留现有 marker 编辑和 UI 交互过程，不把具体拖动/编辑行为合并进解算函数。
3. **将 validity 收敛为外层 `sampleValid`。** `sampleValid` 只表示一次 canonical sample capture 的整体成功/失败，不能要求每种约束的可选字段（例如未启用的 effector）都存在。采样入口、command 构建入口和导出/预览入口统一在最外层阻断 false；通过后内部直接按有效 sample 执行，不再保留逐通道 `validMask` fallback。旧 `validMask` 字段在迁移期仅保留读取兼容或归档转换用途，不参与新执行语义；不在内部按通道把旧 mask 重新合成为 `sampleValid`。`sampleValid == true` 直接放行，`false` 在最外层阻断。
4. **收窄 `enableMask` 的语义。** 只保留四个 effector 的 enabled 意图；root、heading、muscle、root/foot TQ 不再经 `enableMask` gating。heading 是否构建由 marker 的 `allowHeading` 选项控制；当前 UI 将该选项序列化在 `sampleData.enableMask.rootHeading`，迁移时保留其持久化可读性，但把执行语义从通用 enableMask 中抽出。Timeline sample 仍必须带有效 heading。effector enabled 只影响 raw constraint 构建，不控制预览目标的绘制或 carry 变换。
5. **合并 FullBody、Effector、Root2D 的采样与求解准备。** 三类共享完整 support sample、root 保留、heading 解析、effector 目标准备、carry 和一次 pose solve；仅最终 raw constraint 序列化按具体类型有必要差异。此批不重写 UI 编辑流程，也不引入新的 per-type 采样/fallback 路径。
6. **固化 carry 顺序和边界。** 先从 sample/command 得到 effector target，对有效目标应用现有 root 刚体 delta，再结算 root 并使用最终目标解算。carry 不启用或禁用 effector，不生成缺失目标；有效但 disabled 的 target 可以随 root 变换并供预览显示，但不进入 raw effector constraint。
7. **建立 command/UI 行为一致性测试。** 比较两条路径的最终具体类型、canonical `KimodoMarkerSampleResult`、root、heading、effector targets、enabled 意图、carry 后数值、解算次数和 raw JSON。旧 Mix 测试先分类：机械不变量保留；依赖 Mix 多输出、按通道 fallback 或旧 carry 特判的断言不作为新行为 oracle；失效测试只加注释说明原因并移动到 archive，不直接删除。Timeline projection 误差作为独立诊断，不归因于 carry 或 Mix，单独跟踪。
8. **破坏性升级和归档。** 新路径接管并通过验证后，归档旧 Mix Runtime/Editor 分支、旧 mask validity 语义、无调用者兼容路径、失效测试/fixture/文档；更新活动文档。归档内容不可被运行时或测试加载器当作活动协议扫描。若字段/类仍被活动序列化资产引用，先验证引用和版本拒绝边界，再移动相应历史资料。
9. **最终验证与差异审查。** 执行相关 Unity Editor/NUnit 检查、静态术语审计和 `git diff --check`；分别报告 command/UI 一致性、Runtime/export 行为、Timeline projection、构建和未验证事项。检查最终 diff 仅包含本计划所需文件，并保留既有无关工作树改动。

### 每批的边界与保护

- 每批开始前记录 `git status` 和目标文件 diff；不得 reset、clean、checkout 或覆盖既有改动。
- 第一批切断 Mix 执行时，旧 `validMask` 和非 effector enable 位可能仍被其他路径读取；应按调用图逐步替换，不在同一批全局改名或清理。
- 单一 `KimodoMarkerSampleResult.sampleValid` 会影响所有采样生产者和外层消费者，不能只改 marker sampler；特别要覆盖 command pose、retarget marker sampler、path/root sample、export projector、preview solve 和 session 序列化。内部求解不再为每个通道保留 validity 分支。
- command 外层禁止同一帧多个 constraint；重复 point frame 直接报“未定义行为”错误并阻断。`root_path` 具有更高优先级，外层先登记/检查 root path frame；同帧若再出现其他 constraint，不合并、不覆盖，按未定义行为阻断。当前实现的重复帧拒绝应移到外层并保留为正式边界。
- 旧 Mix 测试不默认代表正确行为。每项断言先标注为机械不变量、旧语义、目标语义或独立诊断，再决定保留、拆分、改写或归档。
- 不要求新 candidate 与旧 `KimodoConstraintSampleComposer` 的输出相同；后者含有待审查的旧 Mix/通道 fallback 语义，只作为现状证据，不作为新行为正确性 oracle。

## 验证要求

1. 静态检查文档中的命令名、路径和旧术语。
2. 在维护的 Unity 项目中完成最小导入/编译检查。
3. 按需验证场景上下文、分析图片、Pose/Path、生成终态和派生 Clip 不可变性。
4. 分开报告静态、运行时、图像、场景和播放证据。
5. 只有实际打开返回的视觉证据后，才能报告视觉通过；证据不足时使用 `not_verified`、`needs_revision` 或 `unsupported`。

## 完成标准

- 产品仓库不引用包外的验证夹具、评分键或监督路径。
- 文档、实时 schema 和运行时返回值对同一能力的描述一致。
- 不把兼容层或实验性功能描述成稳定产品能力。
- 每个视觉结论都能追溯到实际打开的返回证据。

## 当前实施记录

- 第一批已切断 Runtime/Editor 的 Mix 执行分支，并在 command 外层拒绝同帧冲突；`root_path` 起点和终点均参与顺序无关的冲突校验。
- 第二批已将 command preflight、pose retarget 和 UI preview 接入 `KimodoConstraintPosePipeline.TryApplyCanonical`，Root2D support pose 的显示特判集中在该入口。
- 第三批已加入 `KimodoMarkerSampleResult.sampleValid`。采样成功路径写入 `true`，共享 composer、内部构建、求解、预览写回、runtime projector 和 session 输出在外层拒绝 `false`；`validMask` 仍保留为旧序列化兼容字段。
- 第四批已收窄新执行语义：`enableMask` 不再写入或 gating root position、muscle、root/foot TQ；四个 effector 位保留为 raw constraint 意图，`rootHeading` 仅作为 `allowHeading` 的现有持久化位。Root payload 由 `rootOverride` 是否存在决定，旧 `validMask` 仅保留读取/兼容。command pose、Timeline sampler、RawMotion producer、UI root editing 和 preview promotion 已按此收口。
- 已验证：Unity 脚本导入/编译成功；Constraint SampleData 边界测试 12 个通过、0 个失败、11 个旧语义测试忽略。PoseRig 测试 18 个通过、1 个独立 Timeline projection 诊断忽略、1 个旧 FullBody carry 断言归档、0 个活动失败。
- 第五批已完成受限切口：Root2D command point 在构造 canonical sample 后，通过 `KimodoConstraintPosePipeline.TryApplyCanonical` 做一次 support solve；RootEffect/FullBody 继续复用同一入口。新增 Root2D command support solve 和 disabled-but-valid carry 测试；carry 现在对所有有效 target payload 应用 root delta，effector enabled 位只控制 raw 输出。
- 第六批已完成 carry 边界固化：Root2D 不 carry，sample invalid 不 carry，其他具体 constraint 家族对有效 target payload 持续 carry；carry 不会隐式开启 effector enabled 位。
- 第七批已加入 command/UI canonical intent 对照测试，验证 sampleValid、具体 constraintMode、allowHeading、effector enabled、rootOverride 和 target payload 在两条路径上保持一致。
- 第八批已完成测试归档 ledger：所有忽略的旧 Mix、多通道 validity fallback、旧 root enableMask gating 和独立 Timeline projection 诊断均有原因记录；运行时旧序列化字段继续保留，避免破坏历史资产读取。
- 第九批已完成当前范围的静态审查和 Unity 编译；SampleData 测试 13 通过、0 失败、11 忽略，PoseRig 测试 20 通过、0 失败、2 忽略。完整 EditMode 矩阵已于 2026-10-07 完成：排除四项独立兼容/夹具失败后，130 通过、0 失败、13 忽略；最终全文件 diff 审查仍待完成。
- 分析根轨迹已去除旧 `enableMask.rootPosition` 门槛，改为由 `sampleValid + rootOverride` 判定完整 root payload；序列化边界测试已在 Unity CLI 中通过（同帧拒绝、异帧保留各 1 个）。ArcWalk 已通过 root trajectory 阶段；当前剩余失败来自测试环境不支持 `RGBA8 sRGB` 随机写入的 GPU 渲染路径。
- Unity CLI 图形后端完整 EditMode（Unity 2022.3.62f3c1，147 项）为 130 通过、4 失败、13 忽略；ArcWalk 与 PoseDepth 均通过。剩余 4 项均为独立问题：HandGoal 180° Avatar 诊断、RawMotion heading override 既有行为失败、PoseGet fixture 场景不可用、RealKModel async 返回值与 Runner 不兼容；没有新的 Constraint 协议断言失败。RootEffect、SampleData、PoseRig 和新增协议边界测试均无活动失败。无图形后端的 ArcWalk 另有 RGBA8 sRGB random-write 环境限制，不作为产品失败。
- 当前边界：`validMask` 的生产/序列化兼容字段和 `KimodoConstraintSampleComposer` 中的历史注释代码仍保留；`KimodoConstraintProtocolNormalizer` 现在只在序列化边界拒绝 Root2D 与其他 root-context 同帧组合，不再折叠或改写 payload；只有在完成完整项目测试矩阵和资产迁移拒绝边界验证后，才继续归档无调用者的旧实现。
- 最终回归（2026-10-07）：在 Unity 2022.3.62f3c1 CLI 中重新编译后，`KimodoConstraintProtocolBoundaryTests` 2/2 通过，覆盖同帧 Root2D + FullBody 拒绝及异帧 payload 保留；`GenerationRequest_RootEffect_EmitsRootAndEffectorAtOneFrame` 1/1 通过。此次重写后的 `KimodoConstraintProtocolNormalizer` 未产生编译错误或活动测试失败。
- 兼容项排除后的完整 EditMode 矩阵（2026-10-07）：明确排除 HandGoal Avatar 旋转、RawMotion heading override、PoseGet fixture 和 RealKModel async Runner 四个已知独立问题后，Unity 2022.3.62f3c1 CLI 执行 143 个可运行测试，130 通过、0 失败、13 忽略；未发现新的 Constraint 协议活动失败。
- 破坏性收口（2026-10-07）：旧 `validMask` 不再参与 Constraint 执行、导出或 session 持久化；旧字段仅作为 Unity 资产反序列化占位，缺失的 `sampleValid` 按 `true` 处理。Mix 不再映射到具体执行 family，活动路径直接拒绝 Mix；旧 Composer 的同帧合并实现已移出活动编译路径。旧 `pose_get.timeline_time_seconds` 和 `keyframe_count`/`keyframes.max_count` 兼容清理本轮仅完成现状核查，尚未实施。
- 空间与 carry 切口（2026-10-08）：`KimodoRigidTransform` 增加 per-effector `KimodoConstraintSpace`，UI 首选 Local、command 明确写入 World；local/world 转换集中在 `KimodoConstraintSpaceUtility`，pipeline、pose command、preview handle 和 AutoSample 边界统一使用一次转换。rootOverride 仍为 world-space，root 编辑不再修改任何 effector。
- UI/Preview 收口（2026-10-08）：FullBody、Effector、Root2D 共用 effector 面板；Root2D 面板只读；Inspector 与 Edit Window 继续复用同一入口；移除 carry 的 Inspector/SceneView 行为。`TryApplyCanonical` 不再把 Root2D 上层提升为 FullBody，Root2D 由底层 solver 直接处理且不执行 effector IK。
- Command Root2D（2026-10-08）：Root2D command 先构造 canonical root payload，再仅在 command 边界将 root position 的 Y 固定为 1；UI authored Root2D 高度不被覆盖，RootEffect 复用该路径。
- 验证（2026-10-08）：`KimodoTool.Editor.Tests.csproj` 编译通过（0 错误）；旧 carry 行为测试已标记 Ignore 作为归档语义，新增长度往返、root 编辑不改 effector、Root2D command Y=1 回归断言。完整 Unity EditMode 运行需等桌面 FullDemo 解锁后执行。
- Foot IK 边界（2026-10-08）：Constraint 专用 `AnimationClipPlayable` 显式关闭 `SetApplyFootIK`。原生 Foot IK 不再绕过 effector enabled mask 固定脚部；普通采样路径仍保留原有 Foot IK 默认行为。
