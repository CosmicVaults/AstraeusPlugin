using NINA.Equipment.Equipment.MySafetyMonitor;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Payloads;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine {
    public class SafetyMonitor(
        Observatory observatory,
        IWebSocketBus webSocketBus,
        ISafetyMonitorMediator mediator)
        : DeviceComponent<ISafetyMonitorVM, ISafetyMonitorConsumer, SafetyMonitorInfo>(mediator, observatory,
            webSocketBus) {
        public override string DeviceType { get; } = "safetymonitor";
        public override string DisplayName { get; } = "Safety Monitor";

        public string DeviceName => (IsConnected ? Mediator.GetInfo().Name : null) ?? DisplayName;

        public override WsMessage? GetUpdateMessage() {
            if (!IsConnected || LastInfo == null)
                return null;
            SafetyMonitorPayload payload = new SafetyMonitorPayload {
                DeviceId = DeviceId,
                IsSafe = LastInfo.IsSafe,
                IsConnected = true
            };
            return new SafetyMonitorUpdate(payload, "dashboard");
        }

        public override WsMessage? GetDeviceStaticInfo() {
            SafetyMonitorInfo info = mediator.GetInfo();
            SafetyMonitorPayload payload = new SafetyMonitorPayload {
                DeviceId = info.DeviceId,
                Name = info.Name,
                IsConnected = info.Connected,
                DisplayName = info.DisplayName,
                Description = info.Description,
                DriverInfo = info.DriverInfo,
            };
            return new SafetyMonitorUpdate(payload);
        }

        /// <summary>
        /// False when the monitor is disconnected or can't be read, so the roof and autopilot watchers
        /// don't lean on N.I.N.A. clearing IsSafe on disconnect. A read error counts as unsafe instead
        /// of escaping the watcher as a fault.
        /// </summary>
        public bool IsSafe() {
            try {
                SafetyMonitorInfo info = Mediator.GetInfo();
                return info.Connected && info.IsSafe;
            } catch (Exception ex) {
                LogDebug($"Could not read the safety monitor, so treating conditions as unsafe: {ex.Message}");
                return false;
            }
        }

        protected override Task HandleCommandAsync(WsCommand command) {
            return Task.CompletedTask;
        }
    }
}