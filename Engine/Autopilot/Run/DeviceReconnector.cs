using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CosmicVaults.NINA.Astraeus.Engine.Components;
using CosmicVaults.NINA.Astraeus.Engine.Utility;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot
{
    /// <summary>
    /// Puts back a device that dropped mid-run: one reconnect attempt per cooldown, bounded, and a camera
    /// cooled again on its way back. Lives for the run, so the cooldowns carry across states.
    /// </summary>
    internal sealed class DeviceReconnector(Context context)
    {
        // When each device last had a reconnect attempt that failed, for the cooldown.
        private readonly Dictionary<IDeviceComponent, DateTime> _lastReconnectAttemptUtc = new();

        /// <summary>
        /// Whether device is connected, after one reconnect attempt if it dropped. Only puts back what
        /// the run connected, so nothing before the night's start-up is complete or with auto-connect off.
        /// A camera that comes back is cooled again, since it reconnects warm.
        /// </summary>
        public async Task<bool> EnsureConnectedAsync(IDeviceComponent? device, CancellationToken cancellationToken)
        {
            if (device == null) return false;
            if (device.IsConnected) return true;
            // No default device means it isn't used here, so there's nothing to reconnect or lose.
            if (!context.Night.IsStartupComplete || !device.HasDefaultDevice) return false;
            if (Context.IsManualDevice(device.DefaultDevice)) return false;   // The night started without a manual default
            DateTime now = context.UtcNow;
            if (_lastReconnectAttemptUtc.TryGetValue(device, out DateTime lastAttempt)
                && now - lastAttempt < AutopilotConstants.ReconnectCooldown)
            {
                return false;
            }
            _lastReconnectAttemptUtc[device] = now;

            if (!context.ShouldAutoConnectEquipment)
            {
                ReportDeviceLost(device);
                return false;
            }

            context.LogWarning($"{device.DisplayName} is not connected. Reconnecting to {device.DefaultDevice}.");
            if (!await ConnectWithTimeoutAsync(device, cancellationToken))
            {
                context.LogWarning($"{device.DisplayName} did not reconnect. The next attempt is in " +
                                   $"{AutopilotConstants.ReconnectCooldown.TotalMinutes:F0} minutes at the earliest.");
                ReportDeviceLost(device);
                return false;
            }
            _lastReconnectAttemptUtc.Remove(device);
            context.Log($"{device.DisplayName} reconnected.");

            if (context.IsCameraCoolingEnabled && context.Camera is { } camera && ReferenceEquals(device, camera))
            {
                context.Log("Cooling the reconnected camera again.");
                // The camera logs why when it returns false, as it does for the dusk cool-down.
                if (!await CoolCameraForNightAsync(camera, cancellationToken))
                {
                    context.Log("Camera is not at its target temperature. Carrying on regardless.");
                }
            }
            return true;
        }

        /// <summary>
        /// Cools the camera to CameraCoolingTemperature and returns whether it got there. A colder set
        /// point is someone's choice, maybe a start-up sequence's, so we keep it and warn.
        /// </summary>
        public async Task<bool> CoolCameraForNightAsync(Camera camera, CancellationToken cancellationToken)
        {
            double wanted = context.CameraCoolingTemperature;
            double target = wanted;
            if (camera.CoolerSetPoint is double setPoint && setPoint < wanted)
            {
                context.LogWarning(
                    $"The cooler is already at {setPoint:F1}°C, colder than the autopilot's {wanted:F1}°C, " +
                    $"so tonight's frames are taken at {setPoint:F1}°C.");
                target = setPoint;
            }
            return await camera.CoolCameraAsync(target, cancellationToken);
        }

        // One connect, bounded by the reconnect timeout and the caller's token. If it outlasts either we
        // press N.I.N.A.'s own Cancel on it, unless it waits on N.I.N.A.'s prompt. A timeout returns
        // false and a cancellation throws.
        public async Task<bool> ConnectWithTimeoutAsync(IDeviceComponent device, CancellationToken cancellationToken)
        {
            Task<bool> connecting = device.TryConnectDefaultDeviceAsync();
            Task firstToFinish = await Task.WhenAny(connecting,
                Task.Delay(AutopilotConstants.ReconnectTimeout, context.Observatory.Clock, cancellationToken)
                    .ContinueWith(_ => { }, TaskScheduler.Default));
            if (firstToFinish == connecting)
            {
                try
                {
                    return await connecting;
                }
                catch (Exception ex)
                {
                    context.LogWarning($"Connecting {device.DisplayName} failed: {ex.Message}");
                    return false;
                }
            }

            // Observed here so a connect that fails after we stop waiting is not an unobserved fault.
            connecting.ObserveFaults(exception =>
                context.LogDebug($"Connecting {device.DisplayName} failed after the wait: {exception.Message}"));

            // Cancel can't close the prompt, and the answer is the user's.
            string? prompt = device.FindOpenConnectPrompt();
            if (prompt is null) device.TryCancelConnect();
            cancellationToken.ThrowIfCancellationRequested();
            if (prompt is not null)
            {
                context.LogWarning($"Connecting {device.DisplayName} is waiting on N.I.N.A.'s \"{prompt}\" window. " +
                                   "It is left open for somebody to answer in N.I.N.A.");
                return false;
            }
            context.LogWarning($"Connecting {device.DisplayName} did not finish within " +
                               $"{AutopilotConstants.ReconnectTimeout.TotalSeconds:F0} s and was cancelled.");
            return false;
        }

        // Otherwise a device lost mid-night only shows as frames failing one after another. The
        // headline is fixed per device, so the server's cooldown absorbs a flapping connection.
        private void ReportDeviceLost(IDeviceComponent device)
        {
            string reconnect = context.ShouldAutoConnectEquipment ? "did not reconnect" : "auto-connect is off";
            context.ReportCritical($"Autopilot: {device.DisplayName} is not connected.",
                new { device = device.DisplayName, state = "disconnected", reconnect });
        }
    }
}
