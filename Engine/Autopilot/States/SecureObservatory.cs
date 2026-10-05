using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    /// <param name="isFault">
    /// Whether the session is ending badly. Faults record their reason so the teardown can report the
    /// cause. The routine unsafe and window-closed secures don't, since the run comes back.
    /// </param>
    /// <param name="isEmergency">
    /// Whether this is the unsafe-conditions secure, where the roof closes without waiting on anything
    /// else. It leaves the flat panel alone, since the night comes back once conditions clear.
    /// </param>
    internal sealed class SecureObservatory(string name, Context context, Func<string>? reasonProvider = null,
        bool isFault = false, bool isEmergency = false)
        : StateBase(name) {
        public SecureObservatory(string name, Context context, string reason, bool isFault = false)
            : this(name, context, () => reason, isFault) { }

        // Read when the state runs, so a reason only known at abort time (a mount-limit breach) is the
        // one that gets logged and reported.
        private string Reason => reasonProvider?.Invoke() ?? "conditions unsafe";

        // It's already responding to unsafe, so the safety watcher mustn't re-abort it. It also finishes
        // parking if dawn arrives, instead of bouncing to shutdown mid-park.
        protected internal override bool ShouldWatchSafety => false;
        protected internal override bool ShouldWatchObservingWindow => false;

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {
            string reason = Reason;
            if (isFault) {
                // First cause wins. A state that failed on the way here may already have said why, and
                // the machine's catch names the exception that brought it down.
                context.SessionEndReason ??= reason;
            }

            // Its own clock, not the machine's, because a started secure must finish. Cancelling it (the
            // autopilot switched off mid-park, say) would leave the mount stopped wherever it got to under
            // an open roof. The machine's stop path still runs after this, and stopping a parked mount is
            // harmless. N.I.N.A. closing can't wait this long, so Autopilot.Destroy bounds its own wait.
            using CancellationTokenSource secureCancellation = new CancellationTokenSource(Context.SecureTimeout);
            bool isParked = false;
            try {
                await StopGuidingAsync(secureCancellation.Token);
                isParked = await ParkScopeAsync(reason, secureCancellation.Token);
                // The flat panel closes only when the night ends, before the roof if the cover can't move
                // under a closed roof and after it otherwise. The unsafe secure leaves it alone so the
                // night can carry on once the roof reopens.
                bool hasTriedFlatPanel = !isEmergency &&
                    await FlatPanelControl.CloseBeforeRoofAsync(context, reason, secureCancellation.Token);
                if (context.RoofMode == RoofMode.Operate || context.HasOpenedRoofTonight) {
                    await SecureRoofAsync(isParked, reason, secureCancellation.Token);
                }
                if (!isEmergency) {
                    await FlatPanelControl.CloseAfterRoofAsync(context, reason, hasAlreadyTried: hasTriedFlatPanel,
                        secureCancellation.Token);
                }
            } catch (OperationCanceledException) when (secureCancellation.IsCancellationRequested) {
                // Fixed first line, since the server keys its alert cooldown on it.
                context.ReportCritical(
                    "Securing the observatory did not finish in time. Check the mount and roof manually.",
                    new {
                        reason,
                        timeout = $"{Context.SecureTimeout.TotalMinutes:F0} minutes",
                        mount = context.Mount?.IsParked == true ? "parked" : "not parked",
                        roof = context.Dome?.ShutterStatusName ?? "no dome",
                        flat_panel = context.FlatPanel?.CoverStateName ?? "none",
                    });
            }

            // An unfinished park can leave the mount tracking, and on the dawn path nothing else stops it
            // before dusk, so it would track into the pier all day. We stop it without parking again,
            // since the park had its chance and the alert has gone out.
            if (!isParked) await StopUnparkedMountAsync();

            // What the session-end email reports as `secured`. Only true when parked, and the roof reads
            // closed if this autopilot works it.
            context.IsSessionSecured = isParked && IsRoofClosedOrNotOurs();
            if (context.IsSessionSecured) context.SessionSecuredAtUtc = DateTime.UtcNow;
            // The Dome's watchdog takes the roof back from here, and nothing images under it until
            // it is seen open again.
            context.IsRoofUnderAutopilotControl = false;
            context.IsRoofExpectedOpen = false;

            await context.ReportStartedImageFailedAsync($"cycle aborted: {reason}");

            return AutopilotStateResult.Completed; // -> waitForSafety (via the specific edge)
        }

        // Before the park, since the mount is about to move a long way. A guider that won't stop is
        // logged and swallowed so the park still happens. The guider bounds its own stop, so one that
        // never answers costs seconds of the secure's clock.
        private async Task StopGuidingAsync(CancellationToken cancellationToken) {
            if (context.Guider is not { IsConnected: true } guider) return;
            try {
                await guider.StopGuidingAsync(cancellationToken);
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                throw;
            } catch (Exception ex) {
                context.LogWarning($"Could not stop guiding while securing: {ex.Message}");
            }
        }

        /// <summary>Returns true only when it's confirmed parked, so callers know the roof can move.</summary>
        private async Task<bool> ParkScopeAsync(string reason, CancellationToken cancellationToken) {
            if (context.Mount == null) {
                context.LogWarning("Cannot park scope. Mount component not available.");
                return false;
            }

            // A mount that dropped gets one reconnect before the park. One that stays gone fails the
            // park below and is reported as not parked.
            await context.EnsureConnectedAsync(context.Mount, cancellationToken);
            if (context.Mount.IsParked) {
                context.Log($"Scope is already parked, so no park needed ({reason}).");
                return true;
            }

            context.AutopilotStatus = AutopilotStatus.Parking;
            context.Log($"Parking scope ({reason}).");
            Stopwatch watch = Stopwatch.StartNew();
            try {
                // Needs both a successful park call and AtPark, since a driver can report success
                // without reaching the park position.
                bool didParkCallSucceed = await context.Mount.ParkAsync(cancellationToken);
                bool isParked = didParkCallSucceed && context.Mount.IsParked;
                if (isParked) {
                    context.Log($"Scope parked in {Context.FormatElapsed(watch)} ({reason}).");
                } else if (didParkCallSucceed) {
                    context.LogWarning(
                        "Park call succeeded but the mount does not report AtPark after " +
                        $"{Context.FormatElapsed(watch)}, so treating as NOT parked.");
                } else {
                    context.LogWarning($"Park call failed after {Context.FormatElapsed(watch)}.");
                }

                return isParked;
            } catch (OperationCanceledException) {
                // Only the secure's own clock can cancel this (see ExecuteAsync).
                context.LogWarning(
                    $"Park did not finish after {Context.FormatElapsed(watch)}, so treating as NOT parked.");
                throw;
            } catch (Exception ex) {
                context.LogError($"Failed to park scope: {ex.Message}");
                context.LogDebug($"Park exception detail: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Stops tracking and any slew on a mount the secure couldn't park. StopMovementAsync takes no token,
        /// so this runs even after the secure's clock has run out. Never throws.
        /// </summary>
        private async Task StopUnparkedMountAsync() {
            if (context.Mount is not { IsConnected: true } mount) return;
            try {
                if (await mount.StopMovementAsync()) {
                    context.LogWarning("Mount is not parked. Tracking stopped so it stays where it is.");
                } else {
                    context.LogError("Mount is not parked and did not confirm it stopped. Check the mount.");
                }
            } catch (Exception ex) {
                context.LogError($"Mount is not parked and could not be stopped: {ex.Message}");
            }
        }

        private async Task SecureRoofAsync(bool isParked, string reason, CancellationToken cancellationToken) {
            // A dropped dome gets one reconnect, since nothing else can close the roof.
            await context.EnsureConnectedAsync(context.Dome, cancellationToken);
            if (context.Dome is not { IsConnected: true } dome) {
                // Normal before the connect chain has run (a failed start-up sequence, say). Nothing can
                // be confirmed either way, so it's only a warning.
                context.LogWarning(
                    "Dome is not connected: the roof cannot be closed, and its state cannot be confirmed.");
                return;
            }
            if (dome.IsShutterClosed()) {
                context.Log($"Roof is already closed ({reason}).");
                return;
            }
            if (isParked) {
                context.AutopilotStatus = AutopilotStatus.ClosingRoof;
                await CloseRoofAsync(dome, reason, cancellationToken);
                return;
            }
            // Closing over an unparked mount risks a collision, and N.I.N.A. refuses it when
            // RefuseUnsafeShutterMove is set. This gets its own email even though a session-end one
            // follows, since an open roof over an unparked mount needs somebody to go out there.
            context.ReportCritical(
                "Roof NOT closed because the mount is not parked. Secure the observatory manually.",
                new { mount = "not parked", roof = dome.ShutterStatusName, reason });
        }

        /// <summary>
        /// Closes the roof, retrying a few times before emailing. Judged by the shutter, since N.I.N.A. answers
        /// "done" whatever happened. Each attempt is bounded by the Dome's move timeout, so a driver stuck on
        /// "closing" gets retried.
        /// </summary>
        private async Task CloseRoofAsync(Dome dome, string reason, CancellationToken cancellationToken) {
            context.Log($"Closing roof ({reason}).");
            Stopwatch watch = Stopwatch.StartNew();
            for (int attempt = 1; attempt <= Context.RoofCloseAttempts; attempt++) {
                if (await dome.CloseShutterAsync(cancellationToken)) {
                    context.Log($"Roof closed in {Context.FormatElapsed(watch)} ({reason}).");
                    return;
                }
                if (attempt < Context.RoofCloseAttempts) {
                    context.LogWarning(
                        $"Roof did not close (attempt {attempt} of {Context.RoofCloseAttempts}, shutter " +
                        $"{dome.ShutterStatusName}). Trying again in {Context.RoofCloseRetrySeconds}s.");
                    await Task.Delay(TimeSpan.FromSeconds(Context.RoofCloseRetrySeconds), cancellationToken);
                }
            }
            // Its own email even though a session-end one may follow, since a roof that won't close
            // over a parked mount needs somebody to go and look.
            context.ReportCritical(
                "Roof NOT closed after parking. Check the roof manually.",
                new {
                    reason,
                    shutter = dome.ShutterStatusName,
                    attempts = Context.RoofCloseAttempts,
                    elapsed = Context.FormatElapsed(watch),
                });
        }

        // A disconnected dome in Operate mode counts as true, since that's normal before the connect
        // chain has run. The session-end email shows its `roof` as "unknown".
        private bool IsRoofClosedOrNotOurs() {
            if (context.RoofMode != RoofMode.Operate && !context.HasOpenedRoofTonight) return true;
            if (context.Dome is not { IsConnected: true } dome) return true;
            return dome.IsShutterClosed();
        }
    }
}
