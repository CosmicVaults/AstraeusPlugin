using Microsoft.Win32;
using CosmicVaults.NINA.Astraeus.Engine;
using CosmicVaults.NINA.Astraeus.Engine.Calibration;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace CosmicVaults.NINA.Astraeus.Controls {
    public class CalibrationSettingsViewModel : BaseViewModel {
        private readonly AstraeusSettings _settings;
        private readonly MasterFrameGenerator? _generator;
        private CancellationTokenSource? _generationCancellation;
        private bool _isGenerating;
        private string _generationStatus = string.Empty;

        public CalibrationSettingsViewModel(AstraeusSettings settings, MasterFrameGenerator? generator) {
            _settings = settings;
            _generator = generator;

            BrowseScanFolderCommand          = new RelayCommand(BrowseScanFolder);
            ClearScanFolderCommand           = new RelayCommand(() => ScanFolder = null);
            BrowseMastersOutputFolderCommand = new RelayCommand(BrowseMastersOutputFolder);
            ClearMastersOutputFolderCommand  = new RelayCommand(() => MastersOutputFolder = null);
            GenerateAndUploadMastersCommand  = new RelayCommand(ToggleGenerate, () => _generator != null);
        }

        public string GenerateButtonText => _isGenerating ? "Stop" : "Generate & upload masters";

        public string? ScanFolder {
            get => _settings.GetCalibrationScanFolder();
            set { _settings.SetCalibrationScanFolder(value); OnPropertyChanged(); }
        }

        /// <summary>Blank means write them into the scan folder.</summary>
        public string? MastersOutputFolder {
            get => _settings.GetCalibrationMastersOutputFolder();
            set { _settings.SetCalibrationMastersOutputFolder(value); OnPropertyChanged(); }
        }

        public string GenerationStatus {
            get => _generationStatus;
            private set { _generationStatus = value; OnPropertyChanged(); }
        }

        public RelayCommand BrowseScanFolderCommand { get; }
        public RelayCommand ClearScanFolderCommand { get; }
        public RelayCommand BrowseMastersOutputFolderCommand { get; }
        public RelayCommand ClearMastersOutputFolderCommand { get; }
        public RelayCommand GenerateAndUploadMastersCommand { get; }

        private void BrowseScanFolder() {
            string? picked = PickFolder("Select the raw calibration frames folder", ScanFolder);
            if (picked != null) ScanFolder = picked;
        }

        private void BrowseMastersOutputFolder() {
            string? picked = PickFolder("Select the masters output folder", MastersOutputFolder);
            if (picked != null) MastersOutputFolder = picked;
        }

        private static string? PickFolder(string title, string? currentFolder) {
            OpenFolderDialog dialog = new OpenFolderDialog { Title = title, Multiselect = false };
            if (!string.IsNullOrEmpty(currentFolder)) {
                try {
                    if (Directory.Exists(currentFolder)) dialog.InitialDirectory = currentFolder;
                } catch { /* best-effort seed only */ }
            }
            return dialog.ShowDialog() == true ? dialog.FolderName : null;
        }

        private void ToggleGenerate() {
            if (_generator == null) return;
            if (_isGenerating) {
                _generationCancellation?.Cancel();
                GenerationStatus = "Stopping...";
                return;
            }
            SetGenerating(true);
            _generationCancellation = new CancellationTokenSource();
            _ = RunGenerationAsync(_generationCancellation.Token);
        }

        private async Task RunGenerationAsync(CancellationToken cancellationToken) {
            try {
                await _generator!.GenerateAndUploadAsync(SetStatusOnUi, cancellationToken);
            } finally {
                SetGenerating(false);
                _generationCancellation?.Dispose();
                _generationCancellation = null;
            }
        }

        private void SetGenerating(bool value) {
            _isGenerating = value;
            OnPropertyChanged(nameof(GenerateButtonText));
        }

        // The generator reports progress from a background thread, so marshal to the UI thread.
        private void SetStatusOnUi(string message) {
            Dispatcher? dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess()) dispatcher.Invoke(() => GenerationStatus = message);
            else GenerationStatus = message;
        }
    }
}
