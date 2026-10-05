using NINA.Core.Model;
using NINA.Equipment.Equipment.MyFlatDevice;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.WPF.Base.ViewModel.Equipment.Telescope;
using CosmicVaults.NINA.Astraeus.Engine.Utility;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Payloads;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine {
    public class FlatPanel(
        Observatory observatory,
        IWebSocketBus webSocketBus,
        IFlatDeviceMediator flatMediator) :
        DeviceComponent<IFlatDeviceVM, IFlatDeviceConsumer, FlatDeviceInfo>(flatMediator, observatory, webSocketBus) {
        public override string DeviceType { get; } = "flat";
        public override string DisplayName { get; } = "Flat Panel";
        
        public override WsMessage? GetUpdateMessage() {
            if (LastInfo is null) {
                return null;
            }

            FlatPayload payload = new FlatPayload {
                DeviceId = DeviceId,
                IsLightOn = LastInfo.LightOn,
                Brightness = LastInfo.Brightness,
                CoverState = LastInfo.CoverState.ToString(),
                IsConnected = true
            };
            return new FlatUpdate(payload);
        }

        public override WsMessage? GetDeviceStaticInfo() {
            FlatDeviceInfo info = flatMediator.GetInfo();
            FlatPayload payload = new FlatPayload {
                DeviceId = info.DeviceId,
                Name = info.Name,
                IsConnected = info.Connected,
                DisplayName = info.DisplayName,
                Description = info.Description,
                DriverInfo = info.DriverInfo,
                MinBrightness = info.MinBrightness,
                MaxBrightness = info.MaxBrightness,
                SupportsOnOff = info.SupportsOnOff
            };
            return new FlatUpdate(payload);
        }

        protected override Task HandleCommandAsync(WsCommand command) {
            switch (command.Action) {
                case "toggleLight":
                    if (JsonFields.ReadBool(command.Payload, "isOn", out bool isLightOn) != JsonFieldState.Valid) {
                        RejectMalformedCommandAsync(command, "isOn").ObserveFaults(LogCommandFault);
                        break;
                    }
                    ToggleLightCommandAsync(command.Id, isLightOn).ObserveFaults(LogCommandFault);
                    break;
                case "setBrightness":
                    if (JsonFields.ReadInt(command.Payload, "brightness", out int brightness) != JsonFieldState.Valid) {
                        RejectMalformedCommandAsync(command, "brightness").ObserveFaults(LogCommandFault);
                        break;
                    }
                    SetBrightnessCommandAsync(command.Id, brightness).ObserveFaults(LogCommandFault);
                    break;
                case "open":
                    OpenCoverCommandAsync(command.Id).ObserveFaults(LogCommandFault);
                    break;
                case "close":
                    CloseCoverCommandAsync(command.Id).ObserveFaults(LogCommandFault);
                    break;
            }
            return Task.CompletedTask;
        }

        private CancellationTokenSource? _coverCancellationSource;

        private async Task ToggleLightCommandAsync(string id, bool isLightOn) {
            await BroadcastMessageReceivedAsync(id, true);
            await flatMediator.ToggleLight(isLightOn, NoProgress, ComponentToken);
            string state = isLightOn ? "on" : "off";
            Log($"Flat switched {state}");
        }

        private async Task SetBrightnessCommandAsync(string id, int brightness) {
            await BroadcastMessageReceivedAsync(id, true);
            await flatMediator.SetBrightness(brightness, NoProgress, ComponentToken);
            Log($"Brightness set to: {brightness}");
        }

        private async Task OpenCoverCommandAsync(string id) {
            await BroadcastMessageReceivedAsync(id, true);
            Log("Opening Flat Panel...");
            try {
                await flatMediator.OpenCover(NoProgress, BeginOperation(ref _coverCancellationSource));
                Log("Flat panel cover open.");
            } catch (OperationCanceledException) {
                Log("Flat panel open aborted.");
            }
        }

        private async Task CloseCoverCommandAsync(string id) {
            await BroadcastMessageReceivedAsync(id, true);
            Log("Closing Flat Panel...");
            try {
                await flatMediator.CloseCover(NoProgress, BeginOperation(ref _coverCancellationSource));
                Log("Flat panel cover closed.");
            } catch (OperationCanceledException) {
                Log("Flat panel close aborted.");
            }
        }

        ///////////// Autopilot Helpers /////////////

        /// <summary>
        /// Cap on a cover move or light switch. N.I.N.A. waits on a moving cover with no limit, and the
        /// dawn secure holds the roof open while it waits.
        /// </summary>
        public static readonly TimeSpan CoverMoveTimeout = TimeSpan.FromMinutes(2);

        // N.I.N.A. refreshes device info on its own poll, so right after a move it can lag the device.
        // The read-back gives it this long to catch up.
        private static readonly TimeSpan ReadBackGrace = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan ReadBackPollInterval = TimeSpan.FromSeconds(1);

        private FlatDeviceInfo? CurrentInfo => IsConnected ? flatMediator.GetInfo() : null;

        public bool HasCover =>
            CurrentInfo is { SupportsOpenClose: true } info && info.CoverState != CoverState.NotPresent;

        public bool IsCoverOpen() => CurrentInfo?.CoverState == CoverState.Open;

        /// <summary>Not the same as !IsCoverOpen. A cover that's moving, in error or unknown is neither.</summary>
        public bool IsCoverClosed() => CurrentInfo?.CoverState == CoverState.Closed;

        public bool IsLightOn => CurrentInfo is { SupportsOnOff: true, LightOn: true };

        public string CoverStateName => CurrentInfo?.CoverState.ToString() ?? "disconnected";

        /// <summary>
        /// True only if the cover reads open afterwards. Like the dome shutter, we go by the cover's state
        /// and not N.I.N.A.'s answer.
        /// </summary>
        public async Task<bool> OpenCoverAsync(CancellationToken cancellationToken) {
            if (!IsConnected) return false;
            if (IsCoverOpen()) return true;
            bool isFinished = await RunTimedAsync(token => flatMediator.OpenCover(NoProgress, token),
                "cover opening", cancellationToken);
            return isFinished && await ReadBackAsync(IsCoverOpen, cancellationToken);
        }

        /// <summary>True only if it reads closed afterwards.</summary>
        public async Task<bool> CloseCoverAsync(CancellationToken cancellationToken) {
            if (!IsConnected) return false;
            if (IsCoverClosed()) return true;
            bool isFinished = await RunTimedAsync(token => flatMediator.CloseCover(NoProgress, token),
                "cover closing", cancellationToken);
            return isFinished && await ReadBackAsync(IsCoverClosed, cancellationToken);
        }

        /// <summary>True if it reads off afterwards, or the panel has no switchable light.</summary>
        public async Task<bool> TurnLightOffAsync(CancellationToken cancellationToken) {
            if (!IsConnected) return false;
            if (!IsLightOn) return true;
            bool isFinished = await RunTimedAsync(token => flatMediator.ToggleLight(false, NoProgress, token),
                "light switching off", cancellationToken);
            return isFinished && await ReadBackAsync(() => !IsLightOn, cancellationToken);
        }

        // A caller's cancel still throws. Our own timeout or a driver exception returns false.
        private async Task<bool> RunTimedAsync(Func<CancellationToken, Task> action, string what,
            CancellationToken cancellationToken) {
            using CancellationTokenSource moveCancellationSource =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            moveCancellationSource.CancelAfter(CoverMoveTimeout);
            try {
                await action(moveCancellationSource.Token);
                return true;
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                throw;
            } catch (OperationCanceledException) {
                LogWarning($"Flat panel {what} did not finish within {CoverMoveTimeout.TotalMinutes:F0} minutes.");
                return false;
            } catch (Exception ex) {
                LogWarning($"Flat panel {what} failed: {ex.Message}");
                return false;
            }
        }

        private static async Task<bool> ReadBackAsync(Func<bool> isThere, CancellationToken cancellationToken) {
            DateTime deadline = DateTime.UtcNow + ReadBackGrace;
            while (!isThere()) {
                if (DateTime.UtcNow >= deadline) return false;
                await Task.Delay(ReadBackPollInterval, cancellationToken);
            }
            return true;
        }
    }
}