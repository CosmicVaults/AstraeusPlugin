using NINA.Core.Enum;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket;
using CosmicVaults.NINA.Astraeus.Engine.WebSocket.Messages;
using System.ComponentModel;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace CosmicVaults.NINA.Astraeus.Engine {
    public partial class Settings {
        private DebouncedPush? _imageFileSettingsPush;

        // The save folder isn't here because file paths are never synced with the server.
        private async Task HandleImageFileUpdateAsync(WsCommand command) {
            if (!command.Payload.TryGetProperty("data", out JsonElement data)) return;

            IImageFileSettings imageFileSettings = Profile!.ImageFileSettings;
            SettingsUpdateBatch update = new SettingsUpdateBatch(data)
                .String("file_pattern", value => SetFilePatternOnOptionsPage("FilePattern", value))
                .String("file_pattern_dark", value => SetFilePatternOnOptionsPage("FilePatternDARK", value))
                .String("file_pattern_bias", value => SetFilePatternOnOptionsPage("FilePatternBIAS", value))
                .String("file_pattern_flat", value => SetFilePatternOnOptionsPage("FilePatternFLAT", value))
                .EnumNumber<FileTypeEnum>("file_type", value => imageFileSettings.FileType = value)
                .EnumNumber<TIFFCompressionTypeEnum>("tiff_compression_type",
                    value => imageFileSettings.TIFFCompressionType = value)
                .EnumNumber<XISFCompressionTypeEnum>("xisf_compression_type",
                    value => imageFileSettings.XISFCompressionType = value)
                .EnumNumber<XISFChecksumTypeEnum>("xisf_checksum_type",
                    value => imageFileSettings.XISFChecksumType = value)
                .Bool("xisf_byte_shuffling", value => imageFileSettings.XISFByteShuffling = value)
                .EnumNumber<FITSCompressionTypeEnum>("fits_compression_type",
                    value => imageFileSettings.FITSCompressionType = value)
                .Bool("fits_add_fz_extension", value => imageFileSettings.FITSAddFzExtension = value)
                .Bool("fits_use_legacy_writer", value => imageFileSettings.FITSUseLegacyWriter = value);
            if (!await TryApplyServerUpdateAsync(update, "image file", PushImageFileSettingsAsync)) return;

            Log($"Image file settings updated via WS: filePattern={imageFileSettings.FilePattern}, " +
                $"fileType={imageFileSettings.FileType}", device: "system");
        }

        // N.I.N.A. checks and saves the file patterns through its options view model, not the profile.
        private void SetFilePatternOnOptionsPage(string propertyName, string pattern) {
            IOptionsVM optionsViewModel = Observatory.Settings.OptionsViewModel;
            Application.Current.Dispatcher.Invoke(
                () => optionsViewModel.GetType().GetProperty(propertyName)?.SetValue(optionsViewModel, pattern));
        }

        internal Task PushImageFileSettingsAsync() {
            IImageFileSettings imageFileSettings = Profile!.ImageFileSettings;
            ImageFileSettingsPayload payload = new ImageFileSettingsPayload {
                FilePattern = imageFileSettings.FilePattern,
                FilePatternDARK = imageFileSettings.FilePatternDARK,
                FilePatternBIAS = imageFileSettings.FilePatternBIAS,
                FilePatternFLAT = imageFileSettings.FilePatternFLAT,
                FileType = imageFileSettings.FileType,
                TIFFCompressionType = imageFileSettings.TIFFCompressionType,
                XISFCompressionType = imageFileSettings.XISFCompressionType,
                XISFChecksumType = imageFileSettings.XISFChecksumType,
                XISFByteShuffling = imageFileSettings.XISFByteShuffling,
                FITSCompressionType = imageFileSettings.FITSCompressionType,
                FITSAddFzExtension = imageFileSettings.FITSAddFzExtension,
                FITSUseLegacyWriter = imageFileSettings.FITSUseLegacyWriter
            };
            return WebSocketBus.SendAsync(SettingsUpdate.Create(payload, "imagefile"));
        }

        private void OnImageFileSettingsChanged(object? sender, PropertyChangedEventArgs eventArgs) {
            _imageFileSettingsPush ??= CreatePush(PushImageFileSettingsAsync, "image file");
            _imageFileSettingsPush.Schedule();
        }
    }
}
