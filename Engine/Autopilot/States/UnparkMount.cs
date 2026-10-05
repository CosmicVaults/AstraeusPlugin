using System;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    /// <summary>
    /// Clears IsSessionSecured, since every roof mode passes through here and OpenDome only runs under
    /// RoofMode.Operate.
    /// </summary>
    internal sealed class UnparkMount(string name, Context context, int maxAttempts = UnparkMount.DefaultMaxAttempts)
        : StateBase(name) {
        private const int DefaultMaxAttempts = 3;

        // The roof is open throughout, and a refused unpark waits a minute between tries, which is long
        // enough for weather to arrive.
        protected internal override bool ShouldWatchSafety => true;

        private int _attemptCount;

        public override Task OnEntryAsync(CancellationToken cancellationToken) {
            _attemptCount = 0;
            return base.OnEntryAsync(cancellationToken);
        }

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {
            // Failed here ends in the secure-and-disable path, so each refusal names itself for the
            // session-end email.
            if (context.Mount == null) {
                context.SessionEndReason = "Mount component not available, so cannot unpark.";
                context.LogError(context.SessionEndReason);
                return AutopilotStateResult.Failed;
            }
            // A disconnected mount reads as not parked, which would wave it through to the slew.
            if (!await context.EnsureConnectedAsync(context.Mount, cancellationToken)) {
                context.SessionEndReason = "Mount is not connected, so cannot unpark.";
                context.LogError(context.SessionEndReason);
                return AutopilotStateResult.Failed;
            }
            if (!context.Mount.IsParked) {
                context.IsSessionSecured = false;
                return AutopilotStateResult.Completed;
            }

            // N.I.N.A. refuses to unpark while the roof is shut (Dome settings -> 'Refuse mount unpark if
            // shutter is not open'). Retrying won't help, so fail fast with the reason. This bites
            // hardest in RoofMode.Ignore, where the autopilot never opens or checks the roof.
            if (context.IsUnparkRefusedWithoutOpenShutter && context.Dome?.IsShutterOpen() != true) {
                context.SessionEndReason =
                    "N.I.N.A. refuses to unpark the mount unless the roof is open, and the roof is not open.";
                string ignoreModeNote = context.RoofMode == RoofMode.Ignore
                    ? " (roof handling is set to Ignore, so the autopilot never opens it)"
                    : "";
                context.LogError(
                    "Not unparking: N.I.N.A. refuses to unpark the mount while the roof is not open" +
                    ignoreModeNote + ".");
                return AutopilotStateResult.Failed;
            }
            while (_attemptCount < maxAttempts) {
                _attemptCount += 1;
                if (await context.Mount.UnparkAsync(cancellationToken)) {
                    context.IsSessionSecured = false;
                    return AutopilotStateResult.Completed;
                }

                if (_attemptCount < maxAttempts) {
                    int delaySeconds = Context.DefaultRetryDelaySeconds;
                    context.LogWarning($"Unpark before the slew failed (attempt {_attemptCount} of " +
                                       $"{maxAttempts}); retrying in {delaySeconds}s.");
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
                }
            }
            context.SessionEndReason = $"Unable to unpark the mount after {maxAttempts} attempts.";
            context.LogError("Unable to unpark mount after all attempts. Autopilot disabled.");
            return AutopilotStateResult.Failed;

        }
    }
}