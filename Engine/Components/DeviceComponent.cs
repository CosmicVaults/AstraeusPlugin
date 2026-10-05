using CosmicVaults.NINA.Astraeus.Engine.Components;
using NINA.Core.Utility;
using NINA.Equipment.Equipment;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using CosmicVaults.NINA.Astraeus.Engine.Utility;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace CosmicVaults.NINA.Astraeus.Engine {
    public abstract class DeviceComponent<THandler, TConsumer, TInfo>(
        IDeviceMediator<THandler, TConsumer, TInfo> mediator,
        Observatory observatory,
        IWebSocketBus webSocketBus)
        : BaseComponent(observatory, webSocketBus), IDeviceComponent
        where THandler : IDeviceVM<TInfo>
        where TConsumer : IDeviceConsumer<TInfo>
        where TInfo : DeviceInfo {

        public abstract string DisplayName { get; }

        public override LogCategory DefaultLogCategory => LogCategory.Equipment;

        public string DeviceId { get; private set; } = "Unknown";
        
        protected IDeviceMediator<THandler, TConsumer, TInfo> Mediator { get; } = mediator;

        // Read by the autopilot's thread as well as the plugin loop's.
        private volatile bool _isConnected;
        public bool IsConnected {
            get => _isConnected;
            set {
                if (value == _isConnected) return;
                _isConnected = value;
                if (_isConnected) {
                    DeviceId = Mediator.GetDevice()?.Id ?? "Unknown";
                }
                WebSocketBus.NotifyConnectionAsync(DeviceType, DeviceId, _isConnected, DisplayName)
                    .ObserveFaults(exception =>
                        LogDebug($"Could not report the connection change: {exception.Message}"));
            }
        }
        protected TInfo? LastInfo { get; private set; }

        public event EventHandler? DefaultDeviceChanged;

        ///////////// Cancellation /////////////
        // One lifetime token per component, cancelled on Destroy, plus a slot per kind of motion.
        // Manual commands run long mediator calls in a slot so the matching AbortX() can cancel just
        // that motion.

        private readonly CancellationTokenSource _lifetimeCancellationSource = new();
        private readonly object _operationGate = new();
        private readonly HashSet<CancellationTokenSource> _linkedOperations = new();

        protected CancellationToken ComponentToken => _lifetimeCancellationSource.Token;

        /// <summary>
        /// Starts an operation in slot and returns a token linked to ComponentToken. Any operation
        /// already in the slot is replaced, since a device does one motion of each kind at a time.
        /// Pair with AbortOperation.
        /// </summary>
        protected CancellationToken BeginOperation(ref CancellationTokenSource? slot) {
            lock (_operationGate) {
                if (slot is not null) {
                    // Cancel the old one before dropping it, or it keeps running where no abort can reach it.
                    _linkedOperations.Remove(slot);
                    slot.Cancel();
                    slot.Dispose();
                }
                slot = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellationSource.Token);
                _linkedOperations.Add(slot);
                return slot.Token;
            }
        }

        protected void AbortOperation(ref CancellationTokenSource? slot) {
            lock (_operationGate) {
                try { slot?.Cancel(); } catch (ObjectDisposedException) { }
            }
        }

        /// <summary>Unset reads as NoDevice ("None"), meaning not in use.</summary>
        public string? DefaultDevice =>
            Observatory.Settings.GetDefaultDeviceId(DeviceType) is { Length: > 0 } id ? id : NoDevice;

        // Defaults have been lost without trace, so every real change logs what it was and who made it.
        public void SetDefaultDevice(string? deviceId, string source) {
            string oldDevice = DefaultDevice ?? NoDevice;
            string newDevice = string.IsNullOrEmpty(deviceId) ? NoDevice : deviceId;
            if (newDevice == oldDevice) return;

            Observatory.Settings.SetDefaultDeviceId(DeviceType, newDevice);
            string message = $"Default {DisplayName} changed from '{oldDevice}' to '{newDevice}' ({source}).";
            if (newDevice == NoDevice) LogWarning(message);
            else Log(message);
            DefaultDeviceChanged?.Invoke(this, EventArgs.Empty);
        }

        public bool HasDefaultDevice => !string.IsNullOrEmpty(DefaultDevice) && DefaultDevice != NoDevice;

        public Task<bool> TryConnectDefaultDeviceAsync() =>
            HasDefaultDevice ? TryConnectAsync(DefaultDevice!) : Task.FromResult(false);

        public abstract WsMessage? GetDeviceStaticInfo();

        protected async Task SendStaticInfoAsync() {
            WsMessage? staticInfo = GetDeviceStaticInfo();
            if (staticInfo is not null) await WebSocketBus.SendAsync(staticInfo with { Context = "event" });
        }

        public override async Task Start() {
            await base.Start();
            Mediator.Connected += OnMediatorConnected;
            Mediator.Disconnected += OnMediatorDisconnected;
            try {
                TInfo info = Mediator.GetInfo();
                IsConnected = info.Connected;
            } catch (Exception ex) {
                LogDebug($"Initial GetInfo failed, assuming disconnected: {ex.Message}");
                IsConnected = false;
            }
        }

        // Drivers time out now and then, so one failed read isn't a disconnect. This many in a row is.
        private const int FailedReadsBeforeDisconnected = 3;
        private int _consecutiveFailedReads;

        public override Task Update() {
            TInfo info;
            try {
                info = Mediator.GetInfo();
            } catch (Exception ex) {
                if (IsConnected) CountFailedRead(ex);
                return Task.CompletedTask;
            }
            _consecutiveFailedReads = 0;

            // If failed reads marked us disconnected but N.I.N.A. still has the device, pick it back up.
            // No Connected event comes for a connection that never dropped.
            if (!IsConnected && info.Connected) IsConnected = true;
            if (IsConnected) LastInfo = info;
            return Task.CompletedTask;
        }

        private void CountFailedRead(Exception exception) {
            _consecutiveFailedReads++;
            if (_consecutiveFailedReads < FailedReadsBeforeDisconnected) {
                LogDebug($"GetInfo failed ({_consecutiveFailedReads} in a row): {exception.Message}");
                return;
            }
            LogWarning($"GetInfo failed {_consecutiveFailedReads} times in a row, treating as disconnected: " +
                       exception.Message);
            _consecutiveFailedReads = 0;
            IsConnected = false;
        }

        public override Task LateUpdate() {
            LastInfo = null;
            return Task.CompletedTask;
        }

        // The lifetime source is cancelled but not disposed. Work still unwinding reads ComponentToken,
        // and a disposed source would throw there. It holds no timer.
        public override Task Destroy() {
            if (IsDestroyed) return Task.CompletedTask;
            Mediator.Connected -= OnMediatorConnected;
            Mediator.Disconnected -= OnMediatorDisconnected;
            _lifetimeCancellationSource.Cancel();
            lock (_operationGate) {
                foreach (CancellationTokenSource operation in _linkedOperations) operation.Dispose();
                _linkedOperations.Clear();
            }
            return base.Destroy();
        }

        protected override async Task RouteCommandAsync(WsCommand command) {
            switch (command.Action) {
                case "toggle":
                    // Every exit answers the dashboard, or its button spins until the browser times out.
                    if (JsonFields.ReadString(command.Payload, "id", out string deviceId) != JsonFieldState.Valid) {
                        await BroadcastMessageReceivedAsync(command.Id, false, "No device id in the request");
                        return;
                    }
                    if (!TryGetDeviceChooser(out IDeviceChooserVM? chooser)) {
                        LogWarning($"{DeviceType}: DeviceChooser not available");
                        await BroadcastMessageReceivedAsync(command.Id, false,
                            $"N.I.N.A. has no {DisplayName} chooser");
                        return;
                    }
                    if (!TryGetDeviceById(chooser!.Devices, deviceId, out IDevice? device)) {
                        LogWarning("Selected device is not available");
                        await BroadcastMessageReceivedAsync(command.Id, false,
                            "That driver is no longer in N.I.N.A.'s list");
                        return;
                    }
                    await BroadcastMessageReceivedAsync(command.Id, true);
                    InvokeOnUiThread(() => chooser.SelectedDevice = device);
                    // Run off the receive loop. A driver that blocks before its first await would
                    // stall the socket, and the cancel that fixes it could never arrive. It also
                    // keeps the half-open-link detector ticking.
                    _ = Task.Run(ToggleConnectionAsync);
                    break;
                case "cancelConnect":
                    await CancelConnectAsync(command.Id);
                    break;
                case "fetchDevices":
                    await BroadcastMessageReceivedAsync(command.Id, true);
                    await SendDeviceListAsync();
                    break;
                case "fetchStaticInfo":
                    await BroadcastMessageReceivedAsync(command.Id, true);
                    await SendStaticInfoAsync();
                    break;
                default:
                    await base.RouteCommandAsync(command);
                    break;
            }
        }

        private async Task ToggleConnectionAsync() {
            try {
                if (IsConnected) {
                    Log($"Disconnecting {DisplayName}...");
                    await Mediator.Disconnect();
                    return;
                }
                Log($"Connecting {DisplayName}...");
                using CancellationTokenSource dialogWatchCancellationSource = new CancellationTokenSource();
                Task dialogWatch = WatchForBlockingDialogAsync(dialogWatchCancellationSource.Token);
                try {
                    // IsConnected only reports changes and stays false on a failure, so we tell
                    // the dashboard ourselves.
                    if (!await Mediator.Connect()) await ReportConnectFailedAsync($"{DisplayName} did not connect.");
                } finally {
                    dialogWatchCancellationSource.Cancel();
                    await dialogWatch;
                }
            } catch (OperationCanceledException) {
                await ReportConnectFailedAsync($"The {DisplayName} connect was cancelled.");
            } catch (Exception ex) {
                LogError($"{DeviceType}/toggle failed: {ex.Message}");
                await ReportConnectFailedAsync($"{DisplayName} failed to connect: {ex.Message}");
            }
        }

        /// <summary>
        /// Presses N.I.N.A.'s own Cancel button for this device. IDeviceMediator has no cancel, but every
        /// device VM has a public CancelConnectCommand that cancels the token passed to IDevice.Connect.
        /// The handler TryGetDeviceViewModel reaches is that VM.
        /// </summary>
        private async Task CancelConnectAsync(string? commandId) {
            if (FindCancelConnectCommand(out string whyNot) is not { } cancelCommand) {
                await BroadcastMessageReceivedAsync(commandId, false, whyNot);
                return;
            }
            Log($"Cancelling the {DisplayName} connect...");
            await BroadcastMessageReceivedAsync(commandId, true);
            InvokeOnUiThread(() => cancelCommand.Execute(null));
        }

        /// <summary>
        /// Presses N.I.N.A.'s Cancel button on a connect in progress, like the dashboard's cancel.
        /// False when this build has none to press. Never throws.
        /// </summary>
        public bool TryCancelConnect() {
            try {
                if (FindCancelConnectCommand(out _) is not { } cancelCommand) return false;
                InvokeOnUiThread(() => cancelCommand.Execute(null));
                return true;
            } catch (Exception ex) {
                LogWarning($"Cancelling the {DisplayName} connect failed: {ex.Message}");
                return false;
            }
        }

        private ICommand? FindCancelConnectCommand(out string whyNot) {
            if (!TryGetDeviceViewModel<object>(out object? deviceViewModel)) {
                whyNot = "Device view model not available";
                return null;
            }
            // Read as ICommand because FlatDeviceVM declares this one as IRelayCommand<object> and the
            // other ten use ICommand.
            ICommand? cancelCommand = deviceViewModel.GetType()
                .GetProperty("CancelConnectCommand", BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(deviceViewModel) as ICommand;
            whyNot = cancelCommand is null ? "This N.I.N.A. build cannot cancel a connect" : "";
            return cancelCommand;
        }

        private static readonly TimeSpan BlockingDialogPollInterval = TimeSpan.FromMilliseconds(750);

        /// <summary>
        /// Reports whenever a modal starts or stops blocking the connect. N.I.N.A.'s site-settings
        /// prompt is the usual one, and it raises no event, status or log line, so we watch the window list.
        /// </summary>
        private async Task WatchForBlockingDialogAsync(CancellationToken token) {
            string? reportedTitle = null;
            try {
                while (!token.IsCancellationRequested) {
                    await Task.Delay(BlockingDialogPollInterval, token);
                    string? title = FindBlockingDialogTitle();
                    if (title == reportedTitle) continue;
                    reportedTitle = title;
                    await WebSocketBus.SendAsync(new DeviceConnectBlocked(DeviceType, title is not null, title));
                    if (title is not null) LogWarning($"Waiting on \"{title}\" before {DisplayName} can connect.");
                }
            } catch (OperationCanceledException) {
                // The connect finished. Below we clear the flag only if we raised it.
            } catch (Exception ex) {
                LogDebug($"Blocking-dialog watch stopped: {ex.Message}");
            }
            if (reportedTitle is not null)
                await WebSocketBus.SendAsync(new DeviceConnectBlocked(DeviceType, false, null));
        }

        /// <summary>
        /// The title of a modal owned by N.I.N.A.'s main window, or null. Dispatcher.Invoke still
        /// works while one is open because WPF's ShowDialog pumps a nested dispatcher loop.
        /// </summary>
        private string? FindBlockingDialogTitle() {
            Application? application = Application.Current;
            if (application is null) return null;
            return application.Dispatcher.Invoke(() => {
                // Only a modal holds up the connect. A tool window left open doesn't.
                if (!System.Windows.Interop.ComponentDispatcher.IsThreadModal) return (string?)null;
                foreach (Window window in application.Windows) {
                    if (ReferenceEquals(window, application.MainWindow)) continue;
                    if (!window.IsVisible) continue;
                    if (!ReferenceEquals(window.Owner, application.MainWindow)) continue;
                    return string.IsNullOrWhiteSpace(window.Title) ? "a dialog" : window.Title;
                }
                return (string?)null;
            });
        }

        private async Task ReportConnectFailedAsync(string message) {
            LogWarning(message);
            await WebSocketBus.SendAsync(new DeviceConnectionUpdate(DeviceType, DeviceId, DisplayName, false));
            await WebSocketBus.SendAsync(new WsNotification(
                device: DeviceType, category: "connection", message: message, isToast: true, severity: "ERROR"));
        }

        protected virtual string? RegistrationEndpoint => null;

        /// <summary>
        /// Registers with the server in the background. N.I.N.A. awaits every Connected handler and warns
        /// past a second, and nothing downstream waits on the registration.
        /// </summary>
        protected virtual Task OnMediatorConnected(object sender, EventArgs eventArgs) {
            IsConnected = true;
            _ = RegisterDeviceInBackgroundAsync();
            return Task.CompletedTask;
        }

        private async Task RegisterDeviceInBackgroundAsync() {
            try {
                await RegisterDeviceAsync();
            } catch (Exception ex) {
                LogWarning($"Registering {DisplayName} with the server failed: {ex.Message}");
            }
        }

        protected virtual async Task RegisterDeviceAsync() {
            if (RegistrationEndpoint is null) return;
            TInfo info = Mediator.GetInfo();
            await Observatory.AstraeusWebClient.PostObservatoryAsync(RegistrationEndpoint, new {
                name = info.Name,
                device_id = info.DeviceId,
                last_connected = UtcTimestampNow()
            });
        }

        protected virtual Task OnMediatorDisconnected(object sender, EventArgs eventArgs) {
            IsConnected = false;
            LastInfo = null;
            return Task.CompletedTask;
        }

        private const int DeviceListAttempts = 10;
        private static readonly TimeSpan DeviceListRetryDelay = TimeSpan.FromMilliseconds(200);

        private async Task SendDeviceListAsync() {
            IDeviceChooserVM? chooser = null;
            for (int attempt = 0; attempt < DeviceListAttempts; attempt++) {
                if (TryGetDeviceChooser(out chooser) && chooser != null && chooser.Devices.Count > 0)
                    break;
                await Task.Delay(DeviceListRetryDelay);
            }
            if (chooser == null) {
                LogWarning($"DeviceChooser not available after waiting");
                return;
            }
            List<DeviceListItem> devices = chooser.Devices
                .Select(available => new DeviceListItem(available.Id, available.DisplayName))
                .ToList();
            await WebSocketBus.SendAsync(new DeviceListUpdate(DeviceType, devices, chooser.SelectedDevice?.Id));
        }

        /// <summary>
        /// Runs action on N.I.N.A.'s UI thread, where its view models and device choosers are bound.
        /// Server commands arrive on the socket thread.
        /// </summary>
        protected static void InvokeOnUiThread(Action action) {
            System.Windows.Threading.Dispatcher? dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) {
                action();
                return;
            }
            dispatcher.Invoke(action);
        }

        protected bool TryGetDeviceViewModel<TDeviceViewModel>(
            [NotNullWhen(true)] out TDeviceViewModel? deviceViewModel) where TDeviceViewModel : class {
            deviceViewModel = null;
            try {
                FieldInfo? handlerField = Mediator.GetType()
                    .GetField("handler", BindingFlags.Instance | BindingFlags.NonPublic);
                if (handlerField == null) return false;
                object? handler = handlerField.GetValue(Mediator);
                if (handler == null) return false;
                if (handler is TDeviceViewModel viewModel) { deviceViewModel = viewModel; return true; }
                PropertyInfo? viewModelProperty = handler.GetType()
                    .GetProperty("DeviceVM", BindingFlags.Instance | BindingFlags.Public);
                if (viewModelProperty != null) {
                    deviceViewModel = viewModelProperty.GetValue(handler) as TDeviceViewModel;
                    return deviceViewModel != null;
                }
                return false;
            } catch {
                deviceViewModel = null;
                return false;
            }
        }

        public IList<IDevice> GetAvailableDevices() {
            if (TryGetDeviceChooser(out IDeviceChooserVM? chooser) && chooser != null)
                return chooser.Devices;
            return new List<IDevice>();
        }

        public bool IsConnectedToDevice(string deviceId) {
            return IsConnected && Mediator.GetInfo().DeviceId == deviceId;
        }

        public string? ConnectedDeviceId => IsConnected ? Mediator.GetInfo().DeviceId : null;

        public async Task<bool> TryConnectAsync(string deviceId) {
            if (IsConnectedToDevice(deviceId)) return true;
            if (!TryGetDeviceChooser(out IDeviceChooserVM? chooser) || chooser == null) return false;
            if (!TryGetDeviceById(chooser.Devices, deviceId, out IDevice? device) || device == null) return false;
            try {
                if (IsConnected) await Mediator.Disconnect();
                InvokeOnUiThread(() => chooser.SelectedDevice = device);
                await Mediator.Connect();
                return Mediator.GetInfo().Connected;
            } catch (Exception ex) {
                LogWarning($"Connecting {DisplayName} failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Disconnects whatever is connected. True once nothing is, including when nothing was.
        /// False if the device is still connected afterwards. Never throws.
        /// </summary>
        public async Task<bool> TryDisconnectAsync() {
            if (!IsConnected) return true;
            try {
                await Mediator.Disconnect();
                return !Mediator.GetInfo().Connected;
            } catch (Exception ex) {
                LogWarning($"Disconnecting {DisplayName} failed: {ex.Message}");
                return false;
            }
        }

        protected bool TryGetDeviceChooser([NotNullWhen(true)] out IDeviceChooserVM? chooser) {
            chooser = null;
            try {
                FieldInfo? handlerField = Mediator.GetType()
                    .GetField("handler", BindingFlags.Instance | BindingFlags.NonPublic);
                if (handlerField == null) return false;
                object? handler = handlerField.GetValue(Mediator);
                if (handler == null) return false;
                PropertyInfo? chooserProperty = handler.GetType()
                    .GetProperty("DeviceChooserVM", BindingFlags.Instance | BindingFlags.Public);
                if (chooserProperty == null) return false;
                chooser = chooserProperty.GetValue(handler) as IDeviceChooserVM;
                return chooser != null;
            } catch {
                chooser = null;
                return false;
            }
        }

        protected bool TryGetDeviceById(IList<IDevice> devices, string? id, [NotNullWhen(true)] out IDevice? device) {
            device = devices.FirstOrDefault(candidate => candidate.Id == id);
            return device != null;
        }
    }
}