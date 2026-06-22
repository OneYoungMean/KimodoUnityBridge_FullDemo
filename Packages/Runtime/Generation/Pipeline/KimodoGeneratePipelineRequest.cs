namespace KimodoBridge
{
    public sealed class KimodoGeneratePipelineRequest
    {
        public KimodoBackendType BackendType;
        public KimodoRuntimeGenerationSettings RuntimeSettings;
        public KimodoGenerationRequestDto GenerationRequest;
    }
}
