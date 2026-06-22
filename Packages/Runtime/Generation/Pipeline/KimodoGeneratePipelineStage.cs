namespace KimodoBridge
{
    public enum KimodoGeneratePipelineStage
    {
        None = 0,
        Validate = 1,
        Constraint = 2,
        InvokeBackend = 3,
        AssetWrite = 4,
        Bake = 5,
        Retarget = 6,
        Finalize = 7,
        Completed = 8
    }
}
