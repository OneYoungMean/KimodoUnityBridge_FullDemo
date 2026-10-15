namespace KimodoUnityBridge.Command
{
    /// <summary>vNext Kimodo command facade. command_dispatcher remains the public discovery endpoint.</summary>
    public static class command_kimodo
    {
        public const string HelpCommand = command_context.HelpCommand;
        public const string SceneContextCommand = command_context.SceneContextCommand;
        public const string InstallServerCommand = command_context.InstallServerCommand;
        public const string GenerateAnimationCommand = command_context.GenerateAnimationCommand;
        public const string AnalyzeCommand = command_context.AnimationAnalyzeCommand;
        public const string PoseTestCommand = command_context.PoseTestCommand;
        public const string RetargetAnimationCommand = command_context.RetargetAnimationCommand;
        public const string GetGenerationCommand = command_context.GetGenerationCommand;
        public const string CancelGenerationCommand = command_context.CancelGenerationCommand;
        public const string SessionClearCommand = command_context.SessionClearCommand;

        public static string Help(string argumentsJson = "{}") => command_dispatcher.Invoke(HelpCommand, argumentsJson);
        public static string SceneContext(string argumentsJson = "{}") => command_dispatcher.Invoke(SceneContextCommand, argumentsJson);
        public static string InstallServer(string argumentsJson = "{}") => command_dispatcher.Invoke(InstallServerCommand, argumentsJson);
        public static string GenerateAnimation(string argumentsJson) => command_dispatcher.Invoke(GenerateAnimationCommand, argumentsJson);
        public static string Analyze(string argumentsJson) => command_dispatcher.Invoke(AnalyzeCommand, argumentsJson);
        public static string TestPose(string argumentsJson) => command_dispatcher.Invoke(PoseTestCommand, argumentsJson);
        public static string RetargetAnimation(string argumentsJson) => command_dispatcher.Invoke(RetargetAnimationCommand, argumentsJson);
        public static string GetGeneration(string argumentsJson) => command_dispatcher.Invoke(GetGenerationCommand, argumentsJson);
        public static string CancelGeneration(string argumentsJson) => command_dispatcher.Invoke(CancelGenerationCommand, argumentsJson);
        public static string ClearSession(string argumentsJson = "{}") => command_dispatcher.Invoke(SessionClearCommand, argumentsJson);
    }
}
