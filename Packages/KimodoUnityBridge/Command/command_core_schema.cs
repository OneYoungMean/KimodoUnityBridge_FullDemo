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
        private static CommandCatalog BuildCommandCatalog()
        {
            JObject document = JObject.Parse(GetCommandDefinitionsJson());
            var handlers = new Dictionary<string, Func<string, string>>(StringComparer.Ordinal)
            {
                [HelpCommand] = GetCommandHelp,
                [SceneContextCommand] = SceneContextGet,
                [InstallServerCommand] = InstallServer,
                [MocapVideoAnalysisCommand] = AnalyzeMocapVideo,
                [MocapPictureAnalysisCommand] = AnalyzeMocapPicture,
                [AnimationAnalyzeCommand] = AnimationAnalyze,
                [TransformCaptureCommand] = TransformCapture,
                [RetargetAnimationCommand] = RetargetAnimation,
                [GenerateAnimationCommand] = GenerateAnimationAsset,
                [PoseGetCommand] = PoseGet,
                [PoseTestCommand] = PoseTest,
                [PoseSetCommand] = PoseSet,
                [PoseSetRootTransformCommand] = PoseSetRootTransform,
                [PoseSetMuscleCommand] = PoseSetMuscle,
                [GetGenerationCommand] = GetGeneration,
                [CancelGenerationCommand] = CancelGeneration,
                [SessionClearCommand] = ClearSession,
            };

            var registrations = new List<CommandRegistration>();
            foreach (JObject definition in document["tools"]?.Values<JObject>() ?? Enumerable.Empty<JObject>())
            {
                string name = definition.Value<string>("name");
                if (string.IsNullOrWhiteSpace(name) || !handlers.TryGetValue(name, out Func<string, string> handler))
                {
                    throw new InvalidOperationException($"Command definition '{name ?? string.Empty}' has no handler.");
                }
                registrations.Add(new CommandRegistration(definition, handler));
            }

            if (registrations.Count != handlers.Count)
            {
                throw new InvalidOperationException("Command definitions and handlers are out of sync.");
            }
            return new CommandCatalog(registrations);
        }

        [MenuItem("Kimodo/Command/Export Help JSON")]
        public static void ExportCommandDefinitionsJson()
        {
            UnityEditor.PackageManager.PackageInfo package =
                UnityEditor.PackageManager.PackageInfo.FindForAssetPath(HelpAssetPath);
            if (package == null || string.IsNullOrWhiteSpace(package.resolvedPath))
            {
                throw new InvalidOperationException($"Kimodo package was not found for '{HelpAssetPath}'.");
            }

            File.WriteAllText(
                Path.Combine(package.resolvedPath, "Command", "help.json"),
                JObject.Parse(BuildCommandDefinitionsJson()).ToString(Formatting.Indented));
            AssetDatabase.Refresh();
        }

        private static JObject CommandDefinition(string name, string description, JObject inputSchema)
        {
            return new JObject
            {
                ["name"] = name,
                ["description"] = description,
                ["documentation"] = BuildCommandDocumentation(name),
                ["inputSchema"] = inputSchema,
                ["outputSchema"] = BuildCommandOutputSchema(name),
                ["examples"] = BuildCommandExamples(name)
            };
        }

        private static JObject BuildCommandDocumentation(string command)
        {
            return command switch
            {
                HelpCommand => CommandDocumentation(
                    "Discover the public command contract, inspect one command, list viable models, or review constraint rules.",
                    new[] { "Pass command to return its complete definition; otherwise section selects the command overview, model list, or constraint reference." },
                    new[] { "The command-specific form returns the full input and output schemas with usage examples." },
                    new[] { "Read-only, except section=models queries the configured local generation service." },
                    new[] { "The models section reports current availability and may change with runtime configuration." },
                    SceneContextCommand, GenerateAnimationCommand),
                SceneContextCommand => CommandDocumentation(
                    "Read the active Unity scene, its Kimodo Timeline, character handles, and current Session identity.",
                    new[] { "Use returned scene object references for later commands instead of guessing from names or asset paths.", "Set include_history only when the Session-wide animation, generation, and analysis index is needed." },
                    new[] { "Returns current scene and director details, character/Clip references, frame-rate metadata, and Session identifiers.", "When requested, history includes compact animation, generation, and analysis records." },
                    new[] { "Read-only; does not create, modify, or clear scene objects or animation assets." },
                    new[] { "Context reflects the active loaded scene and current Session; history is omitted by default.", "A Clip asset path is asset metadata and is not itself a scene Clip handle." },
                    GenerateAnimationCommand, AnimationAnalyzeCommand, PoseGetCommand),
                InstallServerCommand => CommandDocumentation(
                    "Install or refresh the project-local QuickServer runtime before generation when it is unavailable or needs updating.",
                    new[] { "Starts an asynchronous install task; save the returned request_id and poll it with kimodo_get_generation." },
                    new[] { "The accepted response contains request_id and status; terminal progress and failure details come from polling." },
                    new[] { "Changes project-local runtime files; existing model files and the Python environment are preserved." },
                    new[] { "Installation requests cannot be canceled with kimodo_cancel_generation." },
                    GetGenerationCommand),
                AnimationAnalyzeCommand => CommandDocumentation(
                    "Analyze one loaded animation Clip and produce structured motion data with renderable image evidence.",
                    new[] { "Use the scene character and a loaded Clip name or an Assets/Packages asset path.", "Open returned pictures before making visual claims; use structured fields for measurements and temporal positions." },
                    new[] { "Returns one clip analysis and one pictures object; analysis frame positions use Timeline-global 60 FPS coordinates." },
                    new[] { "An external Clip asset is appended to the character's Kimodo Timeline for analysis; the source asset is not modified." },
                    new[] { "The clips array must contain exactly one Clip.", "Static pictures do not establish playback continuity, velocity smoothness, foot sliding, or other time-continuous properties." },
                    SceneContextCommand, PoseGetCommand, TransformCaptureCommand),
                TransformCaptureCommand => CommandDocumentation(
                    "Render a four-view image and world-space joint measurements for a scene character or a sampled External Pose, optionally framed with scene objects.",
                    new[] { "Use character_path for the current scene character or a Pose reference returned by pose_get.", "Use @pose or @pose/<relative bone path> to include the evaluated Pose in the capture." },
                    new[] { "Returns image_path, view/tile metadata, target references, and evaluated joint positions in world space." },
                    new[] { "Creates image evidence but does not change scene poses." },
                    new[] { "To capture a Clip frame, sample it first with pose_get; transforms must resolve in the current scene or Pose." },
                    PoseGetCommand, SceneContextCommand),
                RetargetAnimationCommand => CommandDocumentation(
                    "Retarget one loaded source Clip to another character in the current scene context.",
                    new[] { "Resolve both characters and the source Clip from current scene context before submission." },
                    new[] { "Returns the source character, target character, and the newly retargeted animation reference." },
                    new[] { "Appends a derived animation to the target character; the source Clip remains available." },
                    new[] { "Source and target must be supported scene characters; Session preview objects are rejected." },
                    SceneContextCommand, AnimationAnalyzeCommand),
                GenerateAnimationCommand => CommandDocumentation(
                    "Submit one animation generation request for a character in the current scene context.",
                    new[] { "Describe one simple semantic action in prompt; aim for 25 words or fewer.", "Represent exact body pose, root position, effector targets, duration, path, loop, and Clip windows in their explicit fields when required.", "Save request_id and use kimodo_get_generation to poll; do not resubmit a running request." },
                    new[] { "The accepted response contains request_id, status, non-blocking check results, and warnings. Pose/effect safety preflight runs only when the same point constraint explicitly contains both pose and effect; effect-only constraints skip it.", "On completion, kimodo_get_generation returns the generated Clip metadata, asset path, animation_id, and available generation diagnostics." },
                    new[] { "A completed result is appended as a Timeline Clip; existing completed Clips are retained.", "The first scene pose is used as the automatic starting anchor unless explicit first-frame or In/Out samples take precedence." },
                    new[] { "Trajectory overrides require an explicit requirement or confirmed scene/analysis evidence; prompt words do not select path options.", "analysis_option is lightweight generation-time analysis, not the full animation_analyze command.", "Command checks validate supported request boundaries; they do not judge semantic or artistic quality." },
                    SceneContextCommand, PoseGetCommand, GetGenerationCommand, CancelGenerationCommand, AnimationAnalyzeCommand),
                PoseGetCommand => CommandDocumentation(
                    "Materialize an External Pose from a Timeline frame, the current scene Animator pose, or the Avatar skeleton rest pose.",
                    new[] { "For an animation frame, pass its absolute Timeline-global frame at 60 FPS.", "Inspect the returned Pose and confirm its role before using its {track,index} reference as a generation constraint." },
                    new[] { "Returns an External Pose reference, source details, character_root world Transform, and sampled Pose data.", "character_root is the bound scene GameObject root; data.root is the sampled Pose root channel." },
                    new[] { "Every call creates an External Pose slot in the current scene context." },
                    new[] { "A Timeline frame and scene_pose/skeleton_rest_pose mode are alternative source forms.", "Use full_data only when all muscle and TQ channels are needed." },
                    SceneContextCommand, AnimationAnalyzeCommand, TransformCaptureCommand, PoseSetCommand),
                PoseTestCommand => CommandDocumentation(
                    "Measure the actual world-space positions of Humanoid joints in an External Pose, optionally checking them against expected contact targets.",
                    new[] { "Call pose_get for the desired scene or Timeline frame first, then pass its returned pose reference.", "Use targets when a contact or reach criterion needs a numerical pass/fail check; target positions and tolerances are in meters in world space." },
                    new[] { "Returns measured joint world positions, optional target distances and tolerance results, and an overall status.", "This samples the evaluated Pose rig; it does not judge whether the pose looks natural or whether a generated Clip plays smoothly." },
                    new[] { "Read-only with respect to the scene, Timeline, Pose slot, and animation assets; evaluation uses a transient preview rig." },
                    new[] { "pose_test explicitly measures the actual joints in any sampled Pose, including after effect-only generation. Generation-time pose/effect safety preflight is separate and runs only when the same constraint explicitly includes both pose and effect.", "Requires a valid Humanoid character and a Pose reference from the current context." },
                    PoseGetCommand, TransformCaptureCommand, GenerateAnimationCommand),
                PoseSetCommand => CommandDocumentation(
                    "Update the root transform, muscle channels, or effector targets of an existing External Pose slot.",
                    new[] { "Use only a {track,index} reference returned by pose_get or another Pose editing command.", "Supply at least one of root, muscles, or effector; multiple supplied channels are updated together." },
                    new[] { "Returns the Pose reference and the updated Pose data." },
                    new[] { "Mutates the referenced External Pose slot in the current scene context." },
                    new[] { "This edits Pose data; it does not generate an animation Clip." },
                    PoseGetCommand, PoseSetRootTransformCommand, PoseSetMuscleCommand),
                PoseSetRootTransformCommand => CommandDocumentation(
                    "Compatibility alias for a root-only pose_set update.",
                    new[] { "Use pose_set for new calls; this command accepts one Pose reference and root transform." },
                    new[] { "Returns the updated Pose reference and data." },
                    new[] { "Mutates the referenced External Pose slot." },
                    new[] { "Only the root transform is accepted by this alias." },
                    PoseSetCommand, PoseGetCommand),
                PoseSetMuscleCommand => CommandDocumentation(
                    "Compatibility alias for a muscles-only pose_set update.",
                    new[] { "Use pose_set for new calls; this command accepts one Pose reference and a muscle-value map." },
                    new[] { "Returns the updated Pose reference and data." },
                    new[] { "Mutates the referenced External Pose slot." },
                    new[] { "Only muscle channels are accepted by this alias." },
                    PoseSetCommand, PoseGetCommand),
                GetGenerationCommand => CommandDocumentation(
                    "Read progress, terminal status, and results for an installation or generation request.",
                    new[] { "Pass the exact request_id returned by kimodo_install_server or kimodo_generate_animation.", "Poll until a terminal status; a successful command response can still report task status failed or canceled." },
                    new[] { "Returns lifecycle status, stage, progress, and error details; completed generation includes Clip metadata and available diagnostics.", "Set include_request_json=true only when the exact backend payload is needed." },
                    new[] { "Read-only; does not resubmit or alter the task." },
                    new[] { "Request records are retained for a bounded number of recent jobs; an unknown or expired id returns an error." },
                    InstallServerCommand, GenerateAnimationCommand, CancelGenerationCommand),
                MocapVideoAnalysisCommand => CommandDocumentation(
                    "Use when a video file should be converted into a time-ordered SMPL-X pose array for downstream retargeting or analysis.",
                    new[] { "Pass an absolute video_path that the QuickServer machine can read. Supported formats are MP4, MOV, M4V, AVI, and MKV; the file must not exceed 2 GiB.", "The command returns immediately with status=accepted and a request_id. Poll kimodo_get_generation using that exact id until status is completed, failed, or canceled; do not submit the video again just to check progress.", "For a two-person video, optionally set person to left or right to select the performer by screen position. Omit person for the default selection." },
                    new[] { "A completed request returns poses in source-video order plus one top-level constraints array, fps, frame_count, source_frame_count, joint_names, betas, and rest_joints_m.", "Each pose contains raw global_orient_axis_angle, body_pose_axis_angle, and translation_m values. The top-level constraints array uses the same merged raw FullBody constraint format accepted by animation generation.", "Rotations are axis-angle radians; translation and rest joints are in meters. The global coordinate space is gravity-aligned Y-up. Optional incam_* fields use OpenCV camera coordinates: X-right, Y-down, Z-forward.", "The result does not create a clip, Playable track, character binding, or timeline edit." },
                    new[] { "Read-only; does not create, modify, or clear Unity scene objects or animation assets." },
                    new[] { "The top-level constraints array groups the whole sequence into the existing raw FullBody payload; the command still does not return a rendered animation or create a Unity Humanoid muscle track.", "The request can fail if QuickServer's GVHMR runtime, checkpoints, or the SMPL-X Avatar asset are unavailable." },
                    MocapPictureAnalysisCommand, GetGenerationCommand, RetargetAnimationCommand),
                MocapPictureAnalysisCommand => CommandDocumentation(
                    "Use when a still image should be converted into a single SMPL-X pose sample for downstream retargeting or analysis.",
                    new[] { "Pass an absolute image_path for a PNG, JPG, or JPEG file that the QuickServer machine can read. The file must not exceed 2 GiB.", "The command returns immediately with status=accepted and a request_id. Poll kimodo_get_generation using that exact id until status is completed, failed, or canceled; do not submit the image again just to check progress.", "The estimator temporarily treats the still image as a one-second clip and returns one pose sample from it." },
                    new[] { "A completed request returns one raw SMPL-X pose plus a top-level constraints array containing the matching raw FullBody constraint, along with joint_names, betas, and rest_joints_m.", "The pose contains raw global_orient_axis_angle, body_pose_axis_angle, and translation_m values. The constraints array uses the same format accepted by animation generation.", "Rotations are axis-angle radians; translation and rest joints are in meters. The global coordinate space is gravity-aligned Y-up. Optional incam_* fields use OpenCV camera coordinates: X-right, Y-down, Z-forward.", "The result does not create a clip, Playable track, character binding, or timeline edit." },
                    new[] { "Read-only; does not create, modify, or clear Unity scene objects or animation assets." },
                    new[] { "The constraints array is a raw FullBody payload with one frame; the command still does not return a rendered animation or create a Unity Humanoid muscle track.", "The request can fail if QuickServer's GVHMR runtime, checkpoints, or the SMPL-X Avatar asset are unavailable." },
                    MocapVideoAnalysisCommand, GetGenerationCommand, RetargetAnimationCommand),
                CancelGenerationCommand => CommandDocumentation(
                    "Request cancellation of an active animation generation.",
                    new[] { "Pass the exact generation request_id, then poll with kimodo_get_generation to confirm its terminal state." },
                    new[] { "Returns the compact cancellation/lifecycle result and any completed Clip metadata." },
                    new[] { "May stop generation work that has not already completed; it does not undo a completed Clip." },
                    new[] { "Installation tasks cannot be canceled with this command.", "A cancellation request is not proof of a canceled terminal state; check the returned status." },
                    GetGenerationCommand, GenerateAnimationCommand),
                SessionClearCommand => CommandDocumentation(
                    "Clear transient Kimodo context objects from the active scene Session.",
                    new[] { "Use when the current transient Session context must be reset." },
                    new[] { "Returns whether the context was cleared, how many context objects were destroyed, and lazy recreation information." },
                    new[] { "Destroys transient context GameObjects; persisted Timeline and animation assets are retained." },
                    new[] { "References to transient Session objects may no longer resolve after clearing; the next command recreates context lazily." },
                    SceneContextCommand)
            };
        }

        private static JObject CommandDocumentation(
            string whenToUse,
            string[] behavior,
            string[] resultNotes,
            string[] sceneEffects,
            string[] limits,
            params string[] relatedCommands)
        {
            return new JObject
            {
                ["when_to_use"] = whenToUse,
                ["behavior"] = new JArray(behavior),
                ["result_notes"] = new JArray(resultNotes),
                ["scene_effects"] = new JArray(sceneEffects),
                ["limits"] = new JArray(limits),
                ["related_commands"] = new JArray(relatedCommands)
            };
        }

        private static JObject BuildCommandOutputSchema(string command)
        {
            var properties = new JObject
            {
                ["ok"] = new JObject { ["type"] = "boolean" },
                ["error"] = new JObject
                {
                    ["description"] = "Optional command error. Command errors use {code, message, details?}; asynchronous job failures include this object while the poll command itself succeeds.",
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["code"] = new JObject { ["type"] = "string" },
                        ["message"] = new JObject { ["type"] = "string" },
                        ["details"] = new JObject { ["type"] = "object" }
                    },
                    ["required"] = new JArray("code", "message"),
                    ["additionalProperties"] = true
                }
            };

            string[] fields = command switch
            {
                HelpCommand => new[] { "manual", "usage", "execution_model", "routing", "handles", "workflow", "commands", "constraints", "constraint_rules", "configs", "count" },
                SceneContextCommand => new[] { "session", "history", "scene", "scene_name", "director", "directors", "command_context_director", "playable_asset", "playable_asset_path", "duration_frames_60", "frame_rate", "current_frame_60", "characters" },
                InstallServerCommand => new[] { "request_id", "status" },
                MocapVideoAnalysisCommand or MocapPictureAnalysisCommand => new[] { "request_id", "status" },
                AnimationAnalyzeCommand => new[] { "analysis_schema_version", "clip", "pictures" },
                TransformCaptureCommand => new[] { "image_path", "width", "height", "tile_size", "tiles", "source", "targets", "evaluated_joints_world" },
                RetargetAnimationCommand => new[] { "retargeted", "source_character", "character", "animation" },
                GenerateAnimationCommand => new[] { "request_id", "status", "check", "warnings" },
                PoseGetCommand => new[] { "pose", "source", "character_root", "data" },
                PoseTestCommand => new[] { "pose", "source", "overall", "joints" },
                PoseSetCommand or PoseSetRootTransformCommand or PoseSetMuscleCommand => new[] { "pose", "data" },
                GetGenerationCommand => new[] { "request_id", "status", "stage", "message", "started_at_utc", "estimated_completion_utc", "eta_seconds", "progress_current", "progress_total", "progress_rate", "backend_progress", "runtime_root", "runtime_version", "install_mode", "server_connected", "clip", "animation_id", "seed", "analysis", "analysis_schema_version", "source_type", "skeleton", "person", "fps", "source_frame_count", "frame_count", "joint_names", "pose_parameterization", "coordinate_spaces", "betas", "rest_joints_m", "poses", "constraints", "constraint_loss", "inout_sampling", "target_alive", "backend_request_json" },
                CancelGenerationCommand => new[] { "request_id", "status", "stage", "message", "canceled", "clip" },
                SessionClearCommand => new[] { "cleared", "name", "destroyed_context_count", "recreated_on_next_command" },
                _ => Array.Empty<string>()
            };

            foreach (string field in fields)
            {
                if (properties.Property(field) != null) continue;
                properties[field] = BuildCommandOutputFieldSchema(command, field);
            }

            return new JObject
            {
                ["type"] = "object",
                ["description"] = "Command responses keep domain fields at the top level. Success uses ok=true; command errors use ok=false and an error object. Async job failures are reported by status with an error object.",
                ["properties"] = properties,
                ["required"] = new JArray("ok"),
                ["additionalProperties"] = true
            };
        }

        private static JObject BuildCommandOutputFieldSchema(string command, string field)
        {
            switch (field)
            {
                case "status":
                    string[] statuses = command == InstallServerCommand || command == GenerateAnimationCommand
                        ? new[] { "accepted" }
                        : command == CancelGenerationCommand
                            ? new[] { "running", "completed", "failed", "canceled" }
                            : new[] { "accepted", "running", "completed", "failed", "canceled" };
                    return new JObject { ["type"] = "string", ["enum"] = new JArray(statuses) };
                case "request_id": case "message": case "usage": case "scene": case "scene_name": case "director": case "animation_id":
                case "command_context_director": case "playable_asset": case "playable_asset_path": case "runtime_root":
                case "runtime_version": case "install_mode": case "image_path": case "source_character": case "character":
                case "name": case "asset_path": case "stage": case "started_at_utc": case "estimated_completion_utc":
                case "backend_progress": case "analysis_schema_version":
                case "source_type": case "skeleton": case "person":
                    return new JObject { ["type"] = "string" };
                case "width": case "height": case "tile_size": case "duration_frames_60": case "current_frame_60":
                case "destroyed_context_count": case "duration_frames": case "progress_current": case "progress_total": case "count": case "seed":
                case "source_frame_count": case "frame_count": case "source_frame":
                    return new JObject { ["type"] = "integer" };
                case "frame_rate": case "fps":
                    return new JObject { ["type"] = "number" };
                case "eta_seconds": case "progress_rate":
                    return NullableSchema(new JObject { ["type"] = "number" });
                case "ok": case "retargeted": case "cleared": case "server_connected": case "canceled": case "target_alive":
                    return new JObject { ["type"] = "boolean" };
                case "manual":
                    return new JObject { ["oneOf"] = new JArray(new JObject { ["type"] = "string" }, new JObject { ["type"] = "object" }) };
                case "session": return ObjectSchema("session_id", "session_revision", "schema_version");
                case "history":
                {
                    JObject schema = ObjectSchema("animations", "generations", "analyses");
                    schema["properties"]["animations"] = ArraySchema("object", "animation_id", "name", "character", "source", "kind", "start_timeline_frame_60", "duration_frames", "segments", "transition");
                    schema["properties"]["generations"] = ArraySchema("object", "request_id", "animation_id", "character", "animation", "status", "stage", "message", "error", "started_at_utc", "updated_at_utc");
                    schema["properties"]["analyses"] = ArraySchema("object", "analysis_id", "session_id", "character", "animation_id", "animation_name", "input_signature", "created_at_utc", "analysis_path", "picture_paths");
                    schema["properties"]["analyses"]["items"]["properties"]["picture_paths"] = ArraySchema("string");
                    return schema;
                }
                case "animation":
                    return ObjectSchema("animation_id", "name", "character", "source", "kind", "start_timeline_frame_60", "duration_frames", "segments", "transition");
                case "clip":
                    return command == AnimationAnalyzeCommand
                        ? ObjectSchema("analysis_id", "session_id", "character", "name", "start_timeline_frame_60", "end_timeline_frame_60_exclusive", "analysis_mode", "phase_track_version", "phase_track", "keyframes", "foot_contacts", "root_trajectory", "endpoint_pose_comparison", "motion_profile")
                        : ObjectSchema("name", "character", "asset_path", "start_timeline_frame_60", "end_timeline_frame_60_exclusive");
                case "picture": return ObjectSchema("tiles", "output", "environment");
                case "pictures":
                {
                    JObject schema = ObjectSchema("picture", "level", "aspect", "tile_count", "render_version", "image_path", "width", "height", "resolution", "supersample", "images", "tile_paths", "cached", "header_height", "screenshot_generated_at");
                    schema["properties"]["picture"] = ObjectSchema("tiles", "output", "environment");
                    return schema;
                }
                case "animations": return ArraySchema("object", "animation_id", "name", "character", "source", "kind", "start_timeline_frame_60", "duration_frames", "segments", "transition");
                case "generations": return ArraySchema("object", "request_id", "animation_id", "character", "animation", "status", "stage", "message", "error", "started_at_utc", "updated_at_utc");
                case "analyses": return ArraySchema("object", "analysis_id", "session_id", "character", "animation_id", "animation_name", "input_signature", "created_at_utc", "analysis_path", "picture_paths");
                case "picture_paths": return ArraySchema("string");
                case "directors": return ArraySchema("object", "object_ref", "hierarchy_path", "name", "enabled", "playable_asset", "timeline_frame_60", "duration_frames_60", "frame_rate");
                case "characters":
                {
                    JObject schema = ArraySchema("object", "name", "role", "scene_object", "object_ref", "animator", "timeline_preview", "avatar", "avatar_valid_humanoid", "animations");
                    schema["items"]["properties"]["animations"] = ArraySchema("object", "animation_id", "name", "source", "kind", "start_timeline_frame_60", "duration_frames", "segments", "transition");
                    return schema;
                }
                case "tiles": return ArraySchema("object", "tile", "view", "projection", "transforms", "bounds");
                case "targets": return ArraySchema("object", "key", "world_position");
                case "joints":
                {
                    JObject schema = ArraySchema("object", "joint", "world_position", "target_position", "distance_m", "tolerance_m", "status");
                    schema["items"]["properties"]["joint"] = new JObject { ["type"] = "string" };
                    schema["items"]["properties"]["world_position"] = NumberArraySchema();
                    schema["items"]["properties"]["target_position"] = NullableSchema(NumberArraySchema());
                    schema["items"]["properties"]["distance_m"] = NullableSchema(new JObject { ["type"] = "number" });
                    schema["items"]["properties"]["tolerance_m"] = NullableSchema(new JObject { ["type"] = "number" });
                    schema["items"]["properties"]["status"] = new JObject { ["type"] = "string", ["enum"] = new JArray("passed", "failed", "not_tested") };
                    return schema;
                }
                case "constraints" when command == GetGenerationCommand:
                    return new JObject { ["type"] = "array", ["items"] = RawConstraintSchema() };
                case "warnings": case "execution_model": case "routing": case "workflow": case "constraints": case "constraint_rules":
                    return new JObject { ["type"] = "array" };
                case "world_position": return NumberArraySchema();
                case "poses":
                {
                    JObject schema = ArraySchema("object", "frame", "source_frame", "timestamp_seconds", "global_orient_axis_angle", "body_pose_axis_angle", "translation_m", "incam_global_orient_axis_angle", "incam_body_pose_axis_angle", "incam_translation_m", "static_confidence");
                    return schema;
                }
                case "joint_names": return new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } };
                case "betas": case "rest_joints_m": return new JObject { ["type"] = "array" };
                case "configs": return ArraySchema("object");
                case "commands":
                {
                    JObject commands = ArraySchema("object", "name", "description", "documentation", "required", "outputSchema", "examples");
                    commands["items"]["properties"]["documentation"] = CommandDocumentationSchema();
                    return commands;
                }
                case "backend_request_json":
                    return new JObject { ["oneOf"] = new JArray(ObjectSchema(), new JObject { ["type"] = "string" }) };
                case "pose_parameterization": case "coordinate_spaces":
                    return ObjectSchema();
                case "pose": return ObjectSchema("track", "index");
                case "character_root":
                    return new JObject
                    {
                        ["type"] = "object",
                        ["description"] = "The bound scene character GameObject root Transform in Unity world space. This is distinct from data.root, which is the sampled Pose root channel (Hips).",
                        ["properties"] = new JObject
                        {
                            ["position"] = NumberArraySchema(),
                            ["rotation"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "number" }, ["minItems"] = 4, ["maxItems"] = 4, ["description"] = "World-space quaternion [x,y,z,w]." },
                            ["space"] = new JObject { ["type"] = "string", ["enum"] = new JArray("world") }
                        },
                        ["required"] = new JArray("position", "rotation", "space"),
                        ["additionalProperties"] = false
                    };
                case "source":
                {
                    JObject source = ObjectSchema("type", "character", "character_path", "pose", "clip", "frame", "timeline_frame_60", "mode");
                    source["properties"]["clip"] = NullableSchema(new JObject { ["type"] = "string" });
                    source["properties"]["frame"] = NullableSchema(new JObject { ["type"] = "integer" });
                    return source;
                }
                case "overall": return new JObject { ["type"] = "string", ["enum"] = new JArray("passed", "failed", "not_tested") };
                case "data": case "check": case "analysis": case "constraint_loss": case "inout_sampling": case "evaluated_joints_world":
                    return ObjectSchema();
                default:
                    return new JObject { ["type"] = "object" };
            }
        }

        private static JObject ObjectSchema(params string[] propertyNames)
        {
            var properties = new JObject();
            foreach (string name in propertyNames)
            {
                properties[name] = name switch
                {
                    "name" or "character" or "source" or "kind" or "asset_path" or "analysis_schema_version" or "track" or
                    "analysis_mode" or "phase_track_version" or "tile" or "view" or "projection" or "key" or "role" or
                    "scene_object" or "object_ref" or "animator" or "avatar" or "hierarchy_path" or "playable_asset" or
                    "render_version" or "level" or "aspect" or "image_path" or "output" or "mode" or "path" or "type" or "clip" or "screenshot_generated_at" => new JObject { ["type"] = "string" },
                    "session_id" or "schema_version" or "animation_id" or "analysis_id" or "animation_name" or "input_signature" or "created_at_utc" or "analysis_path" or "request_id" or "status" or "stage" or "message" or "error" or "started_at_utc" or "updated_at_utc" or "source_type" or "skeleton" or "person" or "pose_parameterization" => new JObject { ["type"] = "string" },
                    "session_revision" or "start_timeline_frame_60" or "end_timeline_frame_60_exclusive" or "duration_frames" or "timeline_frame_60" or
                    "duration_frames_60" or "tile_count" or "width" or "height" or "resolution" or "supersample" or "header_height" or "index" or "frame" or "source_frame" or "source_frame_count" or "frame_count" => new JObject { ["type"] = "integer" },
                    "enabled" or "avatar_valid_humanoid" or "cached" => new JObject { ["type"] = "boolean" },
                    "frame_rate" or "fps" or "timestamp_seconds" => new JObject { ["type"] = "number" },
                    "segments" or "phase_track" or "keyframes" or "foot_contacts" or "images" or "tile_paths" or "picture_paths" or "transforms" or "animations" or "generations" or "analyses" or "tiles" or "headers" or "global_orient_axis_angle" or "body_pose_axis_angle" or "translation_m" or "incam_global_orient_axis_angle" or "incam_body_pose_axis_angle" or "incam_translation_m" or "static_confidence" or "joint_names" or "coordinate_spaces" or "betas" or "rest_joints_m" or "poses" => new JObject { ["type"] = "array" },
                    "session" or "history" or "root_trajectory" or "endpoint_pose_comparison" or "motion_profile" or "environment" or "bounds" or "timeline_preview" or "outputSchema" or "title" or "header" or "root" or "hands" or "feet" => new JObject { ["type"] = "object" },
                    "world_position" => NumberArraySchema(),
                    "required" or "examples" => new JObject { ["type"] = "array" },
                    "description" => new JObject { ["type"] = "string" },
                    _ => new JObject { ["type"] = "object" }
                };
            }
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["additionalProperties"] = true
            };
        }

        private static JObject CommandDocumentationSchema()
        {
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["when_to_use"] = new JObject { ["type"] = "string" },
                    ["behavior"] = ArraySchema("string"),
                    ["result_notes"] = ArraySchema("string"),
                    ["scene_effects"] = ArraySchema("string"),
                    ["limits"] = ArraySchema("string"),
                    ["related_commands"] = ArraySchema("string")
                },
                ["required"] = new JArray("when_to_use", "behavior", "result_notes", "scene_effects", "limits", "related_commands"),
                ["additionalProperties"] = false
            };
        }

        private static JObject ArraySchema(string itemType, params string[] itemProperties)
        {
            JObject items = itemType == "object" ? ObjectSchema(itemProperties) : new JObject { ["type"] = itemType };
            return new JObject { ["type"] = "array", ["items"] = items };
        }

        private static JObject NumberArraySchema()
        {
            return new JObject
            {
                ["type"] = "array",
                ["items"] = new JObject { ["type"] = "number" }
            };
        }

        private static JObject RawConstraintSchema()
        {
            JObject schema = ObjectSchema("type", "frame_indices", "smooth_root_2d", "global_root_heading", "local_joints_rot", "root_positions", "target_positions", "joint_names", "dense_path");
            schema["properties"]["frame_indices"] = new JObject { ["type"] = "array" };
            schema["properties"]["smooth_root_2d"] = new JObject { ["type"] = "array" };
            schema["properties"]["global_root_heading"] = new JObject { ["type"] = "array" };
            schema["properties"]["local_joints_rot"] = new JObject { ["type"] = "array" };
            schema["properties"]["root_positions"] = new JObject { ["type"] = "array" };
            schema["properties"]["target_positions"] = new JObject { ["type"] = "array" };
            schema["properties"]["joint_names"] = new JObject { ["type"] = "array" };
            schema["properties"]["dense_path"] = new JObject { ["type"] = "boolean" };
            return schema;
        }

        private static JObject NullableSchema(JObject schema)
        {
            return new JObject
            {
                ["oneOf"] = new JArray(schema, new JObject { ["type"] = "null" })
            };
        }

        private static JObject Properties(params PropertyDefinition[] definitions)
        {
            var properties = new JObject();
            var required = new JArray();
            foreach (PropertyDefinition definition in definitions)
            {
                properties[definition.Name] = definition.Schema;
                if (definition.IsRequired)
                {
                    required.Add(definition.Name);
                }
            }
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required,
                ["additionalProperties"] = false
            };
        }

        private static PropertyDefinition Required(string name, string type, string description)
        {
            return new PropertyDefinition(name, type, description, true);
        }

        private static PropertyDefinition Optional(string name, string type, string description)
        {
            return new PropertyDefinition(name, type, description, false);
        }

        private static PropertyDefinition OptionalLoop()
        {
            JObject axis = new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["x"] = new JObject { ["type"] = "boolean" },
                    ["y"] = new JObject { ["type"] = "boolean" },
                    ["z"] = new JObject { ["type"] = "boolean" }
                }
            };
            JObject structured = new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["enabled"] = new JObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "Enable bounded loop preprocessing; defaults to true when the loop object is supplied."
                    },
                    ["lock_pos"] = axis.DeepClone(),
                    ["lock_rot"] = axis.DeepClone()
                }
            };
            return new PropertyDefinition("loop", new JObject
            {
                ["description"] = "Enable loop preprocessing, or provide per-axis endpoint locks with {enabled, lock_pos:{x,y,z}, lock_rot:{x,y,z}}.",
                ["oneOf"] = new JArray(
                    new JObject { ["type"] = "boolean" },
                    structured)
            }, false);
        }

        private static PropertyDefinition OptionalObject(string name, JObject properties, string description)
        {
            return new PropertyDefinition(name, new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = properties,
                ["description"] = description
            }, false);
        }

        private static PropertyDefinition OptionalPath()
        {
            return OptionalObject("path", new JObject
            {
                ["start_angle"] = new JObject { ["type"] = "number" },
                ["end_angle"] = new JObject { ["type"] = "number" },
                ["distance"] = new JObject { ["type"] = "number", ["minimum"] = 0 },
                ["heading"] = new JObject { ["type"] = "number" }
            }, "Optional Root2D path override. Use only when the trajectory is known from an explicit requirement or confirmed scene/analysis evidence; never infer these fields from prompt keywords. Any angle enables path override; distance replaces the measured path length; heading is applied after path composition.");
        }

        private static PropertyDefinition OptionalGeneration()
        {
            return OptionalObject("generation", new JObject
            {
                ["model"] = new JObject { ["type"] = "string" },
                ["text_encoder"] = new JObject { ["type"] = "string", ["enum"] = new JArray("high_performance", "high_precision") },
                ["seed"] = new JObject { ["type"] = "integer" },
                ["diffusion_steps"] = new JObject { ["type"] = "integer" }
            }, "Optional model and deterministic generation settings.");
        }

        private static PropertyDefinition OptionalOutput()
        {
            return OptionalObject("output", new JObject
            {
                ["mode"] = new JObject { ["type"] = "string", ["enum"] = new JArray("humanoid_muscle", "character_bone", "model_bone") },
                ["folder"] = new JObject { ["type"] = "string" },
                ["name"] = new JObject { ["type"] = "string" }
            }, "Optional animation output settings.");
        }


        private static PropertyDefinition OptionalArray(string name, string itemType, string description)
        {
            return new PropertyDefinition(name, new JObject
            {
                ["type"] = "array",
                ["items"] = new JObject { ["type"] = itemType },
                ["description"] = description
            }, false);
        }

        private static PropertyDefinition RequiredArray(string name, string itemType, string description)
        {
            return new PropertyDefinition(name, new JObject
            {
                ["type"] = "array",
                ["items"] = new JObject { ["type"] = itemType },
                ["description"] = description
            }, true);
        }

        private static PropertyDefinition OptionalEnumArray(string name, string description, params string[] values)
        {
            return new PropertyDefinition(name, new JObject
            {
                ["type"] = "array",
                ["items"] = new JObject
                {
                    ["type"] = "string",
                    ["enum"] = new JArray(values)
                },
                ["description"] = description
            }, false);
        }

        private static PropertyDefinition RequiredEnumArray(string name, string description, params string[] values)
        {
            return new PropertyDefinition(name, new JObject
            {
                ["type"] = "array",
                ["items"] = new JObject
                {
                    ["type"] = "string",
                    ["enum"] = new JArray(values)
                },
                ["description"] = description
            }, true);
        }

        private static PropertyDefinition OptionalConstraints(string name, string description)
        {
            JObject poseReference = PoseReferenceSchema();
            JObject vector2 = new JObject
            {
                ["type"] = "array",
                ["items"] = new JObject { ["type"] = "number" },
                ["minItems"] = 2,
                ["maxItems"] = 2
            };
            JObject effectTarget = new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["position"] = new JObject
                    {
                        ["type"] = "array", ["minItems"] = 3, ["maxItems"] = 3,
                        ["items"] = new JObject { ["type"] = "number" },
                        ["description"] = "World-space target position [x,y,z]."
                    },
                    ["rotation"] = new JObject
                    {
                        ["type"] = "array", ["minItems"] = 4, ["maxItems"] = 4,
                        ["items"] = new JObject { ["type"] = "number" },
                        ["description"] = "Target rotation quaternion [x,y,z,w]."
                    }
                },
                ["anyOf"] = new JArray(
                    new JObject { ["required"] = new JArray("position") },
                    new JObject { ["required"] = new JArray("rotation") })
            };
            JObject effectors = new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["left_hand"] = effectTarget.DeepClone(),
                    ["right_hand"] = effectTarget.DeepClone(),
                    ["left_foot"] = effectTarget.DeepClone(),
                    ["right_foot"] = effectTarget.DeepClone()
                },
                ["anyOf"] = new JArray(
                    new JObject { ["required"] = new JArray("left_hand") },
                    new JObject { ["required"] = new JArray("right_hand") },
                    new JObject { ["required"] = new JArray("left_foot") },
                    new JObject { ["required"] = new JArray("right_foot") })
            };
            JObject root = new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["position"] = new JObject { ["type"] = "array", ["minItems"] = 3, ["maxItems"] = 3, ["items"] = new JObject { ["type"] = "number" } },
                    ["rotation"] = new JObject { ["type"] = "array", ["minItems"] = 4, ["maxItems"] = 4, ["items"] = new JObject { ["type"] = "number" } }
                },
                ["minProperties"] = 1
            };
            JObject muscles = new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = new JObject { ["type"] = "number" },
                ["minProperties"] = 1
            };
            JObject root2dPosition = (JObject)vector2.DeepClone();
            root2dPosition["description"] = "Planar root position [x,z].";
            JObject root2dHeading = (JObject)vector2.DeepClone();
            root2dHeading["description"] = "Planar heading direction [x,z].";
            var pointItem = new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["frame"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
                    ["pose"] = poseReference.DeepClone(),
                    ["root2d"] = new JObject
                    {
                        ["type"] = "object",
                        ["description"] = "Planar root position and heading constraint; supply both channels together.",
                        ["additionalProperties"] = false,
                        ["properties"] = new JObject
                        {
                            ["position"] = root2dPosition,
                            ["heading"] = root2dHeading
                        },
                        ["required"] = new JArray("position", "heading")
                    },
                    ["effect"] = effectors.DeepClone()
                },
                ["required"] = new JArray("frame"),
                ["anyOf"] = new JArray(
                    new JObject { ["required"] = new JArray("pose") },
                    new JObject { ["required"] = new JArray("root2d") },
                    new JObject { ["required"] = new JArray("effect") })
            };
            var rootPathItem = new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["frame"] = new JObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = 0,
                        ["default"] = 0,
                        ["description"] = "First path frame; defaults to the clip start. Use a confirmed path for the intended character and motion; root_path generates only start and end Root2D constraints, not intermediate route enforcement."
                    },
                    ["root_path"] = new JObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["properties"] = new JObject
                        {
                            ["path"] = poseReference.DeepClone()
                        },
                        ["required"] = new JArray("path")
                    }
                },
                ["required"] = new JArray("root_path")
            };
            ((JObject)rootPathItem["properties"]?["root_path"]?["properties"]?["path"])["description"] =
                "Confirmed analyzed Root Path slot for the intended character and motion, returned by animation_analyze at clip.root_trajectory.path.";
            return new PropertyDefinition(name, new JObject
            {
                ["type"] = "array",
                ["description"] = description,
                ["items"] = new JObject { ["oneOf"] = new JArray(pointItem, rootPathItem, InOutConstraintSchema()) }
            }, false);
        }

        private static JObject InOutConstraintSchema()
        {
            JObject source = Properties(
                Required("clip", "string", "Safe completed source Clip name in the current scene context."),
                Optional("character", "string", "Defaults to the generation character; cross-character sources are not supported."),
                new PropertyDefinition("frame", new JObject { ["type"] = "integer", ["minimum"] = 0,
                    ["description"] = "Optional boundary frame at 60 FPS, local to the Clip's played Timeline range. Defaults: outside In=last model frame / Out=first; inside In=first / Out=last." }, false));
            JObject side = Properties(
                new PropertyDefinition("source", source, true),
                new PropertyDefinition("window_frames", new JObject { ["type"] = "integer", ["minimum"] = 1, ["default"] = 1,
                    ["description"] = "Coverage at 60 FPS. Converted to model frames by ceil(window_frames * model_fps / 60), minimum 1; never silently shortened." }, false),
                new PropertyDefinition("sample_count", new JObject { ["type"] = "integer", ["minimum"] = 1, ["default"] = 1,
                    ["description"] = "Distinct samples within the converted window. One preserves the seam; multiple samples include both endpoints." }, false));
            JObject config = Properties(
                new PropertyDefinition("mode", new JObject { ["type"] = "string", ["enum"] = new JArray("outside", "inside"), ["default"] = "outside",
                    ["description"] = "Outside pads generation with source context then crops it off; inside constrains the returned clip's start/end windows. Output duration is preserved." }, false),
                new PropertyDefinition("in", (JObject)side.DeepClone(), false),
                new PropertyDefinition("out", (JObject)side.DeepClone(), false));
            config["anyOf"] = new JArray(new JObject { ["required"] = new JArray("in") }, new JObject { ["required"] = new JArray("out") });
            return Properties(new PropertyDefinition("inout", config, true));
        }

        private static PropertyDefinition RequiredPoseSource(string name)
        {
            JObject schema = new JObject
            {
                ["type"] = "object",
                ["description"] = "Select exactly one source form: a Timeline-global frame, the current scene Animator pose, or the Avatar skeleton rest pose.",
                ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["character"] = new JObject { ["type"] = "string", ["description"] = "Current scene character name or object_ref." },
                    ["mode"] = new JObject { ["type"] = "string", ["enum"] = new JArray("scene_pose", "skeleton_rest_pose"), ["description"] = "Sample the original scene Animator's currently evaluated pose, or the Avatar's stored skeleton rest pose." },
                    ["timeline_frame_60"] = new JObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = 0,
                        ["description"] = "Absolute Timeline-global frame at 60 FPS. Use animation_analyze to find the selected Clip's start frame, then add the clip-local frame offset."
                    }
                },
                ["required"] = new JArray("character"),
                ["oneOf"] = new JArray(
                    new JObject
                    {
                        ["required"] = new JArray("character", "timeline_frame_60"),
                        ["not"] = new JObject { ["required"] = new JArray("mode") }
                    },
                    new JObject
                    {
                        ["required"] = new JArray("character", "mode"),
                        ["properties"] = new JObject { ["mode"] = new JObject { ["enum"] = new JArray("scene_pose") } },
                        ["not"] = new JObject { ["required"] = new JArray("timeline_frame_60") }
                    },
                    new JObject
                    {
                        ["required"] = new JArray("character", "mode"),
                        ["properties"] = new JObject { ["mode"] = new JObject { ["enum"] = new JArray("skeleton_rest_pose") } },
                        ["not"] = new JObject { ["required"] = new JArray("timeline_frame_60") }
                    })
            };
            return new PropertyDefinition(name, schema, true);
        }


        private static PropertyDefinition RequiredPoseReference(string name)
        {
            return new PropertyDefinition(name, PoseReferenceSchema(), true);
        }

        private static PropertyDefinition OptionalPoseTestTargets()
        {
            JObject position = new JObject
            {
                ["type"] = "array", ["minItems"] = 3, ["maxItems"] = 3,
                ["items"] = new JObject { ["type"] = "number" }
            };
            JObject target = new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["joint"] = new JObject { ["type"] = "string", ["enum"] = new JArray("Hips", "Head", "LeftHand", "RightHand", "LeftFoot", "RightFoot", "LeftUpperArm", "RightUpperArm", "LeftLowerArm", "RightLowerArm", "LeftUpperLeg", "RightUpperLeg", "LeftLowerLeg", "RightLowerLeg") },
                    ["position"] = position,
                    ["tolerance_m"] = new JObject { ["type"] = "number", ["minimum"] = 0, ["default"] = 0.05 }
                },
                ["required"] = new JArray("joint", "position")
            };
            return new PropertyDefinition("targets", new JObject
            {
                ["type"] = "array", ["items"] = target,
                ["description"] = "Optional expected world-space joint positions and per-target tolerances in meters."
            }, false);
        }

        private static JObject PoseSetSchema()
        {
            JObject transform = new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["position"] = new JObject { ["type"] = "array", ["minItems"] = 3, ["maxItems"] = 3, ["items"] = new JObject { ["type"] = "number" } },
                    ["rotation"] = new JObject { ["type"] = "array", ["minItems"] = 4, ["maxItems"] = 4, ["items"] = new JObject { ["type"] = "number" } }
                },
                ["anyOf"] = new JArray(
                    new JObject { ["required"] = new JArray("position") },
                    new JObject { ["required"] = new JArray("rotation") })
            };
            JObject effectors = new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["left_hand"] = transform.DeepClone(), ["right_hand"] = transform.DeepClone(),
                    ["left_foot"] = transform.DeepClone(), ["right_foot"] = transform.DeepClone()
                },
                ["minProperties"] = 1
            };
            foreach (string key in new[] { "left_hand", "right_hand", "left_foot", "right_foot" })
            {
                JObject value = (JObject)effectors["properties"][key];
                value["description"] = "Effector target position and/or rotation.";
            }
            effectors["description"] = "World-space targets for one or more hands or feet. Supply position and/or rotation for each target.";
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["pose"] = PoseReferenceSchema(),
                    ["root"] = new JObject
                    {
                        ["type"] = "object",
                        ["description"] = "Root position [x,y,z] and/or rotation quaternion [x,y,z,w]. A rotation update requires an existing or supplied root position.",
                        ["properties"] = new JObject
                        {
                            ["position"] = new JObject { ["type"] = "array", ["minItems"] = 3, ["maxItems"] = 3, ["items"] = new JObject { ["type"] = "number" } },
                            ["rotation"] = new JObject { ["type"] = "array", ["minItems"] = 4, ["maxItems"] = 4, ["items"] = new JObject { ["type"] = "number" } }
                        },
                        ["additionalProperties"] = false,
                        ["minProperties"] = 1
                    },
                    ["muscles"] = new JObject
                    {
                        ["type"] = "object",
                        ["description"] = "Non-empty map of canonical Humanoid muscle channel names to finite numeric values.",
                        ["additionalProperties"] = new JObject { ["type"] = "number" },
                        ["minProperties"] = 1
                    },
                    ["effector"] = effectors
                },
                ["required"] = new JArray("pose"),
                ["anyOf"] = new JArray(
                    new JObject { ["required"] = new JArray("root") },
                    new JObject { ["required"] = new JArray("muscles") },
                    new JObject { ["required"] = new JArray("effector") }),
                ["additionalProperties"] = false
            };
        }

        private static JObject PoseReferenceSchema()
        {
            return new JObject
            {
                ["type"] = "object",
                ["description"] = "External Pose slot in the current scene context.",
                ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["track"] = new JObject { ["type"] = "string" },
                    ["index"] = new JObject { ["type"] = "integer", ["minimum"] = 0 }
                },
                ["required"] = new JArray("track", "index")
            };
        }

        private static PropertyDefinition RequiredSamples(string name)
        {
            return new PropertyDefinition(name, new JObject
            {
                ["type"] = "array",
                ["minItems"] = 1,
                ["items"] = new JObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["properties"] = new JObject
                    {
                        ["character"] = new JObject { ["type"] = "string" },
                        ["time"] = new JObject { ["type"] = "number" }
                    },
                    ["required"] = new JArray("character", "time")
                }
            }, true);
        }

        private static PropertyDefinition RequiredAnalysisClips()
        {
            return new PropertyDefinition("clips", new JObject
            {
                ["type"] = "array",
                ["description"] = "Exactly one animation clip reference. Use a loaded Clip name or an Assets/Packages path; an external asset is appended to the character's Kimodo Timeline before analysis and is never modified.",
                ["minItems"] = 1,
                ["maxItems"] = 1,
                ["items"] = new JObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["properties"] = new JObject
                    {
                        ["character"] = new JObject { ["type"] = "string" },
                        ["clip"] = new JObject { ["type"] = "string" }
                    },
                    ["required"] = new JArray("character", "clip")
                }
            }, true);
        }

        private static PropertyDefinition Enum(string name, params string[] values)
        {
            return new PropertyDefinition(name, new JObject
            {
                ["type"] = "string",
                ["enum"] = new JArray(values),
                ["default"] = values[0]
            }, false);
        }

        private static PropertyDefinition OptionalEnumWithDefault(string name, string defaultValue, params string[] values)
        {
            return new PropertyDefinition(name, new JObject
            {
                ["type"] = "string",
                ["enum"] = new JArray(values),
                ["default"] = defaultValue
            }, false);
        }

        private static PropertyDefinition RequiredEnum(string name, params string[] values)
        {
            return new PropertyDefinition(name, new JObject
            {
                ["type"] = "string",
                ["enum"] = new JArray(values)
            }, true);
        }

        private sealed class CommandCatalog
        {
            private readonly IReadOnlyList<CommandRegistration> registrations;
            private readonly Dictionary<string, CommandRegistration> byName;

            public CommandCatalog(IEnumerable<CommandRegistration> registrations)
            {
                this.registrations = registrations.ToList();
                byName = new Dictionary<string, CommandRegistration>(StringComparer.Ordinal);
                foreach (CommandRegistration registration in this.registrations)
                {
                    if (byName.ContainsKey(registration.Name))
                    {
                        throw new InvalidOperationException($"Duplicate Kimodo command '{registration.Name}'.");
                    }
                    byName.Add(registration.Name, registration);
                }
            }

            public bool TryGet(string name, out CommandRegistration registration)
            {
                registration = null;
                return name != null && byName.TryGetValue(name, out registration);
            }

            public string ToJson()
            {
                var tools = new JArray();
                foreach (CommandRegistration registration in registrations)
                {
                    tools.Add(registration.ToJson());
                }
                return new JObject { ["tools"] = tools }.ToString(Formatting.None);
            }
        }

        private sealed class CommandRegistration
        {
            private readonly JObject definition;

            public CommandRegistration(JObject definition, Func<string, string> handler)
            {
                this.definition = (JObject)definition.DeepClone();
                Handler = handler ?? throw new ArgumentNullException(nameof(handler));
                Name = this.definition.Value<string>("name");
            }

            public string Name { get; }
            public Func<string, string> Handler { get; }

            public JObject ToJson()
            {
                return (JObject)definition.DeepClone();
            }
        }

        private sealed class JobRecord
        {
            public JobRecord(
                UnityEngine.Object target,
                KimodoEditorGenerationJobSession session,
                TimelineGenerationTrace timelineGenerationTrace)
            {
                Target = target;
                Session = session;
                TimelineGenerationTrace = timelineGenerationTrace;
            }

            public UnityEngine.Object Target { get; }
            public KimodoEditorGenerationJobSession Session { get; }
            public TimelineGenerationTrace TimelineGenerationTrace { get; }
        }

        private sealed class CommandException : InvalidOperationException
        {
            public CommandException(string code, string message) : base(message)
            {
                Code = string.IsNullOrWhiteSpace(code) ? "invalid_argument" : code;
            }

            public string Code { get; }
        }

        private sealed class GenerationRangeLockedException : InvalidOperationException
        {
            public GenerationRangeLockedException(
                string command,
                Guid requestId,
                string character,
                string track,
                int lockedStartFrame,
                int lockedEndFrame,
                int requestedStartFrame,
                int requestedEndFrame)
                : base($"{command} cannot access [{requestedStartFrame},{requestedEndFrame}) on '{track}' while generation {requestId:D} locks [{lockedStartFrame},{lockedEndFrame}).")
            {
                Command = command;
                RequestId = requestId;
                Character = character;
                Track = track;
                LockedStartFrame = lockedStartFrame;
                LockedEndFrame = lockedEndFrame;
                RequestedStartFrame = requestedStartFrame;
                RequestedEndFrame = requestedEndFrame;
            }

            public string Command { get; }
            public Guid RequestId { get; }
            public string Character { get; }
            public string Track { get; }
            public int LockedStartFrame { get; }
            public int LockedEndFrame { get; }
            public int RequestedStartFrame { get; }
            public int RequestedEndFrame { get; }
        }

        private readonly struct ResolvedCharacter
        {
            public ResolvedCharacter(GameObject root, Animator animator, Avatar avatar, string name)
            {
                Root = root;
                Animator = animator;
                Avatar = avatar;
                Name = name;
            }

            public GameObject Root { get; }
            public Animator Animator { get; }
            public Avatar Avatar { get; }
            public UnityEngine.Object Target => Root;
            public string Name { get; }
        }

        private readonly struct PropertyDefinition
        {
            public PropertyDefinition(string name, string type, string description, bool required)
                : this(name, new JObject { ["type"] = type, ["description"] = description }, required)
            {
            }

            public PropertyDefinition(string name, JObject schema, bool required)
            {
                Name = name;
                Schema = schema;
                IsRequired = required;
            }

            public string Name { get; }
            public JObject Schema { get; }
            public bool IsRequired { get; }
        }
    }
}
