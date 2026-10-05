using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    /// <summary>
    /// A disk that fills mid-night gives captures that expose fine and then fail to save. Runs before
    /// MarkImageStarted so a full disk leaves the image PENDING on the server, not stuck at CAPTURING.
    /// </summary>
    internal sealed class CheckDiskSpace(string name, Context context) : StateBase(name) {
        public override Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {
            string? path = context.Observatory.Settings.ProfileService.ActiveProfile.ImageFileSettings.FilePath;
            if (string.IsNullOrWhiteSpace(path)) {
                context.LogWarning("No image file path configured, so skipping the disk space check.");
                return Task.FromResult(AutopilotStateResult.Completed);
            }

            // These lines name the drive, never the folder, since they reach the dashboard and a path
            // can carry the Windows user name.
            try {
                string? root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root)) {
                    context.LogWarning("Could not determine the drive of N.I.N.A.'s image folder, so skipping the " +
                                       "disk space check.");
                    return Task.FromResult(AutopilotStateResult.Completed);
                }

                DriveInfo drive = new DriveInfo(root);
                if (!drive.IsReady) {
                    context.LogWarning($"Drive {root} is not ready, so skipping the disk space check.");
                    return Task.FromResult(AutopilotStateResult.Completed);
                }

                double freeGigabytes = drive.AvailableFreeSpace / (double)(1024L * 1024L * 1024L);
                if (freeGigabytes < Context.MinimumFreeDiskGb) {
                    // Named here so the session-end email says "disk full", not "unrecoverable fault".
                    context.SessionEndReason =
                        $"Only {freeGigabytes:F1} GB free on {root} (minimum {Context.MinimumFreeDiskGb} GB).";
                    context.LogError(
                        $"Only {freeGigabytes:F1} GB free on {root} (minimum {Context.MinimumFreeDiskGb} GB). " +
                        "Securing the observatory and disabling the autopilot.");
                    return Task.FromResult(AutopilotStateResult.Failed);
                }

                context.LogDebug($"{freeGigabytes:F1} GB free on {root}.");
                return Task.FromResult(AutopilotStateResult.Completed);
            } catch (Exception ex) {
                // A path we can't inspect is no reason to stop imaging. The save itself reports a real
                // failure if there's no room.
                context.LogWarning($"Disk space check of N.I.N.A.'s image folder failed ({ex.Message}), so continuing.");
                return Task.FromResult(AutopilotStateResult.Completed);
            }
        }
    }
}
