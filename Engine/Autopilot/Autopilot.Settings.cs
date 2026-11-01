using CosmicVaults.NINA.Astraeus.Engine.Utility;
using NINA.Astrometry;
using System;
using System.Runtime.CompilerServices;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot
{
    /// <summary>
    /// The autopilot settings as properties backed by AstraeusSettings, so they live in the active N.I.N.A.
    /// profile. Each setter writes the value and raises SettingsChanged with the property's wire key, which
    /// the options page and the Settings component both listen for.
    /// </summary>
    public partial class Autopilot
    {
        ///////////// Public events /////////////
        public event EventHandler<SettingChangedEventArgs>? SettingsChanged;

        /// <summary>
        /// wireKey must match the setting's [JsonPropertyName] in AutopilotSettingsPayload, since the
        /// Settings component filters the payload down to it.
        /// </summary>
        private void RaiseChanged(string? wireKey, [CallerMemberName] string propertyName = "") =>
            SettingsChanged?.Invoke(this, new SettingChangedEventArgs(propertyName, wireKey));

        ///////////// Settings-backed properties /////////////

        /// <summary>
        /// The only way the state machine starts and stops, used by the WS command, the UI and
        /// plugin-disable.
        /// </summary>
        public bool IsEnabled
        {
            get => Observatory.Settings.IsAutopilotEnabled();
            set
            {
                if (value == IsEnabled) return;
                Observatory.Settings.SetIsAutopilotEnabled(value);
                RaiseChanged("is_autopilot_enabled");
                Observatory.Cadence.Signal();
                ApplyEnabledStateAsync().ObserveFaults(exception =>
                    LogError($"Could not {(value ? "start" : "stop")} the autopilot: {exception.Message}"));
            }
        }

        public RoofMode RoofMode
        {
            get => Observatory.Settings.GetAutopilotRoofMode();
            set
            {
                Observatory.Settings.SetAutopilotRoofMode(value);
                RaiseChanged("roof_mode");
            }
        }

        /// <summary>
        /// When false autopilot never tries to connect to anything. It waits for the required devices to be connected in
        /// N.I.N.A.
        /// </summary>
        public bool ShouldAutoConnectEquipment
        {
            get => Observatory.Settings.ShouldAutoConnectEquipment();
            set
            {
                Observatory.Settings.SetShouldAutoConnectEquipment(value);
                RaiseChanged("auto_connect_equipment");
            }
        }

        public OperatingWindow OperatingWindow
        {
            get => Observatory.Settings.GetAutopilotOperatingWindow();
            set
            {
                Observatory.Settings.SetAutopilotOperatingWindow(value);
                RaiseChanged("operating_window");
            }
        }

        /// <summary>
        /// Cools the camera once the equipment is connected and warms it at dawn after securing. A
        /// switch-off or fault doesn't warm it, since those leave the observatory as found.
        /// </summary>
        public bool IsCameraCoolingEnabled
        {
            get => Observatory.Settings.IsCameraCoolingEnabled();
            set
            {
                Observatory.Settings.SetIsCameraCoolingEnabled(value);
                RaiseChanged("is_camera_cooling_enabled");
            }
        }

        /// <summary>
        /// The temperature (°C) the autopilot cools the camera to at dusk. A cooler already set colder
        /// than this is left where it is.
        /// </summary>
        public double CameraCoolingTemperature
        {
            get => Observatory.Settings.GetCameraCoolingTemperature();
            set
            {
                Observatory.Settings.SetCameraCoolingTemperature(value);
                RaiseChanged("camera_cooling_temperature_celsius");
            }
        }

        ///////////// Mount pointing / tracking limits /////////////

        /// <summary>Master switch for the mount pointing and tracking limits. Off enforces none.</summary>
        public bool IsMountLimitEnabled
        {
            get => Observatory.Settings.IsMountLimitEnabled();
            set
            {
                Observatory.Settings.SetIsMountLimitEnabled(value);
                RaiseChanged("is_mount_limit_enabled");
            }
        }

        /// <summary>Whether the fixed minimum-altitude floor is enforced.</summary>
        public bool IsAltitudeLimitEnabled
        {
            get => Observatory.Settings.IsAltitudeLimitEnabled();
            set
            {
                Observatory.Settings.SetIsAltitudeLimitEnabled(value);
                RaiseChanged("is_altitude_limit_enabled");
            }
        }

        /// <summary>The minimum altitude (degrees) the mount may point at or track down to.</summary>
        public double MinAltitudeDegrees
        {
            get => Observatory.Settings.GetMountMinAltitudeDegrees();
            set
            {
                Observatory.Settings.SetMountMinAltitudeDegrees(value);
                RaiseChanged("min_altitude_degrees");
            }
        }

        /// <summary>Whether the profile's custom horizon is enforced as an azimuth-dependent floor.</summary>
        public bool IsCustomHorizonLimitEnabled
        {
            get => Observatory.Settings.IsCustomHorizonLimitEnabled();
            set
            {
                Observatory.Settings.SetIsCustomHorizonLimitEnabled(value);
                RaiseChanged("is_custom_horizon_limit_enabled");
            }
        }

        /// <summary>
        /// Whether a target the scheduler lets cross the meridian limit gets a flip instead of being
        /// refused. Off by default. The limit comes from N.I.N.A.'s Meridian Flip settings and only
        /// applies with the mount limits on.
        /// </summary>
        public bool IsMeridianFlipEnabled
        {
            get => Observatory.Settings.IsMeridianFlipEnabled();
            set
            {
                Observatory.Settings.SetIsMeridianFlipEnabled(value);
                RaiseChanged("is_meridian_flip_enabled");
            }
        }

        /// <summary>
        /// How close (arc-minutes) the mount must already be to a new target to skip the slew and plate
        /// solve and capture straight away.
        /// </summary>
        public double RecenterToleranceArcmin
        {
            get => Observatory.Settings.GetRecenterToleranceArcmin();
            set
            {
                Observatory.Settings.SetRecenterToleranceArcmin(value);
                RaiseChanged("recenter_tolerance_arcmin");
            }
        }

        /// <summary>
        /// Seconds the safety monitor must read Safe without a break before the autopilot reopens the
        /// roof and unparks after an unsafe spell. Zero resumes on the first Safe reading.
        /// </summary>
        public int SafeSettleSeconds
        {
            get => Observatory.Settings.GetSafeSettleSeconds();
            set
            {
                Observatory.Settings.SetSafeSettleSeconds(value);
                RaiseChanged("safe_settle_seconds");
            }
        }

        ///////////// Rotator /////////////

        /// <summary>The default, None, leaves the rotator alone.</summary>
        public RotatorFallbackMode RotatorFallbackMode
        {
            get => Observatory.Settings.GetAutopilotRotatorFallbackMode();
            set
            {
                Observatory.Settings.SetAutopilotRotatorFallbackMode(value);
                RaiseChanged("rotator_fallback_mode");
            }
        }

        /// <summary>
        /// The angle (degrees) for the rotator fallback modes other than None. It's a sky position angle
        /// or a mechanical angle, depending on RotatorFallbackMode.
        /// </summary>
        public double RotatorFallbackAngle
        {
            get => Observatory.Settings.GetAutopilotRotatorFallbackAngle();
            set
            {
                // Fold into [0, 360) so 720 or -90 is stored, and shown on the options page, as the
                // angle the rotator really gets driven to.
                Observatory.Settings.SetAutopilotRotatorFallbackAngle(AstroUtil.EuclidianModulus(value, 360));
                RaiseChanged("rotator_fallback_angle");
            }
        }

        /// <summary>
        /// "Mount can move under closed roof": the park position is reachable with the roof closed. Lets
        /// OpenDome park so N.I.N.A. will open the roof, and lets a secure park whatever the roof reads,
        /// skipping RoofAwarePark's roof check. Off by default, since a park position only reachable with
        /// the roof open means a collision.
        /// </summary>
        public bool CanMountMoveUnderClosedRoof
        {
            get => Observatory.Settings.CanMountMoveUnderClosedRoof();
            set
            {
                Observatory.Settings.SetCanMountMoveUnderClosedRoof(value);
                RaiseChanged("park_mount_to_open_roof");
            }
        }

        ///////////// Flat panel /////////////

        public FlatPanelMode FlatPanelMode
        {
            get => Observatory.Settings.GetAutopilotFlatPanelMode();
            set
            {
                Observatory.Settings.SetAutopilotFlatPanelMode(value);
                RaiseChanged("flat_panel_mode");
            }
        }

        /// <summary>
        /// Only matters for OpenAndClose. When off, the cover closes before the roof at dawn, or stays open
        /// with a warning if the roof is already shut. Bad-weather closes leave the cover alone either way.
        /// </summary>
        public bool ShouldOperateFlatPanelWithRoofClosed
        {
            get => Observatory.Settings.ShouldOperateFlatPanelWithRoofClosed();
            set
            {
                Observatory.Settings.SetShouldOperateFlatPanelWithRoofClosed(value);
                RaiseChanged("flat_panel_operates_with_roof_closed");
            }
        }

        ///////////// Autofocus /////////////

        public bool IsAutofocusEnabled
        {
            get => Observatory.Settings.IsAutofocusEnabled();
            set
            {
                Observatory.Settings.SetIsAutofocusEnabled(value);
                RaiseChanged("is_autofocus_enabled");
            }
        }

        public bool ShouldAutofocusOnTemperatureChange
        {
            get => Observatory.Settings.ShouldAutofocusOnTemperatureChange();
            set
            {
                Observatory.Settings.SetShouldAutofocusOnTemperatureChange(value);
                RaiseChanged("autofocus_on_temperature_change");
            }
        }

        /// <summary>Temperature change (°C) since the last autofocus that triggers a refocus.</summary>
        public double AutofocusTemperatureThreshold
        {
            get => Observatory.Settings.GetAutofocusTemperatureThreshold();
            set
            {
                Observatory.Settings.SetAutofocusTemperatureThreshold(value);
                RaiseChanged("autofocus_temperature_threshold");
            }
        }

        public bool ShouldAutofocusOnFilterChange
        {
            get => Observatory.Settings.ShouldAutofocusOnFilterChange();
            set
            {
                Observatory.Settings.SetShouldAutofocusOnFilterChange(value);
                RaiseChanged("autofocus_on_filter_change");
            }
        }

        public bool ShouldAutofocusOnTimeInterval
        {
            get => Observatory.Settings.ShouldAutofocusOnTimeInterval();
            set
            {
                Observatory.Settings.SetShouldAutofocusOnTimeInterval(value);
                RaiseChanged("autofocus_on_time_interval");
            }
        }

        public int AutofocusIntervalMinutes
        {
            get => Observatory.Settings.GetAutofocusIntervalMinutes();
            set
            {
                Observatory.Settings.SetAutofocusIntervalMinutes(value);
                RaiseChanged("autofocus_interval_minutes");
            }
        }

        /// <summary>
        /// When on, the server decides when to refocus and owns the per-filter focus positions. We post
        /// each autofocus result and move the focuser to the server's suggested position in between.
        /// </summary>
        public bool IsSmartAutofocusEnabled
        {
            get => Observatory.Settings.IsSmartAutofocusEnabled();
            set
            {
                Observatory.Settings.SetIsSmartAutofocusEnabled(value);
                RaiseChanged("is_smart_autofocus_enabled");
            }
        }

        ///////////// Cloud upload /////////////

        /// <summary>
        /// Whether frames and their previews go up to cloud storage. Off keeps everything on this
        /// machine, though the live preview still reaches the dashboard. Read fresh for each frame.
        /// </summary>
        public bool IsCloudUploadEnabled
        {
            get => Observatory.Settings.IsCloudUploadEnabled();
            set
            {
                Observatory.Settings.SetIsCloudUploadEnabled(value);
                RaiseChanged("is_cloud_upload_enabled");
            }
        }

        /// <summary>
        /// Absolute path to a NINA advanced-sequencer .json run once at dusk by the startup state.
        /// Null/empty disables it. Machine-local (not in the web contract).
        /// </summary>
        public string? StartupSequencePath
        {
            get => Observatory.Settings.GetAutopilotStartupSequencePath();
            set
            {
                Observatory.Settings.SetAutopilotStartupSequencePath(value);
                RaiseChanged(null);
            }
        }

        /// <summary>
        /// Absolute path to a NINA advanced-sequencer .json run once at dawn by the shutdown state.
        /// Null/empty disables it. Machine-local (not in the web contract).
        /// </summary>
        public string? ShutdownSequencePath
        {
            get => Observatory.Settings.GetAutopilotShutdownSequencePath();
            set
            {
                Observatory.Settings.SetAutopilotShutdownSequencePath(value);
                RaiseChanged(null);
            }
        }
    }
}
