using NINA.Profile.Interfaces;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using NINA.Core.Utility;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine {
    public partial class Settings(Observatory observatory, IWebSocketBus webSocketBus)
        : ServiceComponent(observatory, webSocketBus) {
        public override string DeviceType => "settings";
        public override LogCategory DefaultLogCategory => LogCategory.System;
        public override WsMessage? GetUpdateMessage() => null;
        public IProfile? Profile { get; private set; }

        /// <summary>
        /// Collapses a burst of setting changes into one push. Each call replaces any push still waiting,
        /// so one options-page change or one server update touching a dozen properties sends one message.
        /// </summary>
        private sealed class DebouncedPush(Func<Task> push, Action<string> onError) {
            private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(400);
            private int _generation;

            public async void Schedule() {
                int generation = Interlocked.Increment(ref _generation);
                try {
                    await Task.Delay(DebounceWindow);
                    if (Volatile.Read(ref _generation) != generation) return;
                    await push();
                } catch (Exception ex) {
                    onError(ex.Message);
                }
            }
        }

        private DebouncedPush CreatePush(Func<Task> push, string settingsName) =>
            new DebouncedPush(push,
                message => LogError($"Failed to push {settingsName} settings: {message}", device: "system"));

        /// <summary>
        /// If any field has the wrong type nothing is applied, and we send back the current settings so
        /// the dashboard shows what N.I.N.A. holds.
        /// </summary>
        private async Task<bool> TryApplyServerUpdateAsync(SettingsUpdateBatch update, string settingsName,
            Func<Task> pushCurrentSettings) {
            if (update.TryApply()) return true;
            LogWarning($"Ignored a {settingsName} settings update from the server: wrong type for " +
                       $"{update.InvalidFieldNames}.", device: "system");
            await pushCurrentSettings();
            return false;
        }

        public override async Task Awake() {
            await base.Awake();
            WebSocketBus.Connected += OnConnected;
            Profile = Observatory.Settings.ProfileService.ActiveProfile;
        }

        public override async Task Start() {
            await base.Start();
            SubscribeProfileSettings();
            SubscribeAutopilotSettings();
        }

        public override Task Destroy() {
            WebSocketBus.Connected -= OnConnected;
            UnsubscribeAutopilotSettings();
            UnsubscribeProfileSettings();
            return base.Destroy();
        }

        /// <summary>Moves the settings sync to the newly active N.I.N.A. profile.</summary>
        public async Task RebindProfileAsync() {
            if (Profile == null) return;
            UnsubscribeProfileSettings();
            Profile = Observatory.Settings.ProfileService.ActiveProfile;
            SubscribeProfileSettings();
            if (Observatory.IsSocketOpen) await PushSettingsAsync();
        }

        private void SubscribeProfileSettings() {
            Profile!.AstrometrySettings.PropertyChanged += OnAstrometrySettingsChanged;
            Profile.TelescopeSettings.PropertyChanged += OnTelescopeSettingsChanged;
            Profile.CameraSettings.PropertyChanged += OnCameraSettingsChanged;
            Profile.FilterWheelSettings.PropertyChanged += OnFilterWheelSettingsChanged;
            Profile.AlpacaSettings.PropertyChanged += OnAlpacaSettingsChanged;
            Profile.FocuserSettings.PropertyChanged += OnFocuserSettingsChanged;
            Profile.DomeSettings.PropertyChanged += OnDomeSettingsChanged;
            Profile.ImageFileSettings.PropertyChanged += OnImageFileSettingsChanged;
            SubscribeMeridianFlipSettings();
            SubscribeFilterItems();
        }

        private void UnsubscribeProfileSettings() {
            if (Profile == null) return;
            UnsubscribeMeridianFlipSettings();
            Profile.AstrometrySettings.PropertyChanged -= OnAstrometrySettingsChanged;
            Profile.TelescopeSettings.PropertyChanged -= OnTelescopeSettingsChanged;
            Profile.CameraSettings.PropertyChanged -= OnCameraSettingsChanged;
            Profile.FilterWheelSettings.PropertyChanged -= OnFilterWheelSettingsChanged;
            Profile.AlpacaSettings.PropertyChanged -= OnAlpacaSettingsChanged;
            Profile.FocuserSettings.PropertyChanged -= OnFocuserSettingsChanged;
            Profile.DomeSettings.PropertyChanged -= OnDomeSettingsChanged;
            Profile.ImageFileSettings.PropertyChanged -= OnImageFileSettingsChanged;
            UnsubscribeFilterItems();
        }

        protected override Task HandleCommandAsync(WsCommand command) {
            switch (command.Action) {
                case "settingsChanged" when command.Context == "astrometry":
                    return HandleAstrometryUpdateAsync(command);
                case "settingsChanged" when command.Context == "telescope":
                    return HandleTelescopeUpdateAsync(command);
                case "settingsChanged" when command.Context == "camera":
                    return HandleCameraUpdateAsync(command);
                case "settingsChanged" when command.Context == "filterwheel":
                    return HandleFilterWheelUpdateAsync(command);
                case "settingsChanged" when command.Context == "alpaca":
                    return HandleAlpacaUpdateAsync(command);
                case "settingsChanged" when command.Context == "focuser":
                    return HandleFocuserUpdateAsync(command);
                case "settingsChanged" when command.Context == "dome":
                    return HandleDomeUpdateAsync(command);
                case "settingsChanged" when command.Context == "imagefile":
                    return HandleImageFileUpdateAsync(command);
                case "settingsChanged" when command.Context == "meridianflip":
                    return HandleMeridianFlipUpdateAsync(command);
                case "settingsChanged" when command.Context == "autopilot":
                    return HandleAutopilotUpdateAsync(command);
            }
            return Task.CompletedTask;
        }

        private async void OnConnected() {
            try {
                await Task.Delay(PushOnConnectDelay);
                await PushSettingsAsync();
            } catch (Exception ex) {
                LogError($"Failed to push settings on connect: {ex.Message}", device: "system");
            }
        }

        private async Task PushSettingsAsync() {
            await PushAstrometrySettingsAsync();
            await PushTelescopeSettingsAsync();
            await PushCameraSettingsAsync();
            await PushFilterWheelSettingsAsync();
            await PushAlpacaSettingsAsync();
            await PushFocuserSettingsAsync();
            await PushDomeSettingsAsync();
            await PushImageFileSettingsAsync();
            await PushMeridianFlipSettingsAsync();
            await PushAutopilotSettingsAsync();
        }
    }
}