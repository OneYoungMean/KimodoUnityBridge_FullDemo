using System;
using Newtonsoft.Json.Linq;

namespace KimodoUnityBridge.Command
{
    internal static partial class command_context
    {
        // Examples are part of the generated catalog, not a second hand-written
        // help file. Angle-bracket names must be replaced by returned handles.
        private static JArray BuildCommandExamples(string command)
        {
            switch (command)
            {
                case HelpCommand: return Examples("Inspect generation parameters and examples.",
                    @"{'command':'kimodo_generate_animation'}");
                case SceneContextCommand: return new JArray(
                    Example("Read the scene and current Session identity.", @"{}"),
                    Example("Include the compact Session artifact history when needed.", @"{'include_history':true}"));
                case InstallServerCommand: return Examples("Install the local server, then poll the returned request_id.", @"{}");
                case MocapVideoAnalysisCommand: return Examples("Estimate SMPL-X poses from a video, then poll for the pose array.",
                    @"{'video_path':'C:/data/mocap.mp4','person':'left'}");
                case MocapPictureAnalysisCommand: return Examples("Estimate one SMPL-X pose from a still image, then poll for the pose array.",
                    @"{'image_path':'C:/data/pose.jpg'}");
                case AnimationAnalyzeCommand: return Examples("Analyze one returned Clip; open the returned image to inspect it.",
                    @"{'clips':[{'character':'<character>','clip':'<clip>'}],'picture':{'output':'composite'},'resolution':512}");
                case RetargetAnimationCommand: return Examples("Retarget a loaded Clip to another scene character.",
                    @"{'source_character':'<source character>','animation':'<clip>','target_character':'<target character>','output':{'name':'RetargetedMotion'}}");
                case TransformCaptureCommand: return Examples("Capture the External Pose returned by pose_get together with scene objects, without moving the scene character.",
                    @"{'pose':{'track':'<returned pose track>','index':0},'transforms':['@pose','<table hierarchy path>','<apple hierarchy path>'],'resolution':768}");
                case GenerateAnimationCommand: return new JArray(
                    Example("Submit one Clip request; use the returned request_id with kimodo_get_generation.",
                        @"{'character':'<character>','prompt':'<short action for one Clip>','duration_frames':120}"),
                    Example("Continue a source Clip using its final half-second sampled five times. Names must come from the current scene context; inspect inout_sampling after generation completes.",
                        @"{'character':'<character>','prompt':'<short action for one Clip>','duration_frames':120,'loop':false,'constraints':[{'inout':{'mode':'outside','in':{'source':{'clip':'<source clip>'},'window_frames':30,'sample_count':5}}}]}"),
                    Example("Use explicit previous/next Clips as context on both sides; crop context from the returned two-second output.",
                        @"{'character':'<character>','prompt':'<short action for one Clip>','duration_frames':120,'loop':false,'constraints':[{'inout':{'in':{'source':{'clip':'<previous clip>'},'window_frames':30,'sample_count':5},'out':{'source':{'clip':'<next clip>'},'window_frames':12,'sample_count':3}}}]}"),
                    Example("Keep sampled opening and closing windows inside the returned Clip.",
                        @"{'character':'<character>','prompt':'<short action for one Clip>','duration_frames':120,'loop':false,'constraints':[{'inout':{'mode':'inside','in':{'source':{'clip':'<reference clip>','frame':0},'window_frames':12,'sample_count':3},'out':{'source':{'clip':'<reference clip>'},'window_frames':12,'sample_count':3}}}]}"),
                    Example("Set one Root2D point. The selected data fields determine the constraint family; no type tag is needed.",
                        @"{'character':'<character>','prompt':'<short action for one Clip>','duration_frames':120,'constraints':[{'frame':60,'root2d':{'position':[2.4,5.0],'heading':[0,1]}}]}"),
                    Example("Mix a FullBody pose, Root2D root override, and hand target at one frame. Root2D overlays the pose root channels and effect overlays the named hand target.",
                        @"{'character':'<character>','prompt':'<short action for one Clip>','duration_frames':180,'loop':false,'constraints':[{'frame':90,'pose':{'track':'<pose track>','index':0},'root2d':{'position':[2.4,5.0],'heading':[0,1]},'effect':{'right_hand':{'position':[2.55,0.95,5.35]}}}]}"));
                case PoseGetCommand: return new JArray(
                    Example("Capture a pose at an absolute Timeline-global frame. Analyze the Clip first, then add its local frame offset to start_timeline_frame_60.",
                        @"{'source':{'character':'<character>','timeline_frame_60':90},'full_data':true}"),
                    Example("Capture the original scene Animator's currently evaluated pose, independent of Timeline preview time.",
                        @"{'source':{'character':'<character>','mode':'scene_pose'},'full_data':true}"),
                    Example("Capture the Humanoid Avatar's stored skeleton rest pose.",
                        @"{'source':{'character':'<character>','mode':'skeleton_rest_pose'},'full_data':true}"));
                case PoseTestCommand: return new JArray(
                    Example("Measure the actual world-space hand positions in a sampled Timeline pose.",
                        @"{'pose':{'track':'<pose track returned by pose_get>','index':0},'joints':['RightHand','LeftHand']}"),
                    Example("Check whether the sampled right hand reaches a world-space contact point within 5 cm.",
                        @"{'pose':{'track':'<pose track returned by pose_get>','index':0},'targets':[{'joint':'RightHand','position':[1.2,0.9,2.4],'tolerance_m':0.05}]}"));
                case PoseSetCommand: return Examples("Edit the root of the Pose slot returned by pose_get; position is in Character world space.",
                    @"{'pose':{'track':'<returned pose track>','index':0},'root':{'position':[0,1,0],'rotation':[0,0,0,1]}}");
                case PoseSetRootTransformCommand: return Examples("Set a returned Pose slot's root position.",
                    @"{'pose':{'track':'<returned pose track>','index':0},'root':{'position':[0,1,0]}}");
                case PoseSetMuscleCommand: return Examples("Edit the Spine Front-Back muscle of a returned Pose slot.",
                    @"{'pose':{'track':'<returned pose track>','index':0},'muscles':{'Spine Front-Back':0.1}}");
                case GetGenerationCommand: return Examples("Poll using the exact request_id returned by generation or installation.",
                    @"{'request_id':'11111111-1111-1111-1111-111111111111'}");
                case CancelGenerationCommand: return Examples("Cancel using the exact active generation request_id.",
                    @"{'request_id':'11111111-1111-1111-1111-111111111111','reason':'User requested cancellation'}");
                case SessionClearCommand: return Examples("Clear the current runtime session while retaining persisted assets.", @"{}");
                default: throw new InvalidOperationException($"Command '{command}' is missing a help example.");
            }
        }

        private static JArray Examples(string description, string arguments) => new JArray(Example(description, arguments));
        private static JObject Example(string description, string arguments) => new JObject
        {
            ["description"] = description,
            ["arguments"] = JObject.Parse(arguments)
        };
    }
}
