using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KimodoUnityBridge;
using KimodoBridge;
using KimodoBridge.Editor;
using TimelineInject;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;

namespace KimodoUnityBridge.Command
{
    internal static partial class command_context
    {
        public const string HelpCommand = "kimodo_help";
        public const string SceneContextCommand = "scene.context";
        public const string InstallServerCommand = "kimodo_install_server";
        public const string GenerateAnimationCommand = "kimodo_generate_animation";
        public const string AnimationAnalyzeCommand = "animation_analyze";
        public const string MocapVideoAnalysisCommand = "analysis.video";
        public const string MocapPictureAnalysisCommand = "analysis.picture";
        public const string TransformCaptureCommand = "transform_capture";
        public const string RetargetAnimationCommand = "kimodo_retarget_animation";
        public const string PoseGetCommand = "pose_get";
        public const string PoseTestCommand = "pose_test";
        public const string PoseSetCommand = "pose_set";
        public const string PoseSetRootTransformCommand = "pose_set_root_transform";
        public const string PoseSetMuscleCommand = "pose_set_muscle";
        public const string GetGenerationCommand = "kimodo_get_generation";
        public const string CancelGenerationCommand = "kimodo_cancel_generation";
        public const string SessionClearCommand = "session.clear";
        internal const string HelpAssetPath = "Packages/com.unity.kimodo_unity_motion_tools/Command/help.json";

        private const int MaxRememberedJobs = 128;
        private static readonly Dictionary<Guid, JobRecord> Jobs = new Dictionary<Guid, JobRecord>();
        private static readonly Dictionary<string, JObject> InstallTasks = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, JObject> MocapAnalysisTasks = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
        private static readonly object JobsLock = new object();
        private static readonly Lazy<CommandCatalog> Commands = new Lazy<CommandCatalog>(BuildCommandCatalog, LazyThreadSafetyMode.ExecutionAndPublication);

        public static string GetCommandDefinitionsJson() => BuildCommandDefinitionsJson();

        private static string BuildCommandDefinitionsJson()
        {
            return new JObject
            {
                ["tools"] = new JArray
                {
                    CommandDefinition(HelpCommand,
                        "Return the command manual, detailed parameter documentation for one command, or currently viable model configurations.",
                        Properties(
                            Optional("command", "string", "Command name whose full manual entry should be returned."),
                            new PropertyDefinition("section", new JObject
                            {
                                ["type"] = "string",
                                ["enum"] = new JArray("commands", "models", "constraints"),
                                ["default"] = "commands",
                                ["description"] = "Select the command overview, currently viable models, or generation constraint reference; ignored when command is supplied."
                            }, false))),
                    CommandDefinition(SceneContextCommand,
                        "Return the current active-scene context JSON. Use this response as the authoritative source for scene characters, Clips, Timeline ranges, and object references. Set include_history=true to include the compact Session artifact index.",
                        Properties(Optional("include_history", "boolean", "Include compact animation, generation, and analysis history; defaults to false."))),
                    CommandDefinition(InstallServerCommand,
                        "Start an asynchronous project-local QuickServer installation task. Returns only request_id and status; poll that request with kimodo_get_generation. Models and the Python environment are preserved.",
                        Properties()),
                    CommandDefinition(AnimationAnalyzeCommand,
                        "Analyze one animation clip at 60 FPS and render graph-space picture evidence. Pass a clips array containing exactly one scene character and one loaded Clip name or Assets/Packages Clip path. The result contains one clip analysis and one pictures evidence object; temporal positions use absolute Timeline-global 60 FPS frames. External Clips are appended to the character's Kimodo Timeline and never modified.",
                        Properties(
                            RequiredAnalysisClips(),
                            new PropertyDefinition("picture", new JObject
                            {
                                ["type"] = "object",
                                ["description"] = "Unified graph-space picture request with tiles and output mode."
                            }, false),
                            new PropertyDefinition("resolution", new JObject
                            {
                                ["type"] = "integer",
                                ["minimum"] = 64,
                                ["maximum"] = 4096,
                                ["description"] = "Final picture tile resolution in pixels; accepts 64 through 4096. Rendering uses a 2x supersample and downsamples to this size. Defaults to 1920."
                            }, false))),
                    CommandDefinition(MocapVideoAnalysisCommand,
                        "Estimate a per-frame SMPL-X pose sequence from a local video. This starts an asynchronous analysis job; poll kimodo_get_generation with its request_id. The result includes raw SMPL-X poses and matching FullBody constraints in the existing raw constraint format; it does not create or apply a Unity animation.",
                        Properties(
                            Required("video_path", "string", "Absolute path to a local video readable by QuickServer."),
                            new PropertyDefinition("person", new JObject
                            {
                                ["type"] = "string",
                                ["enum"] = new JArray("left", "right"),
                                ["description"] = "Select one performer by screen side in a two-person video."
                            }, false))),
                    CommandDefinition(MocapPictureAnalysisCommand,
                        "Estimate one SMPL-X pose from a local still image. This starts an asynchronous analysis job; poll kimodo_get_generation with its request_id. The result includes raw SMPL-X pose data and a FullBody constraint in the existing raw constraint format; it does not create or apply a Unity animation.",
                        Properties(
                            Required("image_path", "string", "Absolute path to a local PNG or JPEG readable by QuickServer."))),
                    CommandDefinition(TransformCaptureCommand,
                        "Capture the current scene or an External Pose returned by pose_get together with scene objects, in world space. Returns a four-view image and evaluated joint positions; does not change scene poses.",
                        Properties(
                            Optional("character_path", "string", "Active-scene character hierarchy path; required unless pose is supplied."),
                            new PropertyDefinition("pose", PoseReferenceSchema(), false),
                            new PropertyDefinition("transforms", new JObject
                            {
                                ["type"] = "array", ["minItems"] = 1,
                                ["items"] = new JObject { ["type"] = "string" },
                                ["description"] = "Scene hierarchy paths or character-relative paths. With pose, use @pose for the evaluated character or @pose/<relative bone path>. Include environment paths to frame them together."
                            }, true),
                            Optional("resolution", "integer", "Per-view pixel size, 64 through 4096; defaults to 1024. To capture an animation frame, first call pose_get and pass its pose reference."))),
                    CommandDefinition(RetargetAnimationCommand,
                        "Retarget one loaded animation to another current scene context character and append the result.",
                        Properties(
                            Required("source_character", "string", "Source scene character name or object_ref. Session preview objects are rejected."),
                            Required("animation", "string", "Safe source animation name."),
                            Required("target_character", "string", "Target scene character name or object_ref. Session preview objects are rejected."),
                            OptionalOutput())),
                    CommandDefinition(GenerateAnimationCommand,
                        "Start asynchronous generation for a character in the current scene context. Command-created Timeline Clips enable AutoBegin anchoring automatically; explicit first-frame or In/Out samples take precedence. The accepted response contains request_id, status, and non-blocking request/constraint warnings; pose/effect safety preflight runs only when the same point constraint explicitly contains both pose and effect, never for effect-only constraints. These warnings are not measured joint positions in the generated motion; use pose_get then pose_test to measure sampled joint positions and contact reach after generation. Supply trajectory settings only when the requested path is known from explicit requirements or confirmed scene/analysis evidence; prompt wording never selects path options.",
                        Properties(
                            Required("character", "string", "Scene character name or object_ref. Session preview objects are rejected; use the source scene character."),
                            Required("prompt", "string", "Short semantic description of the current Clip's action; aim for 25 words or fewer. Express exact FullBody, position, and effector anchors through structured constraints."),
                            new PropertyDefinition("duration_frames", new JObject
                            {
                                ["type"] = "integer",
                                ["minimum"] = 1,
                                ["default"] = 300,
                                ["description"] = "Duration in 60 FPS frames; defaults to 300 frames (5 seconds)."
                            }, false),
                            OptionalLoop(),
                            OptionalPath(),
                            OptionalGeneration(),
                            OptionalOutput(),
                            Optional("analysis_option", "object", "Optional generation-time lightweight analysis. When enabled, generation returns sparse salient keyframe markers and foot-contact changes; it does not run the full animation_analyze command."),
                            OptionalConstraints("constraints", "Point constraints are selected by optional pose, root2d, and effect data; multiple fields at one frame compose by channel. Pose/effect safety preflight runs only when the same point constraint explicitly contains both pose and effect; effect-only constraints skip it. root_path and inout remain separate constraint forms; duplicate frames are rejected; command frames use 60 FPS."))),
                    CommandDefinition(PoseGetCommand,
                        "Sample one absolute Timeline-global frame at 60 FPS, or select mode scene_pose / skeleton_rest_pose to read the original scene Animator or Avatar rest pose. Every response includes character_root, the bound scene GameObject root Transform in world space; data.root is the sampled Pose root channel. Every call creates an External Pose slot and returns {track,index}.",
                        Properties(
                            RequiredPoseSource("source"),
                            Optional("full_data", "boolean", "Return all 49 muscles and TQ channels; defaults to false."))),
                    CommandDefinition(PoseTestCommand,
                        "Measure actual Humanoid joint world positions after evaluating any External Pose, including a pose sampled from a generated effect-only result. Optionally compare selected joints with world-space target positions; this is an explicit pose measurement, separate from generation-time non-blocking warnings.",
                        Properties(
                            RequiredPoseReference("pose"),
                            OptionalEnumArray("joints", "Humanoid joints to measure. Defaults to hips, head, hands, and feet; joints named in targets are included automatically.", "Hips", "Head", "LeftHand", "RightHand", "LeftFoot", "RightFoot", "LeftUpperArm", "RightUpperArm", "LeftLowerArm", "RightLowerArm", "LeftUpperLeg", "RightUpperLeg", "LeftLowerLeg", "RightLowerLeg"),
                            OptionalPoseTestTargets())),
                    CommandDefinition(PoseSetCommand,
                        "Modify an External Pose slot. Supply root, muscles, effector, or any combination.",
                        PoseSetSchema()),
                    CommandDefinition(PoseSetRootTransformCommand,
                        "Compatibility alias for pose_set with a root-only update. Prefer pose_set for new calls.",
                        Properties(
                            RequiredPoseReference("pose"),
                            Required("root", "object", "Root position and rotation."))),
                    CommandDefinition(PoseSetMuscleCommand,
                        "Compatibility alias for pose_set with a muscles-only update. Prefer pose_set for new calls.",
                        Properties(
                            RequiredPoseReference("pose"),
                            Required("muscles", "object", "Map of muscle channel names to values."))),
                    CommandDefinition(GetGenerationCommand,
                        "Get status and results for install, generation, or Mocap analysis requests. A completed Mocap analysis returns poses and SMPL-X coordinate metadata at the top level. include_request_json explicitly opts into the submitted generation payload.",
                        Properties(
                            Required("request_id", "string", "Request id returned by kimodo_install_server, kimodo_generate_animation, analysis.video, or analysis.picture."),
                            Optional("include_request_json", "boolean", "Include the exact JSON payload sent to QuickServer; defaults to false."))),
                    CommandDefinition(CancelGenerationCommand,
                        "Request cancellation of an active animation generation and return its compact lifecycle result. Installation requests cannot be canceled; use kimodo_get_generation for detailed progress and diagnostics.",
                        Properties(
                            Required("request_id", "string", "Generation request id returned by kimodo_generate_animation."),
                            Optional("reason", "string", "Optional cancellation reason."))),
                    CommandDefinition(SessionClearCommand,
                        "Clear the active scene context by destroying its transient context GameObjects. Persisted Timeline and animation assets are retained, and one fresh context is recreated lazily by the next command.",
                        Properties()),
                }
            }.ToString(Formatting.None);
        }

        public static string Invoke(string toolName, string argumentsJson = "{}")
        {
            string commandName = toolName?.Trim();
            if (Commands.Value.TryGet(commandName, out CommandRegistration command))
            {
                return command.Handler(argumentsJson);
            }
            return Error("unknown_command", $"Unknown Kimodo command '{toolName ?? string.Empty}'.");
        }

        public static string ListModels(string argumentsJson = "{}")
        {
            return Execute(argumentsJson, _ =>
            {
                EnsureCanManageServer();
                KimodoPlayableClipGenerationSettings settings = KimodoPlayableClipGenerationSettings.instance;
                JObject response = KimodoBridgeService.Shared.ListModelConfigurationsAsync(
                    ResolveModelName(null),
                    KimodoTextEncoderModeProtocol.ToProtocolValue(settings.DefaultTextEncoderMode),
                    settings.LocalModelsPath?.Trim() ?? string.Empty,
                    null,
                    CancellationToken.None).GetAwaiter().GetResult();
                var result = new JObject(response);
                result.Remove("status");
                result["count"] = (result["configs"] as JArray)?.Count ?? 0;
                return Ok(result);
            });
        }

        public static string GetCommandHelp(string argumentsJson = "{}")
        {
            return Execute(argumentsJson, arguments =>
            {
                string section = (arguments.Value<string>("section") ?? "commands").Trim().ToLowerInvariant();
                string command = arguments.Value<string>("command")?.Trim();
                if (!string.IsNullOrWhiteSpace(command))
                {
                    if (!Commands.Value.TryGet(command, out CommandRegistration registration))
                    {
                        throw new InvalidOperationException($"Unknown Kimodo command '{command}'.");
                    }
                    return Ok(new JObject
                    {
                        ["manual"] = registration.ToJson(),
                        ["usage"] = $"{command}(<arguments matching inputSchema>)"
                    });
                }
                if (section == "models")
                {
                    return ListModels("{}");
                }
                if (section == "constraints")
                {
                    return Ok(BuildConstraintManual());
                }
                if (section != "commands")
                {
                    throw new InvalidOperationException("section must be commands, models, or constraints.");
                }

                JObject all = JObject.Parse(Commands.Value.ToJson());
                JObject constraintManual = BuildConstraintManual();
                return Ok(new JObject
                {
                    ["manual"] = "Kimodo command reference",
                    ["execution_model"] = new JArray
                    {
                        "Commands resolve scene and asset references directly; no public Session lifecycle call is required.",
                        "Pass returned object references to later commands; request_id polls installation, generation, or Mocap analysis tasks.",
                        "Installation, generation, and Mocap analysis are asynchronous: save request_id and poll kimodo_get_generation. Tasks use accepted, running, completed, and failed states; generation may also be canceled.",
                        "Generation acceptance returns the request handle, a non-blocking check, and optional warnings. Completion returns the generated Clip metadata and asset path; request details are available only with include_request_json."
                    },
                    ["routing"] = new JArray
                    {
                        Route("discover schema or models", HelpCommand),
                        Route("read active scene context", SceneContextCommand),
                        Route("reinstall the local server", InstallServerCommand, "then " + GetGenerationCommand),
                        Route("generate motion", GenerateAnimationCommand, "then " + GetGenerationCommand),
                        Route("analyze a video for SMPL-X poses", MocapVideoAnalysisCommand, "then " + GetGenerationCommand),
                        Route("analyze a picture for one SMPL-X pose", MocapPictureAnalysisCommand, "then " + GetGenerationCommand),
                        Route("analyze and render motion", AnimationAnalyzeCommand, "returns one composite picture and self-describing tiles"),
                        Route("materialize or edit a pose", PoseGetCommand, "then pose_set"),
                        Route("measure actual joint positions or test contact reach", PoseGetCommand, "then " + PoseTestCommand),
                        Route("obtain a reusable root trajectory", AnimationAnalyzeCommand, "then reference root_trajectory.path from a generation root_path constraint"),
                    },
                    ["handles"] = new JObject
                    {
                        ["request_id"] = "Returned by kimodo_install_server, kimodo_generate_animation, analysis.video, or analysis.picture. Pass it to kimodo_get_generation; only generation request ids can be canceled.",
                        ["pictures.image_path"] = "Read the composite PNG returned by animation_analyze.",
                        ["pose"] = "A {track,index} reference returned by pose_get or a pose editing command.",
                        ["clip.asset_path"] = "A project-relative generated animation asset path returned after generation completes."
                    },
                    ["workflow"] = new JArray
                    {
                        new JObject { ["command"] = InstallServerCommand, ["arguments"] = new JObject(), ["save"] = "request_id", ["before"] = "all other Commands" },
                        new JObject { ["command"] = GetGenerationCommand, ["arguments"] = new JObject { ["request_id"] = "<install_request_id>" }, ["repeat_until"] = "status is completed or failed" },
                        new JObject { ["command"] = GenerateAnimationCommand, ["arguments"] = new JObject { ["character"] = "<character>", ["prompt"] = "<short action for one Clip>", ["duration_frames"] = 60 }, ["save"] = "request_id" },
                        new JObject { ["command"] = GetGenerationCommand, ["arguments"] = new JObject { ["request_id"] = "<request_id>" }, ["repeat_until"] = "status is completed, failed, or canceled" },
                        new JObject { ["command"] = AnimationAnalyzeCommand, ["arguments"] = new JObject { ["clips"] = new JArray(new JObject { ["character"] = "<character>", ["clip"] = "<completed animation>" }), ["picture"] = new JObject { ["output"] = "both" } }, ["save"] = "pictures.image_path" },
                    },
                    ["commands"] = new JArray(all["tools"].Children<JObject>().Select(item => new JObject
                    {
                        ["name"] = item.Value<string>("name"),
                        ["description"] = item.Value<string>("description"),
                        ["documentation"] = item["documentation"]?.DeepClone() ?? new JObject(),
                        ["required"] = item["inputSchema"]?["required"]?.DeepClone() ?? new JArray(),
                        ["outputSchema"] = item["outputSchema"]?.DeepClone() ?? new JObject(),
                        ["examples"] = item["examples"]?.DeepClone() ?? new JArray()
                    })),
                    ["constraints"] = constraintManual["constraints"].DeepClone(),
                    ["constraint_rules"] = constraintManual["rules"].DeepClone()
                });
            });
        }

        private static JObject BuildConstraintManual()
        {
            return new JObject
            {
                ["manual"] = "Kimodo generation constraint reference",
                ["constraints"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "point",
                        ["description"] = "Optional pose, root2d, and effect data select and compose constraint channels at one frame. Pose/effect safety preflight runs only when the same point constraint explicitly contains both pose and effect.",
                        ["shape"] = new JObject
                        {
                            ["frame"] = "Relative frame in the generated clip.",
                            ["pose"] = "Optional FullBody External Pose reference: {track,index}.",
                            ["root2d"] = "Optional root position [x,z] and heading [x,z]; overlays the pose root channels.",
                            ["effect"] = "Optional hand/foot targets with position and optional rotation."
                        }
                    },
                    new JObject
                    {
                        ["type"] = "inout",
                        ["description"] = "Explicit source Clip windows used as boundary context. Outside adds and crops context; inside retains constrained windows within the output.",
                        ["shape"] = InOutConstraintSchema(),
                        ["examples"] = BuildCommandExamples(GenerateAnimationCommand)
                    },
                    new JObject
                    {
                        ["type"] = "root_path",
                        ["description"] = "A reusable analyzed Root Path compiled to Root2D samples during generation.",
                        ["shape"] = new JObject
                        {
                            ["frame"] = "Optional first path frame; defaults to 0.",
                            ["root_path"] = new JObject { ["path"] = "{track,index} from animation_analyze clip.root_trajectory.path" }
                        }
                    }
                },
                ["rules"] = new JArray
                {
                    "Point constraint family is inferred from the presence of pose, root2d, and effect data. Any non-empty combination composes by channel; Root2D overlays planar root channels and effect data overlays the named end effectors.",
                    "Pose/effect safety preflight runs only when the same point constraint explicitly contains both pose and effect. Effect-only constraints skip this preflight; pose_test remains available to measure actual sampled poses after generation.",
                    "A point constraint must contain at least one of pose, root2d, or effect. Put mixed data in one point object; separate point objects at a duplicate frame are rejected.",
                    "Use animation_analyze, confirm that the returned Root Path belongs to the intended character and motion, then reference clip.root_trajectory.path from root_path.",
                    "Use path angles, distance, or heading only for a known trajectory backed by an explicit requirement or confirmed evidence. Do not infer any trajectory setting from prompt keywords; omit path when the route is unknown.",
                    "Use root_path only with a confirmed animation_analyze Root Path reference for the intended character and motion. It creates only start and end Root2D constraints, so it does not prove an intermediate route.",
                    "An explicit point frame cannot overlap a root_path sample frame; the command rejects the collision.",
                    "At most one standalone inout entry; in/out each require an explicit completed source Clip. Source character defaults to the generation character and must be the same character.",
                    "In/Out window_frames and source.frame use 60 FPS. Windows round up to model frames; sample_count must fit distinct model frames. One sample uses the seam; multiple samples include both endpoints.",
                    "In/Out rejects short sources, overlapping inside windows, conflicting point samples, unsupported ARDY models, loop generation, and runtime duration above the model limit. It does not silently clamp or downgrade.",
                    "inout_sampling is returned after generation completes, with source-local and Timeline-global 60 FPS sample frames, output/runtime model frames, model-frame context counts and the exclusive model-frame crop range. Padding/cropping is automatic; the final requested duration is preserved."
                }
            };
        }

        public static string InstallServer(string argumentsJson = "{}")
        {
            return Execute(argumentsJson, _ =>
            {
                EnsureCanManageServer();
                string requestId = "install:" + Guid.NewGuid().ToString("N");
                lock (JobsLock)
                {
                    InstallTasks[requestId] = new JObject
                    {
                        ["status"] = "queued",
                        ["task_id"] = requestId,
                        ["progress"] = "0/0",
                        ["eta_seconds"] = 60.0,
                        ["message"] = "Server installation waiting to start."
                    };
                    while (InstallTasks.Count > MaxRememberedJobs)
                    {
                        InstallTasks.Remove(InstallTasks.Keys.First());
                    }
                }
                EditorApplication.delayCall += async () =>
                {
                    await RunInstallServerTaskAsync(requestId);
                };
                return Ok(new JObject
                {
                    ["request_id"] = requestId,
                    ["status"] = "accepted"
                });
            });
        }

        private static async Task RunInstallServerTaskAsync(string requestId)
        {
            try
            {
                UpdateInstallTask(requestId, "loading", 60.0, "Installing server runtime...");
                string runtimeRoot = KimodoBridgeServerTool.GetRuntimeRootPath();
                using (KimodoBridgeServerTool.EnterRuntimeMaintenanceScope())
                {
                    await KimodoBridgeService.Shared.StopAsync(CancellationToken.None);
                    // Installation is an explicit recovery/bootstrap boundary.  A
                    // refresh leaves stale setup sentinels and venv paths in a
                    // copied project runtime, which can make the launcher resolve
                    // Python from a different project.  Reinstall clears the
                    // runtime while preserving its models and copies a clean
                    // package template before warmup.
                    if (!KimodoBridgeServerTool.ReinstallRuntimeRoot())
                    {
                        throw new InvalidOperationException("Failed to reinstall runtime root from package template.");
                    }
                }

                UpdateInstallTask(requestId, "loading", 60.0, "Starting QuickServer...");
                await KimodoBridgeService.Shared.WarmupAsync(null, CancellationToken.None);
                UpdateInstallTask(requestId, "done", 0.0, "Server installation complete.", runtimeRoot);
            }
            catch (Exception ex)
            {
                UpdateInstallTask(requestId, "error", 0.0, ex.Message);
            }
        }

        private static void UpdateInstallTask(string requestId, string status, double? eta, string message, string runtimeRoot = null)
        {
            lock (JobsLock)
            {
                if (!InstallTasks.TryGetValue(requestId, out JObject task)) return;
                task["status"] = status;
                task["eta_seconds"] = eta.HasValue ? (JToken)eta.Value : JValue.CreateNull();
                task["message"] = message ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(runtimeRoot))
                {
                    task["runtime_root"] = runtimeRoot;
                    task["runtime_version"] = KimodoServerRuntimeUtil.ReadQuickServerVersion(runtimeRoot);
                    task["install_mode"] = "reinstall";
                    task["server_connected"] = KimodoBridgeService.Shared.IsConnected;
                }
            }
        }

        public static string GetGeneration(string argumentsJson)
        {
            return Execute(argumentsJson, arguments =>
            {
                string requestValue = RequiredStringValue(arguments, "request_id");
                bool includeRequestJson = arguments.Value<bool?>("include_request_json") == true;
                if (requestValue.StartsWith("install:", StringComparison.OrdinalIgnoreCase))
                {
                    lock (JobsLock)
                    {
                        if (InstallTasks.TryGetValue(requestValue, out JObject installStatus))
                        {
                            return Ok(NormalizeInstallStatus(installStatus, requestValue));
                        }
                    }
                    throw new InvalidOperationException($"Unknown or expired request_id '{requestValue}'.");
                }
                if (requestValue.StartsWith("mocap:", StringComparison.OrdinalIgnoreCase))
                {
                    return Ok(GetMocapAnalysisTask(requestValue));
                }
                TimelineSessionRecord session = RequireCurrentTimelineSession();
                if (!Guid.TryParse(requestValue, out Guid requestId))
                {
                    throw new InvalidOperationException("request_id is not a valid GUID or install task id.");
                }
                if (!TryGetJob(requestId, out JobRecord record))
                {
                    JObject persisted = LoadPersistedGenerationJob(session, requestId);
                    persisted["target_alive"] = false;
                    return Ok(NormalizeGenerationStatus(persisted, requestValue, includeRequestJson));
                }
                EnsureGenerationBelongsToSession(record, session);
                JObject status = BuildStatus(record, includeRequestJson);
                try
                {
                    JObject serverStatus = KimodoBridgeService.Shared
                        .GetStatusAsync(requestId.ToString("N"), CancellationToken.None)
                        .GetAwaiter().GetResult();
                    string serverState = serverStatus?.Value<string>("status");
                    if (serverStatus != null && !string.IsNullOrWhiteSpace(serverState) &&
                        !string.Equals(serverState, "idle", StringComparison.OrdinalIgnoreCase))
                    {
                        status["request_id"] = requestValue;
                        // Keep the Unity job status as the public lifecycle source of truth.
                        // QuickServer's `done` only means the backend response is ready; the
                        // Unity-side asset write/bake may still be running.
                        status["backend_progress"] = serverStatus["progress"]?.DeepClone() ?? "0/0";
                        status["eta_seconds"] = serverStatus["eta_seconds"]?.DeepClone() ?? JValue.CreateNull();
                        status["message"] = serverStatus["message"]?.DeepClone() ?? string.Empty;
                    }
                }
                catch
                {
                    // Preserve the local completed result if the server has already exited.
                }
                status["target_alive"] = record.Target != null;
                return Ok(NormalizeGenerationStatus(status, requestValue, includeRequestJson));
            });
        }

        public static string CancelGeneration(string argumentsJson)
        {
            return Execute(argumentsJson, arguments =>
            {
                TimelineSessionRecord session = RequireCurrentTimelineSession();
                Guid requestId = RequiredRequestId(arguments);
                if (!TryGetJob(requestId, out JobRecord record))
                {
                    JObject persisted = LoadPersistedGenerationJob(session, requestId);
                    persisted["canceled"] = false;
                    return Ok(BuildCancellationStatus(
                        NormalizeGenerationStatus(persisted, requestId.ToString("D")),
                        requestId.ToString("D"),
                        canceled: false));
                }
                EnsureGenerationBelongsToSession(record, session);
                string reason = arguments.Value<string>("reason")?.Trim();
                bool canceled = KimodoEditorGenerationJobService.Cancel(
                    requestId,
                    string.IsNullOrWhiteSpace(reason) ? "Generation canceled by command." : reason);
                PersistGenerationJobStatus(record.Session);
                JObject status = BuildStatus(record);
                status["canceled"] = canceled;
                return Ok(BuildCancellationStatus(
                    NormalizeGenerationStatus(status, requestId.ToString("D")),
                    requestId.ToString("D"),
                    canceled));
            });
        }

        private static JObject BuildCancellationStatus(JObject normalized, string requestId, bool canceled)
        {
            var result = new JObject
            {
                ["request_id"] = requestId,
                ["status"] = normalized?.Value<string>("status") ?? string.Empty,
                ["stage"] = normalized?.Value<string>("stage") ?? string.Empty,
                ["message"] = normalized?.Value<string>("message") ?? string.Empty,
                ["canceled"] = canceled
            };
            if (normalized?["clip"] != null)
            {
                result["clip"] = normalized["clip"].DeepClone();
            }
            if (normalized?["error"] != null)
            {
                result["error"] = normalized["error"].DeepClone();
            }
            return result;
        }

        public static string GenerateAnimationAsset(string argumentsJson)
        {
            return Execute(argumentsJson, arguments =>
            {
                EnsureCanGenerate();
                TimelineSessionRecord session = RequireCurrentTimelineSession();
                string prompt = RequiredStringValue(arguments, "prompt");
                ResolvedCharacter character = ResolveCharacter(session, RequiredStringValue(arguments, "character"));
                JObject pathOptions = arguments["path"] as JObject;
                JObject generationOptions = arguments["generation"] as JObject;
                JObject outputOptions = arguments["output"] as JObject;
                string outputMode = ParseOutputMode(outputOptions?.Value<string>("mode"));
                string requestedModel = generationOptions?.Value<string>("model")?.Trim();
                string requestedTextEncoder = generationOptions?.Value<string>("text_encoder")?.Trim();
                string modelName = ResolveModelName(requestedModel);
                KimodoTextEncoderMode textEncoderMode = ResolveTextEncoderMode(requestedTextEncoder);
                JObject modelConfiguration = null;
                if (!string.IsNullOrWhiteSpace(requestedModel) || !string.IsNullOrWhiteSpace(requestedTextEncoder))
                {
                    modelConfiguration = EnsureRegisteredModel(modelName, textEncoderMode);
                }
                float frameRate = ResolveFrameRate(modelName, modelConfiguration);
                int durationFrames = arguments.Value<int?>("duration_frames") ?? 300;
                if (durationFrames <= 0)
                {
                    throw new InvalidOperationException("duration_frames must be a positive integer at 60 FPS.");
                }
                JObject loopObject = arguments["loop"] as JObject;
                bool loopRequested = loopObject != null
                    ? loopObject.Value<bool?>("enabled") ?? true
                    : arguments.Value<bool?>("loop") ?? false;
                JObject lockPosition = loopObject?["lock_pos"] as JObject;
                JObject lockRotation = loopObject?["lock_rot"] as JObject;
                bool loopLockPositionX = lockPosition?.Value<bool?>("x") ?? false;
                bool loopLockPositionY = lockPosition?.Value<bool?>("y") ?? true;
                bool loopLockPositionZ = lockPosition?.Value<bool?>("z") ?? false;
                bool loopLockRotationX = lockRotation?.Value<bool?>("x") ?? true;
                bool loopLockRotationY = lockRotation?.Value<bool?>("y") ?? false;
                bool loopLockRotationZ = lockRotation?.Value<bool?>("z") ?? true;
                bool hasPathBeginAngle = pathOptions?["start_angle"] != null;
                float pathBeginAngleDegrees = hasPathBeginAngle
                    ? ReadFiniteFloat(pathOptions["start_angle"], "path.start_angle")
                    : 0f;
                bool hasPathEndAngle = pathOptions?["end_angle"] != null;
                float pathEndAngleDegrees = hasPathEndAngle
                    ? ReadFiniteFloat(pathOptions["end_angle"], "path.end_angle")
                    : 0f;
                bool overridePathAngle = hasPathBeginAngle || hasPathEndAngle;
                bool overridePathDistance = pathOptions?["distance"] != null;
                float pathDistance = overridePathDistance
                    ? ReadFiniteFloat(pathOptions["distance"], "path.distance")
                    : 0f;
                if (overridePathDistance && pathDistance < 0f)
                {
                    throw new InvalidOperationException("path.distance must be non-negative.");
                }
                if (overridePathDistance && !overridePathAngle)
                {
                    throw new InvalidOperationException("path.distance requires a path angle override.");
                }
                bool overrideHeading = pathOptions?["heading"] != null;
                float headingDegrees = overrideHeading
                    ? ReadFiniteFloat(pathOptions["heading"], "path.heading")
                    : 0f;
                float duration = (float)(durationFrames / SessionFrameRate);
                string analysisOptionsJson = ParseAnalysisOptionsJson(arguments);
                int frameCount = Math.Max(1, KimodoFrameTimeUtility.SecondsToFrameCount(duration, frameRate));
                int seed = generationOptions?.Value<int?>("seed") ?? (Guid.NewGuid().GetHashCode() & int.MaxValue);
                int steps = generationOptions?.Value<int?>("diffusion_steps") ?? ResolveDiffusionSteps(arguments, modelName, modelConfiguration);
                string outputFolder = KimodoEditorOutputPathUtility.NormalizeOutputFolder(outputOptions?.Value<string>("folder"));
                string requestedAnimationName = string.IsNullOrWhiteSpace(outputOptions?.Value<string>("name"))
                    ? prompt
                    : outputOptions.Value<string>("name").Trim();
                Avatar originAvatar = KimodoTimelineGenerationOutputPlanner.ResolveOriginRetargetAvatar(modelName);
                if (!KimodoRetargetCoreUtility.IsValidHumanoid(originAvatar))
                {
                    throw new InvalidOperationException($"Model '{modelName}' does not provide a valid humanoid origin Avatar.");
                }
                List<KimodoMarkerSampleResult> poseConstraints = BuildPoseConstraints(
                    arguments,
                    modelName,
                    originAvatar,
                    frameCount,
                    frameRate,
                    durationFrames,
                    character.Root,
                    ResolveSessionCharacterByReference(session, character.Name, addIfMissing: false),
                    out List<string> poseWarnings);

                if (outputMode != "model_bone" && !KimodoRetargetCoreUtility.IsValidHumanoid(character.Avatar))
                {
                    throw new InvalidOperationException($"Character '{character.Name}' does not provide a valid target humanoid Avatar for output_mode '{outputMode}'.");
                }

                JObject generationCheck = BuildGenerationCheck(
                    arguments, character, durationFrames, (float)SessionFrameRate, modelName, poseConstraints);
                TimelineGenerationTrace trace = PrepareGenerationTrace(arguments, character, duration);
                KimodoExternalConstraintRequest inOutConstraints = BuildCommandInOutConstraints(
                    arguments, trace, modelName, frameCount, frameRate, loopRequested, poseConstraints);
                KimodoPlayableClip playableClip = CreateGenerationPlayableClip(trace, requestedAnimationName, inOutConstraints != null);
                playableClip.bridgeModelName = modelName;
                playableClip.textEncoderMode = textEncoderMode;
                playableClip.motionPrompt = prompt;
                playableClip.generationFrames = frameCount;
                playableClip.diffusionSteps = steps;
                playableClip.randomSeed = false;
                playableClip.seed = seed;
                playableClip.generateLoop = loopRequested;
                playableClip.loopLockPositionX = loopLockPositionX;
                playableClip.loopLockPositionY = loopLockPositionY;
                playableClip.loopLockPositionZ = loopLockPositionZ;
                playableClip.loopLockRotationX = loopLockRotationX;
                playableClip.loopLockRotationY = loopLockRotationY;
                playableClip.loopLockRotationZ = loopLockRotationZ;
                playableClip.overridePathAngle = overridePathAngle;
                playableClip.pathBeginAngleDegrees = pathBeginAngleDegrees;
                playableClip.pathEndAngleDegrees = pathEndAngleDegrees;
                playableClip.overridePathDistance = overridePathDistance;
                playableClip.pathDistance = pathDistance;
                playableClip.overrideHeading = overrideHeading;
                playableClip.headingDegrees = headingDegrees;
                playableClip.loop = loopRequested
                    ? UnityEngine.Timeline.AnimationPlayableAsset.LoopMode.On
                    : UnityEngine.Timeline.AnimationPlayableAsset.LoopMode.Off;
                playableClip.analysisOptionsJson = analysisOptionsJson;
                playableClip.generatedAssetName = trace.Animation.Name;
                playableClip.generatedOutputFolder = outputFolder;
                playableClip.generationOutputMode = ParseGenerationOutputMode(outputMode);
                WriteGenerationConstraintMarkers(trace, poseConstraints, (float)SessionFrameRate);
                ReserveGenerationTimelineRange(trace);
                SaveTimelineSession(session);

                bool started = KimodoEditorGenerationJobService.Start(
                    character.Target,
                    async (generationSession, token) =>
                    {
                        string previousTaskId = KimodoBridgeService.GenerationTaskIdContext.Value;
                        Action<string> previousRequestPayload = KimodoBridgeService.GenerationRequestPayloadContext.Value;
                        KimodoBridgeService.GenerationTaskIdContext.Value = generationSession.RequestId.ToString("N");
                        KimodoBridgeService.GenerationRequestPayloadContext.Value = payload =>
                            generationSession.BackendRequestJson = payload;
                        try
                        {
                            return await ExecutePlayableClipGenerationAsync(
                                playableClip,
                                trace,
                                inOutConstraints,
                                character.Target,
                                generationSession,
                                token);
                        }
                        finally
                        {
                            KimodoBridgeService.GenerationTaskIdContext.Value = previousTaskId;
                            KimodoBridgeService.GenerationRequestPayloadContext.Value = previousRequestPayload;
                            // Temporary sampling context is owned by the command runtime.
                        }
                    },
                    PersistGenerationJobStatus,
                    out KimodoEditorGenerationJobSession generation,
                    out string error);
                if (!started)
                {
                    throw new InvalidOperationException(error);
                }

                Remember(character.Target, generation, trace);
                PersistGenerationJobStatus(generation);
                var accepted = new JObject { ["check"] = generationCheck };
                if (poseWarnings != null && poseWarnings.Count > 0)
                    accepted["warnings"] = new JArray(poseWarnings);
                return Started(generation, accepted);
            });
        }

        private static async Task<KimodoEditorGenerationResult> ExecutePlayableClipGenerationAsync(
            KimodoPlayableClip playableClip,
            TimelineGenerationTrace trace,
            KimodoExternalConstraintRequest inOutConstraints,
            UnityEngine.Object target,
            KimodoEditorGenerationJobSession session,
            CancellationToken token)
        {
            KimodoEditorGenerationResult result = await KimodoPlayableClipGenerationExecutionService.GenerateAndFinalizeAsync(
                playableClip,
                externalConstraint: inOutConstraints,
                (stage, message) => KimodoEditorGenerationJobService.UpdateProgress(target, session.RequestId, stage, message),
                token,
                trace.TimelineClip);
            FinalizePlayableClipTrace(trace, result);
            return result;
        }

        private static KimodoGenerationOutputMode ParseGenerationOutputMode(string outputMode)
        {
            switch (outputMode)
            {
                case "character_bone":
                    return KimodoGenerationOutputMode.CharacterBone;
                case "model_bone":
                    return KimodoGenerationOutputMode.ModelBone;
                default:
                    return KimodoGenerationOutputMode.HumanoidMuscle;
            }
        }

        internal static string ParseOutputMode(string value)
        {
            string mode = string.IsNullOrWhiteSpace(value) ? "humanoid_muscle" : value.Trim().ToLowerInvariant();
            if (mode != "humanoid_muscle" && mode != "character_bone" && mode != "model_bone")
            {
                throw new InvalidOperationException("output_mode must be humanoid_muscle, character_bone, or model_bone.");
            }

            return mode;
        }

    }
}
