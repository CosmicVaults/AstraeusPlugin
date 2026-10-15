using CosmicVaults.NINA.Astraeus.Engine.Components;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    /// <summary>
    /// When isRequired, a failure routes to the wait-for-manual-connection node. Otherwise the device is
    /// skipped. Either way a failed auto-connect sends a critical alert.
    /// </summary>
    internal sealed class ConnectEquipment(string name, Context context, IDeviceComponent? component, bool isRequired,
        Func<bool>? isInUse = null)
        : StateBase(name) {

        private bool IsInUse => isInUse?.Invoke() ?? true;

        public override Task OnEntryAsync(CancellationToken cancellationToken) {
            if (component == null || !IsInUse) return Task.CompletedTask;
            context.AutopilotStatus = AutopilotStatus.WaitingEquipment;
            context.Log($"Ensuring {component.DisplayName} is connected...");
            return Task.CompletedTask;
        }

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {
            if (!IsInUse) {
                context.LogDebug($"{Name}: not in use under the current settings, so skipped.");
                return AutopilotStateResult.Completed;
            }

            if (component == null) {
                if (isRequired) {
                    context.LogWarning($"{Name}: no device component available, so waiting for manual connection.");
                    return AutopilotStateResult.Failed;
                }
                context.LogDebug($"{Name}: no device component available, so skipped (optional).");
                return AutopilotStateResult.Completed;
            }

            // A manual device would hold the night at its first change, so it makes way for the default.
            if (Context.IsManualDevice(component.ConnectedDeviceId)) {
                context.LogWarning($"{component.DisplayName} is N.I.N.A.'s manual one, which waits for someone " +
                                   "at the PC on every change, so disconnecting it.");
                await component.TryDisconnectAsync();
            }

            if (component.IsConnected) {
                // Whatever is connected is used as found, since somebody connected it on purpose. We warn
                // so a simulator left connected instead of the real roof isn't a silent surprise.
                if (component.HasDefaultDevice && !component.IsConnectedToDevice(component.DefaultDevice!)) {
                    context.LogWarning(
                        $"{component.DisplayName} is connected to '{component.ConnectedDeviceId}', not the " +
                        $"configured '{component.DefaultDevice}', so using the connected device.");
                } else {
                    context.LogDebug($"{component.DisplayName} already connected.");
                }
                return AutopilotStateResult.Completed;
            }

            if (!component.HasDefaultDevice) {
                if (isRequired) {
                    context.LogWarning($"No {component.DisplayName} selected, so waiting for manual connection.");
                    return AutopilotStateResult.Failed;
                }
                context.Log($"{component.DisplayName} not in use (None), so skipped.");
                return AutopilotStateResult.Completed;
            }

            // Both manual devices are optional, so the night carries on without one.
            if (Context.IsManualDevice(component.DefaultDevice)) {
                context.LogWarning($"The default {component.DisplayName} is N.I.N.A.'s manual one, so the night " +
                                   "runs without it.");
                return AutopilotStateResult.Completed;
            }

            if (!context.ShouldAutoConnectEquipment) {
                if (isRequired) {
                    context.LogWarning($"{component.DisplayName} is not connected and auto-connect is off, so " +
                                       "waiting for manual connection.");
                    return AutopilotStateResult.Failed;
                }
                context.Log($"{component.DisplayName} is not connected and auto-connect is off, so skipped (optional).");
                return AutopilotStateResult.Completed;
            }

            if (await context.ConnectWithTimeoutAsync(component, cancellationToken)) {
                context.Log($"Connected {component.DisplayName} to {component.DefaultDevice}.");
                return AutopilotStateResult.Completed;
            }

            string next = isRequired ? "waiting for a manual connection" : "carrying on without it";
            context.ReportCritical($"Autopilot: {component.DisplayName} did not connect, so {next}.",
                new {
                    device = component.DisplayName,
                    default_device = component.DefaultDevice,
                    blocked_by = component.FindOpenConnectPrompt()
                });
            return isRequired ? AutopilotStateResult.Failed : AutopilotStateResult.Completed;
        }
    }
}
