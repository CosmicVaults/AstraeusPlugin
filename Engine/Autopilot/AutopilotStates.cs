using CosmicVaults.NINA.Astraeus.Engine.Autopilot.States;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot {
    internal sealed class AutopilotStates(Context context) {

        private WaitForDusk WaitForDusk { get; } = new WaitForDusk("Entry State: Wait for Dusk", context);
        private RefreshSession RefreshSession { get; } = new RefreshSession("Renew Astraeus Session", context);
        private PrepareSmartAutofocus PrepareSmartAutofocus { get; } =
            new PrepareSmartAutofocus("Prepare Smart Autofocus", context);
        private RunSequence RunStartupSequence { get; } =
            new RunSequence("StartUp Sequence", context, SequencePhase.Startup);
        private StartSession StartSession { get; } = new StartSession("Start Autopilot Session", context);

        // Connect Equipment Nodes
        // The switch hub connects first, before the user's start-up sequence, because it could power
        // everything else. 
        private ConnectEquipment ConnectSwitch { get; } =
            new ConnectEquipment("Connect Switch", context, context.SwitchHub, false);
        private ConnectEquipment ConnectSafety { get; } =
            new ConnectEquipment("Connect SafetyMonitor", context, context.SafetyMonitor, true);
        private ConnectEquipment ConnectMount { get; } =
            new ConnectEquipment("Connect Mount", context, context.Mount, true);
        private ConnectEquipment ConnectCamera { get; } =
            new ConnectEquipment("Connect Camera", context, context.Camera, true);
        private ConnectEquipment ConnectFilterWheel { get; } =
            new ConnectEquipment("Connect FilterWheel", context, context.FilterWheel, false);
        // Optional devices. With isRequired false, ConnectEquipment only returns Completed, so no wait node.
        private ConnectEquipment ConnectFocuser { get; } =
            new ConnectEquipment("Connect Focuser", context, context.Focuser, false);
        private ConnectEquipment ConnectWeather { get; } =
            new ConnectEquipment("Connect Weather", context, context.Weather, false);
        private ConnectEquipment ConnectRotator { get; } =
            new ConnectEquipment("Connect Rotator", context, context.Rotator, false);
        private ConnectEquipment ConnectGuider { get; } =
            new ConnectEquipment("Connect Guider", context, context.Guider, false);
        private ConnectEquipment ConnectFlatPanel { get; } =
            new ConnectEquipment("Connect Flat Panel", context, context.FlatPanel, true,
                isInUse: () => context.FlatPanelMode != FlatPanelMode.Ignore);
        private DomeControlSettingCheck ConnectDomeCheck { get; } =
            new DomeControlSettingCheck("Connect Dome Check", context);
        private ConnectEquipment ConnectDome { get; } =
            new ConnectEquipment("Connect Dome", context, context.Dome, true);
        private CoolCamera CoolCamera { get; } = new CoolCamera("Cool Camera", context);

        // Wait for Equipment Connection Nodes. Required devices only.
        private WaitForEquipmentConnected WaitForSafetyConnect { get; } =
            new WaitForEquipmentConnected("Wait on Safety Connected", context, context.SafetyMonitor);
        private WaitForEquipmentConnected WaitForMountConnect { get; } =
            new WaitForEquipmentConnected("Wait for Mount Connected", context, context.Mount);
        private WaitForEquipmentConnected WaitForCameraConnect { get; } =
            new WaitForEquipmentConnected("Wait for Camera Connected", context, context.Camera);
        private WaitForEquipmentConnected WaitForDomeConnect { get; } =
            new WaitForEquipmentConnected("Wait for Dome Connected", context, context.Dome);
        private WaitForEquipmentConnected WaitForFlatPanelConnect { get; } =
            new WaitForEquipmentConnected("Wait for Flat Panel Connected", context, context.FlatPanel,
                "Autopilot: waiting for the flat panel to be connected.",
                () => new {
                    mode = context.FlatPanelMode.ToString(),
                    default_device = context.FlatPanel?.DefaultDevice ?? BaseComponent.NoDevice,
                    auto_connect = context.ShouldAutoConnectEquipment
                });

        // Image Run Phase 1: Wait for Safe and Open Roof
        private WaitForSafe WaitForSafe { get; } = new WaitForSafe("Wait for Safe Conditions", context);
        private DomeControlSettingCheck OpenRoofCheck { get; } =
            new DomeControlSettingCheck("Open Roof Check", context);
        private OpenDome OpenDomeShutter { get; } = new OpenDome("Open Dome Shutter", context);
        private WaitForDomeOpen WaitForDomeShutter { get; } =
            new WaitForDomeOpen("Wait for dome shutter to open", context);
        // In Operate mode nobody else will open the roof, so a failed open goes back through the
        // safety check after a pause and tries again.
        private WaitForNextPoll RetryRoofOpen { get; } =
            new WaitForNextPoll("Wait before retrying roof open", context, Context.DefaultRetryDelaySeconds);
        private PrepareFlatPanel PrepareFlatPanel { get; } = new PrepareFlatPanel("Prepare Flat Panel", context);

        // Image Run Phase 2: Get Target
        private PollForTarget PollForTarget { get; } = new PollForTarget("Poll for Target", context);
        private CheckTargetLimits CheckTargetLimits { get; } =
            new CheckTargetLimits("Check Target Within Limits", context, Context.FrameWindow);
        private CheckTargetLimits CheckLimitsBeforeFrame { get; } =
            new CheckTargetLimits("Check Limits (before exposure)", context, Context.ExposureWindow);
        private IsMeridianFlipRequired CheckMeridianFlip { get; } =
            new IsMeridianFlipRequired("Check Meridian Flip (before slew)", context, Context.FrameWindow);
        private RunMeridianFlip RunMeridianFlip { get; } = new RunMeridianFlip("Meridian Flip (before slew)", context);
        private IsMeridianFlipRequired CheckFlipBeforeFrame { get; } =
            new IsMeridianFlipRequired("Check Meridian Flip (before exposure)", context, Context.ExposureWindow);
        private RunMeridianFlip RunFlipBeforeFrame { get; } =
            new RunMeridianFlip("Meridian Flip (before exposure)", context);
        private CheckTargetChanged CheckTargetChanged { get; } =
            new CheckTargetChanged("Skip Slew If Already Centred", context);
        private DisableTracking DisableOnPollFail { get; } =
            new DisableTracking("Disable Tracking If No Target", context);
        private WaitForNextPoll DelayNextPoll { get; } =
            new WaitForNextPoll("Wait set time before polling again", context);
        private DisableTracking StopTrackingForLimit { get; } =
            new DisableTracking("Stop Tracking (Limit Reached)", context);
        // A refused target may have the mount sitting at its limit, and nothing watches the mount
        // until the next slew. So we stop tracking instead of tracking through the limit.
        private DisableTracking StopTrackingForRefusal { get; } =
            new DisableTracking("Stop Tracking (Target Refused)", context);

        // Image Run Phase 3: Slew to Target
        private UnparkMount UnparkForSlew { get; } = new UnparkMount("Unpark for Slew", context);
        private SlewToTarget SlewToTarget { get; } = new SlewToTarget("Slew to Target", context);
        private CenterOnTarget CenterOnTarget { get; } = new CenterOnTarget("Center After Slew", context);
        private RotateToTarget RotateToTarget { get; } = new RotateToTarget("Rotate to Position Angle", context);
        private StopGuiding StopGuiding { get; } = new StopGuiding("Stop Guiding Before Slew", context);
        private StartGuiding StartGuiding { get; } = new StartGuiding("Start Guiding", context);
        private DitherFrame DitherFrame { get; } = new DitherFrame("Dither", context);
        private CheckDiskSpace CheckDiskSpace { get; } = new CheckDiskSpace("Check Disk Space", context);
        private CheckR2Space CheckR2Space { get; } = new CheckR2Space("Check Cloud Storage Space", context);
        private CheckCloudUpload CheckCloudUpload { get; } =
            new CheckCloudUpload("Check Cloud Upload Enabled", context);

        // Image Run Phase 4: Capture Image
        private ChangeFilter ChangeFilter { get; } = new ChangeFilter("Change Filter", context);
        private IsAutofocusRequired CheckAutofocus { get; } =
            new IsAutofocusRequired("Check Autofocus Required", context);
        private RunAutofocus RunAutofocus { get; } = new RunAutofocus("Run Autofocus", context);
        private ApplyRecommendedFocus ApplyRecommendedFocus { get; } =
            new ApplyRecommendedFocus("Apply Recommended Focus", context);
        private MarkImageStarted MarkImageStarted { get; } = new MarkImageStarted("Mark Image Started", context);
        private CaptureImage CaptureImage { get; } = new CaptureImage("Capture Image", context);
        private ReportResult ReportResult { get; } = new ReportResult("Report results back to server", context);
        private CalibrateFrame CalibrateFrame { get; } = new CalibrateFrame("Calibrate Frame", context);
        private QueueUpload QueueUpload { get; } = new QueueUpload("Queue Cloud Upload", context);

        // End and abort states. The unsafe secure is the emergency one. It goes back to waiting for
        // safe and leaves the flat panel as it is, because the night may resume. Every other secure
        // ends the night and closes the panel if the mode says to.
        private DisableAutopilot DisableAutopilot { get; } = new DisableAutopilot("Disable Autopilot", context);
        private SecureObservatory SecureForSafety { get; } =
            new SecureObservatory("Secure (Unsafe)", context, isEmergency: true);
        private SecureObservatory SecureForShutdown { get; } =
            new SecureObservatory("Secure (Window Closed)", context, "the observing window has closed");
        // On an unrecoverable fault, park and close before switching off so the observatory is never
        // left open and tracking unattended.
        private SecureObservatory SecureForFault { get; } =
            new SecureObservatory("Secure (Fault)", context, "unrecoverable fault", isFault: true);
        // A mount limit crossed mid-run means the limit checks got it wrong, so it's treated like a
        // fault, quoting the breach the watcher logged.
        private SecureObservatory SecureForLimit { get; } =
            new SecureObservatory("Secure (Limit Reached)", context,
                () => context.LastLimitBreach ?? "mount limit reached", isFault: true);
        // Cloud storage is full and the account asks to pause instead of imaging on. Not this
        // observatory's fault, but it ends the night the same way, so it parks and closes first and
        // names itself in the session-end email.
        private SecureObservatory SecureForStorageFull { get; } =
            new SecureObservatory("Secure (Storage Full)", context, "cloud storage is full", isFault: true);
        // The server refused this plugin version. Nothing more can be dispatched until it is updated,
        // so the night ends the same way as storage full.
        private SecureObservatory SecureForUpdateRequired { get; } =
            new SecureObservatory("Secure (Update Required)", context,
                "this Astraeus version is no longer supported", isFault: true);
        // The start-up sequence is the observer's own and may have opened the roof or unparked the
        // mount before failing, so both get put back before switching off. The dome usually isn't
        // connected yet, and the secure notes that quietly.
        private SecureObservatory SecureForStartupFailure { get; } =
            new SecureObservatory("Secure (Start-up Failed)", context, "the start-up sequence failed", isFault: true);
        // A cover that won't open would be in every frame, so the night ends here.
        private SecureObservatory SecureForFlatPanel { get; } =
            new SecureObservatory("Secure (Flat Panel)", context,
                "the flat panel cover could not be confirmed open", isFault: true);
        // The roof stopped reading open mid-night. Everything stops where it is, since a park may not
        // be reachable under a closed roof. Then it waits for the roof again (Wait-for-open) or
        // disables (Operate, where nothing else should have moved it).
        private StopForRoofClosed StopForRoofClosed { get; } = new StopForRoofClosed("Stop (Roof Closed)", context);
        // Before the shutdown sequence, so a sequence that cuts switch power never does it to a cold camera.
        private WarmCamera WarmCamera { get; } = new WarmCamera("Warm Camera", context);
        private RunSequence RunShutdownSequence { get; } =
            new RunSequence("Shutdown Sequence", context, SequencePhase.Shutdown);
        private DisconnectEquipment DisconnectEquipment { get; } =
            new DisconnectEquipment("Disconnect Equipment", context);
        private EndSession EndSession { get; } = new EndSession("End Autopilot Session", context);

        public StateBase EntryState => WaitForDusk;

        public StateMachine.AbortRoutes CreateAbortRoutes() {
            return new StateMachine.AbortRoutes(
                Unsafe: SecureForSafety,
                LimitReached: StopTrackingForLimit,
                WindowClosed: SecureForShutdown,
                RoofClosed: StopForRoofClosed,
                Fault: SecureForFault);
        }

        public void WireTransitions() {
            WireStartup();
            WireRoofAndTarget();
            WireImaging();
            WireAborts();
        }

        private void WireStartup() {
            WaitForDusk.OnCompleted(RefreshSession);
            RefreshSession.OnCompleted(PrepareSmartAutofocus).OnFailed(DisableAutopilot);
            // The session only opens here, right after RefreshSession renews the token, so the
            // "started" email uses a fresh credential. Everything after it, even a start-up sequence
            // that won't run, reports its end against this session.
            PrepareSmartAutofocus.OnCompleted(StartSession);
            StartSession.OnCompleted(ConnectSwitch);
            ConnectSwitch.OnCompleted(RunStartupSequence);
            RunStartupSequence.OnCompleted(ConnectSafety).OnFailed(SecureForStartupFailure);

            ConnectSafety.OnCompleted(ConnectMount).OnFailed(WaitForSafetyConnect);
            WaitForSafetyConnect.OnCompleted(ConnectMount);

            ConnectMount.OnCompleted(ConnectCamera).OnFailed(WaitForMountConnect);
            WaitForMountConnect.OnCompleted(ConnectCamera);

            ConnectCamera.OnCompleted(ConnectFilterWheel).OnFailed(WaitForCameraConnect);
            WaitForCameraConnect.OnCompleted(ConnectFilterWheel);

            ConnectFilterWheel.OnCompleted(ConnectFocuser);
            ConnectFocuser.OnCompleted(ConnectWeather);
            ConnectWeather.OnCompleted(ConnectRotator);
            ConnectRotator.OnCompleted(ConnectGuider);
            ConnectGuider.OnCompleted(ConnectFlatPanel);
            ConnectFlatPanel.OnCompleted(CoolCamera).OnFailed(WaitForFlatPanelConnect);
            WaitForFlatPanelConnect.OnCompleted(CoolCamera);
            CoolCamera.OnCompleted(ConnectDomeCheck);

            // Wait-for-open reads the shutter just as Operate drives it, so both connect the dome.
            // With auto-connect off, ConnectEquipment falls to the wait node.
            ConnectDomeCheck.OnPass(WaitForSafe).OnNo(ConnectDome).OnYes(ConnectDome);
            WaitForDomeConnect.OnCompleted(WaitForSafe);
            ConnectDome.OnCompleted(WaitForSafe).OnFailed(WaitForDomeConnect);
        }

        // An open roof, a target, and the slew to it.
        private void WireRoofAndTarget() {
            // Wait for Safe -> Open Roof -> Flat panel out of the way.
            WaitForSafe.OnCompleted(OpenRoofCheck);
            OpenRoofCheck.OnYes(OpenDomeShutter).OnNo(WaitForDomeShutter).OnPass(PrepareFlatPanel);
            OpenDomeShutter.OnCompleted(PrepareFlatPanel).OnFailed(RetryRoofOpen);
            RetryRoofOpen.OnCompleted(WaitForSafe);
            WaitForDomeShutter.OnCompleted(PrepareFlatPanel);
            PrepareFlatPanel.OnCompleted(PollForTarget).OnFailed(SecureForFlatPanel);

            // Roof Open -> Get Target -> Check limits before committing to a slew
            // Decide the flip before the slew, because on a German equatorial the flip is the slew, to
            // the same coordinates on the other side of the pier. Deciding afterwards would mean
            // slewing and centring on the wrong side first. This covers a target drifting past the
            // limit and a new target that needs the other side.
            
            PollForTarget.OnCompleted(CheckTargetLimits).OnFailed(DisableOnPollFail)
                .On(AutopilotStateResult.UpdateRequired, SecureForUpdateRequired);
            CheckTargetLimits.OnYes(CheckMeridianFlip).OnNo(StopTrackingForRefusal);
            CheckMeridianFlip.OnYes(RunMeridianFlip).OnNo(CheckTargetChanged);
            RunMeridianFlip.OnCompleted(CheckTargetChanged).OnFailed(DelayNextPoll);
            CheckTargetChanged.OnYes(StopGuiding).OnNo(ChangeFilter);
            DisableOnPollFail.OnCompleted(DelayNextPoll).OnFailed(DelayNextPoll);
            DelayNextPoll.OnCompleted(PollForTarget);
            StopTrackingForRefusal.OnCompleted(DelayNextPoll).OnFailed(DelayNextPoll);

            // Mount-limit abort: stop the mount at once, then park, close the roof and disable.
            StopTrackingForLimit.OnCompleted(SecureForLimit).OnFailed(SecureForLimit);

            // Got Target -> Stop Guiding -> Slew. Guiding has to stop before any mount movement, and
            // the slew, the rotation solve and the centring all move the mount.
            StopGuiding.OnCompleted(UnparkForSlew);
            UnparkForSlew.OnCompleted(SlewToTarget).OnFailed(SecureForFault);
            SlewToTarget.OnCompleted(RotateToTarget).OnFailed(SecureForFault);
            RotateToTarget.OnCompleted(CenterOnTarget).OnFailed(CenterOnTarget);
            CenterOnTarget.OnCompleted(ChangeFilter).OnFailed(ChangeFilter);
        }

        // Filter, focus, guiding, the exposure and what happens to the frame.
        private void WireImaging() {
            //Set Filter -> decide whether an autofocus is due before committing to the frame
            ChangeFilter.OnCompleted(CheckAutofocus).OnFailed(DelayNextPoll);
            CheckAutofocus.OnYes(RunAutofocus).OnNo(ApplyRecommendedFocus);
            RunAutofocus.OnCompleted(StartGuiding).OnFailed(StartGuiding);
            ApplyRecommendedFocus.OnCompleted(StartGuiding);
            StartGuiding.OnCompleted(DitherFrame);
            DitherFrame.OnCompleted(CheckDiskSpace);
            CheckDiskSpace.OnCompleted(CheckLimitsBeforeFrame).OnFailed(SecureForFault);
            CheckLimitsBeforeFrame.OnYes(CheckFlipBeforeFrame).OnNo(StopTrackingForRefusal);
            CheckFlipBeforeFrame.OnYes(RunFlipBeforeFrame).OnNo(MarkImageStarted);
            RunFlipBeforeFrame.OnCompleted(CheckTargetChanged).OnFailed(DelayNextPoll);
            MarkImageStarted.OnCompleted(CaptureImage).OnFailed(DelayNextPoll);

            // Image Capture
            CaptureImage.OnCompleted(ReportResult).OnFailed(ReportResult);
            ReportResult.OnCompleted(CalibrateFrame);
            CalibrateFrame.OnCompleted(CheckCloudUpload);
            CheckCloudUpload.OnYes(CheckR2Space).OnNo(PollForTarget);
            // Storage check. Completed means there's room. No means the account is full but wants to
            // keep imaging, so the frame stays local. Every frame asks again, so uploads resume once
            // space is freed. Failed means the account is full and wants the autopilot paused.
            CheckR2Space.OnCompleted(QueueUpload).OnNo(PollForTarget).OnFailed(SecureForStorageFull);
            QueueUpload.OnCompleted(PollForTarget);
        }

        // Abort paths for the machine-injected results (see CreateAbortRoutes), and the end of the night.
        private void WireAborts() {
            SecureForSafety.OnCompleted(WaitForSafe);            // Unsafe:       secure -> wait for safe -> resume
            SecureForFault.OnCompleted(DisableAutopilot);        // Fault: secure -> disable
            SecureForLimit.OnCompleted(DisableAutopilot);        // LimitReached: stop -> secure -> disable
            SecureForStorageFull.OnCompleted(DisableAutopilot);  // Storage full: secure -> disable
            SecureForUpdateRequired.OnCompleted(DisableAutopilot); // Version refused: secure -> disable
            SecureForStartupFailure.OnCompleted(DisableAutopilot); // Start-up failed: secure -> disable
            SecureForFlatPanel.OnCompleted(DisableAutopilot);    // Flat panel would not open: secure -> disable
            // RoofClosed: stop in place -> wait for safe and the roof again (Wait-for-open), or disable (Operate)
            StopForRoofClosed.OnCompleted(WaitForSafe).OnFailed(DisableAutopilot)
                .On(AutopilotStateResult.Resumed, PollForTarget);
            SecureForShutdown.OnCompleted(WarmCamera);
            WarmCamera.OnCompleted(RunShutdownSequence);
            RunShutdownSequence.OnCompleted(DisconnectEquipment).OnFailed(DisconnectEquipment);
            DisconnectEquipment.OnCompleted(EndSession);
            EndSession.OnCompleted(WaitForDusk);
        }
    }
}
