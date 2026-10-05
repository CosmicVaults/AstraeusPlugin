using Microsoft.Win32;
using NINA.Equipment.Interfaces;
using CosmicVaults.NINA.Astraeus.Engine;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot;
using CosmicVaults.NINA.Astraeus.Engine.Components;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace CosmicVaults.NINA.Astraeus.Controls {

    public record DeviceOption(string Id, string DisplayName);

    public record RoofModeOption(RoofMode Value, string DisplayName);

    public record RotatorFallbackModeOption(RotatorFallbackMode Value, string DisplayName);

    public record OperatingWindowOption(OperatingWindow Value, string DisplayName);

    public record FlatPanelModeOption(FlatPanelMode Value, string DisplayName);

    /// <summary>
    /// One label, dropdown and refresh row for a single IDeviceComponent, so the XAML declares the
    /// controls once.
    /// </summary>
    public sealed class DeviceSelectorRow : BaseViewModel {
        private const string NinaNoDeviceId = "No_Device";

        private readonly IDeviceComponent? _component;

        public string Label { get; }
        public ObservableCollection<DeviceOption> Options { get; } = new();
        public RelayCommand RefreshCommand { get; }

        public DeviceSelectorRow(string label, IDeviceComponent? component) {
            Label = label;
            _component = component;
            RefreshCommand = new RelayCommand(Refresh);
            if (component != null)
                component.DefaultDeviceChanged += (_, _) => OnPropertyChanged(nameof(SelectedId));
            Refresh();
        }

        public string? SelectedId {
            get => _component?.DefaultDevice ?? BaseComponent.NoDevice;
            set {
                // WPF writes null whenever the selected item leaves Options. Picking "None" arrives as its
                // own id, so a null is never a real choice and mustn't clear the saved default.
                if (value is null) return;
                _component?.SetDefaultDevice(value, "options page");
                OnPropertyChanged();
            }
        }

        public void Refresh() {
            // Always include "None" and the saved selection so the ComboBox has an item matching its
            // bound value. Without a match, a two-way SelectedValue writes null back and wipes the setting.
            List<DeviceOption> desiredOptions = new List<DeviceOption> {
                new DeviceOption(BaseComponent.NoDevice, "None")
            };
            IList<IDevice>? devices = _component?.GetAvailableDevices();
            if (devices != null) {
                IEnumerable<IDevice> realDevices = devices
                    .Where(candidate => candidate.Id != NinaNoDeviceId && !string.IsNullOrEmpty(candidate.Id));
                foreach (IDevice device in realDevices)
                    desiredOptions.Add(new DeviceOption(device.Id, device.DisplayName));
            }
            string? savedId = _component?.DefaultDevice;
            bool isSavedIdOffered = desiredOptions.Any(option => option.Id == savedId);
            if (!string.IsNullOrEmpty(savedId) && savedId != BaseComponent.NoDevice && !isSavedIdOffered)
                desiredOptions.Add(new DeviceOption(savedId, savedId));

            // Sync in place by id, adding new ids before dropping old ones, so the selected id is always
            // present. A rename like "Mount (OFFLINE)" to "Mount" after a rescan replaces the entry, except
            // for the selected one, since replacing that drops the selection too.
            foreach (DeviceOption option in desiredOptions) {
                int index = IndexOfId(option.Id);
                if (index < 0) Options.Add(option);
                else if (Options[index] != option && option.Id != savedId) Options[index] = option;
            }
            for (int index = Options.Count - 1; index >= 0; index--) {
                string offeredId = Options[index].Id;
                if (!desiredOptions.Any(option => option.Id == offeredId)) Options.RemoveAt(index);
            }

            OnPropertyChanged(nameof(SelectedId));
        }

        private int IndexOfId(string id) {
            for (int index = 0; index < Options.Count; index++)
                if (Options[index].Id == id) return index;
            return -1;
        }
    }

    public class AutopilotSettingsViewModel : BaseViewModel {

        private readonly Autopilot _autopilot;

        public AutopilotSettingsViewModel(Observatory observatory) {
            _autopilot = observatory.GetComponent<Autopilot>();

            // Every setting raises this one event with the property name. The VM's property names match
            // the Autopilot's, so the notification passes straight through.
            _autopilot.SettingsChanged += (_, eventArgs) => {
                OnPropertyChanged(eventArgs.PropertyName);
                switch (eventArgs.PropertyName) {
                    case nameof(IsSmartAutofocusEnabled): OnPropertyChanged(nameof(IsCadenceTriggersEnabled)); break;
                    case nameof(RotatorFallbackMode):     OnPropertyChanged(nameof(IsRotatorAngleEnabled)); break;
                    case nameof(FlatPanelMode):           OnPropertyChanged(nameof(IsFlatPanelRoofOptionEnabled)); break;
                    case nameof(SafeSettleSeconds):       OnPropertyChanged(nameof(SafeSettleLabel)); break;
                }
            };

            BrowseStartupSequenceCommand  = new RelayCommand(BrowseStartupSequence);
            ClearStartupSequenceCommand   = new RelayCommand(() => StartupSequencePath = null);
            BrowseShutdownSequenceCommand = new RelayCommand(BrowseShutdownSequence);
            ClearShutdownSequenceCommand  = new RelayCommand(() => ShutdownSequencePath = null);

            DefaultDevices = new List<DeviceSelectorRow> {
                CreateDeviceRow<Mount>("Mount"),
                CreateDeviceRow<Camera>("Camera"),
                CreateDeviceRow<FilterWheel>("Filter wheel"),
                CreateDeviceRow<Focuser>("Focuser"),
                CreateDeviceRow<Weather>("Weather"),
                CreateDeviceRow<Rotator>("Rotator"),
                CreateDeviceRow<Guider>("Guider"),
                CreateDeviceRow<FlatPanel>("Flat panel"),
                CreateDeviceRow<SwitchHub>("Switch"),
                CreateDeviceRow<SafetyMonitor>("Safety monitor"),
                CreateDeviceRow<Dome>("Roof (dome)"),
            };

            DeviceSelectorRow CreateDeviceRow<T>(string label) where T : class, IDeviceComponent {
                observatory.TryGetComponent(out T? component);
                return new DeviceSelectorRow(label, component);
            }
        }
        
        
      

        ///////////// Autopilot settings /////////////

        public bool IsEnabled {
            get => _autopilot.IsEnabled;
            set {
                _autopilot.IsEnabled = value;
                OnPropertyChanged();
            }
        }

        public RoofMode RoofMode {
            get => _autopilot.RoofMode;
            set {
                _autopilot.RoofMode = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Only matters when N.I.N.A.'s "Refuse open or close if mount is unparked" dome setting is on.
        /// </summary>
        public bool ShouldParkMountToOpenRoof {
            get => _autopilot.ShouldParkMountToOpenRoof;
            set {
                _autopilot.ShouldParkMountToOpenRoof = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Seconds the safety monitor must read Safe without a break before the autopilot reopens the
        /// roof and unparks after an unsafe spell. 0 to MaxSafeSettleSeconds, where 0 resumes on the
        /// first Safe reading.
        /// </summary>
        public int SafeSettleSeconds {
            get => _autopilot.SafeSettleSeconds;
            set {
                _autopilot.SafeSettleSeconds = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SafeSettleLabel));
            }
        }

        public int MaxSafeSettleSeconds => AstraeusSettings.MaxSafeSettleSeconds;

        public string SafeSettleLabel => SafeSettleSeconds switch {
            <= 0 => "Safe Settle Timer: off (resume on the first Safe reading)",
            < 60 and int seconds => $"Safe Settle Timer: {seconds} s",
            int seconds when seconds % 60 == 0 => $"Safe Settle Timer: {seconds / 60} min",
            int seconds => $"Safe Settle Timer: {seconds / 60} min {seconds % 60} s",
        };

        public IReadOnlyList<RoofModeOption> RoofModes { get; } = new[] {
            new RoofModeOption(RoofMode.Ignore,      "Ignore roof"),
            new RoofModeOption(RoofMode.WaitForOpen, "Wait for roof to open"),
            new RoofModeOption(RoofMode.Operate,     "Operate roof automatically")
        };

        public bool ShouldAutoConnectEquipment {
            get => _autopilot.ShouldAutoConnectEquipment;
            set {
                _autopilot.ShouldAutoConnectEquipment = value;
                OnPropertyChanged();
            }
        }

        public OperatingWindow OperatingWindow {
            get => _autopilot.OperatingWindow;
            set {
                _autopilot.OperatingWindow = value;
                OnPropertyChanged();
            }
        }

        public IReadOnlyList<OperatingWindowOption> OperatingWindows { get; } = new[] {
            new OperatingWindowOption(OperatingWindow.Sunset,       "Sunset (0°)"),
            new OperatingWindowOption(OperatingWindow.Nautical,     "Nautical twilight (-12°)"),
            new OperatingWindowOption(OperatingWindow.Astronomical, "Astronomical twilight (-18°)")
        };

        public bool IsCameraCoolingEnabled {
            get => _autopilot.IsCameraCoolingEnabled;
            set {
                _autopilot.IsCameraCoolingEnabled = value;
                OnPropertyChanged();
            }
        }

        /// <summary>The temperature (°C) the autopilot cools the camera to. The box shows the clamped value.</summary>
        public double CameraCoolingTemperature {
            get => _autopilot.CameraCoolingTemperature;
            set {
                _autopilot.CameraCoolingTemperature = value;
                OnPropertyChanged();
            }
        }

        ///////////// Flat panel /////////////

        public FlatPanelMode FlatPanelMode {
            get => _autopilot.FlatPanelMode;
            set {
                _autopilot.FlatPanelMode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsFlatPanelRoofOptionEnabled));
            }
        }

        public bool ShouldOperateFlatPanelWithRoofClosed {
            get => _autopilot.ShouldOperateFlatPanelWithRoofClosed;
            set {
                _autopilot.ShouldOperateFlatPanelWithRoofClosed = value;
                OnPropertyChanged();
            }
        }

        /// <summary>The roof-closed option only means anything when the autopilot closes the cover.</summary>
        public bool IsFlatPanelRoofOptionEnabled => FlatPanelMode == FlatPanelMode.OpenAndClose;

        public IReadOnlyList<FlatPanelModeOption> FlatPanelModes { get; } = new[] {
            new FlatPanelModeOption(FlatPanelMode.Ignore,       "Ignore"),
            new FlatPanelModeOption(FlatPanelMode.EnsureOpen,   "Check open and off"),
            new FlatPanelModeOption(FlatPanelMode.OpenAndClose, "Always open and close")
        };

        ///////////// Mount limits /////////////

        public bool IsMountLimitEnabled {
            get => _autopilot.IsMountLimitEnabled;
            set {
                _autopilot.IsMountLimitEnabled = value;
                OnPropertyChanged();
            }
        }

        public bool IsAltitudeLimitEnabled {
            get => _autopilot.IsAltitudeLimitEnabled;
            set {
                _autopilot.IsAltitudeLimitEnabled = value;
                OnPropertyChanged();
            }
        }

        public double MinAltitudeDegrees {
            get => _autopilot.MinAltitudeDegrees;
            set {
                _autopilot.MinAltitudeDegrees = value;
                OnPropertyChanged();
            }
        }

        public bool IsCustomHorizonLimitEnabled {
            get => _autopilot.IsCustomHorizonLimitEnabled;
            set {
                _autopilot.IsCustomHorizonLimitEnabled = value;
                OnPropertyChanged();
            }
        }

        public bool IsMeridianFlipEnabled {
            get => _autopilot.IsMeridianFlipEnabled;
            set {
                _autopilot.IsMeridianFlipEnabled = value;
                OnPropertyChanged();
            }
        }

        public double RecenterToleranceArcmin {
            get => _autopilot.RecenterToleranceArcmin;
            set {
                _autopilot.RecenterToleranceArcmin = value;
                OnPropertyChanged();
            }
        }
        
        ///////////// Rotator /////////////

        public RotatorFallbackMode RotatorFallbackMode {
            get => _autopilot.RotatorFallbackMode;
            set {
                _autopilot.RotatorFallbackMode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsRotatorAngleEnabled));
            }
        }

        public double RotatorFallbackAngle {
            get => _autopilot.RotatorFallbackAngle;
            set {
                _autopilot.RotatorFallbackAngle = value;
                OnPropertyChanged();
            }
        }

        public bool IsRotatorAngleEnabled => RotatorFallbackMode != RotatorFallbackMode.None;

        public IReadOnlyList<RotatorFallbackModeOption> RotatorFallbackModes { get; } = new[] {
            new RotatorFallbackModeOption(RotatorFallbackMode.None,            "Do nothing"),
            new RotatorFallbackModeOption(RotatorFallbackMode.SkyPositionAngle, "Rotate to sky position angle"),
            new RotatorFallbackModeOption(RotatorFallbackMode.MechanicalAngle,  "Rotate to mechanical angle")
        };

        ///////////// Autofocus /////////////

        public bool IsAutofocusEnabled {
            get => _autopilot.IsAutofocusEnabled;
            set {
                _autopilot.IsAutofocusEnabled = value;
                OnPropertyChanged();
            }
        }

        public bool ShouldAutofocusOnTemperatureChange {
            get => _autopilot.ShouldAutofocusOnTemperatureChange;
            set {
                _autopilot.ShouldAutofocusOnTemperatureChange = value;
                OnPropertyChanged();
            }
        }

        public double AutofocusTemperatureThreshold {
            get => _autopilot.AutofocusTemperatureThreshold;
            set {
                _autopilot.AutofocusTemperatureThreshold = value;
                OnPropertyChanged();
            }
        }

        public bool ShouldAutofocusOnFilterChange {
            get => _autopilot.ShouldAutofocusOnFilterChange;
            set {
                _autopilot.ShouldAutofocusOnFilterChange = value;
                OnPropertyChanged();
            }
        }

        public bool ShouldAutofocusOnTimeInterval {
            get => _autopilot.ShouldAutofocusOnTimeInterval;
            set {
                _autopilot.ShouldAutofocusOnTimeInterval = value;
                OnPropertyChanged();
            }
        }

        public int AutofocusIntervalMinutes {
            get => _autopilot.AutofocusIntervalMinutes;
            set {
                _autopilot.AutofocusIntervalMinutes = value;
                OnPropertyChanged();
            }
        }

        public bool IsSmartAutofocusEnabled {
            get => _autopilot.IsSmartAutofocusEnabled;
            set {
                _autopilot.IsSmartAutofocusEnabled = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsCadenceTriggersEnabled));
            }
        }

        /// <summary>The cadence triggers are stored but ignored while Smart Autofocus is on.</summary>
        public bool IsCadenceTriggersEnabled => !IsSmartAutofocusEnabled;

        ///////////// Cloud upload /////////////

        public bool IsCloudUploadEnabled {
            get => _autopilot.IsCloudUploadEnabled;
            set {
                _autopilot.IsCloudUploadEnabled = value;
                OnPropertyChanged();
            }
        }

        ///////////// Default devices /////////////

        public IReadOnlyList<DeviceSelectorRow> DefaultDevices { get; }

        /// <summary>
        /// Call once the equipment components have started, since the rows first fill before any devices
        /// exist. Hops to the UI thread, because the plugin loop calls this from the thread pool.
        /// </summary>
        public void RefreshDevices() {
            System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess()) {
                dispatcher.Invoke(RefreshDevices);
                return;
            }
            foreach (DeviceSelectorRow row in DefaultDevices) {
                row.Refresh();
            }
        }

        ///////////// Startup / shutdown sequences /////////////

        public string? StartupSequencePath {
            get => _autopilot.StartupSequencePath;
            set {
                _autopilot.StartupSequencePath = value;
                OnPropertyChanged();
            }
        }

        public string? ShutdownSequencePath {
            get => _autopilot.ShutdownSequencePath;
            set {
                _autopilot.ShutdownSequencePath = value;
                OnPropertyChanged();
            }
        }

        public RelayCommand BrowseStartupSequenceCommand { get; }
        public RelayCommand ClearStartupSequenceCommand { get; }
        public RelayCommand BrowseShutdownSequenceCommand { get; }
        public RelayCommand ClearShutdownSequenceCommand { get; }

        private void BrowseStartupSequence() {
            string? picked = PickSequenceFile(StartupSequencePath);
            if (picked != null) StartupSequencePath = picked;
        }

        private void BrowseShutdownSequence() {
            string? picked = PickSequenceFile(ShutdownSequencePath);
            if (picked != null) ShutdownSequencePath = picked;
        }

        private static string? PickSequenceFile(string? currentPath) {
            OpenFileDialog dialog = new OpenFileDialog {
                Title = "Select N.I.N.A. sequence (.json)",
                Filter = "N.I.N.A. sequence (*.json)|*.json|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };
            if (!string.IsNullOrEmpty(currentPath)) {
                try {
                    string? directory = Path.GetDirectoryName(currentPath);
                    if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                        dialog.InitialDirectory = directory;
                    dialog.FileName = Path.GetFileName(currentPath);
                } catch { /* best-effort seed only */ }
            }
            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }
    }
}
