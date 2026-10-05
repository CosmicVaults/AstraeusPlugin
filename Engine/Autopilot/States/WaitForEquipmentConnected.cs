using CosmicVaults.NINA.Astraeus.Engine.Components;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    /// <summary>
    /// Blocks until the user connects a device the run requires. A null component counts as satisfied,
    /// since waiting on it would hang the machine all night.
    /// </summary>
    /// <param name="criticalHeadline">
    /// When set, entering the wait emails it at CRITICAL with criticalDetails, for a wait nobody would
    /// notice until morning. Keep it stable, since the server's alert cooldown keys on it.
    /// </param>
    internal sealed class WaitForEquipmentConnected(string name, Context context, IDeviceComponent? component,
        string? criticalHeadline = null, Func<object>? criticalDetails = null)
        : WaitForConditionState(name) {
        protected override Context RunContext => context;

        protected override AutopilotStatus Status => AutopilotStatus.WaitingEquipment;

        public override Task OnEntryAsync(CancellationToken cancellationToken) {
            if (criticalHeadline != null && component is { IsConnected: false }) {
                context.ReportCritical(criticalHeadline, criticalDetails?.Invoke());
            }
            return Task.CompletedTask;
        }

        protected override Task<bool> IsSatisfiedAsync(CancellationToken cancellationToken) {
            return Task.FromResult(component?.IsConnected ?? true);
        }

        protected override string WaitingMessage() {
            return $"Waiting for {component?.DisplayName ?? "device"} to be connected...";
        }
    }
}
