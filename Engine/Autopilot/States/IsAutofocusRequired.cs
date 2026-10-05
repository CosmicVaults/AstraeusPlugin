using System;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    /// <summary>
    /// With Smart Autofocus on only the server decides, and its verdict is kept for the next states. If the
    /// server can't be reached, nothing runs or moves this frame.
    /// </summary>
    internal sealed class IsAutofocusRequired(string name, Context context) : DecisionState(name) {
        // Smart Autofocus asks the server, and the roof is open for the round trip.
        protected internal override bool ShouldWatchSafety => true;

        protected override async Task<bool> DecideAsync(CancellationToken cancellationToken) {
            // Cleared so RunAutofocus and ApplyRecommendedFocus never act on a stale server response.
            // It's set again below only when the server answers.
            context.LastAutofocusStatus = null;

            if (!context.IsAutofocusEnabled)
                return false;

            await context.EnsureConnectedAsync(context.Focuser, cancellationToken);

            if (context.Focuser is not { IsConnected: true } || context.Camera is not { IsConnected: true })
                return false;

            if (context.IsSmartAutofocusEnabled) {
                string? filter = context.FilterWheel?.GetCurrentFilter()?.Name;
                double? ambientTemperature = context.GetAmbientTemperature();
                AutofocusStatusResponse? status = await context.Observatory.AstraeusWebClient.GetAutofocusStatusAsync(
                    filter, ambientTemperature, context.CurrentTarget?.ImageId);
                if (status == null) {
                    context.LogWarning("Smart Autofocus: status unavailable, so skipping autofocus for this frame.");
                    return false;
                }
                context.LastAutofocusStatus = status;
                context.Log(status.NeedsAutofocus
                    ? $"Autofocus due (server): {status.Reason}."
                    : $"Autofocus not required (server): {status.Reason}.");
                return status.NeedsAutofocus;
            }

            if (context.LastAutofocusTimeUtc == null) {
                context.Log("Autofocus due: first focus of the night.");
                return true;
            }

            // Focuser probe, falling back to the weather sensor.
            if (context.ShouldAutofocusOnTemperatureChange
                && context.LastAutofocusTemperature is double lastTemperature
                && context.GetAmbientTemperature() is double temperature) {
                double temperatureChange = Math.Abs(temperature - lastTemperature);
                if (temperatureChange >= context.AutofocusTemperatureThreshold) {
                    context.Log($"Autofocus due: temperature moved {temperatureChange:F1}°C " +
                                $"(>= {context.AutofocusTemperatureThreshold:F1}°C).");
                    return true;
                }
            }

            if (context.ShouldAutofocusOnFilterChange) {
                string? currentFilter = context.FilterWheel?.GetCurrentFilter()?.Name;
                if (!string.IsNullOrEmpty(currentFilter) && currentFilter != context.LastAutofocusFilter) {
                    context.Log($"Autofocus due: filter changed to '{currentFilter}'.");
                    return true;
                }
            }

            if (context.ShouldAutofocusOnTimeInterval) {
                TimeSpan elapsed = DateTime.UtcNow - context.LastAutofocusTimeUtc.Value;
                if (elapsed >= TimeSpan.FromMinutes(context.AutofocusIntervalMinutes)) {
                    context.Log($"Autofocus due: {elapsed.TotalMinutes:F0} min since last focus " +
                                $"(>= {context.AutofocusIntervalMinutes} min).");
                    return true;
                }
            }

            return false;
        }
    }
}
