using NINA.Astrometry;
using NINA.Core.Locale;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Image.Interfaces;
using NINA.PlateSolving;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.WPF.Base.ViewModel.Equipment.Telescope;
using CosmicVaults.NINA.Astraeus.Engine.Utility;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Payloads;
using NINA.Core.Enum;
using NINA.Equipment.Model;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine {
    public class Mount(
        Observatory observatory,
        IWebSocketBus webSocketBus,
        ITelescopeMediator telescopeMediator,
        IImagingMediator imagingMediator,
        IFilterWheelMediator filterWheelMediator,
        IDomeMediator domeMediator,
        IDomeFollower domeFollower,
        IMeridianFlipVMFactory? meridianFlipVMFactory) :
        DeviceComponent<ITelescopeVM, ITelescopeConsumer, TelescopeInfo>(telescopeMediator, observatory, webSocketBus) {
        public override string DeviceType { get; } = "mount";
        public override string DisplayName { get; } = "Telescope Mount";
        protected override string? RegistrationEndpoint => "telescopes";
        public override bool IsBusy => LastInfo?.Slewing == true;

        private CancellationTokenSource? _slewCancellationSource;

        /// <summary>Cancels an in-flight manual slew, park or home. StopMovement does the physical stop.</summary>
        public void AbortSlew() => AbortOperation(ref _slewCancellationSource);

        public override WsMessage? GetUpdateMessage() {
            if (LastInfo is null) {
                return null;
            }

            MountPayload payload = new MountPayload {
                DeviceId = DeviceId,
                Ra = DevicePayload.CleanValue(LastInfo.RightAscension),
                Dec = DevicePayload.CleanValue(LastInfo.Declination),
                IsTracking = LastInfo.TrackingEnabled,
                IsParked = LastInfo.AtPark,
                IsHome = LastInfo.AtHome,
                Azimuth = DevicePayload.CleanValue(LastInfo.Azimuth),
                Altitude = DevicePayload.CleanValue(LastInfo.Altitude),
                TrackingMode = LastInfo.TrackingRate.TrackingMode.ToString(),
                IsSlewing = LastInfo.Slewing,
                SideOfPier = LastInfo.SideOfPier.ToString(),
                IsPulseGuiding = LastInfo.IsPulseGuiding,
                IsConnected = true
            };
            return new MountUpdate(payload);
        }

        public override WsMessage? GetDeviceStaticInfo() {
            TelescopeInfo info = telescopeMediator.GetInfo();
            MountPayload payload = new MountPayload() {
                DeviceId = DeviceId,
                Name = info.Name,
                IsConnected = info.Connected,
                DisplayName = info.DisplayName,
                Description = info.Description,
                DriverInfo = info.DriverInfo,
                SiteLatitude = DevicePayload.CleanValue(info.SiteLatitude),
                SiteLongitude = DevicePayload.CleanValue(info.SiteLongitude),
                SiteElevation = DevicePayload.CleanValue(info.SiteElevation),
                CanFindHome = info.CanFindHome,
                CanPark = info.CanPark,
                CanSetPark = info.CanSetPark,
                CanSetTracking = info.CanSetTrackingEnabled,
                Epoch = info.EquatorialSystem.ToString(),
                HasUnknownEpoch = info.HasUnknownEpoch,
                AlignmentMode = info.AlignmentMode.ToString(),
                CanPulseGuide = info.CanPulseGuide,
                CanSetPierSide = info.CanSetPierSide,
                CanSlew = info.CanSlew
            };
            return new MountUpdate(payload);
        }

        public override async Task Start() {
            await base.Start();
            WebSocketBus.Connected += OnSocketConnected;
        }

        public override Task Destroy() {
            WebSocketBus.Connected -= OnSocketConnected;
            return base.Destroy();
        }

        // The server needs alignment_mode to know whether the meridian rule applies.
        private async void OnSocketConnected() {
            try {
                await Task.Delay(PushOnConnectDelay);
                if (IsConnected) await SendStaticInfoAsync();
            } catch (Exception ex) {
                LogWarning($"Sending the mount's static info on connect failed: {ex.Message}");
            }
        }

        ///////////// Limit backstop outside the autopilot /////////////
        // Set once a breach is reported, so the alert fires once per breach and not every tick.
        // Cleared when the mount is back inside the limits.
        private bool _isReportedOutOfLimits;

        // The same for the quiet note a resting mount gets instead of the alert. Kept separate so
        // stopping a breach doesn't then log a note saying the mount is stopped.
        private bool _isReportedRestingOutOfLimits;

        /// <summary>
        /// Parked, or still with tracking off. Check IsConnected first. The backstop leaves a resting mount
        /// alone, since stopping is its only remedy and a park position is often below the altitude limit.
        /// </summary>
        public bool IsResting {
            get {
                TelescopeInfo info = telescopeMediator.GetInfo();
                return info.AtPark || (!info.TrackingEnabled && !info.Slewing);
            }
        }

        /// <summary>
        /// Limit backstop for when the autopilot isn't driving, since the driver may have no hour-angle
        /// limit set. It's in LateUpdate because Observatory.Update returns early when signed out or offline.
        /// </summary>
        public override async Task LateUpdate() {
            TrackUnpark();
            await EnforceLimitsAsync();
            await base.LateUpdate();
        }

        private async Task EnforceLimitsAsync() {
            // Not DescribeCurrentPositionBreach, which skips a parked mount. A resting breach still
            // gets a log line here, just no alarm.
            string? breach = DescribePointingBreach(shouldIncludeMeridian: true);
            if (breach == null) {
                _isReportedOutOfLimits = false;
                _isReportedRestingOutOfLimits = false;
            }

            if (IsFlipping
                || (Observatory.TryGetComponent<Autopilot.Autopilot>(out Autopilot.Autopilot? autopilot)
                    && autopilot.IsRunning)) {
                return;
            }

            if (TryStartBackstopFlip(breach) || breach == null) return;

            if (IsResting || IsFreshlyUnparked) {
                // Logged once, since where it rests matters for the next slew. Skipped if the alert
                // already fired, because then we stopped it and it's been reported.
                if (!_isReportedOutOfLimits && !_isReportedRestingOutOfLimits) {
                    _isReportedRestingOutOfLimits = true;
                    Log(DescribeRestingBreach(breach));
                }
                return;
            }

            if (!_isReportedOutOfLimits) {
                _isReportedOutOfLimits = true;
                await ReportOutOfLimitsAsync(breach);
            }
        }

        private string DescribeRestingBreach(string breach) {
            if (!IsResting) {
                return $"Mount has just unparked outside the limits: {breach}. Left alone for up to " +
                       $"{UnparkGrace.TotalSeconds:F0} s, because the limits apply to the slew that follows " +
                       "an unpark.";
            }
            string restingState = IsParked ? "parked" : "stopped";
            return $"Mount is {restingState} outside the limits: {breach}. " +
                   "Nothing is moving it, so nothing was done. The limits apply again once it slews or tracks.";
        }

        // Set when the backstop starts a flip, so a failed flip falls back to the stop and alert.
        // Cleared once the mount is clear of the limit and the look-ahead.
        private bool _isBackstopFlipAttempted;

        // How far ahead the backstop flips, like N.I.N.A.'s trigger ahead of its next exposure.
        private static readonly TimeSpan BackstopFlipLookAhead = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Starts the flip in the background, since it waits out N.I.N.A.'s minutes-after-meridian. True
        /// when this tick has nothing more to do. A running N.I.N.A. sequence has its own
        /// MeridianFlipTrigger, so it only gets the stop.
        /// </summary>
        private bool TryStartBackstopFlip(string? breach) {
            AstraeusSettings settings = Observatory.Settings;
            if (!settings.IsMountLimitEnabled() || !settings.IsMeridianFlipEnabled() || !IsGermanEquatorial
                || !IsPierSideKnown || !IsMeridianFlipAvailable || !IsTracking || IsResting || IsFreshlyUnparked)
                return false;

            Coordinates target = CurrentFlipTarget();
            bool isPastLimit = breach != null;
            if (!isPastLimit && !WouldBreachMeridianOnCurrentSide(target, BackstopFlipLookAhead)) {
                _isBackstopFlipAttempted = false;
                return false;
            }
            if (_isBackstopFlipAttempted || DescribePointingBreach(shouldIncludeMeridian: false) != null
                || Observatory.SequenceRunner?.IsNinaSequenceRunning() == true)
                return false;

            if (Observatory.TryGetComponent<Camera>(out Camera? camera) && camera.IsExposing) {
                // Let the exposure finish unless the mount is already past the limit.
                if (!isPastLimit) return true;
                camera.StopExposure("the mount reached the meridian limit and is flipping");
            }

            _isBackstopFlipAttempted = true;
            _ = BackstopFlipAsync(target);
            return true;
        }

        private async Task BackstopFlipAsync(Coordinates target) {
            try {
                Log($"Mount is reaching the meridian limit ({DescribeMeridianLimit()}) outside the autopilot, so " +
                    "flipping it.");
                MeridianFlipOutcome outcome = await FlipAsync(target, null, ComponentToken);
                if (outcome.HasReachedTargetSide) {
                    Log($"Meridian flip outside the autopilot done: {outcome.Before} -> {outcome.After}.");
                    return;
                }
                await FailBackstopFlipAsync(outcome.FailReason ?? "the flip did not complete");
            } catch (OperationCanceledException) when (ComponentToken.IsCancellationRequested) {
                // Shutting down.
            } catch (Exception ex) {
                try {
                    await FailBackstopFlipAsync(ex.Message);
                } catch (Exception reportException) {
                    LogError($"Meridian flip outside the autopilot failed ({ex.Message}), and so did the stop: " +
                             $"{reportException.Message}");
                }
            }
        }

        private Task FailBackstopFlipAsync(string reason) {
            string breach = DescribePointingBreach(shouldIncludeMeridian: true)
                            ?? $"approaching the meridian limit ({DescribeMeridianLimit()})";
            _isReportedOutOfLimits = true;
            return ReportOutOfLimitsAsync($"{breach}; the meridian flip failed: {reason}");
        }

        ///////////// Meridian outside the autopilot /////////////
        // Manual control flips like N.I.N.A.'s MeridianFlipTrigger, and stands aside for anything
        // that handles the meridian itself.

        // The last Control Hub slew target, so a flip goes back to it and not to a drifted position.
        private Coordinates? _lastManualSlewTarget;

        // Look-ahead past a hub exposure, for the readout and the start of the next frame.
        private static readonly TimeSpan ManualExposureMargin = TimeSpan.FromSeconds(30);

        public Coordinates CurrentFlipTarget() {
            if (_lastManualSlewTarget is { } slewTarget && IsPointingAt(slewTarget, PierSideSameFieldDegrees))
                return slewTarget;
            return telescopeMediator.GetCurrentPosition();
        }

        // The autopilot and a N.I.N.A. sequence each handle the meridian themselves.
        private bool ShouldStandAside()
            => (Observatory.TryGetComponent<Autopilot.Autopilot>(out Autopilot.Autopilot? autopilot)
                   && autopilot.IsRunning)
               || Observatory.SequenceRunner?.IsNinaSequenceRunning() == true;

        public ManualExposurePlan PlanManualExposure(TimeSpan exposure) {
            if (!Observatory.Settings.IsMountLimitEnabled() || !IsConnected || IsResting || IsFreshlyUnparked
                || ShouldStandAside() || !IsGermanEquatorial
                || !WouldBreachMeridianOnCurrentSide(CurrentFlipTarget(), exposure + ManualExposureMargin))
                return new ManualExposurePlan(ManualExposureAction.Proceed);

            if (Observatory.Settings.IsMeridianFlipEnabled() && IsMeridianFlipAvailable && IsPierSideKnown)
                return new ManualExposurePlan(ManualExposureAction.Flip);

            string whyNot;
            if (!Observatory.Settings.IsMeridianFlipEnabled()) {
                whyNot = "meridian flips are off (Astraeus Settings → Meridian, or the plugin's Autopilot options)";
            } else if (!IsMeridianFlipAvailable) {
                whyNot = "this N.I.N.A. did not supply its meridian flip workflow";
            } else {
                whyNot = $"the mount reports its side of pier as {SideOfPier}, so it cannot be flipped";
            }
            return new ManualExposurePlan(ManualExposureAction.Refuse,
                $"This exposure would carry the mount past the meridian limit ({DescribeMeridianLimit()}), " +
                $"and {whyNot}.");
        }

        // A park position is often below the altitude limit and an unpark starts tracking there, so
        // we don't alarm just after one. The grace ends at the first slew.
        private static readonly TimeSpan UnparkGrace = TimeSpan.FromSeconds(60);
        private DateTime? _unparkedAtUtc;
        private bool _wasParked;

        private void TrackUnpark() {
            TelescopeInfo info = telescopeMediator.GetInfo();
            if (_wasParked && info.Connected && !info.AtPark) _unparkedAtUtc = DateTime.UtcNow;
            if (info.Slewing) _unparkedAtUtc = null;
            _wasParked = info.Connected && info.AtPark;
        }

        private bool IsFreshlyUnparked =>
            _unparkedAtUtc is DateTime unparkedAt && DateTime.UtcNow - unparkedAt < UnparkGrace;

        /// <summary>
        /// Stops tracking and guiding and tells the observer. It never parks or closes the roof, since
        /// someone may be driving the mount by hand.
        /// </summary>
        private async Task ReportOutOfLimitsAsync(string breach) {
            bool hasStopped;
            try {
                hasStopped = await StopMovementAsync();
            } catch (Exception ex) {
                LogWarning($"Stopping the mount at its limit failed: {ex.Message}");
                hasStopped = false;
            }
            // Guiding a stopped mount sends corrections nowhere. Not awaited, because this runs on the
            // update tick and a guider can take its whole stop timeout to answer.
            StopGuidingForMoveAsync(ComponentToken).ObserveFaults(exception =>
                LogWarning($"Stopping guiding at the limit failed: {exception.Message}"));
            // The server keys its alert cooldown on this first line, so the breach goes in the details.
            const string headline = "Mount limit reached outside the autopilot.";
            LogError($"{headline} The mount is {breach}. Tracking " +
                     (hasStopped ? "stopped." : "could NOT be stopped. Check the mount now."));
            Observatory.ReportEvent(NotificationKind.CriticalError, headline, new {
                breach,
                tracking = hasStopped ? "stopped" : "could not be stopped",
                side_of_pier = SideOfPier.ToString(),
                device = telescopeMediator.GetInfo().Name ?? DisplayName,
            }, Guid.NewGuid().ToString("N"));
        }

        protected override Task HandleCommandAsync(WsCommand command) {
            switch (command.Action) {
                case "park":
                    ParkCommandAsync(command.Id).ObserveFaults(LogCommandFault);
                    break;
                case "set_park":
                    SetParkCommandAsync(command.Id).ObserveFaults(LogCommandFault);
                    break;
                case "home":
                    HomeCommandAsync(command.Id).ObserveFaults(LogCommandFault);
                    break;
                case "set_tracking":
                    if (JsonFields.ReadString(command.Payload, "data", out string trackingMode)
                        != JsonFieldState.Valid) {
                        RejectMalformedCommandAsync(command, "data").ObserveFaults(LogCommandFault);
                        break;
                    }
                    SetTrackingCommandAsync(command.Id, trackingMode).ObserveFaults(LogCommandFault);
                    break;
                case "slew":
                    if (JsonFields.ReadDouble(command.Payload, "ra", out double rightAscension)
                        != JsonFieldState.Valid) {
                        RejectMalformedCommandAsync(command, "ra").ObserveFaults(LogCommandFault);
                        break;
                    }
                    if (JsonFields.ReadDouble(command.Payload, "dec", out double declination) != JsonFieldState.Valid) {
                        RejectMalformedCommandAsync(command, "dec").ObserveFaults(LogCommandFault);
                        break;
                    }
                    SlewCommandAsync(command.Id, rightAscension, declination).ObserveFaults(LogCommandFault);
                    break;
                case "stop_slew":
                    StopSlewCommandAsync(command.Id).ObserveFaults(LogCommandFault);
                    break;
            }
            return Task.CompletedTask;
        }

        ///////////// Core Operations /////////////
        // Called directly by the autopilot. The command handlers below are thin wrappers that parse
        // the payload and send an ack.

        public bool IsParked => telescopeMediator.GetInfo().AtPark;

        public bool IsTracking => telescopeMediator.GetInfo() is { Connected: true, TrackingEnabled: true };

        /// <summary>
        /// pierUnknown when disconnected or not reported. Geometry alone can't tell a mount past the
        /// meridian and not yet flipped from one that flipped an hour ago.
        /// </summary>
        public PierSide SideOfPier {
            get {
                TelescopeInfo info = telescopeMediator.GetInfo();
                return info.Connected ? info.SideOfPier : PierSide.pierUnknown;
            }
        }

        /// <summary>
        /// True when the mount reports a definite side of pier, so a flip can be both aimed and
        /// confirmed. False for a fork, an alt-az, or a driver without SideOfPier.
        /// </summary>
        public bool IsPierSideKnown => SideOfPier is PierSide.pierEast or PierSide.pierWest;

        /// <summary>
        /// It's the only kind with a pier to swing into, so the only one the meridian limit applies to.
        /// </summary>
        public bool IsGermanEquatorial =>
            telescopeMediator.GetInfo() is { Connected: true, AlignmentMode: AlignmentMode.GermanPolar };

        /// <summary>
        /// Whether N.I.N.A. gave the plugin its meridian flip workflow. Checked before a flip so a caller
        /// doesn't wait out the confirmation window on a flip that never started.
        /// </summary>
        public bool IsMeridianFlipAvailable => meridianFlipVMFactory != null;

        /// <summary>
        /// True if it was already parked or the park worked. False when disconnected or the park fails.
        /// </summary>
        public async Task<bool> ParkAsync(CancellationToken cancellationToken = default) {
            if (!telescopeMediator.GetInfo().Connected) return false;
            if (telescopeMediator.GetInfo().AtPark) return true;

            Log("Parking Mount...");
            bool hasParked = await telescopeMediator.ParkTelescope(null, cancellationToken);
            Log(hasParked ? "Mount has Parked." : "Mount failed to Park.");
            return hasParked;
        }

        /// <summary>
        /// True if it was already unparked or the unpark worked. False when disconnected or the unpark fails.
        /// </summary>
        public async Task<bool> UnparkAsync(CancellationToken cancellationToken = default) {
            if (!telescopeMediator.GetInfo().Connected) return false;
            if (!telescopeMediator.GetInfo().AtPark) return true;

            Log("Unparking Mount...");
            bool hasUnparked = await telescopeMediator.UnparkTelescope(null, cancellationToken);
            Log(hasUnparked ? "Mount unparked." : "Mount failed to unpark.");
            return hasUnparked;
        }

        public async Task<bool> FindHomeAsync(CancellationToken cancellationToken = default) {
            if (!telescopeMediator.GetInfo().Connected) return false;

            Log("Finding Home...");
            return await telescopeMediator.FindHome(null, cancellationToken);
        }

        /// <summary>False when disconnected, parked, outside the limits or the slew fails.</summary>
        public async Task<bool> SlewToAsync(Coordinates coordinates, CancellationToken cancellationToken = default) {
            if (!telescopeMediator.GetInfo().Connected) return false;
            if (telescopeMediator.GetInfo().AtPark) {
                LogWarning("Unable to Slew. Telescope is Parked.");
                return false;
            }
            // Checked here so the limits hold whoever is calling.
            if (DescribeLimitBreach(coordinates, DateTime.Now) is string breach) {
                LogWarning($"Can't slew to {coordinates.RAString}, {coordinates.DecString}: {breach}");
                return false;
            }

            Log($"Slewing to RA:{coordinates.RAString}, Dec:{coordinates.DecString}.");
            MountPayload slewingPayload = new MountPayload() { IsSlewing = true, IsConnected = true };
            await WebSocketBus.SendAsync(new MountUpdate(slewingPayload, "event"));
            bool hasSlewed = await telescopeMediator.SlewToCoordinatesAsync(coordinates, cancellationToken);
            if (hasSlewed) {
                Log("Slew to target complete.");
            } else {
                LogError("Failed to slew to the target.");
            }

            return hasSlewed;
        }

        /// <summary>
        /// N.I.N.A. retries the slew twenty times a minute apart, which is far too long with the limit
        /// watcher off during a flip.
        /// </summary>
        private static readonly TimeSpan MeridianFlipTimeout = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Runs N.I.N.A.'s meridian flip workflow, since a plain slew lets the driver pick a side and may
        /// not move at all. The result covers the whole workflow, so a failed plate solve gives the same
        /// false as a mount that never moved. Confirm the side with WaitForPierSideAsync whatever this returns.
        /// </summary>
        private async Task<bool> MeridianFlipAsync(Coordinates coordinates, TimeSpan wait,
            CancellationToken cancellationToken = default) {
            if (!telescopeMediator.GetInfo().Connected) {
                LogWarning("Unable to flip: mount is not connected.");
                return false;
            }
            if (telescopeMediator.GetInfo().AtPark) {
                LogWarning("Unable to flip: mount is parked.");
                return false;
            }
            if (meridianFlipVMFactory == null) {
                LogWarning("Meridian flip unavailable. N.I.N.A. meridian flip factory not present.");
                return false;
            }

            // No DescribeLimitBreach check here. The flip is what fixes the breach.
            string waitDescription = wait > TimeSpan.Zero
                ? $", after {wait.TotalMinutes:F1} min for the target to pass the meridian."
                : ".";
            Log($"Meridian flip to RA:{coordinates.RAString}, Dec:{coordinates.DecString}" + waitDescription);
            // N.I.N.A.'s PassMeridian step stops tracking and waits this out before it flips.
            TimeSpan timeout = MeridianFlipTimeout + wait;
            using CancellationTokenSource flipCancellationSource =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            flipCancellationSource.CancelAfter(timeout);
            // Labels the flip's re-centre and autofocus exposures, which N.I.N.A. takes itself.
            using IDisposable frameLabel = Observatory.FrameLabel.Begin(PreviewSource.MeridianFlip);
            try {
                IMeridianFlipVM flip = meridianFlipVMFactory.Create();
                bool isFinished = await flip.MeridianFlip(coordinates, wait, flipCancellationSource.Token);
                // N.I.N.A. swallows a cancellation and reports success, so check the tokens ourselves.
                // A caller's cancel unwinds. Our own timeout counts as a failed flip.
                cancellationToken.ThrowIfCancellationRequested();
                if (flipCancellationSource.IsCancellationRequested) {
                    LogError($"Meridian flip gave up after {timeout.TotalMinutes:F0} minutes.");
                    return false;
                }
                return isFinished;
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                throw;
            } catch (OperationCanceledException) {
                LogError($"Meridian flip gave up after {timeout.TotalMinutes:F0} minutes.");
                return false;
            } catch (Exception ex) {
                LogError($"Meridian flip threw: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// The real flip check. A mount with no hour-angle limit may serve the flip's final slew on the
        /// side it's already on, and nothing else tells that apart from a real flip.
        /// </summary>
        public async Task<bool> WaitForPierSideAsync(PierSide wanted, TimeSpan timeout,
            CancellationToken cancellationToken = default) {
            if (wanted is PierSide.pierUnknown) return false;
            DateTime deadline = DateTime.UtcNow + timeout;
            while (true) {
                if (SideOfPier == wanted) return true;
                if (DateTime.UtcNow >= deadline) return false;
                await Task.Delay(PierSidePollInterval, cancellationToken);
            }
        }

        private static readonly TimeSpan PierSidePollInterval = TimeSpan.FromSeconds(2);

        // A flip is a slew plus a settle, and drivers report the new side at their own pace. Long
        // enough for a slow driver, short enough to catch a mount that never moved.
        private static readonly TimeSpan MeridianFlipConfirmWindow = TimeSpan.FromSeconds(180);
        // When N.I.N.A. says the flip is done but the side hasn't changed, it chose not to flip. This
        // only covers the mediator's poll lag after a real flip.
        private static readonly TimeSpan MeridianFlipUnchangedSideGrace = TimeSpan.FromSeconds(20);

        // The backstop, a Control Hub capture and the autopilot can each ask for a flip.
        private readonly SemaphoreSlim _flipLock = new(1, 1);

        public bool IsFlipping => _flipLock.CurrentCount == 0;

        public async Task WaitForFlipAsync(CancellationToken cancellationToken) {
            await _flipLock.WaitAsync(cancellationToken);
            _flipLock.Release();
        }

        /// <summary>
        /// Flips to the wanted side, or the other side when null, with N.I.N.A.'s workflow once the
        /// target is past N.I.N.A.'s minutes-after-meridian. Confirms by reading the side back.
        /// </summary>
        public async Task<MeridianFlipOutcome> FlipAsync(Coordinates target, PierSide? wanted,
            CancellationToken cancellationToken) {
            await _flipLock.WaitAsync(cancellationToken);
            try {
                PierSide before = SideOfPier;
                MeridianFlipOutcome Failed(string reason, bool isDeferred = false)
                    => new MeridianFlipOutcome(false, false, before, SideOfPier, reason, isDeferred);

                // Checked first, because N.I.N.A. returns the same false for these as for a mount that
                // didn't move, and the confirmation would wait out its whole window for nothing.
                if (!IsConnected) return Failed("mount is not connected");
                if (IsParked) return Failed("the mount is parked, so there is nothing to flip");
                if (!IsMeridianFlipAvailable)
                    return Failed("this N.I.N.A. did not supply its meridian flip workflow, so there is " +
                                  "nothing here to ask for a flip");
                if (!IsPierSideKnown)
                    return Failed($"the mount reports its side of pier as {before}, so a flip can be neither " +
                                  "aimed nor confirmed");

                PierSide side = wanted ?? (before == PierSide.pierWest ? PierSide.pierEast : PierSide.pierWest);
                if (side == before) {
                    // Another caller's flip got there while this one waited for the lock.
                    Log($"Mount is already on {side}, so no flip needed.");
                    return new MeridianFlipOutcome(true, false, before, before, null, false);
                }

                // N.I.N.A. only flips to counterweight-down, which is pierEast past the meridian. pierWest
                // there can only be reached by tracking in from the east, so defer for the caller to re-judge.
                double hoursPastMeridian = HoursPastMeridianFor(target);
                if (side == PierSide.pierWest && hoursPastMeridian > 0.0) {
                    return Failed(
                        $"pierWest was asked for, but the target is {hoursPastMeridian:F2} h past the " +
                        "meridian, where N.I.N.A. only flips to pierEast",
                        isDeferred: true);
                }

                TimeSpan wait = side == PierSide.pierEast
                    ? TimeSpan.FromSeconds(Math.Max(0.0, SecondsUntilHourAngle(target, EarliestFlipHours)))
                    : TimeSpan.Zero;

                // Only recorded, since N.I.N.A.'s one bool also covers its re-centre and autofocus.
                bool isReportedComplete = await MeridianFlipAsync(target, wait, cancellationToken);

                // Checked whatever the workflow said, since the pier side is the only proof the mount moved.
                // Done with the side unchanged means N.I.N.A. judged it already counterweight-down.
                TimeSpan confirmWindow = isReportedComplete && SideOfPier == before
                    ? MeridianFlipUnchangedSideGrace
                    : MeridianFlipConfirmWindow;
                if (!await WaitForPierSideAsync(side, confirmWindow, cancellationToken)) {
                    return new MeridianFlipOutcome(false, isReportedComplete, before, SideOfPier,
                        $"mount did not reach {side} within {confirmWindow.TotalSeconds:F0} s " +
                        $"(still reporting {SideOfPier})" +
                        (isReportedComplete
                            ? ", though N.I.N.A. reported the flip as complete. It took the side the mount " +
                              "is on as counterweight-down, so the side wanted cannot be reached by flipping"
                            : ""),
                        false);
                }
                return new MeridianFlipOutcome(true, isReportedComplete, before, SideOfPier, null, false);
            } finally {
                _flipLock.Release();
            }
        }

        /// <summary>
        /// Stopped turns tracking off, and an unsupported mode falls back to Sidereal. False when
        /// disconnected or the mount can't set tracking.
        /// </summary>
        public bool SetTracking(TrackingMode mode) {
            TelescopeInfo info = telescopeMediator.GetInfo();
            if (!info.Connected) return false;
            if (!info.CanSetTrackingEnabled) {
                Log("This mount cannot change its tracking.");
                return false;
            }

            if (mode == TrackingMode.Stopped) {
                telescopeMediator.SetTrackingEnabled(false);
                telescopeMediator.SetTrackingMode(TrackingMode.Stopped);
                Log("Tracking stopped.");
                return true;
            }

            if (!info.TrackingModes.Contains(mode)) {
                LogWarning(
                    $"Unable to set tracking mode to {mode}. Defaulting to Sidereal instead.");
                mode = TrackingMode.Sidereal;
            }

            telescopeMediator.SetTrackingEnabled(true);
            telescopeMediator.SetTrackingMode(mode);
            Log($"Tracking set to {mode}.");
            return true;
        }

        /// <summary>
        /// Stops any slew and turns tracking off, then reads the driver straight back, so one that clears
        /// Slewing a poll later reads as still moving. Callers that can wait should use StopMovementAsync.
        /// </summary>
        public bool StopMovement() {
            if (!IssueStop()) return false;
            bool hasStopped = !telescopeMediator.GetInfo().Slewing;
            ReportStop(hasStopped);
            return hasStopped;
        }

        // How long a stop gets to show up in the driver before we report it failed.
        private static readonly TimeSpan StopSettle = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan StopPoll = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// StopMovement, but gives the driver time to report the stop and sends one more StopSlew if
        /// it hasn't. False only when disconnected or still slewing after both.
        /// </summary>
        public async Task<bool> StopMovementAsync() {
            if (!IssueStop()) return false;
            bool hasStopped = await WaitUntilNotSlewingAsync();
            if (!hasStopped) {
                LogWarning("Mount still slewing after StopSlew, so asking again.");
                IssueStop();
                hasStopped = await WaitUntilNotSlewingAsync();
            }
            ReportStop(hasStopped);
            return hasStopped;
        }

        // Both tracking calls are there to be safe.
        private bool IssueStop() {
            TelescopeInfo? info = telescopeMediator.GetInfo();
            if (!info.Connected) return false;
            if (info.Slewing) {
                Log("Stopping Slew");
                telescopeMediator.StopSlew();
            }
            telescopeMediator.SetTrackingEnabled(false);
            telescopeMediator.SetTrackingMode(TrackingMode.Stopped);
            return true;
        }

        private async Task<bool> WaitUntilNotSlewingAsync() {
            DateTime deadline = DateTime.UtcNow + StopSettle;
            while (telescopeMediator.GetInfo().Slewing) {
                if (DateTime.UtcNow >= deadline) return false;
                await Task.Delay(StopPoll);
            }
            return true;
        }

        private void ReportStop(bool hasStopped) {
            if (hasStopped) Log("Mount movement stopped.");
            else LogWarning("Mount still slewing after StopSlew");
        }

        public static TrackingMode ParseTrackingMode(string? trackingType) => trackingType?.ToLowerInvariant() switch {
            "stopped" => TrackingMode.Stopped,
            "lunar" => TrackingMode.Lunar,
            "solar" => TrackingMode.Solar,
            "king" => TrackingMode.King,
            _ => TrackingMode.Sidereal
        };

        ///////////////////////////////////////////  Pointing / tracking limits  ////////////////////////////////////////////////
        // Optional guards against slewing below the horizon or tracking a German equatorial past the
        // meridian into the pier. Every check passes unless Astraeus.IsMountLimitEnabled is on.
        //
        // The driver may have no hour-angle limit set, so never assume the mount will refuse to track
        // somewhere or land counterweight-down. Predictions only decide whether a pointing is worth
        // trying, and what the mount actually did is read back live.

        /// <summary>
        /// Signed hours past the meridian for the target at a time (default now). West is positive,
        /// wrapped to (-12, +12]. Public because the flip logic needs the number, not just a verdict.
        /// </summary>
        public double HoursPastMeridianFor(Coordinates target, DateTime? at = null)
            => PredictAt(target, at ?? DateTime.Now).hoursPastMeridian;

        /// <summary>
        /// Clock seconds until the target's hour angle reaches hoursPastMeridian. Negative if it's already past.
        /// </summary>
        public double SecondsUntilHourAngle(Coordinates target, double hoursPastMeridian, DateTime? at = null) {
            double hours = hoursPastMeridian - HoursPastMeridianFor(target, at);
            return hours / SiderealShiftTrackingRate.SIDEREAL_SEC_PER_SI_SEC * 3600.0;
        }

        private IMeridianFlipSettings MeridianFlipSettings
            => Observatory.Settings.ProfileService.ActiveProfile.MeridianFlipSettings;

        /// <summary>
        /// Minus N.I.N.A.'s Pause before meridian when one is set, else its Maximum minutes after meridian.
        /// Must match the server's effective_meridian_limit_hours.
        /// </summary>
        public double MeridianLimitHours {
            get {
                IMeridianFlipSettings meridianFlip = MeridianFlipSettings;
                return meridianFlip.PauseTimeBeforeMeridian > 0
                    ? -meridianFlip.PauseTimeBeforeMeridian / 60.0
                    : meridianFlip.MaxMinutesAfterMeridian / 60.0;
            }
        }

        public double EarliestFlipHours => MeridianFlipSettings.MinutesAfterMeridian / 60.0;

        public string DescribeMeridianLimit() {
            IMeridianFlipSettings meridianFlip = MeridianFlipSettings;
            return meridianFlip.PauseTimeBeforeMeridian > 0
                ? $"pause {meridianFlip.PauseTimeBeforeMeridian:0.#} min before the meridian"
                : $"{meridianFlip.MaxMinutesAfterMeridian:0.#} min past the meridian";
        }

        // The hour angle each side breaches at. pierWest (or an unknown side) breaches west of the
        // limit, and pierEast east of its mirror. The mirror never goes west of the meridian.
        private double MeridianThresholdHours(PierSide side)
            => side == PierSide.pierEast ? -Math.Max(MeridianLimitHours, 0.0) : MeridianLimitHours;

        /// <summary>
        /// Negative by how far the target is already past the westward limit. Keep the sign, since that's
        /// how we spot a mount that was asked to flip and didn't. Null when limits are off or it isn't a GEM.
        /// </summary>
        public double? SecondsToMeridianLimit(Coordinates target, DateTime? at = null) {
            if (!Observatory.Settings.IsMountLimitEnabled() || !IsGermanEquatorial) return null;
            return SecondsUntilHourAngle(target, MeridianLimitHours, at);
        }

        /// <summary>
        /// Works before a slew as well as while tracking, since the side of pier is read live. Only the
        /// window's ends are tested, because hour angle only rises and each side has a single threshold.
        /// </summary>
        public bool WouldBreachMeridianOnCurrentSide(Coordinates target, TimeSpan window) {
            if (!Observatory.Settings.IsMountLimitEnabled() || !IsGermanEquatorial) return false;
            PierSide side = SideOfPier;
            DateTime start = DateTime.Now;
            DateTime end = start + (window > TimeSpan.Zero ? window : TimeSpan.Zero);
            return DescribeMeridianBreach(HoursPastMeridianFor(target, start), side, "now") != null
                || DescribeMeridianBreach(HoursPastMeridianFor(target, end), side, "shortly") != null;
        }

        /// <summary>
        /// A prediction of which limit the target breaks, used to decide whether a pointing is worth trying.
        /// The real enforcement is DescribeCurrentPositionBreach, on the side the mount actually ends up on.
        /// </summary>
        public string? DescribeLimitBreach(Coordinates target, DateTime at, string when = "now",
            bool shouldIncludeMeridian = true)
            => DescribeLimitBreach(target, at, PierSideForTracking(target), when, shouldIncludeMeridian);

        // The pier side is the slow part to work out and it doesn't change over a look-ahead window,
        // so the sampler works it out once and passes it in.
        private string? DescribeLimitBreach(Coordinates target, DateTime at, PierSide side, string when,
            bool shouldIncludeMeridian) {
            if (!Observatory.Settings.IsMountLimitEnabled()) return null;
            (double altitude, double azimuth, double hoursPastMeridian) = PredictAt(target, at);
            return DescribeAltitudeBreach(altitude, azimuth, when)
                   ?? (shouldIncludeMeridian ? DescribeMeridianBreach(hoursPastMeridian, side, when) : null);
        }

        // The side to judge a target against. The real side if the mount is already on it, otherwise
        // the counterweight-down side a slew would normally land on. That's a guess, since some
        // drivers slew on whatever side they're on, so it only decides whether to try the slew.
        // Whether the result is safe is DescribeCurrentPositionBreach's job.
        private PierSide PierSideForTracking(Coordinates target) {
            if (IsPierSideKnown && IsPointingAt(target, PierSideSameFieldDegrees)) return SideOfPier;
            double localSiderealTime = AstroUtil.GetLocalSiderealTimeNow(SiteLongitudeDegrees);
            return MeridianFlip.ExpectedPierSide(target, Angle.ByHours(localSiderealTime));
        }

        // How close counts as "on this target" when trusting the live pier side. Much looser than the
        // re-centre tolerance, so a dither or a drifting guide star doesn't change the answer.
        private const double PierSideSameFieldDegrees = 5.0;

        private static readonly TimeSpan DefaultTrackingSampleStep = TimeSpan.FromMinutes(1);

        /// <summary>
        /// The first limit the mount would break while tracking the target over the window. Pass
        /// shouldIncludeMeridian false when the caller has a flip lined up.
        /// </summary>
        public string? DescribeTrackingBreach(Coordinates target, TimeSpan window,
            bool shouldIncludeMeridian = true, TimeSpan? step = null) {
            if (!Observatory.Settings.IsMountLimitEnabled()) return null;
            TimeSpan sampleStep = step ?? DefaultTrackingSampleStep;
            if (sampleStep <= TimeSpan.Zero) sampleStep = DefaultTrackingSampleStep;

            // Once for the whole window. The mount doesn't change sides while tracking, and this is the
            // slow part of each sample.
            PierSide side = PierSideForTracking(target);
            DateTime start = DateTime.Now;
            DateTime end = start + (window > TimeSpan.Zero ? window : TimeSpan.Zero);
            for (DateTime sampleTime = start; sampleTime < end; sampleTime += sampleStep) {
                if (DescribeLimitBreach(target, sampleTime, side, WhenLabel(sampleTime - start),
                        shouldIncludeMeridian) is string breach)
                    return breach;
            }
            // Always test the far end so a violation between the last step and the window end isn't missed.
            return DescribeLimitBreach(target, end, side, WhenLabel(end - start), shouldIncludeMeridian);
        }

        private static string WhenLabel(TimeSpan ahead)
            => ahead.TotalMinutes >= 0.5 ? $"in {ahead.TotalMinutes:F0} min" : "now";

        /// <summary>
        /// Polled by the autopilot's limit watcher. True when limits are off, or the mount is parked or
        /// disconnected.
        /// </summary>
        public bool IsCurrentPositionWithinLimits(bool shouldIncludeMeridian = true)
            => DescribeCurrentPositionBreach(shouldIncludeMeridian) == null;

        /// <summary>
        /// The real enforcement, since the driver may have no hour-angle limit set. A parked mount never
        /// breaches, because parking is where every remedy ends. Pass shouldIncludeMeridian false when a
        /// flip a state or two away will fix it.
        /// </summary>
        public string? DescribeCurrentPositionBreach(bool shouldIncludeMeridian = true)
            => IsParked ? null : DescribePointingBreach(shouldIncludeMeridian);

        // The live breach on geometry alone, whatever the mount is doing. Only the backstop uses
        // this, to tell a resting breach from a live one. Everything else uses DescribeCurrentPositionBreach.
        private string? DescribePointingBreach(bool shouldIncludeMeridian) {
            if (!Observatory.Settings.IsMountLimitEnabled()) return null;
            TelescopeInfo info = telescopeMediator.GetInfo();
            if (!info.Connected) return null;
            // A driver that briefly reports no coordinates would give HA = LST, which nearly always
            // reads as past the limit, and the watcher would park, close the roof and end the night.
            if (info.Coordinates == null) return null;

            string? altitude = DescribeAltitudeBreach(info.Altitude, info.Azimuth, "now");
            if (altitude != null || !shouldIncludeMeridian) return altitude;

            double localSiderealTime = AstroUtil.GetLocalSiderealTimeNow(SiteLongitudeDegrees);
            double hourAngle = AstroUtil.GetHourAngle(localSiderealTime, info.Coordinates.RA); // hours, [0, 24)
            return DescribeMeridianBreach(HoursPastMeridian(hourAngle), SideOfPier, "now");
        }

        public bool IsPointingAt(Coordinates target, double toleranceDegrees) {
            TelescopeInfo info = telescopeMediator.GetInfo();
            if (!info.Connected || info.AtPark || info.Slewing || !info.TrackingEnabled) return false;
            if (info.Coordinates == null) return false;
            // The mount reports in its own epoch, usually JNow, and the target is J2000. Without a
            // common epoch the precession offset of tens of arcmin reads as "moved" and forces a re-slew.
            Coordinates current = info.Coordinates.Transform(Epoch.J2000);
            Coordinates reference = target.Transform(Epoch.J2000);
            return AngularSeparationDegrees(current, reference) <= toleranceDegrees;
        }

        /// <summary>Parked counts too, since the next slew starts from the park position anyway.</summary>
        public Coordinates? CurrentCoordinatesJ2000() {
            TelescopeInfo info = telescopeMediator.GetInfo();
            if (!info.Connected || info.Coordinates == null) return null;
            return info.Coordinates.Transform(Epoch.J2000);
        }

        // Haversine, because it's stable for the small separations we test.
        internal static double AngularSeparationDegrees(Coordinates first, Coordinates second) {
            const double degreesToRadians = Math.PI / 180.0;
            double firstRa = first.RADegrees * degreesToRadians;
            double firstDec = first.Dec * degreesToRadians;
            double secondRa = second.RADegrees * degreesToRadians;
            double secondDec = second.Dec * degreesToRadians;
            double sinHalfDecDifference = Math.Sin((secondDec - firstDec) / 2.0);
            double sinHalfRaDifference = Math.Sin((secondRa - firstRa) / 2.0);
            double haversine = sinHalfDecDifference * sinHalfDecDifference
                               + Math.Cos(firstDec) * Math.Cos(secondDec) * sinHalfRaDifference * sinHalfRaDifference;
            return 2.0 * Math.Asin(Math.Min(1.0, Math.Sqrt(haversine))) / degreesToRadians;
        }

        // Predicted altitude, azimuth (degrees) and signed hours past the meridian for a target at a
        // given time.
        private (double altitude, double azimuth, double hoursPastMeridian) PredictAt(Coordinates target, DateTime at) {
            IAstrometrySettings astrometry = Observatory.Settings.ProfileService.ActiveProfile.AstrometrySettings;
            TopocentricCoordinates topocentric = target.Transform(
                Angle.ByDegree(astrometry.Latitude),
                Angle.ByDegree(astrometry.Longitude),
                astrometry.Elevation,
                at);
            double localSiderealTime = AstroUtil.GetLocalSiderealTime(at, astrometry.Longitude);
            // Apparent sidereal time pairs with a JNow RA, as in the live check on the mount's own coordinates.
            double jNowRa = target.Transform(Epoch.JNOW).RA;
            double hourAngle = AstroUtil.GetHourAngle(localSiderealTime, jNowRa); // hours, [0, 24)
            return (topocentric.Altitude.Degree, topocentric.Azimuth.Degree, HoursPastMeridian(hourAngle));
        }

        // The custom horizon's altitude at an azimuth, or NegativeInfinity when it is off or not loaded.
        private double CustomHorizonAltitude(double azimuthDegrees) {
            AstraeusSettings settings = Observatory.Settings;
            if (!settings.IsCustomHorizonLimitEnabled()) return double.NegativeInfinity;
            CustomHorizon? horizon = settings.ProfileService.ActiveProfile?.AstrometrySettings?.Horizon;
            return horizon?.GetAltitude(NormalizeAzimuth(azimuthDegrees)) ?? double.NegativeInfinity;
        }

        // The two hard floors. Nothing waives them and no flip fixes them.
        private string? DescribeAltitudeBreach(double altitudeDegrees, double azimuthDegrees, string when) {
            AstraeusSettings settings = Observatory.Settings;
            if (settings.IsAltitudeLimitEnabled() && altitudeDegrees < settings.GetMountMinAltitudeDegrees())
                return $"below altitude limit ({altitudeDegrees:F1}° {when}, " +
                       $"minimum {settings.GetMountMinAltitudeDegrees():F1}°)";

            double horizonAltitude = CustomHorizonAltitude(azimuthDegrees);
            if (altitudeDegrees < horizonAltitude)
                return $"below custom horizon ({altitudeDegrees:F1}° {when} at azimuth " +
                       $"{NormalizeAzimuth(azimuthDegrees):F0}°, horizon {horizonAltitude:F1}°)";

            return null;
        }

        // The meridian limit L (MeridianLimitHours), mirrored about the meridian. Which side applies
        // depends on where the mount is hanging:
        //
        //                              MERIDIAN (HA = 0)
        //     east                           |                          west
        //   HA -12 ------------------------  0  ------------------------ +12
        //   pierWest (looking east)   safe: [ -12 ........... 0 ... +L ]
        //   pierEast (looking west)   safe:  [ -max(L, 0) ... 0 ........... +12 ]
        //
        // A flip moves the mount from one row to the other, so a flipped mount tracking past +L is
        // fine. A GEM with an unknown side gets the pierWest row. A mount that isn't a GEM gets no
        // meridian rule.
        private string? DescribeMeridianBreach(double hoursPastMeridian, PierSide side, string when) {
            if (!Observatory.Settings.IsMountLimitEnabled() || !IsGermanEquatorial) return null;
            double threshold = MeridianThresholdHours(side);
            string onSide = side is PierSide.pierEast or PierSide.pierWest ? $" on {side}" : "";

            if (side == PierSide.pierEast) {
                if (hoursPastMeridian >= threshold) return null;
                string eastLimit = threshold < 0
                    ? $"limit {-threshold * 60:0.#} min east of the meridian"
                    : "limit at the meridian";
                return $"past meridian limit ({DescribeHourAngle(hoursPastMeridian)} {when}{onSide}, {eastLimit})";
            }

            if (hoursPastMeridian <= threshold) return null;
            string westLimit = MeridianFlipSettings.PauseTimeBeforeMeridian > 0
                ? DescribeMeridianLimit()
                : $"limit {DescribeMeridianLimit()}";
            return $"past meridian limit ({DescribeHourAngle(hoursPastMeridian)} {when}{onSide}, {westLimit})";
        }

        private static string DescribeHourAngle(double hoursPastMeridian)
            => hoursPastMeridian >= 0
                ? $"{hoursPastMeridian * 60:0.#} min west of the meridian"
                : $"{-hoursPastMeridian * 60:0.#} min east of the meridian";

        // Convert an hour angle in [0, 24) hours to signed hours past the meridian (west positive).
        private static double HoursPastMeridian(double hourAngleHours)
            => hourAngleHours <= 12.0 ? hourAngleHours : hourAngleHours - 24.0;

        private static double NormalizeAzimuth(double azimuthDegrees) {
            double wrappedAzimuth = azimuthDegrees % 360.0;
            return wrappedAzimuth < 0 ? wrappedAzimuth + 360.0 : wrappedAzimuth;
        }

        private double SiteLongitudeDegrees
            => Observatory.Settings.ProfileService.ActiveProfile.AstrometrySettings.Longitude;

        ///////////// Guiding around hub moves /////////////
        // Like N.I.N.A.'s sequencer, the hub's commands stop guiding before a slew, park or home, and
        // restart it after a slew. Guiding through a slew loses the star. It can be on without anyone
        // starting it, since N.I.N.A.'s meridian flip resumes guiding whenever a guider is connected.
        // Only the command handlers do this. The autopilot shares the core operations and guides itself.

        /// <summary>Never throws except on cancellation.</summary>
        private async Task<GuidingStopOutcome> StopGuidingForMoveAsync(CancellationToken cancellationToken) {
            if (!Observatory.TryGetComponent<Guider>(out Guider? guider)) return GuidingStopOutcome.NotRunning;
            GuidingStopOutcome outcome = await guider.StopGuidingAsync(cancellationToken);
            if (outcome == GuidingStopOutcome.NotConfirmed) {
                LogWarning("Guider did not confirm it stopped. Moving the mount anyway.");
            }
            return outcome;
        }

        /// <summary>
        /// Restarts guiding after a hub slew it was stopped for, as N.I.N.A.'s sequencer does. The hub
        /// has no guiding controls, so it would otherwise stay off.
        /// </summary>
        private async Task ResumeGuidingAfterSlewAsync(CancellationToken cancellationToken) {
            if (!Observatory.TryGetComponent<Guider>(out Guider? guider)) return;
            if (!await guider.StartGuidingAsync(cancellationToken)) {
                LogWarning("Guiding was running before the slew and did not start again. Start it from N.I.N.A.");
            }
        }

        ///////////// Command Handlers /////////////

        /// <summary>Toggles the park state: unparks when parked, parks otherwise.</summary>
        private async Task ParkCommandAsync(string? id) {
            if (!IsConnected) {
                await BroadcastMessageReceivedAsync(id, false, "No device connected");
                return;
            }

            await BroadcastMessageReceivedAsync(id, true);
            try {
                CancellationToken cancellationToken = BeginOperation(ref _slewCancellationSource);
                if (IsParked) {
                    await UnparkAsync(cancellationToken);
                } else {
                    await StopGuidingForMoveAsync(cancellationToken);
                    await ParkAsync(cancellationToken);
                }
            } catch (OperationCanceledException) {
                Log("Park/unpark aborted.");
            }
        }

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
            if (!telescopeMediator.GetInfo().CanSetPark) {
                await BroadcastMessageReceivedAsync(id, false, "This mount's driver cannot set a park position");
                return;
            }
            Coordinates position = telescopeMediator.GetCurrentPosition();
            if (TryGetDeviceViewModel<TelescopeVM>(out TelescopeVM? mountViewModel)) {
                await BroadcastMessageReceivedAsync(id, true, "Setting park position");
                InvokeOnUiThread(() => mountViewModel.SetParkPositionCommand.Execute(position));
                Log($"Park position set to {position}.");
            } else {
                await BroadcastMessageReceivedAsync(id, false, "N.I.N.A.'s mount controls are not available");
            }
        }

        private async Task HomeCommandAsync(string? id) {
            if (!IsConnected) {
                await BroadcastMessageReceivedAsync(id, false, "No device connected");
                return;
            }
            await BroadcastMessageReceivedAsync(id, true);
            try {
                CancellationToken cancellationToken = BeginOperation(ref _slewCancellationSource);
                await StopGuidingForMoveAsync(cancellationToken);
                await FindHomeAsync(cancellationToken);
            } catch (OperationCanceledException) {
                Log("Find home aborted.");
            } catch (Exception ex) {
                LogWarning($"Mount find home failed: {ex.Message}");
            }
        }

        private async Task SetTrackingCommandAsync(string? id, string? trackingType) {
            bool isTrackingSet = SetTracking(ParseTrackingMode(trackingType));
            if (!isTrackingSet) {
                await BroadcastMessageReceivedAsync(id, false,
                    "The mount is not connected or cannot change its tracking");
                return;
            }
            await BroadcastMessageReceivedAsync(id, true);
        }

        private async Task SlewCommandAsync(string? id, double raHours, double dec) {
            Coordinates coordinates = new Coordinates(raHours, dec, Epoch.J2000, Coordinates.RAType.Hours);
            // Checked before the ack, so a refused slew is rejected with its reason and not just logged.
            if (!IsConnected) {
                await BroadcastMessageReceivedAsync(id, false, "No device connected");
                return;
            }
            if (IsParked) {
                await BroadcastMessageReceivedAsync(id, false, "Mount is parked");
                return;
            }
            if (DescribeLimitBreach(coordinates, DateTime.Now) is string breach) {
                string refusal = $"Can't slew to {coordinates.RAString}, {coordinates.DecString}: {breach}";
                LogWarning(refusal);
                await BroadcastMessageReceivedAsync(id, false, refusal);
                return;
            }

            await BroadcastMessageReceivedAsync(id, true);
            _lastManualSlewTarget = coordinates;
            try {
                CancellationToken cancellationToken = BeginOperation(ref _slewCancellationSource);
                GuidingStopOutcome guiding = await StopGuidingForMoveAsync(cancellationToken);
                if (await SlewToAsync(coordinates, cancellationToken) && guiding == GuidingStopOutcome.Stopped) {
                    await ResumeGuidingAfterSlewAsync(cancellationToken);
                }
            } catch (OperationCanceledException) {
                Log("Slew aborted.");
            }
        }

        private async Task StopSlewCommandAsync(string? id) {
            await BroadcastMessageReceivedAsync(id, true);
            AbortSlew();
            StopMovement();
        }

        /// <summary>
        /// Uses N.I.N.A.'s centering solver with the profile's plate-solve settings. A null filter keeps
        /// the current one, and any filter change is reverted afterwards.
        /// </summary>
        public async Task<bool> CenterAsync(Coordinates target, CancellationToken cancellationToken = default) {
            IPlateSolverFactory factory = Observatory.PlateSolverFactory;
            IProfile profile = Observatory.Settings.ProfileService.ActiveProfile;
            IPlateSolveSettings plateSolveSettings = profile.PlateSolveSettings;

            IPlateSolver? plateSolver = GetConfiguredPlateSolver(factory, plateSolveSettings);
            if (plateSolver == null) return false;
            IPlateSolver blindSolver = factory.GetBlindSolver(plateSolveSettings);
            ICenteringSolver solver = factory.GetCenteringSolver(plateSolver, blindSolver, imagingMediator,
                telescopeMediator, filterWheelMediator, domeMediator, domeFollower);

            CenterSolveParameter parameter = new CenterSolveParameter {
                Coordinates = target,
                Threshold = plateSolveSettings.Threshold,
                NoSync = profile.TelescopeSettings.NoSync
            };
            ApplyProfileSolveSettings(parameter, profile);
            CaptureSequence solveExposure = CreatePlateSolveExposure(plateSolveSettings);

            PlateSolveResult? result;
            // So each solve exposure reaches the dashboard's latest-frame card, labelled.
            using (Observatory.FrameLabel.Begin(PreviewSource.PlateSolve))
                result = await solver.Center(solveExposure, parameter, null, NoProgress, cancellationToken);

            if (result?.Success != true) {
                LogWarning("Centering failed.", device: "autopilot", category: LogCategory.Autopilot);
                return false;
            }

            Log(
                $"Centred: RA={result.Coordinates.RAString}, Dec={result.Coordinates.DecString}, " +
                $"Separation={result.Separation?.Distance.ArcMinutes:F2}', Pixscale={result.Pixscale:F2}\"/px",
                device: "autopilot", category: LogCategory.Autopilot);
            return true;
        }

        /// <summary>
        /// N.I.N.A.'s dome maths throws after a centring slew when its dome geometry isn't set, though the
        /// solve, sync and slew have all worked by then. Matched on N.I.N.A.'s localised text so it works
        /// in any language.
        /// </summary>
        internal static bool IsDomeGeometryFailure(Exception exception) =>
            exception.Message == Loc.Instance["LblDomeRadiusMisconfigured"];
        
        /// <summary>
        /// Solves where the mount points, or null when there's no solver or the solve fails. The rotator
        /// syncs against the result's PositionAngle.
        /// </summary>
        public async Task<PlateSolveResult?> CaptureAndSolveAsync(CancellationToken cancellationToken = default) {
            IPlateSolverFactory factory = Observatory.PlateSolverFactory;
            IProfile profile = Observatory.Settings.ProfileService.ActiveProfile;
            IPlateSolveSettings plateSolveSettings = profile.PlateSolveSettings;

            IPlateSolver? plateSolver = GetConfiguredPlateSolver(factory, plateSolveSettings);
            if (plateSolver == null) return null;
            IPlateSolver blindSolver = factory.GetBlindSolver(plateSolveSettings);
            ICaptureSolver solver = factory.GetCaptureSolver(plateSolver, blindSolver, imagingMediator,
                filterWheelMediator);

            CaptureSolverParameter parameter = new CaptureSolverParameter {
                Coordinates = telescopeMediator.GetCurrentPosition()
            };
            ApplyProfileSolveSettings(parameter, profile);
            CaptureSequence solveExposure = CreatePlateSolveExposure(plateSolveSettings);

            PlateSolveResult? result;
            using (Observatory.FrameLabel.Begin(PreviewSource.PlateSolve))
                result = await solver.Solve(solveExposure, parameter, null, NoProgress, cancellationToken);

            if (result?.Success != true) {
                LogWarning("Plate solve failed.", device: "autopilot", category: LogCategory.Autopilot);
                return null;
            }

            Log($"Solved: RA={result.Coordinates.RAString}, Dec={result.Coordinates.DecString}, " +
                $"PA={result.PositionAngle:F2}°, Flipped={result.Flipped}",
                device: "autopilot", category: LogCategory.Autopilot);
            return result;
        }

        private IPlateSolver? GetConfiguredPlateSolver(IPlateSolverFactory factory,
            IPlateSolveSettings plateSolveSettings) {
            IPlateSolver? plateSolver = factory.GetPlateSolver(plateSolveSettings);
            if (plateSolver == null) {
                LogWarning("No plate solver configured in the N.I.N.A. profile.", device: "autopilot",
                    category: LogCategory.Autopilot);
            }
            return plateSolver;
        }

        private static void ApplyProfileSolveSettings(CaptureSolverParameter parameter, IProfile profile) {
            IPlateSolveSettings plateSolveSettings = profile.PlateSolveSettings;
            parameter.Attempts = plateSolveSettings.NumberOfAttempts;
            parameter.Binning = plateSolveSettings.Binning;
            parameter.DownSampleFactor = plateSolveSettings.DownSampleFactor;
            parameter.FocalLength = profile.TelescopeSettings.FocalLength;
            parameter.MaxObjects = plateSolveSettings.MaxObjects;
            parameter.PixelSize = profile.CameraSettings.PixelSize;
            parameter.ReattemptDelay = TimeSpan.FromMinutes(plateSolveSettings.ReattemptDelay);
            parameter.Regions = plateSolveSettings.Regions;
            parameter.SearchRadius = plateSolveSettings.SearchRadius;
            parameter.BlindFailoverEnabled = plateSolveSettings.BlindFailoverEnabled;
        }

        private static CaptureSequence CreatePlateSolveExposure(IPlateSolveSettings plateSolveSettings) {
            BinningMode binning = new BinningMode(plateSolveSettings.Binning, plateSolveSettings.Binning);
            CaptureSequence solveExposure = new CaptureSequence(plateSolveSettings.ExposureTime,
                CaptureSequence.ImageTypes.SNAPSHOT, plateSolveSettings.Filter, binning, 1);
            solveExposure.Gain = plateSolveSettings.Gain;
            return solveExposure;
        }
    }

    /// <summary>
    /// HasReachedTargetSide comes from reading the pier side back, and IsReportedComplete is N.I.N.A.'s
    /// verdict on its whole flip workflow.
    /// </summary>
    public sealed record MeridianFlipOutcome(bool HasReachedTargetSide, bool IsReportedComplete, PierSide Before,
        PierSide After, string? FailReason, bool IsDeferred);

    public enum ManualExposureAction { Proceed, Flip, Refuse }

    /// <summary>Reason is set on Refuse.</summary>
    public sealed record ManualExposurePlan(ManualExposureAction Action, string? Reason = null);
}