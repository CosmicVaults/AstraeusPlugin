using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyDome;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.WPF.Base.ViewModel.Equipment.Dome;
using NINA.WPF.Base.ViewModel.Equipment.Telescope;
using CosmicVaults.NINA.Astraeus.Engine.Utility;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Payloads;
using System;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine {
    public class Dome(
        Observatory observatory,
        IWebSocketBus webSocketBus,
        IDomeMediator domeMediator) :
        DeviceComponent<IDomeVM, IDomeConsumer, DomeInfo>(domeMediator, observatory, webSocketBus) {
        public override string DeviceType { get; } = "dome";
        public override string DisplayName { get; } = "Dome";
        
        public override bool IsBusy => LastInfo?.Slewing == true;

        private SafetyMonitor? _safetyMonitor;

        private CancellationTokenSource? _azimuthCancellationSource;
        private CancellationTokenSource? _shutterCancellationSource;

        /// <summary>Cancels an in-flight azimuth slew/park/home/sync.</summary>
        public void AbortSlew() => AbortOperation(ref _azimuthCancellationSource);

        public void AbortShutter() => AbortOperation(ref _shutterCancellationSource);

        public override async Task Start() {
            await base.Start();
            if (!Observatory.TryGetComponent(out _safetyMonitor)) {
                LogError("Unable to get access to Safety Monitor.");
            }
            
            domeMediator.Closed += OnDomeClosed;
            domeMediator.Opened += OnDomeOpened;
            domeMediator.Parked += OnDomeParked;
        }

        public override Task Destroy() {
            domeMediator.Closed -= OnDomeClosed;
            domeMediator.Opened -= OnDomeOpened;
            domeMediator.Parked -= OnDomeParked;
            return base.Destroy();
        }

        protected override Task HandleCommandAsync(WsCommand command) {
            switch (command.Action) {
                case "open":
                    OpenShutterCommandAsync(command.Id).ObserveFaults(LogCommandFault);
                    break;
                case "close":
                    CloseShutterCommandAsync(command.Id).ObserveFaults(LogCommandFault);
                    break;
                case "stop":
                    StopCommand(command.Id);
                    break;
                case "park":
                    ParkCommandAsync(command.Id).ObserveFaults(LogCommandFault);
                    break;
                case "sync":
                    SyncCommandAsync(command.Id).ObserveFaults(LogCommandFault);
                    break;
                case "home":
                    HomeCommandAsync(command.Id).ObserveFaults(LogCommandFault);
                    break;
                case "slew":
                    if (JsonFields.ReadDouble(command.Payload, "data", out double azimuthDegrees)
                        != JsonFieldState.Valid) {
                        RejectMalformedCommandAsync(command, "data").ObserveFaults(LogCommandFault);
                        break;
                    }
                    SlewCommandAsync(command.Id, azimuthDegrees).ObserveFaults(LogCommandFault);
                    break;
                case "clockwise_nudge":
                    if (JsonFields.ReadDouble(command.Payload, "data", out double clockwiseDegrees)
                        != JsonFieldState.Valid) {
                        RejectMalformedCommandAsync(command, "data").ObserveFaults(LogCommandFault);
                        break;
                    }
                    MoveClockwiseCommand(command.Id, clockwiseDegrees);
                    break;
                case "anticlockwise_nudge":
                    if (JsonFields.ReadDouble(command.Payload, "data", out double anticlockwiseDegrees)
                        != JsonFieldState.Valid) {
                        RejectMalformedCommandAsync(command, "data").ObserveFaults(LogCommandFault);
                        break;
                    }
                    MoveAntiClockwiseCommand(command.Id, anticlockwiseDegrees);
                    break;
                case "set_park":
                    SetParkCommandAsync(command.Id).ObserveFaults(LogCommandFault);
                    break;
                case "set_follow":
                    if (JsonFields.ReadBool(command.Payload, "data", out bool isFollowing) != JsonFieldState.Valid) {
                        RejectMalformedCommandAsync(command, "data").ObserveFaults(LogCommandFault);
                        break;
                    }
                    SetFollowCommandAsync(command.Id, isFollowing).ObserveFaults(LogCommandFault);
                    break;
                default:
                    Log($"Unhandled Command: '{command.Action}' in Dome");
                    break;
            }
            return Task.CompletedTask;
        }
        
        public override WsMessage? GetUpdateMessage() {
            if (!IsConnected || LastInfo == null)
                return null;
            
            DomePayload payload = new DomePayload {
                DeviceId = DeviceId,
                ShutterStatus = LastInfo.ShutterStatus.ToString(),
                IsAtHome = LastInfo.AtHome,
                IsParked = LastInfo.AtPark,
                IsDriverFollowing = LastInfo.ApplicationFollowing,
                IsSlewing = LastInfo.Slewing,
                Azimuth = DevicePayload.CleanValue(LastInfo.Azimuth),
                IsConnected = IsConnected
            };
            return new DomeUpdate(payload, "dashboard");
        }

        public override WsMessage? GetDeviceStaticInfo() {
            DomeInfo info = domeMediator.GetInfo();
            DomePayload payload = new DomePayload {
                DeviceId = DeviceId,
                Name = info.Name,
                IsConnected = info.Connected,
                DisplayName = info.DisplayName,
                Description = info.Description,
                DriverInfo = info.DriverInfo,
                DriverVersion = info.DriverVersion,
                CanSetShutter = info.CanSetShutter,
                CanSetPark = info.CanSetPark,
                CanSyncAzimuth = info.CanSyncAzimuth,
                CanSetAzimuth = info.CanSetAzimuth,
                CanDriverFollow = info.DriverCanFollow,
                CanFindHome = info.CanFindHome,
                CanPark = info.CanPark,
                IsAtHome = info.AtHome,
                IsParked = info.AtPark,
                Azimuth = DevicePayload.CleanValue(info.Azimuth),
                ShutterStatus = info.ShutterStatus.ToString()
            };
            return new DomeUpdate(payload);
        }

        ///////////// Events /////////////
        
        private Task OnDomeOpened(object sender, EventArgs eventArgs) {
            _ = PostShutterStatusAsync("ShutterOpen");
            ReportRoofEvent(NotificationKind.RoofOpened, "Roof opened.");
            return Task.CompletedTask;
        }

        private Task OnDomeClosed(object sender, EventArgs eventArgs) {
            _ = PostShutterStatusAsync("ShutterClosed");
            ReportRoofEvent(NotificationKind.RoofClosed, "Roof closed.");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Emails the observers that the roof moved, whoever moved it. The message is fixed because the
        /// server keys its cooldown on the first line, so a flapping roof sends one open and one close.
        /// </summary>
        private void ReportRoofEvent(string kind, string message) {
            // Who moved it is the first thing anyone asks at 2am.
            string initiator = Observatory.TryGetComponent<Autopilot.Autopilot>(out Autopilot.Autopilot? autopilot)
                               && autopilot.IsRunning ? "autopilot" : "manual";
            DomeInfo domeInfo = domeMediator.GetInfo();
            var details = new {
                device = domeInfo.Name ?? DisplayName,
                azimuth = domeInfo.Azimuth.ToString("F1", CultureInfo.InvariantCulture),
                initiator,
                safe = _safetyMonitor?.IsSafe() == true ? "yes" : "no",
            };
            Observatory.ReportEvent(kind, message, details, Guid.NewGuid().ToString("N"));
        }
        
        private Task OnDomeParked(object sender, EventArgs eventArgs) {
            _ = PostParkedStatusAsync(true);
            return Task.CompletedTask;
        }
        
        ///////////// Roof watchdog /////////////
        // Like Mount's limit backstop, this watches the roof when the autopilot isn't. It logs a
        // CRITICAL line and emails once per breach, and never moves anything. With the autopilot off,
        // someone may be using the open roof. N.I.N.A.'s own "Close on unsafe" setting is what closes
        // an idle roof, and the alert says whether that's on.

        // Set once the open-and-unsafe alert is sent. Cleared when conditions go safe or the roof
        // closes, so the alert goes out once per breach.
        private bool _isReportedOpenWhileUnsafe;

        // The same for a roof still open after dawn, once per morning.
        private bool _isReportedOpenAfterDawn;

        // In LateUpdate like Mount's backstop, because Observatory.Update returns early when signed
        // out or the socket is down. A safety check has to keep working when the server is unreachable.
        public override Task LateUpdate() {
            try {
                WatchRoof();
            } catch (Exception ex) {
                // One bad pass must not stop the watchdog or the loop.
                LogDebug($"Roof watchdog skipped a pass: {ex.Message}");
            }
            return base.LateUpdate();
        }

        private void WatchRoof() {
            if (!IsConnected) return;
            DomeInfo info = domeMediator.GetInfo();
            // A dome with no shutter, or one reporting an error, is not an open roof to alert about.
            bool isRoofOpen = info.ShutterStatus is ShutterState.ShutterOpen or ShutterState.ShutterOpening;
            if (!isRoofOpen) {
                _isReportedOpenWhileUnsafe = false;
                _isReportedOpenAfterDawn = false;
                return;
            }

            // Stand down while the autopilot is working the roof, since it has its own five-second
            // watcher and can act on it. Merely running isn't enough. Nothing watches the roof while
            // it waits for dusk, connects devices or runs the shutdown sequence.
            if (Observatory.TryGetComponent<Autopilot.Autopilot>(out Autopilot.Autopilot? autopilot)
                && autopilot.IsRoofUnderControl) {
                return;
            }

            bool isUnsafe = _safetyMonitor is { IsConnected: true } monitor && !monitor.IsSafe();
            if (!isUnsafe) {
                _isReportedOpenWhileUnsafe = false;
            } else if (!_isReportedOpenWhileUnsafe) {
                _isReportedOpenWhileUnsafe = true;
                ReportRoofOpen("Roof is open and conditions are unsafe, and the autopilot is not watching it.", info);
            }

            if (!Observatory.IsAfterDawn()) {
                _isReportedOpenAfterDawn = false;
            } else if (!_isReportedOpenAfterDawn) {
                _isReportedOpenAfterDawn = true;
                ReportRoofOpen("Roof is still open after dawn, and the autopilot is not watching it.", info);
            }
        }

        /// <summary>
        /// Logs CRITICAL and emails the observers. The headline stays fixed because the server keys its
        /// alert cooldown on the first line, so the facts go in the details and the log line.
        /// </summary>
        private void ReportRoofOpen(string headline, DomeInfo info) {
            string mount = "disconnected";
            if (Observatory.TryGetComponent<Mount>(out Mount? mountComponent) && mountComponent.IsConnected) {
                mount = mountComponent.IsParked ? "parked" : "unparked";
            }
            bool isCloseOnUnsafeEnabled = Observatory.Settings.ProfileService.ActiveProfile.DomeSettings.CloseOnUnsafe;
            string shutter = info.ShutterStatus.ToString();
            Observatory.LogCritical(
                $"{headline} Shutter {shutter}, mount {mount}, N.I.N.A. 'Close on unsafe' is " +
                $"{(isCloseOnUnsafeEnabled ? "on" : "off")}. Astraeus will not close it.",
                DeviceType, DefaultLogCategory);
            Observatory.ReportEvent(NotificationKind.CriticalError, headline, new {
                shutter,
                mount,
                roof_mode = Observatory.Settings.GetAutopilotRoofMode().ToString(),
                nina_close_on_unsafe = isCloseOnUnsafeEnabled ? "on" : "off",
                action = "none (Astraeus only closes the roof while the autopilot is working it)",
                device = info.Name ?? DisplayName,
            }, Guid.NewGuid().ToString("N"));
        }

        ///////////// Actions /////////////

        private async Task HomeCommandAsync(string id) {
            await BroadcastMessageReceivedAsync(id, true);
            try {
                bool isHome = await domeMediator.FindHome(BeginOperation(ref _azimuthCancellationSource));
                LogOutcome(isHome, "Dome found its home position.", "Dome did not find its home position.");
            } catch (OperationCanceledException) {
                Log("Find home aborted.");
            } catch (Exception ex) {
                LogWarning($"Dome find home failed: {ex.Message}");
            }
        }

        private async Task SlewCommandAsync(string id, double position) {
            await BroadcastMessageReceivedAsync(id, true);
            Log($"Slewing to {position}...");
            try {
                bool isSlewed = await domeMediator.SlewToAzimuth(position,
                    BeginOperation(ref _azimuthCancellationSource));
                LogOutcome(isSlewed, $"Dome slewed to {position}.", $"Dome did not slew to {position}.");
            } catch (OperationCanceledException) {
                Log("Slew aborted.");
            } catch (Exception ex) {
                LogWarning($"Dome slew failed: {ex.Message}");
            }
        }

        private async Task SyncCommandAsync(string id) {
            await BroadcastMessageReceivedAsync(id, true, "Syncing to Mount");
            try {
                await domeMediator.WaitForDomeSynchronization(BeginOperation(ref _azimuthCancellationSource));
                Log("Dome synchronised with the mount.");
            } catch (OperationCanceledException) {
                Log("Sync aborted.");
            } catch (Exception ex) {
                LogWarning($"Dome sync failed: {ex.Message}");
            }
        }

        private void MoveClockwiseCommand(string id, double degrees) {
            if (!TryGetDeviceViewModel<DomeVM>(out DomeVM? domeViewModel)) {
                _ = BroadcastMessageReceivedAsync(id, false, "N.I.N.A.'s dome controls are not available");
                return;
            }
            InvokeOnUiThread(() => {
                domeViewModel.RotateDegrees = degrees;
                domeViewModel.RotateCWCommand.Execute(null);
            });
            _ = BroadcastMessageReceivedAsync(id, true, $"Moving clockwise {degrees} degrees");
        }
        
        private void MoveAntiClockwiseCommand(string id, double degrees) {
            if (!TryGetDeviceViewModel<DomeVM>(out DomeVM? domeViewModel)) {
                _ = BroadcastMessageReceivedAsync(id, false, "N.I.N.A.'s dome controls are not available");
                return;
            }
            InvokeOnUiThread(() => {
                domeViewModel.RotateDegrees = degrees;
                domeViewModel.RotateCCWCommand.Execute(null);
            });
            _ = BroadcastMessageReceivedAsync(id, true, $"Moving anticlockwise {degrees} degrees");
        }

        private void StopCommand(string? id) {
            AbortSlew();
            AbortShutter();
            if (TryGetDeviceViewModel<DomeVM>(out DomeVM? domeViewModel)) {
                InvokeOnUiThread(() => domeViewModel.StopCommand.Execute(null));
            }
            _ = BroadcastMessageReceivedAsync(id, true, $"Stopping all motion.");
        }
        
        private async Task ParkCommandAsync(string? id) {
            await BroadcastMessageReceivedAsync(id, true);
            Log("Parking Dome...");
            try {
                bool isParked = await domeMediator.Park(BeginOperation(ref _azimuthCancellationSource));
                LogOutcome(isParked, "Dome has parked.", "Dome did not park.");
            } catch (OperationCanceledException) {
                Log("Park aborted.");
            } catch (Exception ex) {
                LogWarning($"Dome park failed: {ex.Message}");
            }
        }

        private void LogOutcome(bool isSuccess, string successMessage, string failureMessage) {
            if (isSuccess) {
                Log(successMessage);
            } else {
                LogWarning(failureMessage);
            }
        }

        // N.I.N.A.'s dome driver wrapper checks CanSetPark itself. DomeInfo doesn't expose it.
        private async Task SetParkCommandAsync(string? id) {
            if (!IsConnected) {
                await BroadcastMessageReceivedAsync(id, false, "No device connected");
                return;
            }
            if (IsAutopilotRunning) {
                await BroadcastMessageReceivedAsync(id, false,
                    "The autopilot is running; the park position cannot be changed now");
                return;
            }
            double azimuth = domeMediator.GetInfo().Azimuth;
            if (TryGetDeviceViewModel<DomeVM>(out DomeVM? domeViewModel)) {
                await BroadcastMessageReceivedAsync(id, true, "Setting park position");
                InvokeOnUiThread(() => domeViewModel.SetParkPositionCommand.Execute(azimuth));
                Log($"Park position set to {azimuth}.");
            } else {
                await BroadcastMessageReceivedAsync(id, false, "N.I.N.A.'s dome controls are not available");
            }
        }
        
        private async Task<bool> OpenShutterCommandAsync(string? id) {
            ShutterState currentState = domeMediator.GetInfo().ShutterStatus;
            if (currentState == ShutterState.ShutterOpen || currentState == ShutterState.ShutterOpening) {
                await BroadcastMessageReceivedAsync(id, true, "The shutter is already open or opening");
                return false;
            }
            // Refuse before replying, so the dashboard shows why instead of an accepted open that never happens.
            if (WhyShutterCannotOpen() is string reason) {
                LogWarning($"Shutter cannot open: {reason}.");
                await BroadcastMessageReceivedAsync(id, false, $"The shutter cannot open: {reason}");
                return false;
            }
            await BroadcastMessageReceivedAsync(id, true);
            Log("Shutter is Opening...");
            try {
                bool hasOpened = await domeMediator.OpenShutter(BeginOperation(ref _shutterCancellationSource));
                if (hasOpened) {
                    Log("Shutter opened.");
                } else {
                    LogError("Failed to open the shutter.");
                }
                return hasOpened;
            } catch (OperationCanceledException) {
                Log("Shutter open aborted.");
                return false;
            } catch (Exception ex) {
                LogError($"Opening the shutter failed: {ex.Message}");
                return false;
            }
        }
  
        private async Task<bool> CloseShutterCommandAsync(string? id) {
            await BroadcastMessageReceivedAsync(id, true);
            bool hasClosed = false;

            ShutterState currentState = domeMediator.GetInfo().ShutterStatus;
            if (currentState == ShutterState.ShutterClosed || currentState == ShutterState.ShutterClosing) {
                Log("Shutter is already closed or closing...");
                return false;
            }
            Log("Shutter is Closing...");
            try {
                hasClosed = await domeMediator.CloseShutter(BeginOperation(ref _shutterCancellationSource));
                if (hasClosed) {
                    Log("Shutter closed.");
                } else {
                    LogError("Failed to close the shutter.");
                }
            } catch (OperationCanceledException) {
                Log("Shutter close aborted.");
            }
            return hasClosed;
        }

        private async Task SetFollowCommandAsync(string? id, bool shouldFollow) {
            await BroadcastMessageReceivedAsync(id, true);
            if (shouldFollow) {
                bool hasEnabledFollowing = await domeMediator.EnableFollowing(ComponentToken);
                if (hasEnabledFollowing) {
                    Log("Dome set to follow mount.");
                } else {
                    LogError("Failed to set dome to follow mount.");
                }
                return;
            }
            bool hasDisabledFollowing = await domeMediator.DisableFollowing(ComponentToken);
            if (hasDisabledFollowing) {
                Log("Dome no longer following mount.");
            } else {
                LogError("Failed to stop dome following mount.");
            }
        }
        
        ///////////// Post Helpers /////////////

        private async Task PostShutterStatusAsync(string status) {
            DomePayload payload =
                new DomePayload{DeviceId = DeviceId, ShutterStatus = status, IsConnected = IsConnected};
            DomeUpdate message = new DomeUpdate(payload, "event");
            await WebSocketBus.SendAsync(message);
        }
        
        private async Task PostParkedStatusAsync(bool isParked) {
            DomePayload payload = new DomePayload{DeviceId = DeviceId, IsParked = isParked, IsConnected = IsConnected};
            DomeUpdate message = new DomeUpdate(payload, "event");
            await WebSocketBus.SendAsync(message);
        }
        
        ///////////// Autopilot Helpers /////////////

        public bool IsShutterOpen() {
            return IsConnected && domeMediator.GetInfo().ShutterStatus == ShutterState.ShutterOpen;
        }

        /// <summary>
        /// Not the same as !IsShutterOpen. Opening, closing, error and unknown are neither, and for safety
        /// it's the closed reading that counts.
        /// </summary>
        public bool IsShutterClosed() {
            return IsConnected && domeMediator.GetInfo().ShutterStatus == ShutterState.ShutterClosed;
        }

        public string ShutterStatusName =>
            IsConnected ? domeMediator.GetInfo().ShutterStatus.ToString() : "disconnected";

        /// <summary>
        /// N.I.N.A. waits as long as the driver says "closing", so a stalled roof would hold the autopilot
        /// mid-secure for the rest of the night.
        /// </summary>
        private static readonly TimeSpan RoofMoveTimeout = TimeSpan.FromMinutes(5);

        /// <summary>True only if it reads open afterwards.</summary>
        public async Task<bool> OpenShutterAsync(CancellationToken cancellationToken) {
            if (!IsConnected) return false;
            if (IsShutterOpen()) return true;
            bool isFinished = await RunShutterMoveAsync(domeMediator.OpenShutter, "opening", cancellationToken);
            return isFinished && IsShutterOpen();
        }

        /// <summary>
        /// True only if the shutter reads closed afterwards. N.I.N.A. reports the close as done after its
        /// settle delay whatever the shutter did.
        /// </summary>
        public async Task<bool> CloseShutterAsync(CancellationToken cancellationToken) {
            if (!IsConnected) return false;
            if (IsShutterClosed()) return true;
            bool isFinished = await RunShutterMoveAsync(domeMediator.CloseShutter, "closing", cancellationToken);
            return isFinished && IsShutterClosed();
        }

        /// <summary>
        /// For the autopilot's waits. Closes the shutter only if the mount is connected and reports
        /// parked. False if it isn't, or the close didn't finish.
        /// </summary>
        public async Task<bool> TryCloseWhenParkedAsync(string reason, CancellationToken cancellationToken) {
            if (!IsConnected) return false;
            if (IsShutterClosed()) return true;
            if (!Observatory.TryGetComponent<Mount>(out Mount? mount) || !mount.IsConnected || !mount.IsParked) {
                LogWarning($"Not closing the roof ({reason}): the mount is not confirmed parked.");
                return false;
            }
            Log($"Closing the roof ({reason}).");
            return await CloseShutterAsync(cancellationToken);
        }

        // A caller's cancel still throws. Our own timeout or a driver exception returns false.
        private async Task<bool> RunShutterMoveAsync(Func<CancellationToken, Task<bool>> move, string action,
            CancellationToken cancellationToken) {
            using CancellationTokenSource moveCancellationSource =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            moveCancellationSource.CancelAfter(RoofMoveTimeout);
            try {
                return await move(moveCancellationSource.Token);
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                throw;
            } catch (OperationCanceledException) {
                LogError($"Roof did not finish {action} within {RoofMoveTimeout.TotalMinutes:F0} minutes.");
                return false;
            } catch (Exception ex) {
                LogError($"Roof {action} failed: {ex.Message}");
                return false;
            }
        }

        ///////////// Helpers /////////////
        private string? WhyShutterCannotOpen() {
            if (_safetyMonitor is not { IsConnected: true }) {
                return "no safety monitor is connected, so conditions cannot be confirmed safe";
            }
            return _safetyMonitor.IsSafe() ? null : "the safety monitor reports conditions are unsafe";
        }

    }
}