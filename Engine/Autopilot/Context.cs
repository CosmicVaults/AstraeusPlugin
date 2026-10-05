using CosmicVaults.NINA.Astraeus.Engine.Components;
using CosmicVaults.NINA.Astraeus.Engine.Utility;
using NINA.Astrometry;
using NINA.Core.Enum;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot {
    internal sealed class Context {
        public const int DefaultRetryDelaySeconds = 60;
        public const int SafetyPollIntervalSeconds = 30;
        // Faster while the safe settle runs, so we resume close to when it ends.
        public const int SafetySettlePollIntervalSeconds = 10;

        public const int SafetyWatchIntervalSeconds = 5;

        // Slewing, centring and focusing come before the shutter opens, so the limit look-ahead adds
        // this to the exposure. The server has its own setup margin and the two may drift apart, but
        // this is the one that decides whether the mount actually goes anywhere.
        public const int LimitLookAheadMarginSeconds = 300;

        // Assumed exposure when a target has none, just so the look-ahead has a length.
        public const double DefaultExposureSeconds = 60.0;

        // How many times a secure tries to close the roof, and the pause between tries. N.I.N.A.
        // reports a close as done whatever the shutter did, so we read the shutter back after each
        // try. A driver stuck on "closing" gets another go before anyone is emailed.
        public const int RoofCloseAttempts = 3;
        public const int RoofCloseRetrySeconds = 30;

        // How often WaitForSafe has another go at a roof the secure could not close.
        public static readonly TimeSpan RoofRecloseCooldown = TimeSpan.FromMinutes(5);

        // How long to leave a device that wouldn't reconnect before trying again. The waits poll
        // every 30 seconds, and a missing driver shouldn't be asked twice a minute all night.
        public static readonly TimeSpan ReconnectCooldown = TimeSpan.FromMinutes(5);

        // How long one reconnect gets before we press N.I.N.A.'s own Cancel on it. N.I.N.A. waits
        // on a driver's connect with no time limit.
        public static readonly TimeSpan ReconnectTimeout = TimeSpan.FromSeconds(60);

        // The most a secure may take (see States.SecureObservatory). It covers N.I.N.A.'s ten-minute
        // park, every roof close attempt at the Dome's move timeout, one flat panel cover move, a
        // reconnect each for the mount, dome and panel, and some margin.
        public static readonly TimeSpan SecureTimeout = TimeSpan.FromMinutes(35);

        /// <summary>
        /// How far ahead a frame commits the mount, its exposure plus setup. The limit check and the
        /// meridian-flip decision must use the same window. If they disagree we either refuse a frame for
        /// a limit nothing would fix, or cross a limit with no flip scheduled.
        /// </summary>
        public static TimeSpan FrameWindow(AutopilotNextResponse? target) =>
            TimeSpan.FromSeconds((target?.Exposure ?? DefaultExposureSeconds) + LimitLookAheadMarginSeconds);

        // The pre-slew limit check has to guess how long setup takes, and autofocus can blow past
        // it. So we check again over just the exposure right before it starts.
        public const double ExposureSettleSeconds = 30.0;

        /// <summary>
        /// The exposure plus a settle allowance. That's all that's left between the last limit check
        /// and the shutter closing.
        /// </summary>
        public static TimeSpan ExposureWindow(AutopilotNextResponse? target) =>
            TimeSpan.FromSeconds((target?.Exposure ?? DefaultExposureSeconds) + ExposureSettleSeconds);

        // Waiting states log a "still waiting" line every Nth poll (10 x 30s = 5 minutes), so a long
        // wait shows it's alive without a line every 30 seconds.
        public const int WaitHeartbeatPolls = 10;

        // Free space the image directory needs before a frame starts. Well over one frame, so a long
        // sub can't half-write into a full disk.
        public const double MinimumFreeDiskGb = 5.0;

        // Before a frame, a target this close to where the mount is tracking counts as the same field,
        // so we skip the slew and plate solve. After a frame, a solve further off than this makes the
        // next frame re-centre. Set in the Autopilot settings, default 2.0'.
        public double RecenterToleranceArcmin => Observatory.Settings.GetRecenterToleranceArcmin();
        public double RecenterToleranceDegrees => RecenterToleranceArcmin / 60.0;

        /// <summary>
        /// N.I.N.A.'s centring threshold ("Pointing Tolerance", in arcminutes). Centring stops anywhere
        /// inside it, so we can't ask a centred frame for anything tighter.
        /// </summary>
        public double PointingToleranceArcmin =>
            Observatory.Settings.ProfileService.ActiveProfile.PlateSolveSettings.Threshold;

        /// <summary>Raised whenever something the dashboard shows about this run changes.</summary>
        public event EventHandler? OnStatusChanged;

        public AutopilotStatus AutopilotStatus {
            get => _autopilotStatus;
            set {
                if (value == _autopilotStatus) return;
                _autopilotStatus = value;
                OnStatusChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        private AutopilotStatus _autopilotStatus = AutopilotStatus.Idle;
        private const string Device = "autopilot";

        public Observatory Observatory { get; }

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
            SwitchHub? switchHub) {
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
        }

        ///////////// Live configuration /////////////
        // Re-read on every access, so changes made mid-run apply.
        public RoofMode RoofMode => Observatory.Settings.GetAutopilotRoofMode();
        public bool ShouldAutoConnectEquipment => Observatory.Settings.ShouldAutoConnectEquipment();
        public string? StartupSequencePath => Observatory.Settings.GetAutopilotStartupSequencePath();
        public string? ShutdownSequencePath => Observatory.Settings.GetAutopilotShutdownSequencePath();
        public OperatingWindow OperatingWindow => Observatory.Settings.GetAutopilotOperatingWindow();

        /// <summary>
        /// Whether the autopilot cools the camera to CameraCoolingTemperature at dusk and warms it at
        /// dawn, over N.I.N.A.'s cooling and warming durations.
        /// </summary>
        public bool IsCameraCoolingEnabled => Observatory.Settings.IsCameraCoolingEnabled();

        /// <summary>The temperature (°C) the autopilot cools the camera to.</summary>
        public double CameraCoolingTemperature => Observatory.Settings.GetCameraCoolingTemperature();
        public bool IsMountLimitEnabled => Observatory.Settings.IsMountLimitEnabled();

        ///////////// Meridian flip (live) /////////////

        /// <summary>
        /// Whether the observer lets the autopilot flip the mount. Off by default, and when it's off a
        /// target past the meridian limit is refused.
        /// </summary>
        public bool IsMeridianFlipEnabled => Observatory.Settings.IsMeridianFlipEnabled();

        /// <summary>
        /// The pier side the server pinned for this frame, or null when the choice is ours. A value we
        /// can't parse reads as null, since we shouldn't act on an orientation we don't understand.
        /// </summary>
        public PierSide? RequiredPierSide => CurrentTarget?.RequiredPierSide switch {
            "pierEast" => PierSide.pierEast,
            "pierWest" => PierSide.pierWest,
            _ => null,
        };

        /// <summary>
        /// Whether a meridian limit on the current target is fixed by a flip, not by refusing the frame.
        /// If the server pinned the frame to the side we're already on, no flip happens and the limit applies.
        /// </summary>
        public bool CanFlipForCurrentTarget =>
            IsMeridianFlipEnabled
            && Mount is { IsGermanEquatorial: true, IsPierSideKnown: true } mount
            && RequiredPierSide != mount.SideOfPier;

        // Set when a target is assigned. The server judged its window from then.
        public DateTime TargetAssignedAtUtc { get; set; }

        // Setup has outrun the margin the server allowed for, so a limit refusal now is a
        // timing miss for the server to re-judge, not a fault for the retry ladder.
        public bool HasServerWindowExpired =>
            (DateTime.UtcNow - TargetAssignedAtUtc).TotalSeconds > LimitLookAheadMarginSeconds;

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

        ///////////// Dome safety /////////////
        // From the N.I.N.A. profile. N.I.N.A. enforces these in DomeVM and TelescopeVM, so we can only
        // avoid walking into a refusal. On close it parks the mount itself (ParkMountBeforeShutterMove).
        // On open it refuses without parking, since parking could drive the OTA into a closed roof, so
        // opening is ours to arrange.

        /// <summary>N.I.N.A. refuses to move the shutter while the mount is unparked or disconnected.</summary>
        public bool IsShutterMoveRefusedWithUnparkedMount =>
            Observatory.Settings.ProfileService.ActiveProfile.DomeSettings.RefuseUnsafeShutterMove;

        /// <summary>N.I.N.A. refuses to unpark the mount unless the shutter is open.</summary>
        public bool IsUnparkRefusedWithoutOpenShutter =>
            Observatory.Settings.ProfileService.ActiveProfile.DomeSettings.RefuseUnparkWithoutShutterOpen;

        /// <summary>
        /// N.I.N.A. closes the shutter itself when the safety monitor reports unsafe ("Close on unsafe"
        /// in the Dome settings). The autopilot never closes an idle roof, so this is the only thing that does.
        /// </summary>
        public bool IsRoofClosedOnUnsafe =>
            Observatory.Settings.ProfileService.ActiveProfile.DomeSettings.CloseOnUnsafe;

        /// <summary>
        /// Whether the autopilot may park the mount so the roof can open when
        /// IsShutterMoveRefusedWithUnparkedMount is on. Off by default (see Autopilot.ShouldParkMountToOpenRoof).
        /// </summary>
        public bool ShouldParkMountToOpenRoof => Observatory.Settings.ShouldParkMountToOpenRoof();

        public FlatPanelMode FlatPanelMode => Observatory.Settings.GetAutopilotFlatPanelMode();

        public bool ShouldOperateFlatPanelWithRoofClosed => Observatory.Settings.ShouldOperateFlatPanelWithRoofClosed();

        ///////////// Cloud upload (live) /////////////
        /// <summary>
        /// Whether the frame and its preview go to cloud storage. States.CheckCloudUpload reads it per
        /// frame, so a change mid-night applies from the next capture.
        /// </summary>
        public bool IsCloudUploadEnabled => Observatory.Settings.IsCloudUploadEnabled();

        ///////////// Autofocus (live) /////////////
        public bool IsAutofocusEnabled => Observatory.Settings.IsAutofocusEnabled();
        public bool ShouldAutofocusOnTemperatureChange => Observatory.Settings.ShouldAutofocusOnTemperatureChange();
        public double AutofocusTemperatureThreshold => Observatory.Settings.GetAutofocusTemperatureThreshold();
        public bool ShouldAutofocusOnFilterChange => Observatory.Settings.ShouldAutofocusOnFilterChange();
        public bool ShouldAutofocusOnTimeInterval => Observatory.Settings.ShouldAutofocusOnTimeInterval();
        public int AutofocusIntervalMinutes => Observatory.Settings.GetAutofocusIntervalMinutes();
        
        ///////////// Rotator (live) /////////////
        public RotatorFallbackMode RotatorFallbackMode => Observatory.Settings.GetAutopilotRotatorFallbackMode();
        public double RotatorFallbackAngle => Observatory.Settings.GetAutopilotRotatorFallbackAngle();

        /// <summary>
        /// How close in degrees the rotator must get to the requested angle. Read from the N.I.N.A.
        /// profile so it matches N.I.N.A.'s own Center-and-Rotate.
        /// </summary>
        public double RotationToleranceDegrees =>
            Observatory.Settings.ProfileService.ActiveProfile.PlateSolveSettings.RotationTolerance;

        /// <summary>
        /// The sky position angle to image the current target at, or null. The target's own angle wins,
        /// then the configured fallback in RotatorFallbackMode.SkyPositionAngle mode.
        /// </summary>
        public double? RequestedSkyAngle() {
            // Fold into [0, 360) so every consumer and log line sees the angle the rotator is driven to.
            // The server's value never goes through the settings setter, so this is its only guard.
            if (CurrentTarget?.PositionAngle is double positionAngle) {
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
        public double? RequestedMechanicalAngle() {
            if (CurrentTarget?.PositionAngle is not null) return null;
            return RotatorFallbackMode == RotatorFallbackMode.MechanicalAngle
                ? AstroUtil.EuclidianModulus(RotatorFallbackAngle, 360)
                : null;
        }

        /// <summary>
        /// When on, the server owns the autofocus decision and per-filter focus positions, and the local
        /// triggers are ignored. If the server can't be reached the frame is taken with no focus change.
        /// </summary>
        public bool IsSmartAutofocusEnabled => Observatory.Settings.IsSmartAutofocusEnabled();

        /// <summary>
        /// The latest Smart Autofocus status, or null when it's off or the server was unreachable. Set
        /// by IsAutofocusRequired and reused by RunAutofocus and ApplyRecommendedFocus, so one server
        /// call serves the frame.
        /// </summary>
        public AutofocusStatusResponse? LastAutofocusStatus { get; set; }

        public AutopilotNextResponse? CurrentTarget {
            get => _currentTarget;
            set {
                if (ReferenceEquals(value, _currentTarget)) return;
                _currentTarget = value;
                // PollForTarget sets this after the status is already Polling, and nothing else
                // changes until the slew, so without this the dashboard would show the target late.
                OnStatusChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        private AutopilotNextResponse? _currentTarget;
        public CaptureResult? LastCapture { get; set; }

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
        public void DropPendingUploads() {
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

        public void ResetAutofocusTracking() {
            LastAutofocusTimeUtc = null;
            LastAutofocusTemperature = null;
            LastAutofocusFilter = null;
        }

        public void RecordAutofocus() {
            LastAutofocusTimeUtc = DateTime.UtcNow;
            LastAutofocusTemperature = GetAmbientTemperature();
            LastAutofocusFilter = FilterWheel?.GetCurrentFilter()?.Name;
        }

        /// <summary>
        /// Ambient temperature in °C for the autofocus temperature trigger. Uses the focuser's probe like
        /// N.I.N.A. does, then the weather sensor. Null when neither reports one.
        /// </summary>
        public double? GetAmbientTemperature() => Focuser?.GetTemperature() ?? Weather?.GetTemperature();

        public int NextPollDelaySeconds { get; set; } = DefaultRetryDelaySeconds;

        /// <summary>
        /// True once the current image is marked started on the server (/start/) and not yet reported,
        /// so the abort path fails it instead of leaving it stuck. Cleared by ReportResult and PollForTarget.
        /// </summary>
        public bool IsCurrentTargetStarted { get; set; }

        /// <summary>
        /// Idempotency key for the current exposure, made when the image is marked started. Every report
        /// for it sends the same ref, so the server sees a retried call as the same exposure.
        /// </summary>
        public string? CurrentClientRef { get; set; }

        /// <summary>
        /// Refuses the current frame before it's marked started. Logs why, reports it failed so it shows
        /// in the project's history, and drops the target so the machine goes back to polling.
        /// </summary>
        public async Task RefuseCurrentFrameAsync(string reason) {
            AutopilotNextResponse? target = CurrentTarget;
            LogError($"{reason}. Not taking the frame on {target?.TargetName ?? "the current target"}.");
            NextPollDelaySeconds = DefaultRetryDelaySeconds;
            if (target?.ImageId is int imageId) {
                // No client_ref, since /start/ was never called for this image.
                await Observatory.AstraeusWebClient.PostAutopilotFailAsync(imageId, reason, null);
            }
            CurrentTarget = null;
        }

        /// <summary>
        /// Reports the current image failed if it was marked started on the server, so it doesn't sit
        /// CAPTURING. Images never started stay PENDING and are re-offered on the next poll.
        /// </summary>
        public async Task ReportStartedImageFailedAsync(string reason) {
            if (!IsCurrentTargetStarted || CurrentTarget?.ImageId is not int imageId) return;
            await Observatory.AstraeusWebClient.PostAutopilotFailAsync(imageId, reason, CurrentClientRef);
            IsCurrentTargetStarted = false;
            CurrentClientRef = null;
        }

        /// <summary>
        /// True when States.CheckTargetChanged took the fast path because the mount is already on this
        /// field and nothing has moved since the last frame. That marks the frame as a repeat, and so a
        /// dither point.
        /// </summary>
        public bool HasSkippedSlew { get; set; }

        /// <summary>
        /// True when the frame in hand must be plate solved instead of trusting where the mount says it
        /// points. Set after a meridian flip, since the mount reports the target to arcseconds whether or
        /// not N.I.N.A.'s re-centre ran, and a field rotated 180 degrees gives the same numbers.
        /// </summary>
        public bool IsRecenterRequired { get; set; }

        /// <summary>
        /// True when the last frame's plate solve landed further off target than the tolerance.
        /// Unlike IsRecenterRequired, States.PollForTarget doesn't clear it, because it's set after
        /// the frame and a poll follows every capture.
        /// </summary>
        public bool IsLastFrameOffTarget { get; set; }

        /// <summary>
        /// True once a meridian flip has finished for the frame in hand. One exposure crosses the
        /// meridian once, so a second request means something disagrees, and flipping again would swing
        /// the mount back and forth until dawn. RunMeridianFlip fails the frame instead.
        /// </summary>
        public bool HasFlippedThisFrame { get; set; }

        public bool HasFilterChangedThisFrame { get; set; }

        ///////////// Guiding for the frame in hand /////////////

        private string? _ditherSkipReportedTargetKey;

        /// <summary>
        /// True the first time it's asked for each target, so a dither skipped for lack of guiding is
        /// reported once per target, not on every frame.
        /// </summary>
        public bool ShouldReportDitherSkipped() {
            string targetKey = $"{CurrentTarget?.TargetName}|{CentringTargetKey(CurrentTarget)}";
            if (targetKey == _ditherSkipReportedTargetKey) return false;
            _ditherSkipReportedTargetKey = targetKey;
            return true;
        }

        /// <summary>
        /// Stops guiding before a focuser move when the observer's N.I.N.A. setting says to. With Smart
        /// Autofocus on, N.I.N.A.'s "disable guiding on filter change" never runs, since its filter offsets
        /// are off. Never throws except on cancellation.
        /// </summary>
        public async Task StopGuidingForFocusMoveAsync(int position, bool shouldStopGuiding,
            CancellationToken cancellationToken) {
            if (!shouldStopGuiding || Guider is not { IsConnected: true, IsGuiding: true } guider) return;
            if (Focuser?.Position is not int currentPosition || currentPosition == position) return;
            Log($"Stopping guiding while the focuser moves from {currentPosition} to {position}.");
            if (await guider.StopGuidingAsync(cancellationToken) == GuidingStopOutcome.NotConfirmed) {
                LogWarning("Guider did not confirm it stopped. Moving the focuser anyway.");
            }
        }

        ///////////// Centring outcome for the target in hand /////////////

        /// <summary>
        /// Centring is retried once on the next frame. After that the target is captured on the
        /// mount's own coordinates.
        /// </summary>
        public const int MaxCentringRetries = 1;

        public int CentringFailureCount { get; private set; }

        public string? CentringFailureReason { get; private set; }

        // Which target the count belongs to, so a new target starts clean without a reset.
        private string? _centringFailureTargetKey;
        private bool _isCentringGiveUpAnnounced;

        private static string? CentringTargetKey(AutopilotNextResponse? target) =>
            target?.Ra is double ra && target.Dec is double dec ? $"{ra:F5}|{dec:F5}" : null;

        private bool IsCentringStateForCurrentTarget =>
            _centringFailureTargetKey != null && CentringTargetKey(CurrentTarget) == _centringFailureTargetKey;

        /// <summary>
        /// Whether the frame in hand rests on a centring that worked, meaning none has failed on this
        /// target since the last success. Sent to the server with every frame.
        /// </summary>
        public bool IsFrameCentred => !IsCentringStateForCurrentTarget || CentringFailureCount == 0;

        public bool IsCentringRetryDue =>
            IsCentringStateForCurrentTarget && CentringFailureCount > 0 && CentringFailureCount <= MaxCentringRetries;

        /// <summary>
        /// True once centring on this target has failed more often than it's retried, so we capture on
        /// the mount's own coordinates. The frame's pointing check reads it too, or an off-target solve
        /// would send the next frame back into the centring we gave up on.
        /// </summary>
        public bool IsCentringGivenUp =>
            IsCentringStateForCurrentTarget && CentringFailureCount > MaxCentringRetries;

        public void RecordCentringFailure(string reason) {
            string? key = CentringTargetKey(CurrentTarget);
            if (key != _centringFailureTargetKey) {
                _centringFailureTargetKey = key;
                CentringFailureCount = 0;
                _isCentringGiveUpAnnounced = false;
            }
            CentringFailureCount++;
            CentringFailureReason = reason;
        }

        public void ClearCentringFailures() {
            _centringFailureTargetKey = null;
            _isCentringGiveUpAnnounced = false;
            CentringFailureCount = 0;
            CentringFailureReason = null;
        }

        public bool ShouldAnnounceCentringGivenUp() {
            if (!IsCentringGivenUp) return false;
            if (_isCentringGiveUpAnnounced) return false;
            _isCentringGiveUpAnnounced = true;
            return true;
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
        public void ReportCritical(string message, object? details = null) {
            Observatory.LogCritical(message, Device, LogCategory.Autopilot);
            Observatory.ReportEvent(NotificationKind.CriticalError, message, details,
                                    Guid.NewGuid().ToString("N"));
        }

        // Otherwise a device lost mid-night only shows as frames failing one after another. The
        // headline is fixed per device, so the server's cooldown absorbs a flapping connection.
        private void ReportDeviceLost(IDeviceComponent device) {
            string reconnect = ShouldAutoConnectEquipment ? "did not reconnect" : "auto-connect is off";
            ReportCritical($"Autopilot: {device.DisplayName} is not connected.",
                new { device = device.DisplayName, state = "disconnected", reconnect });
        }

        public static string FormatElapsed(Stopwatch watch) => $"{watch.Elapsed.TotalSeconds:F1}s";

        /// <summary>False when there's nothing to run or it couldn't run.</summary>
        public async Task<bool> RunSequenceAsync(string? path, CancellationToken cancellationToken) {
            SequenceRunner? runner = Observatory.SequenceRunner;
            if (runner == null || string.IsNullOrWhiteSpace(path)) return false;
            return await runner.RunAsync(path, cancellationToken);
        }

        public void DisableAutopilot() {
            if (Observatory.TryGetComponent<Autopilot>(out Autopilot? autopilot)) {
                autopilot.IsEnabled = false;
            }
        }
        
        /// <summary>False with no safety monitor at all.</summary>
        public bool IsSafe() {
            return SafetyMonitor != null && SafetyMonitor.IsSafe();
        }

        public string SafetyMonitorName => SafetyMonitor?.DeviceName ?? "no safety monitor";

        ///////////// Reconnecting a device that dropped /////////////

        // When each device last had a reconnect attempt that failed, for ReconnectCooldown.
        private readonly Dictionary<IDeviceComponent, DateTime> _lastReconnectAttemptUtc = new();

        /// <summary>
        /// Whether device is connected, after one reconnect attempt if it dropped. Only puts back what
        /// the run connected, so nothing before IsStartupComplete or with auto-connect off. A camera that
        /// comes back is cooled again, since it reconnects warm.
        /// </summary>
        public async Task<bool> EnsureConnectedAsync(IDeviceComponent? device, CancellationToken cancellationToken) {
            if (device == null) return false;
            if (device.IsConnected) return true;
            // No default device means it isn't used here, so there's nothing to reconnect or lose.
            if (!IsStartupComplete || !device.HasDefaultDevice) return false;
            DateTime now = DateTime.UtcNow;
            if (_lastReconnectAttemptUtc.TryGetValue(device, out DateTime lastAttempt)
                && now - lastAttempt < ReconnectCooldown) {
                return false;
            }
            _lastReconnectAttemptUtc[device] = now;

            if (!ShouldAutoConnectEquipment) {
                ReportDeviceLost(device);
                return false;
            }

            LogWarning($"{device.DisplayName} is not connected. Reconnecting to {device.DefaultDevice}.");
            if (!await ConnectWithTimeoutAsync(device, cancellationToken)) {
                LogWarning($"{device.DisplayName} did not reconnect. The next attempt is in " +
                           $"{ReconnectCooldown.TotalMinutes:F0} minutes at the earliest.");
                ReportDeviceLost(device);
                return false;
            }
            _lastReconnectAttemptUtc.Remove(device);
            Log($"{device.DisplayName} reconnected.");

            if (IsCameraCoolingEnabled && Camera is { } camera && ReferenceEquals(device, camera)) {
                Log("Cooling the reconnected camera again.");
                // The camera logs why when it returns false, as it does for the dusk cool-down.
                if (!await CoolCameraForNightAsync(camera, cancellationToken)) {
                    Log("Camera is not at its target temperature. Carrying on regardless.");
                }
            }
            return true;
        }

        /// <summary>
        /// Cools the camera to CameraCoolingTemperature and returns whether it got there. A colder set
        /// point is someone's choice, maybe a start-up sequence's, so we keep it and warn.
        /// </summary>
        public async Task<bool> CoolCameraForNightAsync(Camera camera, CancellationToken cancellationToken) {
            double wanted = CameraCoolingTemperature;
            double target = wanted;
            if (camera.CoolerSetPoint is double setPoint && setPoint < wanted) {
                LogWarning(
                    $"The cooler is already at {setPoint:F1}°C, colder than the autopilot's {wanted:F1}°C, " +
                    $"so tonight's frames are taken at {setPoint:F1}°C.");
                target = setPoint;
            }
            return await camera.CoolCameraAsync(target, cancellationToken);
        }

        // One connect, bounded by ReconnectTimeout and the caller's token. If it outlasts either we
        // press N.I.N.A.'s own Cancel on it. A timeout returns false and a cancellation throws.
        public async Task<bool> ConnectWithTimeoutAsync(IDeviceComponent device, CancellationToken cancellationToken) {
            Task<bool> connecting = device.TryConnectDefaultDeviceAsync();
            Task firstToFinish = await Task.WhenAny(connecting,
                Task.Delay(ReconnectTimeout, cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default));
            if (firstToFinish == connecting) {
                try {
                    return await connecting;
                } catch (Exception ex) {
                    LogWarning($"Connecting {device.DisplayName} failed: {ex.Message}");
                    return false;
                }
            }

            // Observed here so a connect that fails after we stop waiting is not an unobserved fault.
            connecting.ObserveFaults(exception =>
                LogDebug($"Connecting {device.DisplayName} failed after the wait: {exception.Message}"));
            device.TryCancelConnect();
            cancellationToken.ThrowIfCancellationRequested();
            LogWarning($"Connecting {device.DisplayName} did not finish within " +
                       $"{ReconnectTimeout.TotalSeconds:F0} s and was cancelled.");
            return false;
        }

        /// <summary>Seconds of unbroken Safe readings needed before the roof and mount move again.</summary>
        public int SafeSettleSeconds => Observatory.Settings.GetSafeSettleSeconds();

        /// <summary>
        /// Whether the mount is inside its limits now. True when limits are off, it's parked or there's
        /// no mount. The meridian is left out while a flip is coming, or the watcher would end the night
        /// a minute before the flip fixes the breach.
        /// </summary>
        public bool IsWithinLimits() =>
            Mount?.IsCurrentPositionWithinLimits(shouldIncludeMeridian: !IsMeridianWaived) ?? true;

        /// <summary>When the committed frame's exposure window ends. Null until a frame is marked started.</summary>
        public DateTime? FrameWindowEndsAtUtc { get; set; }

        // How long the meridian waiver holds past the frame's window. Long enough for the report and
        // calibration to reach the next poll's flip, short enough to bound a stall.
        private static readonly TimeSpan MeridianWaiverGrace = TimeSpan.FromMinutes(5);

        // The watcher's flip waiver, time-limited so a stalled frame can't leave the meridian
        // unwatched for the rest of the night.
        private bool IsMeridianWaived {
            get {
                if (!CanFlipForCurrentTarget) return false;
                if (FrameWindowEndsAtUtc is not DateTime windowEnd) return true;
                return DateTime.UtcNow < windowEnd + MeridianWaiverGrace;
            }
        }

        /// <summary>
        /// Which limit the mount breaks right now, worded for the log. Null when it's clear, limits are
        /// off or there's no mount, and when parked, since parking is the watcher's own remedy. Scoped
        /// like IsWithinLimits, so the watcher reports the reason it tripped.
        /// </summary>
        public string? CurrentLimitBreach() =>
            Mount?.DescribeCurrentPositionBreach(shouldIncludeMeridian: !IsMeridianWaived);

        /// <summary>
        /// The "Mount limit reached on ..." line the last breach was logged with, so the secure state
        /// reports the same cause to the server. Null until a breach.
        /// </summary>
        public string? LastLimitBreach { get; set; }

        /// <summary>
        /// Why a fault ended this session, or null if it was just switched off. The teardown reads it to
        /// pick the "stopped" or "failed" email, so a fault is reported once, with its cause.
        /// </summary>
        public string? SessionEndReason { get; set; }

        /// <summary>
        /// Whether this session's wind-down parked the mount, and closed the roof if we operate it. False
        /// on a deliberate stop, which leaves things as found since that's usually someone taking manual
        /// control. The session-end email warns about it.
        /// </summary>
        public bool IsSessionSecured { get; set; }

        public DateTime? SessionSecuredAtUtc { get; set; }

        ///////////// Notification session (one night) /////////////

        /// <summary>
        /// One night's session, whose id its start and end emails share. A start time, not a stopwatch,
        /// because the email says when the night began, in a timezone the server knows and we may not.
        /// </summary>
        private sealed record Session(string Id, DateTime StartedUtc);

        /// <summary>
        /// The night under way, or null between nights. A session is one night, not one switch-on, so a
        /// week left enabled reports seven pairs. Interlocked so only one of the two closes reports.
        /// </summary>
        private Session? _session;

        public DateTime? SessionStartedUtc => Volatile.Read(ref _session)?.StartedUtc;

        public async Task StartSessionAsync() {
            Session session = new Session(Guid.NewGuid().ToString("N"), DateTime.UtcNow);
            if (Interlocked.Exchange(ref _session, session) is { } stale)
                LogWarning($"Autopilot session {stale.Id} was still open when tonight's started.");

            // Each night is judged on its own. The Context outlives the night, so without this the first
            // fault would be re-reported every night after, and Tuesday's secure would still count on Friday.
            SessionEndReason = null;
            IsSessionSecured = false;
            // Tonight's connect chain hasn't run yet, so a disconnected device hasn't dropped. No
            // reconnects and no device-lost emails while the chain's waits poll.
            IsStartupComplete = false;
            HasOpenedRoofTonight = false;
            HasWarnedDomeGeometryTonight = false;
            HasNotedCloudUploadOffTonight = false;
            DarkFrameStarCounts.Clear();
            HasReportedDarkFrames = false;
            ClearCentringFailures();

            // Awaited, unlike other alerts. Nothing waits on this task, and it cheaply keeps "started"
            // ahead of a fast "failed", since the server orders by arrival.
            await Observatory.ReportEventAsync(NotificationKind.AutopilotStarted,
                "Autopilot session started.", BuildSessionStartDetails(), $"{session.Id}-start");
        }

        /// <summary>
        /// The settings an observer wants confirmed at dusk. The session id goes in client_ref, since it
        /// means nothing to the person reading the email.
        /// </summary>
        private object BuildSessionStartDetails() => new {
            roof_mode = RoofMode.ToString(),
            operating_window = OperatingWindow.ToString(),
            auto_connect_equipment = ShouldAutoConnectEquipment ? "yes" : "no",
            camera_cooling = IsCameraCoolingEnabled ? $"yes, {CameraCoolingTemperature:F1}°C" : "no",
            upload_to_cloud = IsCloudUploadEnabled ? "yes" : "no",
        };

        /// <summary>
        /// Closes the night and reports "failed" if a fault ended it, otherwise "stopped". EndSession at
        /// dawn and the machine teardown both call it, and the atomic take means only the first reports.
        /// A fault is always reported, even with no night open.
        /// </summary>
        public void ReportSessionEnd(string? faultReason = null) {
            Session? session = Interlocked.Exchange(ref _session, null);
            string? reason = faultReason ?? SessionEndReason;
            if (session == null && reason == null) return;

            // Sent here because the session is closed by the time Observatory adds its rig snapshot.
            // Null keys are dropped, so a fault with no night behind it shows no session length.
            string? sessionStartedAt = session?.StartedUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'",
                CultureInfo.InvariantCulture);
            // Read now, since the roof may have been opened again after the last secure, and what
            // matters is where it is when the email goes.
            bool isRoofOpen = Dome is { IsConnected: true } dome && !dome.IsShutterClosed();
            var details = new {
                session_started_at = sessionStartedAt,
                reason = reason ?? "switched off",
                // Drives the "mount is not parked, roof may still be open" warning in the email.
                secured = IsSessionSecured ? "yes" : "no",
                roof = DescribeRoofNow(),
            };
            // A deliberate stop leaves the observatory as found. If the roof is open the first line says
            // so, because "stopped" alone reads as routine and this is what the observer must act on.
            string message = reason ?? (isRoofOpen
                ? "Autopilot session stopped. The roof is still open."
                : "Autopilot session stopped.");
            Observatory.ReportEvent(
                reason == null ? NotificationKind.AutopilotStopped : NotificationKind.AutopilotFailed,
                message, details,
                $"{session?.Id ?? Guid.NewGuid().ToString("N")}-end");
        }

        private string DescribeRoofNow() {
            string state;
            if (Dome is not { IsConnected: true } dome) {
                state = "unknown (dome not connected)";
            } else if (dome.IsShutterClosed()) {
                state = "closed";
            } else {
                state = "open";
            }
            return RoofMode == RoofMode.Operate ? state : $"{state} (not operated by the autopilot)";
        }

        /// <summary>
        /// True from waiting for safe conditions until a secure finishes. The Dome's roof watchdog stands
        /// down while it's set, and covers the other phases, which have no safety watcher.
        /// </summary>
        public bool IsRoofUnderAutopilotControl { get; set; }

        /// <summary>
        /// Whether the roof has been seen open for this imaging stretch, so a roof that stops reading
        /// open is a surprise, not one that hasn't opened yet. Never set under RoofMode.Ignore.
        /// </summary>
        public bool IsRoofExpectedOpen { get; set; }

        /// <summary>
        /// Whether this autopilot opened the roof tonight. The secure closes a roof it opened even if
        /// the roof mode has changed from Operate since.
        /// </summary>
        public bool HasOpenedRoofTonight { get; set; }

        /// <summary>
        /// Whether tonight's log already explained N.I.N.A.'s dome-geometry failure after a centring slew.
        /// </summary>
        public bool HasWarnedDomeGeometryTonight { get; set; }

        /// <summary>
        /// Star counts of the latest frames in a row that had almost no stars and failed their plate
        /// solve, oldest first. Any frame that doesn't empties it.
        /// </summary>
        public List<int> DarkFrameStarCounts { get; } = new();

        public bool HasReportedDarkFrames { get; set; }

        public bool HasNotedCloudUploadOffTonight { get; set; }

        /// <summary>
        /// Whether a roof we're imaging under has stopped reading open, even while the safety monitor
        /// reads Safe. Anything but a shutter reading Open counts, including a disconnected dome, since
        /// then we can't confirm the roof is open.
        /// </summary>
        public bool IsRoofClosedUnexpectedly() =>
            RoofMode != RoofMode.Ignore && IsRoofExpectedOpen && Dome?.IsShutterOpen() != true;

        /// <summary>
        /// Whether tonight's first equipment connect is done. Alerts and reconnects wait for it, since
        /// before then a disconnected device usually means the mount wasn't powered on yet.
        /// </summary>
        public bool IsStartupComplete { get; set; }

        /// <summary>
        /// When the current run of Safe readings began during WaitForSafe's settle, otherwise null. The
        /// dashboard counts the settle up from it, since the log heartbeat only updates every Nth poll.
        /// </summary>
        public DateTime? SafeSinceUtc {
            get => _safeSinceUtc;
            set {
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