using CosmicVaults.NINA.Astraeus.Engine.Components;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    /// <summary>
    /// When isRequired, a failure routes to the wait-for-manual-connection node. Otherwise the device is
    /// skipped.
    /// </summary>
    /// <param name="component">
    /// Null when the plugin has no component for this device type, handled like a device set to "None".
    /// </param>
    /// <param name="isInUse">
    /// Read when the state runs. False skips the device as if set to "None", for a device only needed
    /// under some settings (the flat panel when its handling isn't Ignore).
    /// </param>
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

            if (isRequired) {
                context.LogWarning($"Could not connect {component.DisplayName}, so waiting for manual connection.");
                return AutopilotStateResult.Failed;
            }
            context.Log($"Could not connect {component.DisplayName}, so skipped (optional).");
            return AutopilotStateResult.Completed;
        }
    }
}
