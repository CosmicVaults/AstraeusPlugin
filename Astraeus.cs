using NINA.Astrometry.Interfaces;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.Interfaces;
using NINA.PlateSolving.Interfaces;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using CosmicVaults.NINA.Astraeus.Controls;
using CosmicVaults.NINA.Astraeus.Engine;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot;
using CosmicVaults.NINA.Astraeus.Engine.Calibration;
using CosmicVaults.NINA.Astraeus.Themes;
using System;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus {
    [Export(typeof(IPluginManifest))]
    public class Astraeus : PluginBase, INotifyPropertyChanged {
        private readonly IProfileService _profileService;

        private CancellationTokenSource? _backgroundLoopCancellation;
        private Task? _backgroundTask;
        
        // Gives the other plugins time to finish loading before this one starts.
        private static readonly TimeSpan PluginLoadDelay = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan DisabledPollInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan ErrorRetryDelay = TimeSpan.FromSeconds(2);

        public LoginViewModel LoginViewModel { get; private set; }
        public AutopilotSettingsViewModel AutopilotSettingsViewModel { get; private set; }
        public CalibrationSettingsViewModel CalibrationSettingsViewModel { get; private set; }
        public FeedSettingsViewModel FeedSettingsViewModel { get; private set; }
        public Observatory Observatory { get; private set; }

        private void Log(string message) => Observatory.Log(message, "system", LogCategory.System);
        public AstraeusSettings AstraeusSettings { get; private set; }

        [ImportingConstructor]
        public Astraeus(IProfileService profileService,
            IOptionsVM options,
            IPlateSolverFactory plateSolverFactory,
            ITelescopeMediator telescopeMediator,
            ICameraMediator cameraMediator,
            IFilterWheelMediator filterWheelMediator,
            ISafetyMonitorMediator safetyMonitorMediator,
            IWeatherDataMediator weatherDataMediator,
            IFocuserMediator focuserMediator,
            IFlatDeviceMediator flatMediator,
            IDomeMediator domeMediator,
            IRotatorMediator rotatorMediator,
            ISwitchMediator switchMediator,
            IImagingMediator imagingMediator,
            IImageSaveMediator imageSaveMediator,
            IImageDataFactory imageDataFactory,
            IGuiderMediator guiderMediator,
            IDomeFollower domeFollower,
            INighttimeCalculator nighttimeCalculator,
            [Import(AllowDefault = true)] IAutoFocusVMFactory autoFocusFactory,
            [Import(AllowDefault = true)] IImageHistoryVM imageHistory,
            [Import(AllowDefault = true)] ISequenceMediator sequenceMediator,
            [Import(AllowDefault = true)] IMeridianFlipVMFactory meridianFlipFactory
            ) {
            Guid pluginGuid = Guid.Parse(Identifier);
            _profileService = profileService;
            profileService.ProfileChanged += ProfileService_ProfileChanged;
            AstraeusSettings = new AstraeusSettings(profileService, options, pluginGuid);

            AstraeusThemeMode.Bind(AstraeusSettings);

            SequenceRunner sequenceRunner = new SequenceRunner(sequenceMediator);
            Observatory = new Observatory(AstraeusSettings,
                plateSolverFactory,
                telescopeMediator,
                cameraMediator,
                filterWheelMediator,
                safetyMonitorMediator,
                weatherDataMediator,
                focuserMediator,
                flatMediator,
                domeMediator,
                rotatorMediator,
                switchMediator,
                imagingMediator,
                imageSaveMediator,
                imageDataFactory,
                guiderMediator,
                domeFollower,
                nighttimeCalculator,
                autoFocusFactory,
                imageHistory,
                sequenceRunner,
                meridianFlipFactory);
            
            LoginViewModel = new LoginViewModel(Observatory.Authenticator, Observatory);
            AutopilotSettingsViewModel = new AutopilotSettingsViewModel(Observatory);

            Observatory.TryGetComponent<MasterFrameGenerator>(out MasterFrameGenerator? masterFrameGenerator);
            CalibrationSettingsViewModel = new CalibrationSettingsViewModel(AstraeusSettings, masterFrameGenerator);

            // The feed has no NINA device behind it, so no component handles it.
            FeedSettingsViewModel = new FeedSettingsViewModel(AstraeusSettings, () => Observatory.Cadence.Signal());

            Log("Plugin initializing.");
            _backgroundLoopCancellation = new CancellationTokenSource();
            _backgroundTask = Task.Run(() => BackgroundLoopAsync(_backgroundLoopCancellation.Token));
        }

        private async Task BackgroundLoopAsync(CancellationToken cancellationToken) {
            try {
                await Task.Delay(PluginLoadDelay, cancellationToken);
                while (!IsPluginEnabled) {
                    await Task.Delay(DisabledPollInterval, cancellationToken);
                }
                await InitializeObservatoryAsync();
                await RunHeartbeatAsync(cancellationToken);
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                // Teardown.
            }
        }

        private async Task InitializeObservatoryAsync() {
            try {
                await Observatory.Awake();
                await Observatory.Start();
                AutopilotSettingsViewModel.RefreshDevices();
            } catch (Exception ex) {
                Logger.Error($"Astraeus could not finish starting: {ex}");
            }
        }

        private async Task RunHeartbeatAsync(CancellationToken cancellationToken) {
            while (!cancellationToken.IsCancellationRequested) {
                try {
                    await RunHeartbeatPassAsync(cancellationToken);
                } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                    throw;
                } catch (Exception ex) {
                    Logger.Error($"Astraeus background loop error: {ex}");
                    await Task.Delay(ErrorRetryDelay, cancellationToken);
                }
            }
        }

        private async Task RunHeartbeatPassAsync(CancellationToken cancellationToken) {
            if (!IsPluginEnabled) {
                if (Observatory.TryGetComponent(out Autopilot? autopilot)) {
                    autopilot.IsEnabled = false;
                }
                if (Observatory.IsSocketOpen) {
                    await Observatory.WebSocketClient.StopAsync();
                }
                await Observatory.Cadence.WaitAsync(() => DisabledPollInterval, cancellationToken);
                return;
            }
            await Observatory.Update();
            await Observatory.LateUpdate();
            await Observatory.Cadence.WaitAsync(() => Observatory.NextTickDelay, cancellationToken);
        }

        public override async Task Teardown() {
            _profileService.ProfileChanged -= ProfileService_ProfileChanged;
            Log("Plugin Teardown called");
            _backgroundLoopCancellation?.Cancel();
            if (_backgroundTask != null) {
                try {
                    await _backgroundTask;
                } catch (Exception ex) when (ex is not OperationCanceledException) {
                    Logger.Error($"Astraeus background loop ended with an error: {ex}");
                } catch (OperationCanceledException) {
                    // Teardown.
                }
            }
            // Also destroys the components, which stops the webcam feed's capture loop and releases
            // the camera.
            await Observatory.ShutDownAsync();
            await base.Teardown();
        }

        // Every Astraeus setting, the stored sign-in included, belongs to the N.I.N.A. profile.
        private async void ProfileService_ProfileChanged(object? sender, EventArgs eventArgs) {
            try {
                Log("Profile changed, reloading Astraeus session");
                await Observatory.Authenticator.ReloadForProfileChangeAsync();
                if (Observatory.TryGetComponent(out Autopilot? autopilot)) {
                    autopilot.OnProfileChanged();
                }
                if (Observatory.TryGetComponent(out Settings? settingsComponent)) {
                    await settingsComponent.RebindProfileAsync();
                }
                RefreshOptionsPage();
            } catch (Exception ex) {
                Logger.Error($"Astraeus could not reload after the profile change: {ex}");
            }
        }

        private void RefreshOptionsPage() {
            RaisePropertyChanged(nameof(IsPluginEnabled));
            RaisePropertyChanged(nameof(IsNinaThemeEnabled));
            AutopilotSettingsViewModel.RefreshAllProperties();
            AutopilotSettingsViewModel.RefreshDevices();
            CalibrationSettingsViewModel.RefreshAllProperties();
            FeedSettingsViewModel.RefreshAllProperties();
            AstraeusThemeMode.Raise();
        }

        public bool IsPluginEnabled {
            get => AstraeusSettings.IsPluginEnabled();
            set {
                AstraeusSettings.SetIsPluginEnabled(value);
                Observatory.Cadence.Signal();
                RaisePropertyChanged();
            }
        }

        public bool IsNinaThemeEnabled {
            get => AstraeusSettings.IsNinaThemeEnabled();
            set {
                AstraeusSettings.SetIsNinaThemeEnabled(value);
                // Re-dresses the dictionaries the page already uses, so the switch shows without
                // leaving the page.
                AstraeusThemeMode.Raise();
                RaisePropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null) {
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}