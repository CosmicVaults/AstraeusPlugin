using CosmicVaults.NINA.Astraeus.Engine.Calibration;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Core.Utility.Notification;
using NINA.Sequencer.SequenceItem;
using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.SequenceItems {
    /// <summary>
    /// Builds and uploads calibration masters with the same engine as the settings-page button. Put it at
    /// the end of a calibration sequence.
    /// </summary>
    [ExportMetadata("Name", "Generate & Upload Calibration Masters")]
    [ExportMetadata("Description", "Stacks the raw dark/bias/flat frames in the Astraeus calibration folder " +
                                   "into masters and uploads them.")]
    [ExportMetadata("Icon", "Astraeus_CalibrationMasters_SVG")]
    [ExportMetadata("Category", "Astraeus")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class GenerateCalibrationMastersInstruction : SequenceItem {

        [ImportingConstructor]
        public GenerateCalibrationMastersInstruction() { }

        public GenerateCalibrationMastersInstruction(GenerateCalibrationMastersInstruction copyMe) : this() {
            CopyMetaData(copyMe);
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress,
            CancellationToken cancellationToken) {
            MasterFrameGenerator? generator = MasterFrameGenerator.Active;
            if (generator == null) {
                Notification.ShowWarning("Astraeus: calibration generator unavailable (plugin disabled?).");
                return;
            }

            MasterGenerationResult result = await generator.GenerateAndUploadAsync(
                message => progress?.Report(new ApplicationStatus { Status = message }), cancellationToken);

            // The generator never throws, and N.I.N.A.'s sequencer only sees a failure that is thrown.
            cancellationToken.ThrowIfCancellationRequested();
            if (!result.IsSuccess)
                throw new SequenceEntityFailedException($"Astraeus calibration: {result.Summary}");
            Notification.ShowSuccess($"Astraeus calibration: {result.Summary}");
        }

        public override object Clone() => new GenerateCalibrationMastersInstruction(this);

        public override string ToString() =>
            $"Category: {Category}, Item: {nameof(GenerateCalibrationMastersInstruction)}";
    }
}
