using System;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    /// <param name="isFault">
    /// Whether the session is ending badly. Faults record their reason so the teardown can report the
    /// cause. The routine unsafe and window-closed secures don't, since the run comes back.
    /// </param>
    /// <param name="isEmergency">
    /// Whether the night may resume after this secure: unsafe conditions, or a roof that isn't ours
    /// closing. It leaves the flat panel alone, since the night comes back once conditions clear.
    /// </param>
    internal sealed class SecureObservatory(string name, Context context, Func<string>? reasonProvider = null,
        bool isFault = false, bool isEmergency = false)
        : StateBase(name) {
        public SecureObservatory(string name, Context context, string reason, bool isFault = false)
            : this(name, context, () => reason, isFault) { }

        private string Reason => reasonProvider?.Invoke() ?? "conditions unsafe";
        protected internal override bool ShouldWatchSafety => false;  // It's already responding to unsafe, so the safety watcher mustn't re-abort it.
        protected internal override bool ShouldWatchObservingWindow => false;
        // It watches the roof around its own park, and closes the roof itself.
        protected internal override bool ShouldWatchRoof => false;

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {
            string reason = Reason;
            if (isFault) {
                context.SessionEndReason ??= reason;
            }

            // Its own clock, not the machine's, because a started secure must finish. Cancelling it (the
            // autopilot switched off mid-park, say) would leave the mount stopped wherever it got to under
            // an open roof.
            using CancellationTokenSource secureCancellation =
                new CancellationTokenSource(Context.SecureTimeout, context.Observatory.Clock);
            RoofAwarePark parking = new RoofAwarePark(context, isEmergency);
            RoofAwarePark.Outcome park = RoofAwarePark.Outcome.NotParked;
            try {
                await StopGuidingAsync(secureCancellation.Token);
                park = await parking.RunAsync(reason, secureCancellation.Token);
                // The flat panel closes only when the night ends, before the roof if the cover can't move
                // under a closed roof and after it otherwise.
                bool hasTriedFlatPanel = !isEmergency &&
                    await FlatPanelControl.CloseBeforeRoofAsync(context, reason, secureCancellation.Token);
                if (context.RoofMode == RoofMode.Operate || context.HasOpenedRoofTonight) {
                    await SecureRoofAsync(park, reason, secureCancellation.Token);
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
            // before dusk, so it would track into the pier all day. RoofNotOpen has already stopped it,
            // unless the secure ran out of time.
            bool isParked = park == RoofAwarePark.Outcome.Parked;
            bool isOutOfTime = secureCancellation.IsCancellationRequested;
            if (!isParked && (park == RoofAwarePark.Outcome.NotParked || isOutOfTime)) {
                await parking.StopUnparkedMountAsync(isOutOfTime);
            }

            // What the session-end email reports as `secured`. Only true when parked, and the roof reads
            // closed if this autopilot works it.
            context.IsSessionSecured = isParked && context.IsRoofClosedOrNotOurs();
            if (context.IsSessionSecured) context.SessionSecuredAtUtc = context.UtcNow;
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

        private async Task SecureRoofAsync(RoofAwarePark.Outcome park, string reason,
            CancellationToken cancellationToken) {
            bool isParked = park == RoofAwarePark.Outcome.Parked;
            // A dropped dome gets one reconnect, since nothing else can close the roof.
            await context.EnsureConnectedAsync(context.Dome, cancellationToken);
            if (context.Dome is not { IsConnected: true } dome) {
                // The refused park has already alerted.
                if (park == RoofAwarePark.Outcome.RoofNotOpen) {
                    context.LogWarning("Dome is not connected, so the roof cannot be closed.");
                    return;
                }
                if (context.HasOpenedRoofTonight) {
                    //Roof was connected and opened earlier, but connection lost now.
                    context.ReportCritical(
                        "Roof NOT closed because the dome is not connected. Secure the observatory manually.",
                        new { mount = isParked ? "parked" : "not parked", roof = "unknown, opened by the autopilot tonight", reason });
                    return;
                }
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
            if (park == RoofAwarePark.Outcome.RoofNotOpen) {
                context.LogWarning(
                    $"Roof left {dome.ShutterStatusName}, since closing it over the unparked mount risks a collision.");
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
            DateTime started = context.UtcNow;
            for (int attempt = 1; attempt <= Context.RoofCloseAttempts; attempt++) {
                if (await dome.CloseShutterAsync(cancellationToken)) {
                    context.Log($"Roof closed in {context.ElapsedSince(started)} ({reason}).");
                    return;
                }
                if (attempt < Context.RoofCloseAttempts) {
                    context.LogWarning(
                        $"Roof did not close (attempt {attempt} of {Context.RoofCloseAttempts}, shutter " +
                        $"{dome.ShutterStatusName}). Trying again in {Context.RoofCloseRetrySeconds}s.");
                    await Task.Delay(TimeSpan.FromSeconds(Context.RoofCloseRetrySeconds), context.Observatory.Clock,
                        cancellationToken);
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
                    elapsed = context.ElapsedSince(started),
                });
        }
    }
}
