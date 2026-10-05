using NINA.WPF.Base.Utility.AutoFocus;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    /// <summary>
    /// Runs N.I.N.A.'s autofocus, then records the time, temperature and filter for the cadence triggers.
    /// A failed run is reported to the server and returns Failed, but imaging carries on.
    /// </summary>
    internal sealed class RunAutofocus(string name, Context context) : StateBase(name) {
        protected internal override bool ShouldWatchSafety => true;
        protected internal override bool ShouldWatchMountLimits => true;

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {
            if (context.Focuser == null) {
                context.LogWarning("No focuser available, so skipping autofocus.");
                return AutopilotStateResult.Completed;
            }

            // Without Smart Autofocus the first run of the night may use the profile's autofocus filter
            // (when offsets are in use). With it, every run measures the current filter, since the server
            // keys its model on the filter name and N.I.N.A.'s offsets are off.
            bool shouldUseDesignatedFilter = context.LastAutofocusTimeUtc == null && !context.IsSmartAutofocusEnabled;

            context.AutopilotStatus = AutopilotStatus.Focusing;

            // With Smart Autofocus, start the sweep from the server's starting position instead of wherever
            // the focuser was left. With none (say a new filter the server has no data for), we sweep
            // from the current position.
            if (context.IsSmartAutofocusEnabled
                && context.LastAutofocusStatus?.StartingPosition is int startingPosition) {
                string source = context.LastAutofocusStatus.StartingPositionSource ?? "unknown";
                context.Log(
                    $"Autofocus: seeding focuser to server starting position {startingPosition} (source: {source}).");
                try {
                    // N.I.N.A.'s autofocus stops guiding when the profile says to, but only once its
                    // sweep begins, after this move. Stopped here, its own stop finds nothing running
                    // and StartGuiding restarts guiding instead.
                    await context.StopGuidingForFocusMoveAsync(startingPosition,
                        context.Observatory.Settings.ProfileService.ActiveProfile.FocuserSettings
                            .AutoFocusDisableGuiding,
                        cancellationToken);
                    await context.Focuser.MoveToPositionAsync(startingPosition, cancellationToken);
                } catch (OperationCanceledException) {
                    throw;
                } catch (Exception ex) {
                    context.LogWarning($"Could not move the focuser to {startingPosition} ({ex.Message}), so " +
                                       "sweeping from the current position.");
                }
            } else if (context.IsSmartAutofocusEnabled) {
                context.Log("Autofocus: no server starting position, so sweeping from current focuser position.");
            }

            context.Log("Running autofocus...");
            Stopwatch stopwatch = Stopwatch.StartNew();

            AutoFocusReport? report =
                await context.Focuser.RunAutofocusAsync(shouldUseDesignatedFilter, cancellationToken);
            if (report != null) {
                context.RecordAutofocus();
                FocusPoint focusPoint = report.CalculatedFocusPoint;
                context.Log($"Autofocus completed in {Context.FormatElapsed(stopwatch)}: " +
                            $"position {focusPoint.Position:F0}, HFR {focusPoint.Value:F2}.");
                return AutopilotStateResult.Completed;
            }

            context.LogWarning("Autofocus did not complete. Continuing without a refocus.");
            // Reported so the server waits out its cooldown and doesn't ask again next frame.
            await context.Observatory.AstraeusWebClient.PostAutofocusFailureAsync(
                context.FilterWheel?.GetCurrentFilter()?.Name, context.GetAmbientTemperature());
            return AutopilotStateResult.Failed;
        }
    }
}
