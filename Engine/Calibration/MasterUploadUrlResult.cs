namespace CosmicVaults.NINA.Astraeus.Engine.Calibration {
    /// <summary>Response from POST /api/pipeline/masters/upload-url/, the first step of a master upload.</summary>
    public sealed record MasterUploadUrlResult(
        int MasterFrameId, string UploadUrl, bool IsCreated);
}