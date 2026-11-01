namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot
{
    /// <summary>
    /// The run's live view of the autopilot settings and the N.I.N.A. profile. Every member re-reads its
    /// setting on each access, so a change made mid-night applies from the next state that reads it.
    /// </summary>
    internal sealed partial class Context
    {
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

        /// <summary>Seconds of unbroken Safe readings needed before the roof and mount move again.</summary>
        public int SafeSettleSeconds => Observatory.Settings.GetSafeSettleSeconds();

        ///////////// Meridian flip (live) /////////////

        /// <summary>
        /// Whether the observer lets the autopilot flip the mount. Off by default, and when it's off a
        /// target past the meridian limit is refused.
        /// </summary>
        public bool IsMeridianFlipEnabled => Observatory.Settings.IsMeridianFlipEnabled();

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

        /// <summary>See Autopilot.CanMountMoveUnderClosedRoof.</summary>
        public bool CanMountMoveUnderClosedRoof => Observatory.Settings.CanMountMoveUnderClosedRoof();

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

        /// <summary>
        /// When on, the server owns the autofocus decision and per-filter focus positions, and the local
        /// triggers are ignored. If the server can't be reached the frame is taken with no focus change.
        /// </summary>
        public bool IsSmartAutofocusEnabled => Observatory.Settings.IsSmartAutofocusEnabled();

        ///////////// Rotator (live) /////////////
        public RotatorFallbackMode RotatorFallbackMode => Observatory.Settings.GetAutopilotRotatorFallbackMode();
        public double RotatorFallbackAngle => Observatory.Settings.GetAutopilotRotatorFallbackAngle();

        /// <summary>
        /// How close in degrees the rotator must get to the requested angle. Read from the N.I.N.A.
        /// profile so it matches N.I.N.A.'s own Center-and-Rotate.
        /// </summary>
        public double RotationToleranceDegrees =>
            Observatory.Settings.ProfileService.ActiveProfile.PlateSolveSettings.RotationTolerance;
    }
}
