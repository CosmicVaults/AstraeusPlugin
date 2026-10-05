using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Equipment.MyRotator;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel.Equipment.Rotator;
using CosmicVaults.NINA.Astraeus.Engine.Utility;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Payloads;
using NINA.Core.Enum;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine {
    public class Rotator(
        Observatory observatory,
        IWebSocketBus webSocketBus,
        IRotatorMediator rotatorMediator)
        : DeviceComponent<IRotatorVM, IRotatorConsumer, RotatorInfo>(rotatorMediator, observatory, webSocketBus) {
        public override string DeviceType { get; } = "rotator";
        public override string DisplayName { get; } = "Rotator";
        public override bool IsBusy => LastInfo?.IsMoving == true;
        
        private CancellationTokenSource? _moveCancellationSource;

        // Read from the mediator, not LastInfo. LastInfo only holds a value during the plugin's own tick,
        // and the autopilot reads these from its own thread.
        public float? SkyPosition => IsConnected ? rotatorMediator.GetInfo().Position : null;
        public float? MechanicalPosition => IsConnected ? rotatorMediator.GetInfo().MechanicalPosition : null;
        public bool IsSynced => IsConnected && rotatorMediator.GetInfo().Synced;
        /// <summary>The mechanical angle the rotator would move to for a given sky angle.</summary>
        public float GetTargetPosition(float skyAngle) => rotatorMediator.GetTargetPosition(skyAngle);
        
        public override WsMessage? GetUpdateMessage() {
            if (LastInfo == null) {
                return null;
            }
            RotatorPayload payload = new RotatorPayload {
                DeviceId = DeviceId,
                IsReversed = LastInfo.Reverse,
                Position = DevicePayload.CleanValue(LastInfo.Position),
                MechanicalPosition = DevicePayload.CleanValue(LastInfo.MechanicalPosition),
                IsMoving = LastInfo.IsMoving,
                IsSynced = LastInfo.Synced,
                IsConnected = true
            };
            return new RotatorUpdate(payload);
        }

        public override WsMessage? GetDeviceStaticInfo() {
            RotatorInfo info =  rotatorMediator.GetInfo();
            RotatorPayload payload = new RotatorPayload {
                DeviceId = info.DeviceId,
                Name = info.Name,
                IsConnected = info.Connected,
                DisplayName = info.DisplayName,
                Description = info.Description,
                DriverInfo = info.DriverInfo,
                CanReverse = info.CanReverse,
                StepSize = DevicePayload.CleanValue(info.StepSize),
                
            };
            return new RotatorUpdate(payload);
        }

        protected override Task HandleCommandAsync(WsCommand command) {
            switch (command.Action) {
                case "reverse":
                    if (JsonFields.ReadBool(command.Payload, "reverse", out bool isReversed) != JsonFieldState.Valid) {
                        RejectMalformedCommandAsync(command, "reverse").ObserveFaults(LogCommandFault);
                        break;
                    }
                    SetReverseCommandAsync(command.Id, isReversed).ObserveFaults(LogCommandFault);
                    break;
                case "setMechanicalRange":
                    if (JsonFields.ReadString(command.Payload, "range", out string range) != JsonFieldState.Valid) {
                        RejectMalformedCommandAsync(command, "range").ObserveFaults(LogCommandFault);
                        break;
                    }
                    SetMechanicalRangeCommandAsync(command.Id, range).ObserveFaults(LogCommandFault);
                    break;
                case "move":
                    if (JsonFields.ReadDouble(command.Payload, "position", out double positionDegrees)
                        != JsonFieldState.Valid) {
                        RejectMalformedCommandAsync(command, "position").ObserveFaults(LogCommandFault);
                        break;
                    }
                    MoveCommandAsync(command.Id, (float)positionDegrees).ObserveFaults(LogCommandFault);
                    break;
            }
            
            return Task.CompletedTask;
        }

        /// <summary>
        /// Sets the reverse flag on the driver and in the profile, the way N.I.N.A.'s own rotator
        /// panel does. GetInfo returns a snapshot, so writing the flag there would change nothing.
        /// </summary>
        private async Task SetReverseCommandAsync(string id, bool isReversed) {
            if (rotatorMediator.GetInfo() is not { CanReverse: true }) {
                await BroadcastMessageReceivedAsync(id, false, "This rotator cannot reverse.");
                return;
            }
            if (rotatorMediator.GetDevice() is not IRotator rotator) {
                await BroadcastMessageReceivedAsync(id, false, "No rotator connected.");
                return;
            }
            rotator.Reverse = isReversed;
            Observatory.Settings.ProfileService.ActiveProfile.RotatorSettings.Reverse2 = isReversed;
            await BroadcastMessageReceivedAsync(id, true);
            Observatory.Log(isReversed ? "Rotator set to reverse" : "Rotator no longer set to reverse",
                DeviceType, LogCategory.Equipment);
        }

        ///////////// Autopilot operations /////////////

        /// <summary>
        /// Tells the rotator its current mechanical position is skyAngle, which sets the offset SkyPosition
        /// relies on. The angle comes from a plate solve's PositionAngle.
        /// </summary>
        public void SyncToSkyAngle(float skyAngle) {
            if (!IsConnected) return;
            rotatorMediator.Sync(skyAngle);
            Observatory.Log($"Rotator synced to sky angle {skyAngle:F2}°", DeviceType, LogCategory.Equipment);
        }

        /// <summary>Returns the adjusted target, or NaN if disconnected or cancelled.</summary>
        public async Task<float> MoveRelativeAsync(float degrees, CancellationToken cancellationToken) {
            if (!IsConnected) return float.NaN;
            using CancellationTokenSource moveCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, BeginOperation(ref _moveCancellationSource));
            Observatory.Log($"Rotator moving {degrees:F2}° relative", DeviceType, LogCategory.Equipment);
            return await rotatorMediator.MoveRelative(degrees, moveCancellationSource.Token);
        }

        /// <summary>Returns the adjusted target, or NaN if disconnected or cancelled.</summary>
        public async Task<float> MoveMechanicalAsync(float position, CancellationToken cancellationToken) {
            if (!IsConnected) return float.NaN;
            using CancellationTokenSource moveCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, BeginOperation(ref _moveCancellationSource));
            Observatory.Log($"Rotator moving to mechanical {position:F2}°", DeviceType, LogCategory.Equipment);
            float result = await rotatorMediator.MoveMechanical(position, moveCancellationSource.Token);
            Observatory.Log($"Rotator finished moving to mechanical {position:F2}°", DeviceType,
                LogCategory.Equipment);
            return result;
        }

        ///////////// WS command /////////////

        private async Task MoveCommandAsync(string id, float position) {
            await BroadcastMessageReceivedAsync(id, true);
            try {
                await MoveMechanicalAsync(position, CancellationToken.None);
            } catch (OperationCanceledException) {
                Observatory.Log("Rotator move aborted", DeviceType, LogCategory.Equipment);
            }
        }

        private async Task SetMechanicalRangeCommandAsync(string id, string? range) {
            RotatorRangeTypeEnum rangeType;
            switch (range) {
                case "full":
                    rangeType = RotatorRangeTypeEnum.FULL;
                    break;
                case "180":
                    rangeType = RotatorRangeTypeEnum.HALF;
                    break;
                case "90":
                    rangeType = RotatorRangeTypeEnum.QUARTER;
                    break;
                default:
                    await BroadcastMessageReceivedAsync(id, false, $"Unknown rotator range '{range}'.");
                    return;
            }
            Observatory.Settings.ProfileService.ActiveProfile.RotatorSettings.RangeType = rangeType;
            await BroadcastMessageReceivedAsync(id, true);
            Observatory.Log($"Rotator range set to {range}", DeviceType, LogCategory.Equipment);
        }
    }
}