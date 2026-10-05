using System;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    internal sealed class WaitForSafe(string name, Context context) : WaitForConditionState(name) {
        protected override Context RunContext => context;

        protected override AutopilotStatus Status => AutopilotStatus.WaitingSafety;

        // When we last retried a roof the secure couldn't close, so retries run on a cooldown, not
        // every poll.
        private DateTime _lastRecloseAttemptUtc;

        // When the monitor first read Safe in the current unbroken run of Safe readings, or null while
        // unsafe. It lives on the context because the dashboard payload carries it.
        private DateTime? SafeSinceUtc {
            get => context.SafeSinceUtc;
            set => context.SafeSinceUtc = value;
        }

        /// <summary>
        /// Reaching here means every required device connected, so the run is under way and equipment
        /// alerts can fire. Before this, a disconnected device is just someone powering up late.
        /// </summary>
        public override Task OnEntryAsync(CancellationToken cancellationToken) {
            context.IsStartupComplete = true;
            // From here the machine works the roof, opening it when safe and closing it when not.
            // SecureObservatory hands it back, and the Dome's watchdog covers the rest.
            context.IsRoofUnderAutopilotControl = true;
            // Nothing is imaging under the roof from here until it is seen open again.
            context.IsRoofExpectedOpen = false;
            _lastRecloseAttemptUtc = DateTime.MinValue;
            SafeSinceUtc = null;
            return base.OnEntryAsync(cancellationToken);
        }

        // However the wait ends, the payload must stop saying a settle is running, or the card would
        // keep counting under the next state.
        public override Task OnExitAsync() {
            SafeSinceUtc = null;
            return base.OnExitAsync();
        }

        // Faster while the settle is running, so the resume lands close to the end of it.
        protected override int PollIntervalSeconds =>
            SafeSinceUtc is null ? Context.SafetyPollIntervalSeconds : Context.SafetySettlePollIntervalSeconds;

        protected override async Task<bool> IsSatisfiedAsync(CancellationToken cancellationToken) {
            // A dropped safety monitor reads unsafe and would hold the night here, so it gets a
            // reconnect (at most once per ReconnectCooldown).
            await context.EnsureConnectedAsync(context.SafetyMonitor, cancellationToken);
            if (!context.IsSafe()) {
                if (SafeSinceUtc is not null) {
                    context.Log("Conditions turned unsafe again during the settle, so the wait starts over.");
                    SafeSinceUtc = null;
                }
                await RecloseRoofIfDueAsync(cancellationToken);
                return false;
            }

            // A monitor that flaps in marginal weather would open, unpark and slew on one Safe reading
            // and park and close on the next. Nothing moves until it has read Safe for the whole settle.
            int settleSeconds = context.SafeSettleSeconds;
            if (settleSeconds <= 0) return true;
            if (SafeSinceUtc is not DateTime safeSince) {
                SafeSinceUtc = DateTime.UtcNow;
                context.Log($"Conditions safe, so settling for {settleSeconds} s before resuming.");
                return false;
            }
            return (DateTime.UtcNow - safeSince).TotalSeconds >= settleSeconds;
        }

        // A roof the secure couldn't close gets another go every few minutes while it's unsafe, but only
        // over a parked mount and only in Operate mode. The secure already sent the alarm, so these are
        // log lines only.
        private async Task RecloseRoofIfDueAsync(CancellationToken cancellationToken) {
            if (context.RoofMode != RoofMode.Operate) return;
            if (context.Dome is not { IsConnected: true } dome || dome.IsShutterClosed()) return;
            if (DateTime.UtcNow - _lastRecloseAttemptUtc < Context.RoofRecloseCooldown) return;
            _lastRecloseAttemptUtc = DateTime.UtcNow;

            context.LogWarning("Roof is open while conditions are unsafe, so trying to close it.");
            if (await dome.TryCloseWhenParkedAsync("conditions unsafe", cancellationToken)) {
                context.Log("Roof closed.");
            } else {
                context.LogWarning(
                    $"Roof is still not closed (shutter {dome.ShutterStatusName}). Trying again in " +
                    $"{Context.RoofRecloseCooldown.TotalMinutes:F0} minutes.");
            }
        }

        // The log heartbeat only. The card's own count comes from the payload's safe_since, so it
        // does not wait on this line.
        protected override string WaitingMessage() {
            if (SafeSinceUtc is DateTime safeSince) {
                int elapsedSeconds = (int)(DateTime.UtcNow - safeSince).TotalSeconds;
                return $"Conditions safe for {elapsedSeconds} s of the {context.SafeSettleSeconds} s settle";
            }
            return "Waiting for conditions to become safe";
        }
    }
}
