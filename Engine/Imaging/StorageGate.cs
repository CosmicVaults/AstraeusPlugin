using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Imaging {
    /// <summary>
    /// Asks the server before every upload and remembers nothing, so freed space or a raised quota counts
    /// at once. It's a POST because answering can delete frames for good under Overwrite Oldest, so a
    /// retry or prefetch must never trigger it.
    /// </summary>
    internal static class StorageGate {
        /// <summary>Logs the answer under the caller's device and category. Never throws.</summary>
        public static async Task<StorageVerdict> CheckAsync(Observatory observatory, string logDevice,
            LogCategory logCategory) {
            // Signed out, there's nothing to ask with, and the upload declines on its own a moment
            // later. Asking anyway would log a 401 for every frame.
            if (observatory.Authenticator?.IsAuthenticated != true) return StorageVerdict.Upload;

            StorageCheckResult? result = await observatory.AstraeusWebClient.PostStorageCheckAsync();
            if (result is null) {
                // An older server, or one we couldn't reach. Let the frame go, since the presign
                // answers 507 if there's really no room. Debug level because the failed call has
                // already logged itself and this is the planned fallback.
                observatory.LogDebug(
                    "Could not check cloud storage; the upload goes ahead and the server refuses it " +
                    "if there is no room.", logDevice, logCategory);
                return StorageVerdict.Upload;
            }

            // The server just deleted the observer's frames. It's their data, so we always say so.
            if (result.EvictedFrames > 0) {
                string frames = result.EvictedFrames == 1 ? "frame" : "frames";
                observatory.LogWarning(
                    $"Cloud storage was full: the server deleted your {result.EvictedFrames} oldest {frames} " +
                    $"({StorageCheckResult.FormatBytes(result.FreedBytes)}) to make room, as " +
                    $"{result.DescribePolicy()} asks it to.", logDevice, logCategory);
            }

            if (result.HasSpace) {
                observatory.LogDebug($"Cloud storage: {result.Describe()}.", logDevice, logCategory);
                return StorageVerdict.Upload;
            }

            if (result.ShouldPauseAutopilotWhenFull) {
                observatory.LogError(
                    $"Cloud storage is full ({result.Describe()}) and this account asks the autopilot " +
                    "to pause when it is.", logDevice, logCategory);
                return StorageVerdict.Pause;
            }

            observatory.LogWarning(
                $"Cloud storage is full ({result.Describe()}), so this frame stays on this machine and " +
                "imaging carries on.", logDevice, logCategory);
            return StorageVerdict.Skip;
        }
    }
}
