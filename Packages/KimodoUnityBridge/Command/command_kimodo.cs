namespace KimodoUnityBridge.Command
{
    /// <summary>vNext Kimodo command facade. command_dispatcher remains the public discovery endpoint.</summary>
    public static class command_kimodo
    {
        public const string HelpCommand = command_context.HelpCommand;
        public const string SceneContextCommand = command_context.SceneContextCommand;
        public const string InstallServerCommand = command_context.InstallServerCommand;
        public const string GenerateAnimationCommand = command_context.GenerateAnimationCommand;
        public const string AnimationAnalysisCommand = command_context.AnimationAnalysisCommand;
        public const string PoseAnalysisCommand = command_context.PoseAnalysisCommand;
        public const string PoseFromVideoCommand = command_context.PoseFromVideoCommand;
        public const string PoseFromPictureCommand = command_context.PoseFromPictureCommand;
        public const string RetargetAnimationCommand = command_context.RetargetAnimationCommand;
        public const string GetGenerationCommand = command_context.GetGenerationCommand;
        public const string CancelGenerationCommand = command_context.CancelGenerationCommand;
        public const string SessionClearCommand = command_context.SessionClearCommand;

        public static string Help(string argumentsJson = "{}") => command_dispatcher.Invoke(HelpCommand, argumentsJson);
        public static string SceneContext(string argumentsJson = "{}") => command_dispatcher.Invoke(SceneContextCommand, argumentsJson);
        public static string InstallServer(string argumentsJson = "{}") => command_dispatcher.Invoke(InstallServerCommand, argumentsJson);
        public static string GenerateAnimation(string argumentsJson) => command_dispatcher.Invoke(GenerateAnimationCommand, argumentsJson);
        public static string AnalyzeAnimation(string argumentsJson) => command_dispatcher.Invoke(AnimationAnalysisCommand, argumentsJson);
        public static string AnalyzePose(string argumentsJson) => command_dispatcher.Invoke(PoseAnalysisCommand, argumentsJson);
        public static string AnalyzeVideo(string argumentsJson) => command_dispatcher.Invoke(PoseFromVideoCommand, argumentsJson);
        public static string AnalyzePicture(string argumentsJson) => command_dispatcher.Invoke(PoseFromPictureCommand, argumentsJson);
        public static string RetargetAnimation(string argumentsJson) => command_dispatcher.Invoke(RetargetAnimationCommand, argumentsJson);
        public static string GetGeneration(string argumentsJson) => command_dispatcher.Invoke(GetGenerationCommand, argumentsJson);
        public static string CancelGeneration(string argumentsJson) => command_dispatcher.Invoke(CancelGenerationCommand, argumentsJson);
        public static string ClearSession(string argumentsJson = "{}") => command_dispatcher.Invoke(SessionClearCommand, argumentsJson);
    }
}
