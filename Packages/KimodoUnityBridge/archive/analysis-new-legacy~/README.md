# Retired `analysis.new` command

This command-specific implementation was retired as the public analysis
surface was consolidated into `animation.analysis`, `pose.analysis`,
`pose.fromvideo`, and `pose.frompicture`. `capture.clip`, `capture.transform`,
`pose.test`, `analysis.video`, and `analysis.picture` have no active handlers.

The snippet below preserves the retired `analysis.new` command contract for
history. It is not compiled and is not an active API reference.

```csharp
public const string NewAnalysisCommand = "analysis.new";

CommandDefinition(NewAnalysisCommand,
    "Analyze one scene Clip and render the fixed 20-tile SceneView analysis layout. The result returns the image path, tile rectangles, headers, and the structured Clip analysis.",
    Properties(
        RequiredAnalysisClips(),
        new PropertyDefinition("resolution", new JObject
        {
            ["type"] = "integer",
            ["minimum"] = 64,
            ["maximum"] = 4096,
            ["default"] = 1920,
            ["description"] = "Final composite width in pixels; the fixed 16:9 layout is rendered at the matching height."
        }, false)));

[NewAnalysisCommand] = NewAnalysis,

public static string NewAnalysis(string argumentsJson)
{
    return Execute(argumentsJson, arguments =>
        Ok(AnimationAnalyzeCore(arguments, allowActiveGenerationRange: false, newPictureContract: true)));
}

private static JObject AnimationAnalyzeCore(
    JObject arguments,
    bool allowActiveGenerationRange,
    bool newPictureContract = false,
    bool layoutOnly = false)
{
    JObject picture = newPictureContract
        ? new JObject
        {
            ["tiles"] = new JArray("3d_track", "height_time_track", "3d_ghost", "3d_ghost_track", "key_pose", "step_pose"),
            ["output"] = "composite",
            ["environment"] = new JObject { ["mode"] = "isolated" }
        }
        : AnalysisPictureRequest.Parse(arguments["picture"] as JObject).ToJson();

    return new JObject
    {
        ["analysis_schema_version"] = newPictureContract ? "analysis.new.v1" : AnalysisContractVersion,
        ["clip"] = BuildAnimationAnalyzeClipResult(subject),
        ["pictures"] = BuildPublicPictureMetadata(pictures, subject)
    };
}

case NewAnalysisCommand: return Examples("Render the fixed opaque six-panel analysis image for one Clip.",
    @"{'clips':[{'character':'<character>','clip':'<clip>'}],'resolution':1920}");

NewAnalysisCommand => new[] { "analysis_schema_version", "clip", "pictures" },
```
