using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CosmicVaults.NINA.Astraeus.Engine;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NINA.Astrometry;
using NINA.Astrometry.Interfaces;
using NINA.Astrometry.RiseAndSet;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyDome;
using NINA.Equipment.Equipment.MyFilterWheel;
using NINA.Equipment.Equipment.MyFlatDevice;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Equipment.MyRotator;
using NINA.Equipment.Equipment.MySafetyMonitor;
using NINA.Equipment.Equipment.MySwitch;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Equipment.MyWeatherData;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.Interfaces;
using NINA.PlateSolving.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;

namespace CosmicVaults.NINA.Astraeus.Tests.Helpers {
    /// <summary>
    /// N.I.N.A. around the real plugin, as Astraeus.cs builds it on load: N.I.N.A.'s services as Moq mocks,
    /// its real settings store with nothing behind it, and each device's readings as a plain info object
    /// the test can set. The Observatory is built but never woken or started, so nothing here reaches a
    /// device, a file or the server.
    /// </summary>
    internal sealed class FakeNina {
        public static readonly Guid PluginGuid = new Guid("fd1225f8-142d-4cab-9262-8f423822d905");

        // N.I.N.A.'s real store, which is only dictionaries underneath. Nothing reaches a profile file.
        public PluginSettings PluginSettings { get; } = new PluginSettings();
        public FakeWebSocketBus Bus { get; } = new FakeWebSocketBus();

        /// <summary>The plugin's clock. Advance it instead of waiting.</summary>
        public FakeTimeProvider Clock { get; } = new FakeTimeProvider();

        /// <summary>The Astraeus server. Give it replies, then read the requests the plugin made.</summary>
        public FakeServerHandler Server { get; } = new FakeServerHandler();

        // Auto-mocking, so every settings object the profile hands out exists without a setup each.
        public Mock<IProfileService> ProfileService { get; } = new Mock<IProfileService> { DefaultValue = DefaultValue.Mock };
        public Mock<IOptionsVM> OptionsViewModel { get; } = new();

        public Mock<ITelescopeMediator> TelescopeMediator { get; } = new();
        public Mock<ICameraMediator> CameraMediator { get; } = new();
        public Mock<IFilterWheelMediator> FilterWheelMediator { get; } = new();
        public Mock<ISafetyMonitorMediator> SafetyMonitorMediator { get; } = new();
        public Mock<IWeatherDataMediator> WeatherDataMediator { get; } = new();
        public Mock<IFocuserMediator> FocuserMediator { get; } = new();
        public Mock<IFlatDeviceMediator> FlatDeviceMediator { get; } = new();
        public Mock<IDomeMediator> DomeMediator { get; } = new();
        public Mock<IRotatorMediator> RotatorMediator { get; } = new();
        public Mock<ISwitchMediator> SwitchMediator { get; } = new();
        public Mock<IGuiderMediator> GuiderMediator { get; } = new();
        public Mock<IImagingMediator> ImagingMediator { get; } = new();
        public Mock<IImageSaveMediator> ImageSaveMediator { get; } = new();
        public Mock<IImageDataFactory> ImageDataFactory { get; } = new();
        public Mock<IDomeFollower> DomeFollower { get; } = new();
        public Mock<INighttimeCalculator> NighttimeCalculator { get; } = new();
        public Mock<IPlateSolverFactory> PlateSolverFactory { get; } = new();

        // What each mediator reports. A test sets these before the code under test reads them.
        public TelescopeInfo TelescopeInfo { get; set; } = new TelescopeInfo();
        public CameraInfo CameraInfo { get; set; } = new CameraInfo();
        public FilterWheelInfo FilterWheelInfo { get; set; } = new FilterWheelInfo();
        public SafetyMonitorInfo SafetyMonitorInfo { get; set; } = new SafetyMonitorInfo();
        public WeatherDataInfo WeatherDataInfo { get; set; } = new WeatherDataInfo();
        public FocuserInfo FocuserInfo { get; set; } = new FocuserInfo();
        public FlatDeviceInfo FlatDeviceInfo { get; set; } = new FlatDeviceInfo();
        public DomeInfo DomeInfo { get; set; } = new DomeInfo();
        public RotatorInfo RotatorInfo { get; set; } = new RotatorInfo();
        public SwitchInfo SwitchInfo { get; set; } = new SwitchInfo();
        public GuiderInfo GuiderInfo { get; set; } = new GuiderInfo();

        public AstraeusSettings Settings { get; }
        public Observatory Observatory { get; }

        /// <summary>Every command the plugin sent a device, in order, as "device: command".</summary>
        public List<string> Commands { get; } = new();

        /// <summary>Raised for every command the fake drivers log, so a test can react mid-sequence.</summary>
        public event Action<string>? CommandIssued;

        private void Issue(string command) {
            Commands.Add(command);
            CommandIssued?.Invoke(command);
        }

        /// <summary>Every alert the plugin reported, signed in or not, in order.</summary>
        public List<Observatory.ReportedEvent> Alerts { get; } = new();

        /// <summary>Every log line that would reach the dashboard, in order.</summary>
        public List<Observatory.LogLine> Logs { get; } = new();

        /// <summary>The messages logged at one severity, like "WARNING" or "CRITICAL", in order.</summary>
        public List<string> LogMessages(string severity) =>
            Logs.Where(line => line.Severity == severity).Select(line => line.Message).ToList();

        public Mount Mount => Observatory.GetComponent<Mount>();
        public Dome Dome => Observatory.GetComponent<Dome>();
        public FlatPanel FlatPanel => Observatory.GetComponent<FlatPanel>();
        public SafetyMonitor SafetyMonitor => Observatory.GetComponent<SafetyMonitor>();
        public Guider Guider => Observatory.GetComponent<Guider>();
        public Camera Camera => Observatory.GetComponent<Camera>();

        public FakeNina() {
            IProfile profile = ProfileService.Object.ActiveProfile;
            Mock.Get(profile).Setup(activeProfile => activeProfile.PluginSettings).Returns(PluginSettings);
            // The Settings component enumerates and subscribes to this on Start, so it has to be a real collection.
            Mock.Get(profile.FilterWheelSettings).Setup(settings => settings.FilterWheelFilters)
                .Returns(new ObserveAllCollection<FilterInfo>());

            TelescopeMediator.Setup(mediator => mediator.GetInfo()).Returns(() => TelescopeInfo);
            CameraMediator.Setup(mediator => mediator.GetInfo()).Returns(() => CameraInfo);
            FilterWheelMediator.Setup(mediator => mediator.GetInfo()).Returns(() => FilterWheelInfo);
            SafetyMonitorMediator.Setup(mediator => mediator.GetInfo()).Returns(() => SafetyMonitorInfo);
            WeatherDataMediator.Setup(mediator => mediator.GetInfo()).Returns(() => WeatherDataInfo);
            FocuserMediator.Setup(mediator => mediator.GetInfo()).Returns(() => FocuserInfo);
            FlatDeviceMediator.Setup(mediator => mediator.GetInfo()).Returns(() => FlatDeviceInfo);
            DomeMediator.Setup(mediator => mediator.GetInfo()).Returns(() => DomeInfo);
            RotatorMediator.Setup(mediator => mediator.GetInfo()).Returns(() => RotatorInfo);
            SwitchMediator.Setup(mediator => mediator.GetInfo()).Returns(() => SwitchInfo);
            GuiderMediator.Setup(mediator => mediator.GetInfo()).Returns(() => GuiderInfo);

            // The fake drivers obey at once and log what they were told, so a test reads the commands in
            // order and no stop waits out the plugin's settle time.
            TelescopeMediator.Setup(mediator => mediator.StopSlew()).Callback(() => {
                Issue("mount: StopSlew");
                TelescopeInfo.Slewing = false;
            });
            TelescopeMediator
                .Setup(mediator => mediator.SetTrackingEnabled(It.IsAny<bool>()))
                .Callback<bool>(isEnabled => {
                    LogMountTracking(isEnabled);
                    TelescopeInfo.TrackingEnabled = isEnabled;
                })
                .Returns(true);
            FlatDeviceMediator.Setup(mediator => mediator.CloseCover(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .Callback(() => {
                    Issue("flat panel: CloseCover");
                    FlatDeviceInfo.CoverState = CoverState.Closed;
                })
                .Returns(Task.CompletedTask);

            Settings = new AstraeusSettings(ProfileService.Object, OptionsViewModel.Object, PluginGuid);
            Observatory = new Observatory(
                Settings,
                PlateSolverFactory.Object,
                TelescopeMediator.Object,
                CameraMediator.Object,
                FilterWheelMediator.Object,
                SafetyMonitorMediator.Object,
                WeatherDataMediator.Object,
                FocuserMediator.Object,
                FlatDeviceMediator.Object,
                DomeMediator.Object,
                RotatorMediator.Object,
                SwitchMediator.Object,
                ImagingMediator.Object,
                ImageSaveMediator.Object,
                ImageDataFactory.Object,
                GuiderMediator.Object,
                DomeFollower.Object,
                NighttimeCalculator.Object,
                clock: Clock,
                serverHandler: Server);
            Observatory.EventReported += Alerts.Add;
            Observatory.LogRecorded += Logs.Add;
        }

        /// <summary>The autopilot's context over the observatory's own components.</summary>
        public Context CreateContext() => new Context(
            Observatory,
            Observatory.GetComponent<Mount>(),
            Observatory.GetComponent<Camera>(),
            Observatory.GetComponent<FilterWheel>(),
            Observatory.GetComponent<Focuser>(),
            Observatory.GetComponent<Weather>(),
            Observatory.GetComponent<Rotator>(),
            Observatory.GetComponent<Guider>(),
            Observatory.GetComponent<FlatPanel>(),
            Observatory.GetComponent<SafetyMonitor>(),
            Observatory.GetComponent<Dome>(),
            Observatory.GetComponent<SwitchHub>());

        ///////////// Devices as N.I.N.A. would report them /////////////

        /// <summary>The mount's state. Disconnected when isConnected is false, whatever the rest says.</summary>
        public void SetMount(bool isConnected, bool isParked = false, bool isTracking = false, bool isSlewing = false) {
            TelescopeInfo.Connected = isConnected;
            TelescopeInfo.AtPark = isParked;
            TelescopeInfo.TrackingEnabled = isTracking;
            TelescopeInfo.Slewing = isSlewing;
            // A fresh info says tracking can't be set, which would make every mount read as already stopped.
            TelescopeInfo.CanSetTrackingEnabled = true;
            Mount.IsConnected = isConnected;
        }

        /// <summary>The dome connected with the shutter reading this, or disconnected when null.</summary>
        public void SetRoof(ShutterState? shutter) {
            DomeInfo.Connected = shutter != null;
            DomeInfo.ShutterStatus = shutter ?? ShutterState.ShutterNone;
            Dome.IsConnected = shutter != null;
        }

        /// <summary>The safety monitor connected and reading this, or disconnected when null.</summary>
        public void SetSafety(bool? isSafe) {
            SafetyMonitorInfo.Connected = isSafe != null;
            SafetyMonitorInfo.IsSafe = isSafe ?? false;
            SafetyMonitor.IsConnected = isSafe != null;
        }

        /// <summary>The camera connected, with a driver that logs an aborted exposure, or disconnected.</summary>
        public void SetCamera(bool isConnected) {
            CameraInfo.Connected = isConnected;
            Camera.IsConnected = isConnected;
            CameraMediator.Setup(mediator => mediator.AbortExposure()).Callback(() => Issue("camera: AbortExposure"));
        }

        /// <summary>The guider connected, with a driver that stops guiding at once, or disconnected.</summary>
        public void SetGuider(bool isConnected) {
            GuiderInfo.Connected = isConnected;
            Guider.IsConnected = isConnected;
            GuiderMediator
                .Setup(mediator => mediator.StopGuiding(It.IsAny<CancellationToken>()))
                .Returns(() => {
                    Issue("guider: StopGuiding");
                    return Task.FromResult(true);
                });
        }

        /// <summary>The flat panel connected with its cover reading this, or disconnected when null.</summary>
        public void SetFlatPanel(CoverState? cover) {
            FlatDeviceInfo.Connected = cover != null;
            FlatDeviceInfo.SupportsOpenClose = cover is not null and not CoverState.NotPresent;
            FlatDeviceInfo.CoverState = cover ?? CoverState.Unknown;
            FlatPanel.IsConnected = cover != null;
        }

        /// <summary>N.I.N.A.'s "Refuse unsafe shutter move" dome setting, which makes it refuse to move the shutter over an unparked mount.</summary>
        public void SetNinaRefusesShutterMoveWithUnparkedMount(bool value) {
            IDomeSettings settings = ProfileService.Object.ActiveProfile.DomeSettings;
            Mock.Get(settings).Setup(dome => dome.RefuseUnsafeShutterMove).Returns(value);
        }

        /// <summary>N.I.N.A.'s "Close on unsafe" dome setting.</summary>
        public void SetNinaClosesRoofOnUnsafe(bool value) {
            IDomeSettings settings = ProfileService.Object.ActiveProfile.DomeSettings;
            Mock.Get(settings).Setup(dome => dome.CloseOnUnsafe).Returns(value);
        }

        ///////////// Reconnects /////////////

        /// <summary>
        /// The night under way with auto-connect on and the dome configured, so a dome that drops gets the autopilot's one
        /// reconnect, answered by the given connect.
        /// </summary>
        public void LetDomeReconnect(Context context, Func<bool> connect) {
            context.IsStartupComplete = true;
            Settings.SetShouldAutoConnectEquipment(true);
            Dome.SetDefaultDevice("Dome 1", "test");
            Dome.ConnectDevice = _ => Task.FromResult(connect());
        }

        /// <summary>The same for the safety monitor.</summary>
        public void LetSafetyMonitorReconnect(Context context, Func<bool> connect) {
            context.IsStartupComplete = true;
            Settings.SetShouldAutoConnectEquipment(true);
            SafetyMonitor.SetDefaultDevice("Monitor 1", "test");
            SafetyMonitor.ConnectDevice = _ => Task.FromResult(connect());
        }

        /// <summary>N.I.N.A.'s meridian flip settings, which the meridian limit is read from.</summary>
        public void SetMeridianLimit(double maxMinutesAfterMeridian, double pauseMinutesBeforeMeridian = 0) {
            IMeridianFlipSettings settings = ProfileService.Object.ActiveProfile.MeridianFlipSettings;
            Mock.Get(settings).Setup(flip => flip.MaxMinutesAfterMeridian).Returns(maxMinutesAfterMeridian);
            Mock.Get(settings).Setup(flip => flip.PauseTimeBeforeMeridian).Returns(pauseMinutesBeforeMeridian);
        }

        /// <summary>A custom horizon in N.I.N.A.'s standard format, one "azimuth altitude" pair per line.</summary>
        public void SetCustomHorizon(string standardFormat) {
            IAstrometrySettings settings = ProfileService.Object.ActiveProfile.AstrometrySettings;
            Mock.Get(settings).Setup(astrometry => astrometry.Horizon)
                .Returns(CustomHorizon.FromReader_Standard(new StringReader(standardFormat)));
        }

        /// <summary>What the night-time calculator reports for today: the sun's, nautical and astronomical set and rise.</summary>
        public void SetTwilight(RiseAndSetEvent sun, RiseAndSetEvent nautical, RiseAndSetEvent astronomical) {
            DateTime date = Clock.GetUtcNow().UtcDateTime.Date;
            NighttimeData data = new NighttimeData(date, date, AstroUtil.MoonPhase.Unknown, null,
                twilightRiseAndSet: astronomical, nauticalTwilightRiseAndSet: nautical, sunRiseAndSet: sun,
                moonRiseAndSet: sun, civilTwilightRiseAndSet: sun);
            NighttimeCalculator.Setup(calculator => calculator.Calculate(It.IsAny<DateTime?>())).Returns(data);
        }

        /// <summary>A setting as the plugin would read it, or null when nothing is stored under the key.</summary>
        public string? StoredSetting(string key) =>
            PluginSettings.TryGetValue(PluginGuid, key, out string value) ? value : null;

        public void StoreSetting(string key, string value) => PluginSettings.SetValue(PluginGuid, key, value);

        ///////////// Drivers that park /////////////

        /// <summary>A mount driver that parks at once: AtPark, not slewing, tracking off. Logged as "mount: Park".</summary>
        public void MountParksAtOnce() => MountParkAnswers(true, isAtPark: true);

        /// <summary>A mount driver whose park call answers as given and leaves the mount as given, logged as "mount: Park".</summary>
        public void MountParkAnswers(bool answer, bool isAtPark = false, bool isSlewing = false) {
            TelescopeMediator
                .Setup(mediator => mediator.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .Returns(() => {
                    Issue("mount: Park");
                    TelescopeInfo.AtPark = isAtPark;
                    TelescopeInfo.Slewing = isSlewing;
                    if (isAtPark) TelescopeInfo.TrackingEnabled = false;
                    return Task.FromResult(answer);
                });
        }

        /// <summary>
        /// A mount driver whose park keeps slewing until FinishPark, or until the park is cancelled, which it answers by
        /// throwing like N.I.N.A.'s own wait does.
        /// </summary>
        public void MountParksSlowly() {
            TelescopeMediator
                .Setup(mediator => mediator.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .Returns<IProgress<ApplicationStatus>, CancellationToken>((_, token) => {
                    Issue("mount: Park");
                    TelescopeInfo.Slewing = true;
                    TaskCompletionSource<bool> park = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    token.Register(() => park.TrySetCanceled(token));
                    _pendingPark = park;
                    return park.Task;
                });
        }

        private TaskCompletionSource<bool>? _pendingPark;

        /// <summary>The slow park reaches the park position and the driver answers true.</summary>
        public void FinishPark() {
            SetMount(isConnected: true, isParked: true);
            _pendingPark?.TrySetResult(true);
        }

        ///////////// Drivers that move the roof /////////////

        /// <summary>A dome driver that closes the shutter at once, logged as "dome: CloseShutter".</summary>
        public void RoofClosesAtOnce() => RoofCloseLeavesTheShutter(ShutterState.ShutterClosed);

        /// <summary>
        /// A dome driver that answers the close with "done" and leaves the shutter reading open, as N.I.N.A. does for a
        /// roof that stalls.
        /// </summary>
        public void RoofClosesButStillReadsOpen() => RoofCloseLeavesTheShutter(ShutterState.ShutterOpen);

        private void RoofCloseLeavesTheShutter(ShutterState shutter) {
            DomeMediator
                .Setup(mediator => mediator.CloseShutter(It.IsAny<CancellationToken>()))
                .Returns(() => {
                    Issue("dome: CloseShutter");
                    DomeInfo.ShutterStatus = shutter;
                    return Task.FromResult(true);
                });
        }

        /// <summary>A dome driver that opens the shutter at once, logged as "dome: OpenShutter".</summary>
        public void RoofOpensAtOnce() => RoofOpenLeavesTheShutter(ShutterState.ShutterOpen);

        /// <summary>A dome driver that answers the open with "done" and leaves the shutter reading as given.</summary>
        public void RoofOpenLeavesTheShutter(ShutterState shutter) {
            DomeMediator
                .Setup(mediator => mediator.OpenShutter(It.IsAny<CancellationToken>()))
                .Returns(() => {
                    Issue("dome: OpenShutter");
                    DomeInfo.ShutterStatus = shutter;
                    return Task.FromResult(true);
                });
        }

        ///////////// Drivers that misbehave /////////////

        /// <summary>A dome whose every read throws, as a driver that has lost its controller does.</summary>
        public void DomeReadThrows(string message) =>
            DomeMediator.Setup(mediator => mediator.GetInfo()).Throws(new InvalidOperationException(message));

        /// <summary>The dome reads again after DomeReadThrows.</summary>
        public void DomeReadsAgain() => DomeMediator.Setup(mediator => mediator.GetInfo()).Returns(() => DomeInfo);

        /// <summary>A night-time calculation that throws, so the observing window can't be worked out.</summary>
        public void NightCalculationThrows(string message) =>
            NighttimeCalculator.Setup(calculator => calculator.Calculate(It.IsAny<DateTime?>()))
                .Throws(new InvalidOperationException(message));

        /// <summary>
        /// A mount driver that acknowledges a tracking stop and does nothing about it, so the plugin's settle
        /// runs out. The command is still logged.
        /// </summary>
        public void MountIgnoresTrackingStops() {
            TelescopeMediator
                .Setup(mediator => mediator.SetTrackingEnabled(It.IsAny<bool>()))
                .Callback<bool>(isEnabled => LogMountTracking(isEnabled))
                .Returns(true);
        }

        private void LogMountTracking(bool isEnabled) =>
            Issue($"mount: SetTrackingEnabled({(isEnabled ? "true" : "false")})");

        ///////////// The clock /////////////

        /// <summary>
        /// The clock at this local time on the night of 1 March, with sunset 18:00, nautical dusk 19:00, astronomical dusk
        /// 20:00, and the dawns at 04:00, 05:00 and 06:00 the next morning.
        /// </summary>
        public void SetTypicalNight(DateTime localNow) {
            Clock.SetUtcNow(new DateTimeOffset(localNow, TimeSpan.Zero));
            SetTwilight(
                sun: new FixedRiseAndSet(set: new DateTime(2026, 3, 1, 18, 0, 0), rise: new DateTime(2026, 3, 2, 6, 0, 0)),
                nautical: new FixedRiseAndSet(set: new DateTime(2026, 3, 1, 19, 0, 0), rise: new DateTime(2026, 3, 2, 5, 0, 0)),
                astronomical: new FixedRiseAndSet(set: new DateTime(2026, 3, 1, 20, 0, 0), rise: new DateTime(2026, 3, 2, 4, 0, 0)));
        }

        /// <summary>
        /// Moves the clock in steps until the task finishes or the budget is spent, yielding a millisecond of real
        /// time between steps so the code under test can schedule its next wait. Returns whether it finished.
        /// </summary>
        public async Task<bool> AdvanceUntilAsync(Task task, TimeSpan step, TimeSpan budget) {
            TimeSpan advanced = TimeSpan.Zero;
            while (!task.IsCompleted && advanced < budget) {
                Clock.Advance(step);
                advanced += step;
                await Task.Delay(1);
            }
            return task.IsCompleted;
        }
    }
}