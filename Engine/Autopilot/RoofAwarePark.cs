using CosmicVaults.NINA.Astraeus.Engine.Utility;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot {
    /// <summary>
    /// A secure's park. Where the roof is in play it parks only under a roof that reads Open, and stops the
    /// park if the roof stops reading open part-way. A refused or stopped park leaves the mount stopped where
    /// it is, with an alert.
    /// <para>
    /// Two settings skip the roof check, both off by default: "Mount can move under closed roof"
    /// (CanMountMoveUnderClosedRoof), which says the park is safe whatever the roof does, and "No roof"
    /// (RoofMode.Ignore), for a pier with nothing overhead.
    /// </para>
    /// <para>
    /// What it can't do: stop a roof the autopilot doesn't operate, so a roof controller that closes on its
    /// own needs its own interlock to keep off an unparked mount; or stop a park part-way on a driver whose
    /// Park() blocks until it finishes. The roof is read from N.I.N.A.'s cached state once a second.
    /// </para>
    /// </summary>
    /// <param name="isEmergency">Whether the night may resume after the secure, which the alert's action says.</param>
    internal sealed class RoofAwarePark(Context context, bool isEmergency) {
        public enum Outcome { Parked, NotParked, RoofNotOpen }

        private static readonly TimeSpan RoofPollInterval = TimeSpan.FromSeconds(1);
        // A roof caught opening gets this long to finish before the park is refused.
        private static readonly TimeSpan RoofOpeningWait = TimeSpan.FromMinutes(2);
        // How long a stopped park gets to unwind.
        private static readonly TimeSpan ParkUnwindWait = TimeSpan.FromSeconds(10);
        // How long a park still under way when the park call returns gets to finish, as N.I.N.A.'s own can be.
        private static readonly TimeSpan ParkFinishWait = TimeSpan.FromMinutes(3);

        private const string ParkStoppedAlert =
            "Park stopped part-way because the roof stopped reading open. Check the mount and roof.";

        // Held until the retry has had its go, so a park that then works leaves no false alarm behind. One
        // instance per secure, so nothing carries over.
        private string? _pendingRoofAlert;

        internal bool IsRoofClearanceRequired =>
            (context.RoofMode != RoofMode.Ignore || context.HasOpenedRoofTonight)
            && !context.CanMountMoveUnderClosedRoof;

        /// <summary>
        /// Parks, with one more go if the roof reads open again after a refusal or a stopped park. The roof
        /// alert goes out only if the mount still couldn't be parked, including when the secure's clock runs out.
        /// </summary>
        public async Task<Outcome> RunAsync(string reason, CancellationToken cancellationToken) {
            Outcome outcome = Outcome.NotParked;
            try {
                outcome = await ParkOnceAsync(reason, cancellationToken);
                // A dome that dropped for a moment, or a roof that finished opening late, can read open
                // again. One more go, or the roof would be left open over the unparked mount.
                if (outcome == Outcome.RoofNotOpen && await IsRoofOpenAfterReconnectAsync(cancellationToken)) {
                    context.Log($"Roof reads open again, so trying the park once more ({reason}).");
                    outcome = await ParkOnceAsync(reason, cancellationToken);
                }
                return outcome;
            } finally {
                // Fixed first lines, since the server keys its alert cooldown on them.
                if (_pendingRoofAlert is { } headline && outcome == Outcome.RoofNotOpen) {
                    context.ReportCritical(headline, AlertDetails(reason));
                }
            }
        }

        /// <summary>
        /// Stops tracking and any slew on a mount that couldn't be parked. Only a secure that ran out of time,
        /// and may have cut a park off, uses StopParkAsync. Neither stop takes a token, so this runs even after
        /// the secure's clock has run out. Never throws.
        /// </summary>
        public async Task StopUnparkedMountAsync(bool isOutOfTime) {
            if (context.Mount is not { IsConnected: true } mount) return;
            try {
                bool hasStopped = isOutOfTime ? await mount.StopParkAsync() : await mount.StopMovementAsync();
                if (hasStopped) {
                    context.LogWarning("Mount is not parked. Tracking stopped so it stays where it is.");
                } else {
                    context.LogError("Mount is not parked and did not confirm it stopped. Check the mount.");
                }
            } catch (Exception ex) {
                context.LogError($"Mount is not parked and could not be stopped: {ex.Message}");
            }
        }

        /// <summary>Parked only when confirmed AtPark, so callers know the roof can move.</summary>
        private async Task<Outcome> ParkOnceAsync(string reason, CancellationToken cancellationToken) {
            if (context.Mount == null) {
                context.LogWarning("Cannot park scope. Mount component not available.");
                return Outcome.NotParked;
            }

            // A mount that dropped gets one reconnect before the park. One that stays gone fails the
            // park below and is reported as not parked.
            await context.EnsureConnectedAsync(context.Mount, cancellationToken);
            if (!context.Mount.IsConnected) {
                context.LogWarning($"Cannot park scope ({reason}). The mount is not connected.");
                return Outcome.NotParked;
            }
            if (context.Mount.IsParked) {
                context.Log($"Scope is already parked, so no park needed ({reason}).");
                return Outcome.Parked;
            }

            Dome? dome = null;
            if (IsRoofClearanceRequired) {
                dome = await WaitForOpenRoofAsync(cancellationToken);
                if (dome == null) {
                    // N.I.N.A. may have parked it itself just before closing.
                    if (context.Mount.IsParked) {
                        context.Log($"Scope is already parked, so no park needed ({reason}).");
                        return Outcome.Parked;
                    }
                    bool wasResting = context.Mount.IsResting;
                    if (!wasResting) await StopUnparkedMountAsync(isOutOfTime: false);
                    NoteParkRefused(reason, wasResting);
                    return Outcome.RoofNotOpen;
                }
            }

            context.AutopilotStatus = AutopilotStatus.Parking;
            context.Log($"Parking scope ({reason}).");
            DateTime started = context.UtcNow;
            try {
                bool? didParkCallSucceed = dome == null
                    ? await context.Mount.ParkAsync(cancellationToken)
                    : await ParkWatchingRoofAsync(context.Mount, dome, cancellationToken);
                if (didParkCallSucceed == null) {
                    _pendingRoofAlert = ParkStoppedAlert;
                    return Outcome.RoofNotOpen;
                }
                // A park still under way when the call returns, as N.I.N.A.'s own can be before its close,
                // gets time to finish instead of being stopped part-way.
                if (!context.Mount.IsParked && context.Mount.IsSlewing
                    && !await WaitForParkToFinishAsync(context.Mount, dome, cancellationToken)) {
                    _pendingRoofAlert = ParkStoppedAlert;
                    return Outcome.RoofNotOpen;
                }

                // AtPark decides, so a park N.I.N.A. made itself still counts.
                bool isParked = context.Mount.IsParked;
                if (isParked) {
                    context.Log($"Scope parked in {context.ElapsedSince(started)} ({reason}).");
                } else if (didParkCallSucceed == true) {
                    context.LogWarning(
                        "Park call succeeded but the mount does not report AtPark after " +
                        $"{context.ElapsedSince(started)}, so treating as NOT parked.");
                } else {
                    context.LogWarning($"Park call failed after {context.ElapsedSince(started)}.");
                }
                return isParked ? Outcome.Parked : Outcome.NotParked;
            } catch (OperationCanceledException) {
                // Only the secure's own clock can cancel this.
                context.LogWarning(
                    $"Park did not finish after {context.ElapsedSince(started)}, so treating as NOT parked.");
                throw;
            } catch (Exception ex) {
                context.LogError($"Failed to park scope: {ex.Message}");
                context.LogDebug($"Park exception detail: {ex}");
                return Outcome.NotParked;
            }
        }

        private async Task<bool> IsRoofOpenAfterReconnectAsync(CancellationToken cancellationToken) {
            await context.EnsureConnectedAsync(context.Dome, cancellationToken);
            return context.Dome?.IsShutterOpen() == true;
        }

        /// <summary>The dome if the roof reads open, after waiting out one caught opening. Otherwise null.</summary>
        private async Task<Dome?> WaitForOpenRoofAsync(CancellationToken cancellationToken) {
            await context.EnsureConnectedAsync(context.Dome, cancellationToken);
            if (context.Dome is not { IsConnected: true } dome) return null;
            if (dome.IsShutterOpening()) {
                context.Log("Roof is opening, so waiting for it to finish before parking.");
                DateTime openingSince = context.UtcNow;
                while (dome.IsShutterOpening() && context.UtcNow - openingSince < RoofOpeningWait) {
                    await Task.Delay(RoofPollInterval, context.Observatory.Clock, cancellationToken);
                }
            }
            return dome.IsShutterOpen() ? dome : null;
        }

        /// <summary>
        /// Parks while watching the roof. Null if the roof stopped reading open first, by which time the
        /// mount has been told to stop.
        /// </summary>
        private async Task<bool?> ParkWatchingRoofAsync(Mount mount, Dome dome, CancellationToken cancellationToken) {
            using CancellationTokenSource parkCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task<bool> parking = mount.ParkAsync(parkCancellation.Token);
            while (!parking.IsCompleted) {
                await Task.WhenAny(parking, Task.Delay(RoofPollInterval, context.Observatory.Clock, cancellationToken));
                if (parking.IsCompleted) break;
                if (cancellationToken.IsCancellationRequested) {
                    parking.ObserveFaults(LogLateParkFault);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                if (dome.IsShutterOpen() || mount.IsParked) continue;

                // Stop first, since cancelling only stops N.I.N.A. waiting on the park.
                await StopParkUnderRoofAsync(mount, cancellationToken);
                await parkCancellation.CancelAsync();
                await Task.WhenAny(parking, Task.Delay(ParkUnwindWait, context.Observatory.Clock));
                parking.ObserveFaults(LogLateParkFault);
                return null;
            }
            return await parking;
        }

        private void LogLateParkFault(Exception ex) =>
            context.LogDebug($"Park ended with an error after the secure stopped waiting: {ex.Message}");

        /// <summary>
        /// Waits while a park is still under way, watching the roof. False if the roof stopped reading open
        /// first, by which time the mount has been told to stop.
        /// </summary>
        private async Task<bool> WaitForParkToFinishAsync(Mount mount, Dome? dome, CancellationToken cancellationToken) {
            context.Log("The park call returned with the mount still moving, so waiting for it to reach park.");
            DateTime waitingSince = context.UtcNow;
            while (mount.IsSlewing && !mount.IsParked && context.UtcNow - waitingSince < ParkFinishWait) {
                if (dome != null && !dome.IsShutterOpen()) {
                    await StopParkUnderRoofAsync(mount, cancellationToken);
                    return false;
                }
                await Task.Delay(RoofPollInterval, context.Observatory.Clock, cancellationToken);
            }
            return true;
        }

        // A mount that dropped gets one reconnect, or the stop can't reach it.
        private async Task StopParkUnderRoofAsync(Mount mount, CancellationToken cancellationToken) {
            try {
                if (!mount.IsConnected) await context.EnsureConnectedAsync(mount, cancellationToken);
                if (!await mount.StopParkAsync()) {
                    context.LogError("The park could not be confirmed stopped. Check the mount.");
                }
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                throw;
            } catch (Exception ex) {
                context.LogError($"Could not stop the park: {ex.Message}");
            }
        }

        private void NoteParkRefused(string reason, bool wasResting) {
            // Closed over a mount that was already still, as at dawn after an earlier refusal.
            if (wasResting && context.Dome?.IsShutterClosed() == true) {
                context.LogWarning($"Not parking, because the roof is closed ({reason}). The mount was already still.");
                return;
            }
            _pendingRoofAlert = context.Dome is { IsConnected: true }
                ? "Mount NOT parked because the roof is not open. The mount was stopped where it was."
                : "Mount NOT parked because the roof state is unknown (dome not connected). The mount was stopped where it was.";
        }

        private object AlertDetails(string reason) => new {
            reason,
            mount = context.Mount switch {
                null or { IsConnected: false } => "not connected",
                { IsParked: true } => "parked",
                { IsResting: true } => "stopped",
                _ => "still moving",
            },
            roof = context.Dome?.ShutterStatusName ?? "no dome",
            roof_mode = context.RoofMode.ToString(),
            nina_close_on_unsafe = context.IsRoofClosedOnUnsafe ? "on" : "off",
            action = isEmergency
                ? "waiting for safe conditions and for the roof to open again"
                : "the night has ended; park the mount by hand once the roof is open",
        };
    }
}
