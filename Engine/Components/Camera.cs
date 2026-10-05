using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Equipment.Model;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.PlateSolving;
using NINA.PlateSolving.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using CosmicVaults.NINA.Astraeus.Engine.Imaging;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot;
using CosmicVaults.NINA.Astraeus.Engine.Calibration;
using CosmicVaults.NINA.Astraeus.Engine.Utility;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Payloads;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel.Equipment.Camera;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace CosmicVaults.NINA.Astraeus.Engine {
    public class Camera(
        Observatory observatory,
        IWebSocketBus webSocketBus,
        ICameraMediator cameraMediator,
        IImagingMediator imagingMediator,
        IImageSaveMediator imageSaveMediator)
        : DeviceComponent<ICameraVM, ICameraConsumer, CameraInfo>(cameraMediator, observatory, webSocketBus) {
        public override string DeviceType { get; } = "camera";
        public override string DisplayName { get; } = "Camera";
        protected override string? RegistrationEndpoint => "cameras";
        
        private FilterWheel? _filterWheel;
        private FitsHeaderPatcher? _fitsHeaderPatcher;

        private CancellationTokenSource? _coolingCancellationSource;

        /// <summary>
        /// Set once the missing gain/offset warning is logged, so it shows once per session. A camera
        /// that can't report gain never will.
        /// </summary>
        private bool _hasWarnedMissingGain;

        /// <summary>
        /// Set once the missing DATE-OBS warning is logged, so it shows once per session. Whatever
        /// omits the header will keep omitting it.
        /// </summary>
        private bool _hasWarnedMissingObservedAt;

        /// <summary>
        /// Starting a new capture abandons whatever was here. All per-capture state hangs off this so it
        /// can't outlive its frame.
        /// </summary>
        private FrameCaptureOperationData? _currentCapture;

        private static readonly TimeSpan SaveTimeout = TimeSpan.FromMinutes(2);

        private const int UseProfileGainOrOffset = -1;

        /// <summary>
        /// Cancels a whole dashboard run, not just the current frame. An abort between frames has
        /// nothing in the slot to cancel.
        /// </summary>
        private CancellationTokenSource? _manualRunCancellationSource;

        /// <summary>1 while a manual run is active. A second Capture press is rejected, not queued.</summary>
        private int _manualRunActive;

        /// <summary>Why the manual run stopped, if not by the observer. Null means "Aborted by user".</summary>
        private string? _stopReason;

        /// <summary>
        /// Also true through a whole Control Hub run, between frames too, since the run does its own flip
        /// before each frame.
        /// </summary>
        public bool IsExposing =>
            Volatile.Read(ref _manualRunActive) == 1
            || cameraMediator.GetInfo() is { Connected: true, IsExposing: true };

        /// <summary>
        /// Chains each manual frame's calibration and upload hand-off in the background, so a
        /// short-exposure run doesn't wait on the calibrator or the uplink.
        /// </summary>
        private Task _manualFrames = Task.CompletedTask;

        /// <summary>A N.I.N.A. prepared frame headed for the dashboard, and what it was taken for.</summary>
        private sealed record SnapshotFrame(IRenderedImage Image, string Source);

        /// <summary>The newest frame not yet sent. It replaces any older one still waiting.</summary>
        private SnapshotFrame? _pendingSnapshot;

        /// <summary>1 while RelaySnapshotsAsync is running. There's only ever one.</summary>
        private int _snapshotRelayRunning;

        /// <summary>
        /// Autofocus is a burst of short exposures, and each frame costs an encode here and a send to
        /// every watching tab.
        /// </summary>
        private static readonly TimeSpan SnapshotMinInterval = TimeSpan.FromSeconds(1);

        // Set by the autopilot's thread, read when N.I.N.A. saves a frame on its own.
        private volatile string? _currentProjectName;
        private volatile string? _currentTargetName;
        public string? CurrentProjectName {
            get => _currentProjectName;
            set => _currentProjectName = value;
        }
        public string? CurrentTargetName {
            get => _currentTargetName;
            set => _currentTargetName = value;
        }

        ///////////// Component overrides /////////////
        public override async Task Start() {
            await base.Start();
            _hasWarnedMissingGain = false;
            _hasWarnedMissingObservedAt = false;
            if (Observatory.TryGetComponent<FilterWheel>(out FilterWheel? filterWheel)) {
                _filterWheel = filterWheel;
            } else {
                LogWarning("No filter wheel component registered, so frames will be captured with no filter recorded.");
            }

            _fitsHeaderPatcher = new FitsHeaderPatcher(message => Log(message), message => LogWarning(message));
            imageSaveMediator.BeforeFinalizeImageSaved += OnBeforeFinalizeImageSaved;
            imageSaveMediator.ImageSaved += OnImageSaved;
            imagingMediator.ImagePrepared += OnImagePrepared;
        }

        public override Task Destroy() {
            imageSaveMediator.BeforeFinalizeImageSaved -= OnBeforeFinalizeImageSaved;
            imageSaveMediator.ImageSaved -= OnImageSaved;
            imagingMediator.ImagePrepared -= OnImagePrepared;
            Volatile.Write(ref _pendingSnapshot, null);
            _currentCapture?.Abandon();
            CancelManualRun();
            _coolingCancellationSource?.Dispose();
            return base.Destroy();
        }

        ///////////// Web socket messages /////////////
        public override WsMessage? GetUpdateMessage() {
            if (LastInfo == null) {
                return null;
            }

            double secondsRemaining = 0.0;
            if (LastInfo.IsExposing) {
                // The end time passes while the frame downloads, so clamp at zero.
                double secondsToEnd = (LastInfo.ExposureEndTime - DateTime.Now).TotalSeconds;
                secondsRemaining = Math.Round(Math.Max(0.0, secondsToEnd), 2);
            }
            CameraPayload payload = new CameraPayload {
                DeviceId = DeviceId,
                Temperature = DevicePayload.CleanValue(LastInfo.Temperature),
                TemperatureSetPoint = DevicePayload.CleanValue(LastInfo.TemperatureSetPoint),
                CoolerPower = DevicePayload.CleanValue(LastInfo.CoolerPower),
                IsCoolerOn = LastInfo.CoolerOn,
                IsDewHeaterOn = LastInfo.DewHeaterOn,
                IsExposing = LastInfo.IsExposing,
                ExposureRemaining = secondsRemaining,
                IsConnected = true
            };
            return new CameraUpdate(payload);
        }

        public override WsMessage? GetDeviceStaticInfo() {
            CameraInfo info = cameraMediator.GetInfo();
            CameraPayload payload = new CameraPayload() {
                DeviceId = DeviceId,
                Name = info.Name,
                IsConnected = info.Connected,
                DisplayName = info.DisplayName,
                Description = info.Description,
                DriverInfo = info.DriverInfo,
                DriverVersion = info.DriverVersion,
                CanSetTemperature = info.CanSetTemperature,
                HasShutter = info.HasShutter,
                DefaultGain = info.Gain,
                GainMax = info.GainMax,
                GainMin = info.GainMin,
                ElectronsPerADU = DevicePayload.CleanValue(info.ElectronsPerADU),
                BinX = info.BinX,
                BinY = info.BinY,
                BitDepth = info.BitDepth,
                CanSetOffset = info.CanSetOffset,
                CanSetGain = info.CanSetGain,
                OffsetMin = info.OffsetMin,
                OffsetMax = info.OffsetMax,
                Offset = info.Offset,
                DefaultOffset = info.DefaultOffset,
                USBLimit = info.USBLimit,
                CanSetUSBLimit = info.CanSetUSBLimit,
                USBLimitMax = info.USBLimitMax,
                USBLimitMin = info.USBLimitMin,
                IsSubSampleEnabled = info.IsSubSampleEnabled,
                CameraState = info.CameraState.ToString(),
                XSize = info.XSize,
                YSize = info.YSize,
                PixelSize = DevicePayload.CleanValue(info.PixelSize),
                Battery = info.Battery,
                CanGetGain = info.CanGetGain,
                IsCoolerOn = info.CoolerOn,
                CoolerPower = DevicePayload.CleanValue(info.CoolerPower),
                HasDewHeater = info.HasDewHeater,
                CanSubSample = info.CanSubSample,
                SubSampleX = info.SubSampleX,
                SubSampleY = info.SubSampleY,
                SubSampleWidth = info.SubSampleWidth,
                SubSampleHeight = info.SubSampleHeight,
                ReadoutMode = info.ReadoutMode,
                ReadoutModeForSnapImages = info.ReadoutModeForSnapImages,
                ReadoutModeForNormalImages = info.ReadoutModeForNormalImages,
                // A copy, so serialization never enumerates NINA's live collection.
                ReadoutModes = info.ReadoutModes?.ToList(),
                SensorType = info.SensorType.ToString(),
                BayerOffsetX = info.BayerOffsetX,
                BayerOffsetY = info.BayerOffsetY,
                ExposureMax = DevicePayload.CleanValue(info.ExposureMax),
                ExposureMin = DevicePayload.CleanValue(info.ExposureMin),
                IsLiveViewEnabled = info.LiveViewEnabled,
                CanShowLiveView = info.CanShowLiveView,
            };
            return new CameraUpdate(payload);
        }

        protected override Task HandleCommandAsync(WsCommand command) {
            switch (command.Action) {
                case "capture":
                    StartCaptureCommand(command);
                    break;
                case "abort":
                    AbortCommandAsync(command.Id).ObserveFaults(LogCommandFault);
                    break;
                case "setCooler":
                    SetCoolerCommandAsync(command.Id, command.Payload).ObserveFaults(LogCommandFault);
                    break;
                case "setTemperature":
                    SetTemperatureCommandAsync(command.Id, command.Payload).ObserveFaults(LogCommandFault);
                    break;
                case "setDewHeater":
                    SetDewHeaterCommandAsync(command.Id, command.Payload).ObserveFaults(LogCommandFault);
                    break;
            }
            return Task.CompletedTask;
        }

        private void StartCaptureCommand(WsCommand command) {
            JsonElement payload = command.Payload;
            if (JsonFields.ReadDouble(payload, "exposureTime", out double exposureTime) == JsonFieldState.Valid &&
                JsonFields.ReadInt(payload, "count", out int count) == JsonFieldState.Valid &&
                TryOptionalString(payload, "imageType", out string? imageType) &&
                JsonFields.ReadShort(payload, "binX", out short binX) == JsonFieldState.Valid &&
                JsonFields.ReadShort(payload, "binY", out short binY) == JsonFieldState.Valid &&
                JsonFields.ReadBool(payload, "save", out bool shouldSave) == JsonFieldState.Valid &&
                TryOptionalInt(payload, "gain", out int gain) &&
                TryOptionalInt(payload, "offset", out int offset) &&
                TryOptionalString(payload, "readoutMode", out string? readoutMode)) {
                // Calibrate needs Save, since an unsaved frame has no file to calibrate.
                bool shouldCalibrate = shouldSave &&
                                       payload.TryGetProperty("calibrate", out JsonElement calibrateProperty) &&
                                       calibrateProperty.ValueKind == JsonValueKind.True;
                // So does plate solving, since the solution is written into the saved file.
                bool shouldPlateSolve = shouldSave &&
                                        payload.TryGetProperty("plateSolve", out JsonElement plateSolveProperty) &&
                                        plateSolveProperty.ValueKind == JsonValueKind.True;
                CaptureCommandAsync(command.Id, exposureTime, count, imageType ?? CaptureSequence.ImageTypes.LIGHT,
                    binX, binY, shouldSave, shouldCalibrate, gain, offset, readoutMode, shouldPlateSolve)
                    .ObserveFaults(LogCommandFault);
            } else {
                // A silent drop leaves the dashboard waiting on a 5 s timeout with no reason.
                BroadcastMessageReceivedAsync(command.Id, false, "Malformed capture command")
                    .ObserveFaults(LogCommandFault);
            }
        }

        // Absent or null gives -1, so NINA uses the profile's value. False only for a malformed value.
        private static bool TryOptionalInt(JsonElement payload, string name, out int value) {
            value = UseProfileGainOrOffset;
            if (!payload.TryGetProperty(name, out JsonElement element) || element.ValueKind == JsonValueKind.Null)
                return true;
            return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value) && value >= 0;
        }

        // Absent, null or blank gives null to leave the camera's setting alone. False only for a non-string.
        private static bool TryOptionalString(JsonElement payload, string name, out string? value) {
            value = null;
            if (!payload.TryGetProperty(name, out JsonElement element) || element.ValueKind == JsonValueKind.Null)
                return true;
            if (element.ValueKind != JsonValueKind.String) return false;
            string? text = element.GetString();
            value = string.IsNullOrWhiteSpace(text) ? null : text;
            return true;
        }

        private static bool TryGetBool(JsonElement payload, string name, out bool value) {
            value = false;
            if (!payload.TryGetProperty(name, out JsonElement element)) return false;
            if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
            value = element.GetBoolean();
            return true;
        }

        ///////////// Imaging methods /////////////
        private Task OnBeforeFinalizeImageSaved(object sender, BeforeFinalizeImageSavedEventArgs eventArgs) {
            eventArgs.AddImagePattern(ImagePatternDefinitions.CreateProjectNamePattern(CurrentProjectName));
            eventArgs.AddImagePattern(ImagePatternDefinitions.CreateTargetNamePattern(CurrentTargetName));
            return Task.CompletedTask;
        }

        private void OnImageSaved(object? sender, ImageSavedEventArgs eventArgs) {
            _ = HandleImageSavedAsync(eventArgs);
        }

        /// <summary>
        /// Patches WCS into the saved FITS header, then releases the capture's awaiter. The patch is
        /// awaited first so CaptureAsync only returns once the header on disk is complete.
        /// </summary>
        private async Task HandleImageSavedAsync(ImageSavedEventArgs eventArgs) {
            FrameCaptureOperationData? capture = _currentCapture;
            if (capture == null) return; // a save we didn't start, e.g. from NINA's own UI

            try {
                if (capture.TakePendingWcs() is { } wcs &&
                    eventArgs.PathToImage?.LocalPath is { } savedPath &&
                    _fitsHeaderPatcher is { } patcher)
                    // ComponentToken, not capture.Token, so the header write finishes even after the
                    // capture completes and disposes.
                    await patcher.InjectWcsAsync(savedPath, wcs, ComponentToken);
            } finally {
                capture.CompleteSave(eventArgs);
            }
        }

        /// <summary>
        /// The frame's R2 object key. The target name is unvalidated server JSON, so ObjectKey cleans it,
        /// and ObjectKey.Join keeps forward slashes where System.IO.Path would use backslashes.
        /// </summary>
        internal string BuildR2Filename(string filePath) {
            string date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string fileName = Path.GetFileName(filePath);
            return ObjectKey.Join(ObjectKey.SafeSegment(CurrentTargetName), date, fileName);
        }

        ///////////// Camera controls /////////////
        public bool IsCoolerOn => IsConnected && cameraMediator.GetInfo().CoolerOn;

        /// <summary>
        /// The set point (°C) the running cooler is regulating to, or null when disconnected, the
        /// cooler is off, or the driver reports none.
        /// </summary>
        public double? CoolerSetPoint {
            get {
                if (!IsConnected) return null;
                CameraInfo info = cameraMediator.GetInfo();
                return info.CoolerOn && !double.IsNaN(info.TemperatureSetPoint) ? info.TemperatureSetPoint : null;
            }
        }

        /// <summary>
        /// With no temperature given it cools to N.I.N.A.'s, which the dashboard's temperature box sets. On
        /// a missed target the cooler keeps running flat out and the caller decides. False on a dashboard
        /// abort, but the caller's own cancellation propagates.
        /// </summary>
        public async Task<bool> CoolCameraAsync(double? targetTemperature = null,
            CancellationToken cancellationToken = default) {
            if (!cameraMediator.GetInfo().CanSetTemperature) {
                Log("Camera does not support temperature control, skipping cooling.");
                return false;
            }
            AbortCooling();
            double target = targetTemperature ?? cameraMediator.TargetTemp;
            Log($"Cooling camera to {target}°C...");
            IProgress<ApplicationStatus> progress = NoProgress;
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                BeginOperation(ref _coolingCancellationSource), cancellationToken);
            try {
                ICameraSettings cameraSettings = Observatory.Settings.ProfileService.ActiveProfile.CameraSettings;
                TimeSpan coolingDuration = TimeSpan.FromMinutes(cameraSettings.CoolingDuration);
                bool isAtTarget = await cameraMediator.CoolCamera(target, coolingDuration, progress, linked.Token);
                double temperature = cameraMediator.GetInfo().Temperature;
                if (isAtTarget) {
                    Log($"Cooling complete. Camera temperature: {temperature:F1}°C.");
                } else {
                    LogWarning($"Camera could not reach {target}°C. It is at {temperature:F1}°C with the cooler " +
                               "still working towards it.");
                }
                return isAtTarget;
            } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
                Log("Cooling aborted.");
                return false;
            }
        }

        /// <summary>False on a dashboard abort, but the caller's own cancellation propagates.</summary>
        public async Task<bool> WarmCameraAsync(CancellationToken cancellationToken = default) {
            CameraInfo? info = cameraMediator.GetInfo();
            if (!info.CanSetTemperature) {
                Log("Camera does not support temperature control, skipping warming.");
                return false;
            }
            if (!info.CoolerOn) return true;
            AbortCooling();
            Log("Warming camera...");
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                BeginOperation(ref _coolingCancellationSource), cancellationToken);
            try {
                IProgress<ApplicationStatus> progress = NoProgress;
                ICameraSettings cameraSettings = Observatory.Settings.ProfileService.ActiveProfile.CameraSettings;
                TimeSpan warmingDuration = TimeSpan.FromMinutes(cameraSettings.WarmingDuration);
                bool isWarmed = await cameraMediator.WarmCamera(warmingDuration, progress, linked.Token);
                if (isWarmed) {
                    bool isCoolerReadOff = await WaitForCoolerOffReadingAsync(linked.Token);
                    double temperature = cameraMediator.GetInfo().Temperature;
                    Log(isCoolerReadOff
                        ? $"Warming complete. Camera temperature: {temperature:F1}°C, cooler off."
                        : $"Warming complete. Camera temperature: {temperature:F1}°C, but N.I.N.A. still " +
                          "reports the cooler on.");
                }
                return isWarmed;
            } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
                Log("Warming aborted.");
                return false;
            }
        }

        private static readonly TimeSpan CoolerReadingTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan CoolerReadingPollInterval = TimeSpan.FromMilliseconds(500);

        // N.I.N.A. turns the cooler off on the driver but only refreshes CameraInfo on its next poll
        // (about 5 s with some drivers), so a read straight after still says on, and the dawn disconnect
        // left the camera connected. Bounded, so a stale reading can't hang dawn.
        private async Task<bool> WaitForCoolerOffReadingAsync(CancellationToken cancellationToken) {
            DateTime deadlineUtc = DateTime.UtcNow + CoolerReadingTimeout;
            while (cameraMediator.GetInfo().CoolerOn && DateTime.UtcNow < deadlineUtc) {
                await Task.Delay(CoolerReadingPollInterval, cancellationToken);
            }
            return !cameraMediator.GetInfo().CoolerOn;
        }

        ///////////// Capture functions /////////////
        private async Task CaptureCommandAsync(string? id, double exposureTime, int count, string imageType, short binX,
            short binY, bool shouldSave, bool shouldCalibrate, int gain, int offset, string? readoutMode,
            bool shouldPlateSolve) {
            if (!IsConnected) {
                await BroadcastMessageReceivedAsync(id, false, "Camera not connected");
                return;
            }
            if (count < 1 || exposureTime <= 0) {
                await BroadcastMessageReceivedAsync(id, false, "Invalid exposure time or frame count");
                return;
            }
            // A capture started over the autopilot's would cancel the frame it is taking.
            if (IsAutopilotRunning) {
                await BroadcastMessageReceivedAsync(id, false,
                    "The autopilot is running; switch it off to capture by hand");
                return;
            }
            // Refused, not ignored, since a frame on the wrong mode never matches its masters.
            short? readoutIndex = null;
            if (readoutMode != null) {
                int index = cameraMediator.GetInfo().ReadoutModes?.ToList().IndexOf(readoutMode) ?? -1;
                if (index < 0) {
                    await BroadcastMessageReceivedAsync(id, false, $"Camera has no readout mode \"{readoutMode}\"");
                    return;
                }
                readoutIndex = (short)index;
            }
            Observatory.TryGetComponent<Mount>(out Mount? mount);
            if (mount?.PlanManualExposure(TimeSpan.FromSeconds(exposureTime)) is
                { Action: ManualExposureAction.Refuse, Reason: var refusal }) {
                LogWarning($"Capture refused: {refusal}");
                await BroadcastMessageReceivedAsync(id, false, refusal);
                return;
            }
            if (Interlocked.Exchange(ref _manualRunActive, 1) == 1) {
                await BroadcastMessageReceivedAsync(id, false, "A capture is already running");
                return;
            }

            CancellationTokenSource runCancellationSource =
                CancellationTokenSource.CreateLinkedTokenSource(ComponentToken);
            _manualRunCancellationSource = runCancellationSource;
            CancellationToken runToken = runCancellationSource.Token;
            Volatile.Write(ref _stopReason, null);
            int frame = 0, completed = 0;
            try {
                // Left set on purpose. NINA saves it to the profile, and the autopilot then shoots
                // with whatever the hub chose.
                if (readoutIndex is short mode) cameraMediator.SetReadoutModeForNormalImages(mode);
                await BroadcastMessageReceivedAsync(id, true);
                Log($"Starting manual capture: {count} x {exposureTime}s {imageType}, bin {binX}x{binY}" +
                    $"{(gain >= 0 ? $", gain {gain}" : "")}{(offset >= 0 ? $", offset {offset}" : "")}" +
                    $"{(readoutMode != null ? $", readout {readoutMode}" : "")}" +
                    $"{(shouldPlateSolve ? ", plate-solved" : "")}" +
                    DescribeSaving(shouldSave, shouldCalibrate));

                CaptureRequest request = CaptureRequest.Manual(exposureTime, imageType, new BinningMode(binX, binY),
                    shouldSave, gain, offset, isPlateSolveRequested: shouldPlateSolve);
                for (frame = 1; frame <= count; frame++) {
                    runToken.ThrowIfCancellationRequested();
                    if (!await PrepareMountForFrameAsync(mount, exposureTime, frame, count, shouldSave, runToken))
                        break;
                    if (!await CaptureManualFrameAsync(request, frame, count, shouldCalibrate, runToken)) break;
                    completed++;
                }
                Log($"Manual capture finished: {completed}/{count} frame(s).");
            } catch (OperationCanceledException) {
                string? stopReason = Interlocked.Exchange(ref _stopReason, null);
                if (stopReason == null && ComponentToken.IsCancellationRequested) stopReason = "N.I.N.A. is closing";
                LogWarning(stopReason == null ? "Capture aborted." : $"Capture stopped: {stopReason}.");
                await SendPreviewAsync(ImagePreviewPayload.Failure(Math.Max(frame, 1), count,
                    stopReason ?? "Aborted by user", shouldSave, null));
            } catch (Exception ex) {
                // Fire-and-forget from HandleCommandAsync, so unexpected failures only surface here.
                LogError($"Manual capture failed: {ex.Message}");
            } finally {
                Interlocked.CompareExchange(ref _manualRunCancellationSource, null, runCancellationSource);
                runCancellationSource.Dispose();
                Interlocked.Exchange(ref _manualRunActive, 0);
            }
        }

        private static string DescribeSaving(bool shouldSave, bool shouldCalibrate) {
            if (!shouldSave) return " (preview only, not saved).";
            if (shouldCalibrate) return ", calibrated.";
            return ".";
        }

        /// <summary>
        /// Flips the mount first when this frame would carry it past the meridian limit, as N.I.N.A.'s
        /// MeridianFlipTrigger does. False when the frame must not be taken, which ends the run.
        /// </summary>
        private async Task<bool> PrepareMountForFrameAsync(Mount? mount, double exposureTime, int index, int count,
            bool shouldSave, CancellationToken cancellationToken) {
            if (mount == null) return true;
            // A flip in progress stops tracking and may take N.I.N.A.'s own exposures.
            if (mount.IsFlipping) await mount.WaitForFlipAsync(cancellationToken);

            ManualExposurePlan plan = mount.PlanManualExposure(TimeSpan.FromSeconds(exposureTime));
            string? failReason = plan.Reason;
            if (plan.Action == ManualExposureAction.Proceed) return true;
            if (plan.Action == ManualExposureAction.Flip) {
                Log($"Frame {index}/{count} would carry the mount past the meridian limit " +
                    $"({mount.DescribeMeridianLimit()}), so flipping first.");
                MeridianFlipOutcome outcome = await mount.FlipAsync(mount.CurrentFlipTarget(), null, cancellationToken);
                if (outcome.HasReachedTargetSide) {
                    Log($"Meridian flip done ({outcome.Before} -> {outcome.After}); taking frame {index}/{count}.");
                    return true;
                }
                failReason = $"Meridian flip failed: {outcome.FailReason}";
            }

            LogError($"Frame {index}/{count} not taken: {failReason}");
            await SendPreviewAsync(ImagePreviewPayload.Failure(
                index, count, failReason ?? "Meridian limit", shouldSave, null));
            return false;
        }

        /// <summary>False when the frame failed, which ends the run. Cancellation propagates.</summary>
        private async Task<bool> CaptureManualFrameAsync(CaptureRequest request, int index, int count,
            bool shouldCalibrate, CancellationToken cancellationToken) {
            string clientRef = Guid.NewGuid().ToString("N");

            // 1. Everything that needs the capture slot. A saved frame waits for NINA's ImageSaved before
            //    EndCapture, because HandleImageSavedAsync ignores the event once the slot is empty.
            CaptureResult result = await ExposeManualFrameAsync(request, cancellationToken);

            // 2. A failed frame shows as an error toast and as a failure frame that moves the
            //    dashboard's counter on.
            if (!result.IsSuccess) {
                LogError($"Frame {index}/{count} failed: {result.FailReason}");
                await SendPreviewAsync(ImagePreviewPayload.Failure(
                    index, count, result.FailReason ?? "Capture failed", request.ShouldSaveToDisk, clientRef));
                return false;
            }

            // 3. Register the frame if an upload or a calibration follows, since calibration needs the
            //    server's plan. With uploads off the server settles the row at once instead of waiting
            //    for bytes. An unsaved frame isn't registered or uploaded. Its preview only goes out live.
            int? captureId = null;
            CalibrationPlan? plan = null;
            bool shouldUploadToCloud = Observatory.Settings.IsCloudUploadEnabled();
            if (request.ShouldSaveToDisk && !shouldUploadToCloud)
                Log($"Cloud upload is off, so frame {index}/{count} stays on this machine; preview shown live only.");
            if (request.ShouldSaveToDisk && (shouldUploadToCloud || shouldCalibrate)) {
                if (Observatory.Authenticator.IsAuthenticated)
                    (captureId, plan) = await Observatory.AstraeusWebClient.PostManualCaptureAsync(
                        result, clientRef, shouldUploadToCloud, shouldCalibrate);
                if (captureId == null)
                    LogWarning($"Frame {index}/{count} was saved locally but could not be registered on the server; " +
                               (shouldUploadToCloud ? "it will not be uploaded." : "it will not be calibrated."));
                else if (shouldCalibrate && plan == null)
                    Log($"Frame {index}/{count}: the server found no applicable masters, so it stays uncalibrated.");
            }

            // 4. Preview and statistics go to the dashboard before the slow calibration and upload. The
            //    capture id is sent only when the frame will be in the library.
            await SendPreviewAsync(ImagePreviewPayload.FromResult(
                result, PreviewSource.Manual, request.ShouldSaveToDisk, index, count, clientRef,
                shouldUploadToCloud ? captureId : null));

            // 5. Calibration and upload, serialized in the background so the next exposure starts now.
            bool hasWorkToQueue = shouldUploadToCloud || plan != null;
            if (captureId is int registeredId && result.FilePath is { } filePath && hasWorkToQueue)
                QueueManualFrame(registeredId, filePath, result.PreviewJpeg, plan, shouldUploadToCloud);

            Log($"Frame {index}/{count} captured" +
                (result.Hfr is double hfr ? $": HFR={hfr:F2}, stars={result.DetectedStars}" : "") +
                (result.FilePath != null ? $" ({Path.GetFileName(result.FilePath)})" : "") + ".");
            return true;
        }

        private async Task<CaptureResult> ExposeManualFrameAsync(CaptureRequest request,
            CancellationToken cancellationToken) {
            using FrameCaptureOperationData capture = BeginCapture(request, cancellationToken);
            try {
                IRenderedImage rendered = await CaptureFrameAsync(capture);
                ImageSavedEventArgs? saved = null;
                if (request.ShouldSaveToDisk)
                    saved = await capture.Saved.WaitAsync(SaveTimeout, capture.Token);

                CaptureResult result = saved != null
                    ? BuildCaptureResult(saved, capture.SolveResult, capture.MetaData, capture.ShutterOpenedAtUtc)
                    : await BuildCaptureResultAsync(rendered, capture.MetaData, capture.ShutterOpenedAtUtc);

                if (result.IsSuccess)
                    result.PreviewJpeg = await EncodePreviewAsync(saved?.Image ?? rendered.Image, capture.Token);
                return result;
            } catch (OperationCanceledException) {
                throw;
            } catch (TimeoutException) {
                return new CaptureResult {
                    IsSuccess = false,
                    FailReason = $"N.I.N.A. did not report the saved file within {SaveTimeout.TotalMinutes:0} minutes"
                };
            } catch (Exception ex) {
                return new CaptureResult { IsSuccess = false, FailReason = ex.Message };
            } finally {
                EndCapture(capture);
            }
        }

        private async Task<byte[]?> EncodePreviewAsync(BitmapSource source, CancellationToken cancellationToken) {
            try {
                return await Task.Run(() => ImagePreviewEncoder.EncodeJpeg(source), cancellationToken);
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                LogWarning($"Preview encode failed: {ex.Message}"); // the statistics still go out
                return null;
            }
        }

        /// <summary>A send failure is logged and never ends the run.</summary>
        private async Task SendPreviewAsync(ImagePreviewPayload payload) {
            try {
                await WebSocketBus.SendAsync(new ImagePreviewMessage(payload));
            } catch (Exception ex) {
                string frame = payload.FrameIndex is int index
                    ? $"frame {index}/{payload.FrameCount}"
                    : $"the {payload.Source} frame";
                LogWarning($"Preview for {frame} could not be sent: {ex.Message}");
            }
        }

        ///////////// N.I.N.A.'s own exposures /////////////
        // N.I.N.A. takes plate-solve and autofocus exposures itself, so the plugin only sees them as
        // prepared images. Those taken for one of our routines (Observatory.FrameLabel) go to the
        // dashboard's latest-frame card while someone's watching. Frames N.I.N.A. takes on its own,
        // from its sequencer or Imaging tab, are left alone.

        /// <summary>
        /// Runs on N.I.N.A.'s prepare path for every frame, so it only decides and hands off. The
        /// encode and send happen in RelaySnapshotsAsync.
        /// </summary>
        private void OnImagePrepared(object? sender, ImagePreparedEventArgs eventArgs) {
            IRenderedImage? rendered = eventArgs?.RenderedImage;
            if (rendered == null) return;
            string? source = Observatory.FrameLabel.Current;
            if (source == null || !Observatory.Cadence.HasSubscribers) return;
            // Our own frames already go out with their saved statistics from the capture paths.
            IImageData? ownImageData = Volatile.Read(ref _currentCapture)?.ImageData;
            if (ownImageData != null && ReferenceEquals(ownImageData, rendered.RawImageData))
                return;

            Volatile.Write(ref _pendingSnapshot, new SnapshotFrame(rendered, source));
            if (Interlocked.CompareExchange(ref _snapshotRelayRunning, 1, 0) == 0)
                _ = Task.Run(RelaySnapshotsAsync);
        }

        /// <summary>
        /// Sends the newest waiting frame, then at most one per SnapshotMinInterval until none is left.
        /// An autofocus burst costs one encode per interval, and the card always ends on the latest.
        /// </summary>
        private async Task RelaySnapshotsAsync() {
            try {
                while (true) {
                    SnapshotFrame? next = Interlocked.Exchange(ref _pendingSnapshot, null);
                    if (next != null) {
                        await RelaySnapshotAsync(next);
                        await Task.Delay(SnapshotMinInterval, ComponentToken);
                        continue;
                    }
                    Volatile.Write(ref _snapshotRelayRunning, 0);
                    // A frame that arrived between the exchange and the release saw the loop running
                    // and didn't start another, so pick it up here.
                    if (Volatile.Read(ref _pendingSnapshot) == null
                        || Interlocked.CompareExchange(ref _snapshotRelayRunning, 1, 0) != 0)
                        return;
                }
            } catch (OperationCanceledException) {
                Volatile.Write(ref _snapshotRelayRunning, 0);
            }
        }

        /// <summary>Never throws.</summary>
        private async Task RelaySnapshotAsync(SnapshotFrame frame) {
            try {
                // The last viewer may have gone while this frame waited its turn.
                if (!Observatory.Cadence.HasSubscribers) return;
                IImageData raw = frame.Image.RawImageData;
                // A frame prepared without star detection has an empty analysis (no HFR, zero stars).
                // That isn't the same as a frame with no stars in it.
                IStarDetectionAnalysis? stars = null;
                if (raw.StarDetectionAnalysis is { } analysis && double.IsFinite(analysis.HFR) && analysis.HFR > 0)
                    stars = analysis;
                CaptureResult result = BuildCaptureResult(await raw.Statistics, stars, null, null, raw.MetaData, null);
                result.PreviewJpeg = ImagePreviewEncoder.EncodeJpeg(frame.Image.Image);
                await SendPreviewAsync(ImagePreviewPayload.FromResult(result, frame.Source, isSaved: false));
                LogDebug($"Sent {frame.Source} frame ({result.ExposureSeconds ?? "?"} s) to the dashboard.");
            } catch (Exception ex) {
                LogDebug($"Could not send {frame.Source} frame to the dashboard: {ex.Message}");
            }
        }

        private void QueueManualFrame(int captureId, string filePath, byte[]? previewJpeg, CalibrationPlan? plan,
            bool shouldUpload) {
            _manualFrames = _manualFrames
                .ContinueWith(_ => ProcessManualFrameAsync(captureId, filePath, previewJpeg, plan, shouldUpload),
                    TaskScheduler.Default)
                .Unwrap();
        }

        /// <summary>
        /// Uses the component's token, not the run's, so an abort can't strand a frame the server
        /// already recorded.
        /// </summary>
        private async Task ProcessManualFrameAsync(int captureId, string filePath, byte[]? previewJpeg,
            CalibrationPlan? plan, bool shouldUpload) {
            try {
                PendingUpload pending = new PendingUpload(captureId, filePath, previewJpeg);
                if (plan != null) {
                    string fileName = Path.GetFileName(filePath);
                    Log($"Calibrating {fileName}: {plan.Describe()}.");
                    FrameCalibrator calibrator = new FrameCalibrator(Observatory);
                    CalibrationOutcome outcome = await calibrator.CalibrateAsync(filePath, plan, ComponentToken);
                    if (outcome.IsSuccess) {
                        string? outputName = Path.GetFileName(outcome.OutputPath);
                        Log($"Calibrated {fileName} in {outcome.DurationSeconds:F1}s -> {outputName}.");
                    } else if (outcome.SkipReason != null) {
                        string rawFrameFate = shouldUpload
                            ? "The raw frame is uploaded uncalibrated."
                            : "The raw frame stands.";
                        Log($"Calibration of {fileName} skipped: {outcome.SkipReason} {rawFrameFate}");
                    } else {
                        LogWarning($"Calibration of {fileName} failed: {outcome.Error} The raw frame stands.");
                    }
                    pending = pending with {
                        CalibratedFilePath = outcome.OutputPath,
                        Provenance = CalibrationProvenance.From(plan, outcome),
                    };
                }
                if (!shouldUpload) return;

                // Check storage before the transfer, as the autopilot does. Nothing here can pause the
                // autopilot, so either kind of full just means the frame stays on this machine.
                StorageVerdict verdict = await StorageGate.CheckAsync(Observatory, DeviceType, DefaultLogCategory);
                if (verdict != StorageVerdict.Upload) {
                    LogWarning($"{Path.GetFileName(filePath)} was not uploaded: there is no room in your cloud " +
                               "storage. The frame is still on this machine.");
                    return;
                }

                // Goes through the shared queue so a manual run and the autopilot never split the
                // uplink with two transfers. The key is built now, while the target name is still the
                // one this frame was taken under.
                Observatory.CaptureUploads.TryEnqueue(pending, BuildR2Filename(filePath), DeviceType,
                    DefaultLogCategory);
            } catch (OperationCanceledException) {
                // The plugin is shutting down.
            } catch (Exception ex) {
                LogWarning($"Post-processing of {Path.GetFileName(filePath)} failed: {ex.Message}");
            }
        }

        /// <summary>
        /// One LIGHT frame for the autopilot. With hintCoordinates it's plate-solved and the WCS goes in
        /// the FITS header. A failed solve only logs a warning, and the frame is always saved.
        /// </summary>
        public async Task<CaptureResult> CaptureAsync(
            double exposureTime,
            short binning,
            Coordinates? hintCoordinates,
            int gain,
            int offset,
            int frameNumber,
            CancellationToken cancellationToken) {
            if (!IsConnected)
                return new CaptureResult { IsSuccess = false, FailReason = "Camera not connected" };

            using FrameCaptureOperationData capture = BeginCapture(
                CaptureRequest.Autopilot(exposureTime, binning, hintCoordinates, gain, offset, frameNumber),
                cancellationToken);
            try {
                await CaptureFrameAsync(capture);
                // HandleImageSavedAsync patches the WCS in before completing the capture, so the header
                // is written by the time this returns. The FITS and preview go to R2 later from the
                // autopilot's QueueUpload state, which holds the capture_id.
                ImageSavedEventArgs? saved = await capture.Saved.WaitAsync(SaveTimeout, cancellationToken);
                CaptureResult result = BuildCaptureResult(saved, capture.SolveResult, capture.MetaData,
                    capture.ShutterOpenedAtUtc, hintCoordinates, capture.WasSolveAttempted);

                // Render the preview while the BitmapSource still exists, since the FITS on disk can't be
                // re-rendered later without NINA's pipeline. A failure here isn't fatal.
                if (result.IsSuccess && saved?.Image is { } previewSource) {
                    try {
                        result.PreviewJpeg = await Task.Run(() => ImagePreviewEncoder.EncodeJpeg(previewSource),
                            cancellationToken);
                    } catch (Exception ex) {
                        LogWarning($"Preview encode failed: {ex.Message}");
                    }
                }

                // Sent live because the R2 copy only lands once the upload queue reaches this frame. Not
                // awaited, so a slow uplink can't hold up the autopilot. Not gated on viewers either,
                // since the JPEG exists for R2 anyway and it's one per exposure.
                if (result.IsSuccess)
                    _ = SendPreviewAsync(ImagePreviewPayload.FromResult(result, PreviewSource.Autopilot, isSaved: true));

                return result;
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                LogError($"Capture failed: {ex.Message}");
                return new CaptureResult { IsSuccess = false, FailReason = ex.Message };
            } finally {
                EndCapture(capture);
            }
        }

        private CaptureResult BuildCaptureResult(
            ImageSavedEventArgs? savedImage, PlateSolveResult? solveResult, ImageMetaData? metadata,
            DateTime? shutterOpenedAtUtc, Coordinates? solveHint = null, bool wasSolveAttempted = false) {
            if (savedImage == null)
                return new CaptureResult { IsSuccess = false, FailReason = "Image was not saved" };
            return BuildCaptureResult(savedImage.Statistics, savedImage.StarDetectionAnalysis,
                savedImage.PathToImage?.LocalPath, solveResult, metadata, shutterOpenedAtUtc, solveHint,
                wasSolveAttempted);
        }

        /// <summary>
        /// Statistics for a preview-only manual frame that was never saved. The prepared image carries
        /// the same statistics and star detection ImageSaved would, just with no file.
        /// </summary>
        private async Task<CaptureResult> BuildCaptureResultAsync(IRenderedImage rendered, ImageMetaData? metadata,
            DateTime? shutterOpenedAtUtc) {
            IImageData raw = rendered.RawImageData;
            IImageStatistics? statistics = await raw.Statistics;
            return BuildCaptureResult(statistics, raw.StarDetectionAnalysis, null, null, metadata, shutterOpenedAtUtc);
        }

        private CaptureResult BuildCaptureResult(
            IImageStatistics? imageStatistics, IStarDetectionAnalysis? starDetectionAnalysis, string? filePath,
            PlateSolveResult? solveResult, ImageMetaData? metadata, DateTime? shutterOpenedAtUtc,
            Coordinates? solveHint = null, bool wasSolveAttempted = false) {
            // A driver that can't report gain/offset gives NINA -1, which the server rejects, so the
            // frame would never match a master. Normalise the same way the master generator does.
            bool isGainOrOffsetMissing = AcquisitionMetadata.IsMissing(metadata?.Camera.Gain ?? 0) ||
                                         AcquisitionMetadata.IsMissing(metadata?.Camera.Offset ?? 0);
            if (!_hasWarnedMissingGain && isGainOrOffsetMissing) {
                _hasWarnedMissingGain = true;
                LogWarning($"Camera reports no GAIN/OFFSET (gain={metadata?.Camera.Gain}, " +
                           $"offset={metadata?.Camera.Offset}); reporting 0 so frames still match their masters.");
            }

            double? exposureTimeSeconds = NullIfNaN(metadata?.Image.ExposureTime);
            return new CaptureResult {
                IsSuccess = true,
                IsPlateSolved = solveResult is { Success: true },
                WasPlateSolveAttempted = wasSolveAttempted,
                PointingOffsetArcmin = OffsetFromHintArcmin(solveResult, solveHint),
                FilePath = filePath,
                Hfr = starDetectionAnalysis?.HFR,
                HfrStdDev = starDetectionAnalysis?.HFRStDev,
                DetectedStars = starDetectionAnalysis?.DetectedStars,
                BitDepth = imageStatistics?.BitDepth,
                StdDev = imageStatistics?.StDev,
                Mean = imageStatistics?.Mean,
                Median = imageStatistics?.Median,
                MedianAbsoluteDeviation = imageStatistics?.MedianAbsoluteDeviation,
                MaxAdu = imageStatistics?.Max,
                MaxOccurrences = imageStatistics?.MaxOccurrences,
                MinAdu = imageStatistics?.Min,
                MinOccurrences = imageStatistics?.MinOccurrences,

                ///////////// Acquisition metadata /////////////
                // From the frame's own headers, so it matches the masters.
                CameraName         = AcquisitionMetadata.NullIfBlank(metadata?.Camera.Name),
                Gain               = AcquisitionMetadata.NormalizeGainOrOffset(metadata?.Camera.Gain),
                Offset             = AcquisitionMetadata.NormalizeGainOrOffset(metadata?.Camera.Offset),
                ReadoutMode        = AcquisitionMetadata.NullIfBlank(metadata?.Camera.ReadoutModeName),
                TemperatureC       = AcquisitionMetadata.MatchingTemperature(
                                         metadata?.Camera.Temperature ?? double.NaN, metadata?.Camera.SetPoint ?? double.NaN),
                SensorTemperatureC = NullIfNaN(metadata?.Camera.Temperature),
                BinningX           = metadata?.Camera.BinX,
                BinningY           = metadata?.Camera.BinY,
                ExposureSeconds    = exposureTimeSeconds?.ToString("0.000", CultureInfo.InvariantCulture),
                FilterName         = AcquisitionMetadata.NullIfBlank(metadata?.FilterWheel.Filter),
                ObservedAt         = ResolveObservedAt(metadata, shutterOpenedAtUtc),
                // pierUnknown is the metadata default and also what a fork or alt-az reports. Either
                // way there's nothing to pin to.
                SideOfPier         = metadata?.Telescope.SideOfPier is PierSide side
                                     && side is PierSide.pierEast or PierSide.pierWest
                                        ? side.ToString() : null,
            };
        }

        private static double? NullIfNaN(double? value) =>
            value is double number && !double.IsNaN(number) ? number : null;

        private static double? OffsetFromHintArcmin(PlateSolveResult? solveResult, Coordinates? solveHint) {
            if (solveHint == null || solveResult is not { Success: true } || solveResult.Coordinates == null)
                return null;
            Coordinates solved = solveResult.Coordinates.Transform(Epoch.J2000);
            Coordinates wanted = solveHint.Transform(Epoch.J2000);
            return Mount.AngularSeparationDegrees(solved, wanted) * 60.0;
        }

        /// <summary>
        /// The frame's DATE-OBS, else the time we asked for the exposure. Without it the server would time
        /// the frame from when the report arrived, stretching each cadence interval.
        /// </summary>
        private string? ResolveObservedAt(ImageMetaData? metadata, DateTime? shutterOpenedAtUtc) {
            if (metadata != null && metadata.Image.ExposureStart != default)
                return metadata.Image.ExposureStart.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
            if (shutterOpenedAtUtc is not DateTime requested) return null;
            if (!_hasWarnedMissingObservedAt) {
                _hasWarnedMissingObservedAt = true;
                LogWarning("Frames have no DATE-OBS, so the time each exposure was requested is reported as " +
                           "its start instead (slightly early).");
            }
            return requested.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Shared by the manual command and the autopilot. The pixels saved here are always raw, since
        /// calibration happens later from the server's plan.
        /// </summary>
        private async Task<IRenderedImage> CaptureFrameAsync(FrameCaptureOperationData capture) {
            CaptureRequest request = capture.Request;
            CancellationToken cancellationToken = capture.Token;
            IProgress<ApplicationStatus> progress = NoProgress;

            CaptureSequence sequence = new CaptureSequence(request.ExposureTime, request.ImageType,
                _filterWheel?.GetCurrentFilter(), request.Binning, 1);
            // -1 is NINA's "leave the camera alone" sentinel, and the CaptureSequence default.
            sequence.Gain = request.Gain;
            sequence.Offset = request.Offset;
            // What N.I.N.A. stamps as the frame's ExposureNumber, and so what $$FRAMENR$$ prints. It
            // raises TotalExposureCount itself when this passes it.
            sequence.ProgressExposureCount = request.FrameNumber;
            // The fallback shutter time, stamped as close to the request as we can. Only used when the
            // frame's headers have no DATE-OBS.
            capture.RecordShutterOpen();
            IExposureData exposureData = await imagingMediator.CaptureImage(sequence, cancellationToken, progress);
            IImageData imageData = await exposureData.ToImageData(progress, cancellationToken);
            // ImageSavedEventArgs doesn't carry the headers, and BuildCaptureResult needs them for the
            // acquisition parameters. The pixels let OnImagePrepared tell this frame from N.I.N.A.'s.
            capture.RecordImageData(imageData);

            PrepareImageParameters prepareParameters = new PrepareImageParameters(autoStretch: true, detectStars: true);
            Task<IRenderedImage> prepareTask =
                imagingMediator.PrepareImage(imageData, prepareParameters, cancellationToken);
            if (request.IsPlateSolveRequested) {
                // A manual frame has no target, so start from where the mount said it pointed at exposure.
                if ((request.SolveHint ?? imageData.MetaData.Telescope.Coordinates) is { } hint)
                    await TrySolveAndEmbedWcsAsync(capture, imageData, hint, cancellationToken);
                else
                    Log("Plate solve skipped. No mount position to start from, so the frame is saved without WCS.");
            }

            if (request.ShouldSaveToDisk)
                await imageSaveMediator.Enqueue(imageData, prepareTask, progress, cancellationToken);
            // The preview, and an unsaved frame's statistics, come from the prepared image. Awaiting it
            // here also surfaces the prepare's exceptions.
            return await prepareTask;
        }

        /// <summary>
        /// Makes the request the in-flight capture, abandoning any still running. The camera takes one
        /// frame at a time, and letting two overlap gives a frame's result to the wrong capture.
        /// </summary>
        private FrameCaptureOperationData BeginCapture(CaptureRequest request, CancellationToken cancellationToken) {
            FrameCaptureOperationData capture =
                new FrameCaptureOperationData(request, ComponentToken, cancellationToken);
            // One exchange, so there is never a moment with no capture in the slot for another to take.
            FrameCaptureOperationData? previous = Interlocked.Exchange(ref _currentCapture, capture);
            if (previous != null) {
                LogWarning("A capture was still in flight when a new one started, so abandoning the earlier frame.");
                previous.Abandon();
            }
            return capture;
        }

        private void EndCapture(FrameCaptureOperationData capture) =>
            Interlocked.CompareExchange(ref _currentCapture, null, capture);

        /// <summary>
        /// Cancels the in-flight capture, and every frame still to come in a manual run. The physical
        /// abort is the mediator's job.
        /// </summary>
        public void AbortCapture() {
            _currentCapture?.Cancel();
            CancelManualRun();
        }

        /// <summary>Cancels the manual run's token, tolerating the race with the run's own cleanup.</summary>
        private void CancelManualRun() {
            try { _manualRunCancellationSource?.Cancel(); } catch (ObjectDisposedException) { }
        }

        /// <summary>Safe when nothing is exposing.</summary>
        public void StopExposure() {
            AbortCapture();
            cameraMediator.AbortExposure();
        }

        /// <summary>
        /// StopExposure for a stop the observer didn't ask for. A manual run ends with this reason
        /// instead of "Aborted by user".
        /// </summary>
        public void StopExposure(string reason) {
            Volatile.Write(ref _stopReason, reason);
            LogWarning($"Stopping the exposure: {reason}.");
            StopExposure();
        }

        /// <summary>Cancels an in-flight cool-down or warm-up.</summary>
        public void AbortCooling() => AbortOperation(ref _coolingCancellationSource);

        private async Task AbortCommandAsync(string? id) {
            await BroadcastMessageReceivedAsync(id, true);
            StopExposure();
            LogWarning("Camera exposure aborted by the user.");
        }

        // Acked before cooling or warming starts, since either runs for minutes, far past the 5 s ack window.
        private async Task SetCoolerCommandAsync(string? id, JsonElement payload) {
            if (!TryGetBool(payload, "on", out bool shouldCoolerBeOn)) {
                await BroadcastMessageReceivedAsync(id, false, "Malformed setCooler command");
                return;
            }
            if (await RejectUnlessTemperatureControlAsync(id)) return;
            await BroadcastMessageReceivedAsync(id, true);
            try {
                if (shouldCoolerBeOn) await CoolCameraAsync();
                else await WarmCameraAsync();
            } catch (Exception ex) {
                // Fire-and-forget from HandleCommandAsync, so driver failures only surface here.
                LogError($"{(shouldCoolerBeOn ? "Cooling" : "Warming")} failed: {ex.Message}");
            }
        }

        private async Task SetTemperatureCommandAsync(string? id, JsonElement payload) {
            if (JsonFields.ReadDouble(payload, "temperature", out double temperatureCelsius) != JsonFieldState.Valid) {
                await BroadcastMessageReceivedAsync(id, false, "Malformed setTemperature command");
                return;
            }
            if (await RejectUnlessTemperatureControlAsync(id)) return;
            if (!TryGetDeviceViewModel<CameraVM>(out CameraVM? cameraViewModel)) {
                await BroadcastMessageReceivedAsync(id, false, "Device view model not available");
                return;
            }
            InvokeOnUiThread(() => cameraViewModel.SetTemperature(temperatureCelsius));
            await BroadcastMessageReceivedAsync(id, true);
        }

        private async Task SetDewHeaterCommandAsync(string? id, JsonElement payload) {
            if (!TryGetBool(payload, "on", out bool shouldHeaterBeOn)) {
                await BroadcastMessageReceivedAsync(id, false, "Malformed setDewHeater command");
                return;
            }
            if (!IsConnected) {
                await BroadcastMessageReceivedAsync(id, false, "Camera not connected");
                return;
            }
            if (!cameraMediator.GetInfo().HasDewHeater) {
                await BroadcastMessageReceivedAsync(id, false, "This camera has no dew heater");
                return;
            }
            cameraMediator.SetDewHeater(shouldHeaterBeOn);
            await BroadcastMessageReceivedAsync(id, true);
        }

        // True when it has already sent the rejection.
        private async Task<bool> RejectUnlessTemperatureControlAsync(string? id) {
            string? reason = null;
            if (!IsConnected) reason = "Camera not connected";
            else if (!cameraMediator.GetInfo().CanSetTemperature) reason = "This camera does not support cooling";
            if (reason != null) await BroadcastMessageReceivedAsync(id, false, reason);
            return reason != null;
        }

        ///////////// Plate solving /////////////
        /// <summary>
        /// Plate-solves the image with the profile's solver, writes the WCS into its metadata and
        /// records it on the capture so the saved FITS has the same solution. Never throws except on
        /// cancellation.
        /// </summary>
        private async Task TrySolveAndEmbedWcsAsync(FrameCaptureOperationData capture, IImageData imageData,
            Coordinates hint, CancellationToken cancellationToken) {
            try {
                IPlateSolverFactory factory = Observatory.PlateSolverFactory;
                IProfile? profile = Observatory.Settings.ProfileService.ActiveProfile;
                IPlateSolver? solver = factory.GetPlateSolver(profile.PlateSolveSettings);
                if (solver == null) {
                    LogWarning("No plate solver configured, so saving without WCS.");
                    return;
                }

                PlateSolveParameter parameter = new PlateSolveParameter {
                    Binning = imageData.MetaData.Camera.BinX,
                    Coordinates = hint,
                    FocalLength = profile.TelescopeSettings.FocalLength,
                    PixelSize = profile.CameraSettings.PixelSize,
                    SearchRadius = profile.PlateSolveSettings.SearchRadius,
                    DownSampleFactor = profile.PlateSolveSettings.DownSampleFactor,
                    MaxObjects = profile.PlateSolveSettings.MaxObjects,
                    Regions = profile.PlateSolveSettings.Regions
                };

                IProgress<ApplicationStatus> progress = NoProgress;
                capture.RecordSolveAttempt();
                PlateSolveResult? result = await solver.SolveAsync(imageData, parameter, progress, cancellationToken);

                if (!result.Success) {
                    LogWarning("Per-frame plate solve failed, so saving without WCS.");
                    return;
                }

                // The image's own size, which is the sensor's divided by binning only without a subframe.
                double imageWidth = imageData.Properties.Width;
                double imageHeight = imageData.Properties.Height;

                FrameWcs wcs = FrameWcs.FromPlateSolve(result, imageWidth, imageHeight);
                imageData.MetaData.WorldCoordinateSystem = wcs.ToNinaWcs();
                capture.RecordSolve(result, wcs);

                Log($"WCS embedded: RA={result.Coordinates.RAString}, Dec={result.Coordinates.DecString}, " +
                    $"Pixscale={result.Pixscale:F2}\"/px, PA={result.PositionAngle:F2}°, Flipped={result.Flipped}");
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                LogWarning($"Per-frame plate solve error: {ex.Message} (saving without WCS)");
            }
        }
    }
}