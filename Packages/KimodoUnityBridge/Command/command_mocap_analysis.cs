using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using KimodoBridge;
using KimodoBridge.Editor;

namespace KimodoUnityBridge.Command
{
    internal static partial class command_context
    {
        public static string AnalyzeMocapVideo(string argumentsJson) => Execute(argumentsJson, arguments => StartMocapAnalysis(arguments, "video"));

        public static string AnalyzeMocapPicture(string argumentsJson) => Execute(argumentsJson, arguments => StartMocapAnalysis(arguments, "picture"));

        private static string StartMocapAnalysis(JObject arguments, string kind)
        {
                string mediaPath = RequiredStringValue(arguments, kind == "video" ? "video_path" : "image_path");
                string person = arguments.Value<string>("person")?.Trim();
                if (kind == "video" && person != null && person != "left" && person != "right")
                {
                    throw new InvalidOperationException("person must be 'left' or 'right'.");
                }

                string requestId = "mocap:" + Guid.NewGuid().ToString("N");
                lock (JobsLock)
                {
                    MocapAnalysisTasks[requestId] = new JObject
                    {
                        ["request_id"] = requestId,
                        ["status"] = "accepted",
                        ["stage"] = "queued",
                        ["message"] = "Mocap analysis accepted.",
                        ["source_type"] = kind,
                        ["media_path"] = mediaPath,
                        ["person"] = person ?? string.Empty,
                        ["started_at_utc"] = DateTime.UtcNow.ToString("O")
                    };
                    TrimTerminalTasksLocked(MocapAnalysisTasks, MaxRememberedJobs, "completed", "failed");
                }

                EditorApplication.delayCall += () =>
                {
                    _ = RunMocapAnalysisTaskAsync(requestId, kind, mediaPath, person);
                };
                return Ok(new JObject
                {
                    ["request_id"] = requestId,
                    ["status"] = "accepted"
                });
        }

        private static async Task RunMocapAnalysisTaskAsync(string requestId, string kind, string mediaPath, string person)
        {
            UpdateMocapAnalysisTask(requestId, "running", "estimating", "Running GVHMR pose estimation...");
            try
            {
                JObject analysis = await KimodoBridgeService.Shared.AnalyzeMocapAsync(
                    kind, mediaPath, person, CancellationToken.None);
                analysis = await AppendConstraintsOnEditorThreadAsync(analysis);
                UpdateMocapAnalysisTask(requestId, "completed", "completed", "Mocap pose estimation complete.", analysis);
            }
            catch (Exception exception)
            {
                UpdateMocapAnalysisTask(requestId, "failed", "failed", exception.Message);
            }
        }

        private static Task<JObject> AppendConstraintsOnEditorThreadAsync(JObject analysis)
        {
            var completion = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            EditorApplication.delayCall += () =>
            {
                try
                {
                    if (!KimodoConstraintPoseUtility.TryAppendSmplxConstraints(analysis, out string error))
                    {
                        completion.TrySetException(new InvalidOperationException($"SMPL-X pose conversion failed: {error}"));
                        return;
                    }
                    completion.TrySetResult(analysis);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            };
            return completion.Task;
        }

        private static void UpdateMocapAnalysisTask(
            string requestId,
            string status,
            string stage,
            string message,
            JObject analysis = null)
        {
            lock (JobsLock)
            {
                if (!MocapAnalysisTasks.TryGetValue(requestId, out JObject task)) return;
                task["status"] = status;
                task["stage"] = stage;
                task["message"] = message ?? string.Empty;
                if (analysis != null)
                {
                    foreach (JProperty property in analysis.Properties())
                    {
                        if (!string.Equals(property.Name, "status", StringComparison.Ordinal))
                        {
                            task[property.Name] = property.Value.DeepClone();
                        }
                    }
                }
                if (status == "failed")
                {
                    task["error"] = new JObject
                    {
                        ["code"] = "mocap_analysis_failed",
                        ["message"] = message ?? "Mocap analysis failed."
                    };
                }
            }
        }

        private static JObject GetMocapAnalysisTask(string requestId)
        {
            lock (JobsLock)
            {
                if (!MocapAnalysisTasks.TryGetValue(requestId, out JObject task))
                {
                    throw new InvalidOperationException($"Unknown or expired Mocap request_id '{requestId}'.");
                }
                return (JObject)task.DeepClone();
            }
        }
    }
}
