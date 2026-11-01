using CosmicVaults.NINA.Astraeus.Engine.Components;
using NINA.Astrometry;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot
{
    internal sealed partial class Context
    {
        // Manual devices - The autopilot will refuse to work with manual devices.
        private static readonly HashSet<string> ManualDeviceIds = new() { "Manual Filter Wheel", "Manual Rotator" };
        public static bool IsManualDevice(string? deviceId) => deviceId != null && ManualDeviceIds.Contains(deviceId);

        /// <summary>Raised whenever something the dashboard shows about this run changes.</summary>
        public event EventHandler? OnStatusChanged;

        public AutopilotStatus AutopilotStatus
        {
            get => _autopilotStatus;
            set
            {
                if (value == _autopilotStatus) return;
                _autopilotStatus = value;
                OnStatusChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        private AutopilotStatus _autopilotStatus = AutopilotStatus.Idle;
        private const string Device = "autopilot";

        public Observatory Observatory { get; }

        /// <summary>The time every decision here is made against. See Observatory.Clock.</summary>
        public DateTime UtcNow => Observatory.UtcNow;

        public Mount? Mount { get; }
        public Camera? Camera { get; }
        public FilterWheel? FilterWheel { get; }
        public Focuser? Focuser { get; }
        public Weather? Weather { get; }
        public Rotator? Rotator { get; }
        public Guider? Guider { get; }
        public FlatPanel? FlatPanel { get; }
        public SafetyMonitor? SafetyMonitor { get; }
        public Dome? Dome { get; }
        public SwitchHub? SwitchHub { get; }

        public Context(
            Observatory observatory,
            Mount? mount,
            Camera? camera,
            FilterWheel? filterWheel,
            Focuser? focuser,
            Weather? weather,
            Rotator? rotator,
            Guider? guider,
            FlatPanel? flatPanel,
            SafetyMonitor? safetyMonitor,
            Dome? dome,
            SwitchHub? switchHub)
        {
            Observatory = observatory;
            Mount = mount;
            Camera = camera;
            FilterWheel = filterWheel;
            Focuser = focuser;
            Weather = weather;
            Rotator = rotator;
            Guider = guider;
            FlatPanel = flatPanel;
            SafetyMonitor = safetyMonitor;
            Dome = dome;
            SwitchHub = switchHub;
            Night = new NightSession(this);
            Roof = new RoofState();
            Devices = new DeviceReconnector(this);
            _frame = new Frame(this, null);
        }

        ///////////// The frame in hand /////////////

        /// <summary>
        /// The frame in hand, or an empty one between frames, so it is never null. PollForTarget replaces
        /// it whole, and the dashboard is told, since nothing else changes until the slew.
        /// </summary>
        public Frame Frame
        {
            get => _frame;
            set
            {
                if (ReferenceEquals(value, _frame)) return;
                _frame = value;
                RaiseStatusChanged();
            }
        }
        private Frame _frame;

        internal void RaiseStatusChanged() => OnStatusChanged?.Invoke(this, EventArgs.Empty);

        /// <summary>Puts back a device that dropped, once per cooldown. Lives for the run.</summary>
        public DeviceReconnector Devices { get; }

        ///////////// Meridian flip /////////////

        /// <summary>
        /// Whether a meridian limit on the current target is fixed by a flip, not by refusing the frame.
        /// If the server pinned the frame to the side we're already on, no flip happens and the limit applies.
        /// </summary>
        public bool CanFlipForCurrentTarget =>
            IsMeridianFlipEnabled
            && Mount is { IsGermanEquatorial: true, IsPierSideKnown: true } mount
            && Frame.RequiredPierSide != mount.SideOfPier;

        /// <summary>
        /// The mount's side of pier for the /next/ query, sent even with flips off. Null while parked or not
        /// tracking, since a resting mount reports the park side, which says nothing about the next slew.
        /// </summary>
        public string? SideOfPierForDispatch =>
            Mount is { IsPierSideKnown: true, IsParked: false, IsTracking: true } mount
                ? mount.SideOfPier.ToString() : null;

        /// <summary>
        /// The project and position of the last frame delivered. A pinned side the mount is already on can
        /// only be honoured while it's still tracking that target, as any other slew lands counterweight-down.
        /// </summary>
        public int? LastDeliveredProjectId { get; set; }
        public Coordinates? LastDeliveredCoordinates { get; set; }

        public int? TrackingProjectIdForDispatch =>
            LastDeliveredProjectId is int projectId && LastDeliveredCoordinates is { } coordinates
            && Mount is { } mount && mount.IsPointingAt(coordinates, RecenterToleranceDegrees)
                ? projectId : null;

        /// <summary>The mount's J2000 position now, for the server to score the slew to each candidate.</summary>
        public Coordinates? TelescopePositionForDispatch => Mount?.CurrentCoordinatesJ2000();

        /// <summary>The loaded N.I.N.A. profile, in the same text as the profile list, which the server matches exactly.</summary>
        public string? ProfileIdForDispatch => Observatory.Settings.ProfileService.ActiveProfile?.Id.ToString();

        ///////////// Rotator /////////////

        /// <summary>
        /// The sky position angle to image the current target at, or null. The target's own angle wins,
        /// then the configured fallback in RotatorFallbackMode.SkyPositionAngle mode.
        /// </summary>
        public double? RequestedSkyAngle()
        {
            // Fold into [0, 360) so every consumer and log line sees the angle the rotator is driven to.
            // The server's value never goes through the settings setter, so this is its only guard.
            if (Frame.Target?.PositionAngle is double positionAngle)
            {
                return AstroUtil.EuclidianModulus(positionAngle, 360);
            }
            return RotatorFallbackMode == RotatorFallbackMode.SkyPositionAngle
                ? AstroUtil.EuclidianModulus(RotatorFallbackAngle, 360)
                : null;
        }

        /// <summary>
        /// The mechanical angle to park the rotator at, or null. Only the fallback sets one, since a
        /// target's angle is always a sky angle and takes precedence.
        /// </summary>
        public double? RequestedMechanicalAngle()
        {
            if (Frame.Target?.PositionAngle is not null) return null;
            return RotatorFallbackMode == RotatorFallbackMode.MechanicalAngle
                ? AstroUtil.EuclidianModulus(RotatorFallbackAngle, 360)
                : null;
        }

        /// <summary>
        /// Frames reported but not yet calibrated or uploaded, oldest first. A frame stays until it's
        /// finished, so work cut short by a secure is picked up before the next frame. Whatever is left
        /// when the run ends is dropped, and the server re-offers it after its upload timeout.
        /// </summary>
        public List<PendingFrame> PendingFrames { get; } = new();

        /// <summary>
        /// Keeps every queued frame on this machine, for when cloud upload is off, the account is full
        /// or we're signed out. Calibration already done stays done.
        /// </summary>
        public void DropPendingUploads()
        {
            foreach (PendingFrame frame in PendingFrames) frame.Upload = null;
            PendingFrames.RemoveAll(frame => frame.IsFinished);
        }

        /// <summary>
        /// A null LastAutofocusTimeUtc marks the start-of-night focus as due. ResetAutofocusTracking
        /// clears it at dusk.
        /// </summary>
        public DateTime? LastAutofocusTimeUtc { get; private set; }
        public double? LastAutofocusTemperature { get; private set; }
        public string? LastAutofocusFilter { get; private set; }

        public void ResetAutofocusTracking()
        {
            LastAutofocusTimeUtc = null;
            LastAutofocusTemperature = null;
            LastAutofocusFilter = null;
        }

        public void RecordAutofocus()
        {
            LastAutofocusTimeUtc = UtcNow;
            LastAutofocusTemperature = GetAmbientTemperature();
            LastAutofocusFilter = FilterWheel?.GetCurrentFilter()?.Name;
        }

        /// <summary>
        /// Ambient temperature in °C for the autofocus temperature trigger. Uses the focuser's probe like
        /// N.I.N.A. does, then the weather sensor. Null when neither reports one.
        /// </summary>
        public double? GetAmbientTemperature() => Focuser?.GetTemperature() ?? Weather?.GetTemperature();

        public int NextPollDelaySeconds { get; set; } = AutopilotConstants.DefaultRetryDelaySeconds;

        /// <summary>
        /// Refuses the current frame before it's marked started. Logs why, reports it failed so it shows
        /// in the project's history, and drops the target so the machine goes back to polling.
        /// </summary>
        public async Task RefuseCurrentFrameAsync(string reason)
        {
            AutopilotNextResponse? target = Frame.Target;
            LogError($"{reason}. Not taking the frame on {target?.TargetName ?? "the current target"}.");
            NextPollDelaySeconds = AutopilotConstants.DefaultRetryDelaySeconds;
            if (target?.ImageId is int imageId)
            {
                // No client_ref, since /start/ was never called for this image.
                await Observatory.AstraeusWebClient.PostAutopilotFailAsync(imageId, reason, null);
            }
            Frame.Release();
        }

        /// <summary>
        /// Reports the current image failed if it was marked started on the server, so it doesn't sit
        /// CAPTURING. Images never started stay PENDING and are re-offered on the next poll.
        /// </summary>
        public async Task ReportStartedImageFailedAsync(string reason)
        {
            if (!Frame.IsStarted || Frame.Target?.ImageId is not int imageId) return;
            await Observatory.AstraeusWebClient.PostAutopilotFailAsync(imageId, reason, Frame.ClientRef);
            Frame.IsStarted = false;
            Frame.ClientRef = null;
        }

        /// <summary>
        /// True when the last frame's plate solve landed further off target than the tolerance. It lives
        /// here rather than on the frame because it is set after the frame and read by the next one, and a
        /// poll replaces the frame between the two.
        /// </summary>
        public bool IsLastFrameOffTarget { get; set; }

        ///////////// Guiding for the frame in hand /////////////

        /// <summary>
        /// Stops guiding before a focuser move when the observer's N.I.N.A. setting says to. With Smart
        /// Autofocus on, N.I.N.A.'s "disable guiding on filter change" never runs, since its filter offsets
        /// are off. Never throws except on cancellation.
        /// </summary>
        public async Task StopGuidingForFocusMoveAsync(int position, bool shouldStopGuiding,
            CancellationToken cancellationToken)
        {
            if (!shouldStopGuiding || Guider is not { IsConnected: true, IsGuiding: true } guider) return;
            if (Focuser?.Position is not int currentPosition || currentPosition == position) return;
            Log($"Stopping guiding while the focuser moves from {currentPosition} to {position}.");
            if (await guider.StopGuidingAsync(cancellationToken) == GuidingStopOutcome.NotConfirmed)
            {
                LogWarning("Guider did not confirm it stopped. Moving the focuser anyway.");
            }
        }

        // Every autopilot log goes through these so the device and category are always right.
        public void Log(string message) => Observatory.Log(message, Device, LogCategory.Autopilot);
        public void LogWarning(string message) => Observatory.LogWarning(message, Device, LogCategory.Autopilot);
        public void LogError(string message) => Observatory.LogError(message, Device, LogCategory.Autopilot);
        public void LogDebug(string message) => Observatory.LogDebug(message, Device, LogCategory.Autopilot);

        /// <summary>
        /// Logs at critical and emails the observers, for faults someone has to act on. The message's
        /// first line is the server's dedup key, so keep it fixed and put what varies below it or in details.
        /// </summary>
        public void ReportCritical(string message, object? details = null)
        {
            Observatory.LogCritical(message, Device, LogCategory.Autopilot);
            Observatory.ReportEvent(NotificationKind.CriticalError, message, details,
                                    Guid.NewGuid().ToString("N"));
        }

        public static string FormatElapsed(Stopwatch watch) => FormatElapsed(watch.Elapsed);
        public static string FormatElapsed(TimeSpan elapsed) => $"{elapsed.TotalSeconds:F1}s";

        /// <summary>How long since a moment read from UtcNow, as "12.3s", for the log.</summary>
        public string ElapsedSince(DateTime startedUtc) => FormatElapsed(UtcNow - startedUtc);

        /// <summary>False when there's nothing to run or it couldn't run.</summary>
        public async Task<bool> RunSequenceAsync(string? path, CancellationToken cancellationToken)
        {
            SequenceRunner? runner = Observatory.SequenceRunner;
            if (runner == null || string.IsNullOrWhiteSpace(path)) return false;
            return await runner.RunAsync(path, cancellationToken);
        }

        public void DisableAutopilot()
        {
            if (Observatory.TryGetComponent<Autopilot>(out Autopilot? autopilot))
            {
                autopilot.IsEnabled = false;
            }
        }

        /// <summary>False with no safety monitor at all.</summary>
        public bool IsSafe()
        {
            return SafetyMonitor != null && SafetyMonitor.IsSafe();
        }

        public string SafetyMonitorName => SafetyMonitor?.DeviceName ?? "no safety monitor";

        /// <summary>
        /// Why the mount's position can't be read, or null. Quiet until the night's session has started
        /// and the connect chain has finished, since before then a mount that isn't powered yet is normal.
        /// </summary>
        public string? DescribeUnknownMountPosition() =>
            Night.IsStartupComplete && Night.IsOpen ? Mount?.DescribeUnknownPosition() : null;

        /// <summary>
        /// The "Mount limit reached on ..." line the last breach was logged with, so the secure state
        /// reports the same cause to the server. Null until a breach.
        /// </summary>
        public string? LastLimitBreach { get; set; }

        /// <summary>
        /// The secure's result checked again now, for anything that reports it later: the mount still reads
        /// parked and the roof still reads closed or isn't ours. A device that has dropped since, or a mount
        /// or roof moved by hand, makes it false, since unknown is never secured. A mount the dawn disconnect
        /// let go of while parked still counts as parked until it is connected again.
        /// </summary>
        public bool IsStillSecured =>
            Night.IsSecured && IsMountStillParked() && IsRoofClosedOrNotOurs();

        private bool IsMountStillParked() =>
            Mount is { IsConnected: true, IsParked: true }
            || (Night.HasDisconnectedParkedMount && Mount is { IsConnected: false });

        /// <summary>
        /// True for a roof this autopilot doesn't work. One it works counts only when it reads Closed: a dome
        /// that isn't connected leaves the roof's state unknown, and unknown is never closed.
        /// </summary>
        public bool IsRoofClosedOrNotOurs()
        {
            if (RoofMode != RoofMode.Operate && !Roof.HasOpenedTonight) return true;
            return Dome?.IsShutterClosed() == true;
        }

        ///////////// The night /////////////

        /// <summary>
        /// The night under way, or the placeholder before the first dusk, so it is never null. A session is
        /// one night, not one switch-on, so a week left enabled reports seven pairs. StartSessionAsync
        /// replaces it.
        /// </summary>
        public NightSession Night { get; private set; }

        /// <summary>
        /// Opens tonight's session: a fresh night, so every per-night flag starts clean, then the started
        /// email. Each night is judged on its own.
        /// </summary>
        public async Task StartSessionAsync()
        {
            if (Night.IsOpen) LogWarning($"Autopilot session {Night.Id} was still open when tonight's started.");
            // Tonight's connect chain hasn't run yet, so a disconnected device hasn't dropped. No
            // reconnects and no device-lost emails while the chain's waits poll.
            Night = new NightSession(this);
            Roof = new RoofState();
            await Night.StartAsync();
        }

        /// <summary>
        /// Closes the night, see NightSession.ReportEnd. EndSession at dawn and the machine teardown both
        /// call it, and only the first reports.
        /// </summary>
        public void ReportSessionEnd(string? faultReason = null, string? stopReason = null) =>
            Night.ReportEnd(faultReason, stopReason);

        ///////////// The roof /////////////

        /// <summary>What the run knows about the roof. Replaced with the night, so it is never null.</summary>
        public RoofState Roof { get; private set; }

        /// <summary>
        /// Whether a roof we're imaging under has stopped reading open, even while the safety monitor
        /// reads Safe. Anything but a shutter reading Open counts, including a disconnected dome, since
        /// then we can't confirm the roof is open.
        /// </summary>
        public bool IsRoofClosedUnexpectedly() =>
            RoofMode != RoofMode.Ignore && Roof.IsExpectedOpen && Dome?.IsShutterOpen() != true;

        /// <summary>
        /// When the current run of Safe readings began during WaitForSafe's settle, otherwise null. The
        /// dashboard counts the settle up from it, since the log heartbeat only updates every Nth poll.
        /// </summary>
        public DateTime? SafeSinceUtc
        {
            get => _safeSinceUtc;
            set
            {
                if (value == _safeSinceUtc) return;
                _safeSinceUtc = value;
                OnStatusChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        private DateTime? _safeSinceUtc;

        /// <summary>
        /// Whether it's "daylight" for the operating window. Observatory owns the calculation so the
        /// Dome's roof watchdog gets the same answer.
        /// </summary>
        public bool IsOutsideObservingWindow() => Observatory.IsOutsideObservingWindow();
    }
}