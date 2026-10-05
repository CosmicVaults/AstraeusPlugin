using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.WPF.Base.Utility.AutoFocus;
using NINA.WPF.Base.ViewModel.Equipment.Focuser;
using CosmicVaults.NINA.Astraeus.Engine.Utility;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Payloads;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace CosmicVaults.NINA.Astraeus.Engine {
    public class Focuser(
        Observatory observatory,
        IWebSocketBus webSocketBus,
        IFocuserMediator focuserMediator,
        IAutoFocusVMFactory? autoFocusFactory,
        IImageHistoryVM? imageHistory)
        : DeviceComponent<IFocuserVM, IFocuserConsumer, FocuserInfo>(focuserMediator, observatory, webSocketBus) {
        public override string DeviceType { get; } = "focuser";
        public override string DisplayName { get; } = "Focuser";
        public override bool IsBusy => LastInfo?.IsMoving == true || LastInfo?.IsSettling == true;

        private FilterWheel? _filterWheel;

        public override async Task Start() {
            await base.Start();
            _filterWheel = Observatory.GetComponent<FilterWheel>();
        }

        public override WsMessage? GetUpdateMessage() {
            if (LastInfo == null) {
                return null;
            }

            FocuserPayload payload = new FocuserPayload {
                DeviceId = DeviceId,
                Name = LastInfo.DisplayName,
                Temperature = DevicePayload.CleanValue(LastInfo.Temperature),
                Position = DevicePayload.CleanValue(LastInfo.Position),
                StepSize = DevicePayload.CleanValue(LastInfo.StepSize),
                IsTemperatureCompensationOn = LastInfo.TempComp,
                IsMoving = LastInfo.IsMoving,
                IsSettling = LastInfo.IsSettling,
                IsConnected = true
            };
            return new FocuserUpdate(payload);
        }

        public override WsMessage? GetDeviceStaticInfo() {
            FocuserInfo info = focuserMediator.GetInfo();
            FocuserPayload payload = new FocuserPayload {
                DeviceId = DeviceId,
                Name = info.Name,
                IsConnected = info.Connected,
                DisplayName = info.DisplayName,
                Description = info.Description,
                DriverInfo = info.DriverInfo,
                Temperature = DevicePayload.CleanValue(info.Temperature),
                Position = DevicePayload.CleanValue(info.Position),
                StepSize = DevicePayload.CleanValue(info.StepSize),
                IsTempCompAvailable = info.TempCompAvailable
            };
            return new FocuserUpdate(payload);
        }

        protected override Task HandleCommandAsync(WsCommand command) {
            switch (command.Action) {
                case "move":
                    if (JsonFields.ReadInt(command.Payload, "position", out int position) != JsonFieldState.Valid) {
                        RejectMalformedCommandAsync(command, "position").ObserveFaults(LogCommandFault);
                        break;
                    }
                    MoveCommandAsync(command.Id, position).ObserveFaults(LogCommandFault);
                    break;
                case "toggleTemp":
                    if (JsonFields.ReadBool(command.Payload, "toggle", out bool isTemperatureCompensationOn)
                        != JsonFieldState.Valid) {
                        RejectMalformedCommandAsync(command, "toggle").ObserveFaults(LogCommandFault);
                        break;
                    }
                    SetTemperatureCompensationCommandAsync(command.Id, isTemperatureCompensationOn)
                        .ObserveFaults(LogCommandFault);
                    break;
                case "nudge":
                    if (JsonFields.ReadString(command.Payload, "direction", out string direction)
                        != JsonFieldState.Valid) {
                        RejectMalformedCommandAsync(command, "direction").ObserveFaults(LogCommandFault);
                        break;
                    }
                    NudgeCommandAsync(command.Id, direction).ObserveFaults(LogCommandFault);
                    break;
            }

            return Task.CompletedTask;
        }

        private CancellationTokenSource? _moveCancellationSource;

        private async Task MoveCommandAsync(string id, int position) {
            await BroadcastMessageReceivedAsync(id, true);
            try {
                await MoveToPositionAsync(position, BeginOperation(ref _moveCancellationSource));
            } catch (OperationCanceledException) {
                Log("Focuser move aborted.");
            }
        }

        public int? Position => focuserMediator.GetInfo() is { Connected: true } info ? info.Position : null;

        /// <summary>Moves the focuser to an absolute position.</summary>
        public async Task MoveToPositionAsync(int position, CancellationToken cancellationToken) {
            Log($"Moving Focuser to {position}...");
            await focuserMediator.MoveFocuser(position, cancellationToken);
            Log("Focuser moved.");
        }

        private async Task NudgeCommandAsync(string id, string direction) {
            if (!TryGetDeviceViewModel<FocuserVM>(out FocuserVM? focuserViewModel)) {
                LogWarning("Unable to move the focuser: N.I.N.A.'s focuser controls are not available.");
                await BroadcastMessageReceivedAsync(id, false, "N.I.N.A.'s focuser controls are not available");
                return;
            }
            ICommand? nudgeCommand = direction switch {
                "small_in" => focuserViewModel.MoveFocuserInSmallCommand,
                "small_out" => focuserViewModel.MoveFocuserOutSmallCommand,
                "large_in" => focuserViewModel.MoveFocuserInLargeCommand,
                "large_out" => focuserViewModel.MoveFocuserOutLargeCommand,
                _ => null
            };
            if (nudgeCommand == null) {
                await BroadcastMessageReceivedAsync(id, false, $"Unknown nudge direction '{direction}'");
                return;
            }
            await BroadcastMessageReceivedAsync(id, true);
            InvokeOnUiThread(() => nudgeCommand.Execute(null));
        }

        private async Task SetTemperatureCompensationCommandAsync(string id, bool isTemperatureCompensationOn) {
            await BroadcastMessageReceivedAsync(id, true);
            focuserMediator.ToggleTempComp(isTemperatureCompensationOn);
            string onString = isTemperatureCompensationOn ? "On" : "Off";
            Log($"Temperature Compensation is now {onString}");
        }

        /// <summary>Current focuser temperature in °C, or null when the focuser reports no temperature.</summary>
        public double? GetTemperature() {
            double temperature = focuserMediator.GetInfo()?.Temperature ?? double.NaN;
            return double.IsNaN(temperature) ? null : temperature;
        }

        /// <summary>
        /// The report's CalculatedFocusPoint holds the final position and HFR. Cancellation is rethrown
        /// and any other failure returns null.
        /// </summary>
        /// <param name="shouldUseDesignatedAutofocusFilter">
        /// True focuses on the profile's autofocus filter when filter offsets are in use. Smart Autofocus
        /// needs false, because the server keys its model on the current filter.
        /// </param>
        public async Task<AutoFocusReport?> RunAutofocusAsync(bool shouldUseDesignatedAutofocusFilter,
            CancellationToken cancellationToken) {
            if (autoFocusFactory == null) {
                Observatory.LogWarning("Autofocus unavailable (N.I.N.A. autofocus factory not present).", DeviceType,
                    LogCategory.Equipment);
                return null;
            }

            FilterInfo? filter = ResolveAutofocusFilter(shouldUseDesignatedAutofocusFilter);
            IProgress<ApplicationStatus> progress = new Progress<ApplicationStatus>(_ => { });
            try {
                IAutoFocusVM autoFocus = autoFocusFactory.Create();
                AutoFocusReport? report;
                // So each exposure of the run reaches the dashboard's latest-frame card, labelled.
                using (Observatory.FrameLabel.Begin(PreviewSource.Autofocus))
                    report = await autoFocus.StartAutoFocus(filter, cancellationToken, progress);
                if (report == null) return null;
                imageHistory?.AppendAutoFocusPoint(report);

                // Every autopilot run is reported, so the server's model has history by the time Smart
                // Autofocus is switched on. The client logs its own failures, and a bad push mustn't
                // affect the focus result we return.
                bool wasPosted = await Observatory.AstraeusWebClient.PostAutofocusReportAsync(report);

                // With Smart Autofocus on, the new run may have changed the offset table, so pull it
                // into NINA's own filter offsets.
                if (wasPosted && Observatory.Settings.IsSmartAutofocusEnabled()
                    && Observatory.TryGetComponent<Settings>(out Settings? settings)) {
                    await settings.PullAutofocusOffsetsAsync();
                }

                return report;
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                Observatory.LogError($"Autofocus failed: {ex.Message}", DeviceType, LogCategory.Equipment);
                return null;
            }
        }

        /// <summary>
        /// The current filter is looked up in the profile so it carries its AF exposure time and offset.
        /// Null is fine, and autofocus then uses the global focuser settings.
        /// </summary>
        private FilterInfo? ResolveAutofocusFilter(bool shouldUseDesignatedAutofocusFilter) {
            IProfile profile = Observatory.Settings.ProfileService.ActiveProfile;

            if (shouldUseDesignatedAutofocusFilter && profile.FocuserSettings.UseFilterWheelOffsets) {
                FilterInfo? designatedFilter = profile.FilterWheelSettings.FilterWheelFilters
                    .FirstOrDefault(candidate => candidate.AutoFocusFilter);
                if (designatedFilter != null) return designatedFilter;
            }

            if (_filterWheel?.GetCurrentFilter() is { } selectedFilter) {
                return profile.FilterWheelSettings.FilterWheelFilters
                    .FirstOrDefault(candidate => candidate.Position == selectedFilter.Position) ?? selectedFilter;
            }

            return null;
        }
    }
}