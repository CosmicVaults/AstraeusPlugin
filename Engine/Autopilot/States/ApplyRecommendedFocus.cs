using System;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    /// <summary>
    /// On frames that don't autofocus, moves the focuser to the server's recommended position for this
    /// filter and temperature. The position came with the IsAutofocusRequired verdict, so there's no extra
    /// round trip.
    /// </summary>
    internal sealed class ApplyRecommendedFocus(string name, Context context) : StateBase(name) {
        protected internal override bool ShouldWatchSafety => true;
        protected internal override bool ShouldWatchMountLimits => true;

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {
            if (!context.IsSmartAutofocusEnabled || context.Focuser == null)
                return AutopilotStateResult.Completed;

            AutofocusStatusResponse? status = context.LastAutofocusStatus;
            string filter = status?.Filter ?? context.FilterWheel?.GetCurrentFilter()?.Name ?? "the current filter";
            if (status == null) {
                context.Log("Smart Autofocus: no server verdict this frame, so leaving focuser in place.");
            } else if (status.StartingPosition is int recommendedPosition) {
                string source = status.StartingPositionSource ?? "unknown";
                int deadbandSteps = MoveDeadbandSteps();
                if (context.Focuser.Position is int position &&
                    Math.Abs(recommendedPosition - position) < deadbandSteps) {
                    context.Log($"Smart Autofocus: focuser at {position} is within {deadbandSteps} steps of " +
                                $"recommended {recommendedPosition} for '{filter}', so not moving.");
                    return AutopilotStateResult.Completed;
                }
                context.Log($"Smart Autofocus: moving focuser to recommended position {recommendedPosition} " +
                            $"for '{filter}' (source: {source}).");
                try {
                    // After a filter change the position jumps by the filter offset, which is what
                    // N.I.N.A.'s "disable guiding on filter change" covers. Small temperature moves
                    // between frames of one filter happen while guiding, as N.I.N.A. does.
                    bool shouldStopGuiding = context.HasFilterChangedThisFrame &&
                        context.Observatory.Settings.ProfileService.ActiveProfile.FilterWheelSettings
                            .DisableGuidingOnFilterChange;
                    await context.StopGuidingForFocusMoveAsync(recommendedPosition, shouldStopGuiding,
                        cancellationToken);
                    await context.Focuser.MoveToPositionAsync(recommendedPosition, cancellationToken);
                } catch (OperationCanceledException) {
                    throw;
                } catch (Exception ex) {
                    context.LogWarning(
                        $"Could not move the focuser to {recommendedPosition} ({ex.Message}), so leaving it in place.");
                }
            } else {
                context.Log($"Smart Autofocus: no recommended position for '{filter}', so leaving focuser in place.");
            }

            return AutopilotStateResult.Completed;
        }

        // A tenth of N.I.N.A.'s autofocus step, the spacing its own focus curve treats as meaningful.
        // Every move costs seconds whatever its size (about 5 s even for 0-2 steps, which most moves are).
        private int MoveDeadbandSteps() {
            int stepSize = context.Observatory.Settings.ProfileService.ActiveProfile.FocuserSettings.AutoFocusStepSize;
            return Math.Max(1, stepSize / MoveDeadbandDivisor);
        }

        private const int MoveDeadbandDivisor = 10;
    }
}
