---
name: kimodo-unity-bridge
description: Install KimodoUnityBridge and route Unity animation work through its cooperating capability tools.
---

# KimodoUnityBridge

根 Skill 负责安装门槛、任务入口、命令调用方式、工具编排和统一结果；公共规则见 `tools/common.md`，具体命令参数以实时 schema 为准。

本文件及同目录 `tools/*.md` 是已导入 `com.unity.kimodo_unity_motion_tools` package 的唯一权威 Skill 文档。使用项目入口时，必须先定位该 package 根目录，再按相对路径读取这里的文档；不得使用外部复制的旧工具文档。

## Public command entry / 公共命令入口

CLI、`exec_editor_script` 和其他外部脚本只使用下面的公共 Editor 入口：

```csharp
using KimodoUnityBridge.Command;

string schema = command_dispatcher.GetCommandDefinitionsJson();
string result = command_dispatcher.Invoke(commandName, argumentsJson);
```

调用顺序固定为：

1. 读取 `GetCommandDefinitionsJson()`，或调用 `kimodo_help` 获取实时命令和 schema。
2. 使用 schema 中的协议命令名，例如 `kimodo_generate_animation`、`scene.context`。
3. 将 JSON 参数传给 `command_dispatcher.Invoke(commandName, argumentsJson)`。
4. 对 JSON 返回值检查 `ok`、`status`、`error` 和 `request_id`。

下面这些名称不是 CLI 的命令入口，不要用它们做命令索引或通过反射调用：

- `command_context` 的内部 Handler，例如 `GenerateAnimationAsset`；
- `command_kimodo` 的便利包装方法；
- `KimodoBridgeService.GenerateAsync`、`KimodoBridgeCommand.ExecuteAsync`；
- `KimodoEditorGeneratePipeline.ExecuteAsync` 及其他内部 Editor 管线方法。

`command_kimodo`、Bridge service 和 Editor pipeline 可以供包内 C# 代码使用，但不替代命令目录，也不保证覆盖全部协议命令。CLI 的稳定标识是 schema 中的命令名，不是 C# 方法名。

能力文档中的 `kimodo_generate_animation(args)`、`scene.context(args)` 等写法是协议命令的伪代码表示，不是可直接索引的 C# 方法。若当前环境没有原生能力工具，必须通过 `exec_editor_script` 创建 C# 脚本，并在脚本中调用 `command_dispatcher.Invoke`。

```pseudo
# Shared states / 公共状态
#define YES             1
#define NO              0
#define UNKNOWN         -1
#define NOT_APPLICABLE  -2

WORKFLOW_READY = UNKNOWN
```

## Installation gate / 安装门槛

```pseudo
PACKAGE_IMPORTED = project_contains_package()
UNITY_READY      = unity_import_and_compile_completed()
FIRST_USE        = project_runtime_has_never_been_initialized()
UPGRADE          = package_version_changed()
RECOVERY         = runtime_diagnostics_report_missing_or_broken_components()

if PACKAGE_IMPORTED != YES:
    add_package_to_target_unity_project()

wait_until(UNITY_READY == YES)

if FIRST_USE == YES or UPGRADE == YES or RECOVERY == YES:
    install = kimodo_install_server({})
    install_request_id = install.request_id
    poll kimodo_get_generation({request_id: install_request_id})
        until status in {"done", "error"}
    ASSERT status == "done"

# 普通编译、导入或 Editor 重启不重复安装。
WORKFLOW_READY = (
    PACKAGE_IMPORTED == YES
    and UNITY_READY == YES
    and required_project_components_available() == YES
)

ASSERT WORKFLOW_READY == YES before animation work
```

## Capability tools / 能力工具

工具按能力协作，不按单个命令拆分：

| Tool | 责任 | 入口文档 |
|---|---|---|
| common | 场景上下文、证据、状态和报告公共规则 | `tools/common.md` |
| scene-context | 记录场景解析与证据边界 | `tools/scene-context.md` |
| generation | 生成、轮询、取消，以及 Retarget 派生结果 | `tools/generation.md` |
| recognition | 根据明确语义识别动作 | `tools/recognition.md` |
| comparison | 在相同证据条件下比较两个候选 | `tools/comparison.md` |
| pose | 创建、编辑和复用 External Pose | `tools/pose.md` |
| constraints | 点约束格式、Pose 使用规则、同帧冲突和效应器预检 | `tools/constraints.md` |

命令目录只在调用边界使用；不要把完整参数结构复制到工具文档中。能力工具负责流程和证据，`Command/help.json` 与实时 `kimodo_help` 负责命令名、参数和返回结构。

## Orchestration loop / 总编排 Loop

```pseudo
function run_kimodo_task(request):
    intent = classify_intent(request)
    ensure_installation_gate()

    if request.has_animation_reference():
        analysis = run_analysis_tool(request)
        pose_set = run_pose_loop(analysis, request)
    elif request.has_pose_or_keyframe_reference():
        pose_set = run_pose_loop(request, request)
    else:
        pose_set = EMPTY_CONFIRMED_POSE_SET

    best = NONE
    no_improvement = 0
    do:
        generated = run_generation_tool(request, pose_set)
        analysis = run_analysis_and_evaluation(generated, request)
        if best == NONE:
            best = generated
            no_improvement += 1
        else:
            comparison = run_compare_tool(generated, best, request)
            if comparison.improves_best == YES:
                best = generated
                no_improvement = 0
            else:
                no_improvement += 1
        if no_improvement > request.max_no_improvement:
            return unified_report(best, analysis)
        request, pose_set = plan_next_iteration(request, pose_set, analysis)
    while true
```

Analysis evidence never enters generation directly. It must pass through the
Pose loop and become an explicitly confirmed Pose before generation may use it.
The first generated candidate is only a baseline; Compare must run before final
output. The loop stops when consecutive non-improvement exceeds the configured
`max_no_improvement`.

路由至少覆盖 installation、scene context、generation、recognition、comparison、pose 和 retarget；未匹配到能力时才返回 `unsupported` 或 `needs_clarification`。

## Shared execution rules / 公共执行规则

- 只使用运行时返回的安全角色名、动画名和 `{track,index}` 引用；生成结果的 `path` 是资产元数据，不是场景 Clip handle。
- 不得把伪代码中的能力名称、C# Handler 名称或 Editor pipeline 类型当作 CLI 命令名；命令名必须来自实时 schema。
- 所有命令都以当前活动场景中用户打开/选中的 `Animator` 所属角色为权威来源；不要从保存的 prefab 路径猜测替代场景对象。
- 只要生成请求点名动作（例如 walk/run），生成前必须检查当前场景中是否已有语义匹配的角色/动画；修复、改进、替换、续作或变体请求绝不能跳过这一步。找到的动画先作为上下文证据分析，只有请求明确要求参考、复用或约束时才传入生成约束。
- 视觉结论必须建立在实际打开的返回图像上；静态证据不足时不得报告视觉通过。
- 已完成 Clip 不覆盖；修正、Retarget 和生成变体均追加派生 Clip。
- 安装和生成都返回 `request_id`，并通过 `kimodo_get_generation` 轮询。安装终态为 `done` 或 `error`；生成终态为 `completed`、`failed` 或 `canceled`。轮询必须有固定间隔和总超时；过期或未知请求按失败/未验证报告。
- assembly reload、Editor 退出、切换场景或进入 Play Mode 导致的取消必须如实报告，不声称自动恢复。
- 工具报告至少包含 `result`、`output`、`criteria`、`evidence`、`unverified`；工具可附加专属字段。
- 请求未声明的 source、target、range、path 或 constraint 不得自行补全；按约束格式决定 `pose` 是否需要提供。

需要 Unity 对象细节时，使用命令返回的路径、角色名、动画名或 `{track,index}` 引用；不读取内部上下文文件。
