using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;

namespace CosmicVaults.NINA.Astraeus.Engine.Calibration {
    internal sealed record MasterIndexEntry(
        int MasterFrameId,
        int Revision,
        string? ChecksumSha256,
        string FileName,
        long FileLengthBytes,
        DateTime LastWriteTimeUtc,
        DateTime RecordedAtUtc);

    /// <summary>
    /// The masters this PC generated and uploaded, keyed by server id and revision, so a plan's masters
    /// can be found on disk. Masters are never downloaded. If a plan names one that isn't in here, the
    /// plan is skipped and the frame stays uncalibrated.
    /// </summary>
    internal sealed class MasterIndex {
        private const string IndexFileName = "astraeus-master-index.json";

        // The generator writes and the calibrators read, from different threads.
        private static readonly object IndexFileLock = new();

        private readonly string _mastersFolder;

        public MasterIndex(string mastersFolder) {
            _mastersFolder = mastersFolder;
        }

        private string IndexFilePath => Path.Combine(_mastersFolder, IndexFileName);

        /// <summary>
        /// Where generated masters go. That's the masters output folder, or the calibration scan folder
        /// if it isn't set. Null when neither is set.
        /// </summary>
        public static string? ResolveMastersFolder(AstraeusSettings settings) {
            string? outputFolder = settings.GetCalibrationMastersOutputFolder();
            if (!string.IsNullOrWhiteSpace(outputFolder)) {
                return outputFolder;
            }
            string? scanFolder = settings.GetCalibrationScanFolder();
            if (!string.IsNullOrWhiteSpace(scanFolder)) {
                return scanFolder;
            }
            return null;
        }

        /// <summary>
        /// Records an uploaded master, replacing any earlier entry for the same server id. Returns the
        /// replaced entry's file path, or null when there was none.
        /// </summary>
        public string? Record(int masterFrameId, int revision, string? checksumSha256, string masterFilePath) {
            FileInfo masterFile = new FileInfo(masterFilePath);
            MasterIndexEntry entry = new MasterIndexEntry(
                MasterFrameId: masterFrameId,
                Revision: revision,
                ChecksumSha256: checksumSha256,
                FileName: Path.GetRelativePath(_mastersFolder, masterFile.FullName),
                FileLengthBytes: masterFile.Length,
                LastWriteTimeUtc: masterFile.LastWriteTimeUtc,
                RecordedAtUtc: DateTime.UtcNow);

            lock (IndexFileLock) {
                List<MasterIndexEntry> entries = ReadEntries();
                MasterIndexEntry? replaced = entries.Find(existing => existing.MasterFrameId == masterFrameId);
                entries.RemoveAll(existing => existing.MasterFrameId == masterFrameId);
                entries.Add(entry);
                WriteEntries(entries);
                return replaced == null ? null : Path.Combine(_mastersFolder, replaced.FileName);
            }
        }

        /// <summary>
        /// The local file for master, or null with the reason when this PC doesn't have that master at
        /// that revision.
        /// </summary>
        public string? FindLocalPath(MasterRef master, out string? missingReason) {
            MasterIndexEntry? entry;
            lock (IndexFileLock) {
                entry = ReadEntries().Find(candidate =>
                    candidate.MasterFrameId == master.Id && candidate.Revision == master.Revision);
            }

            if (entry == null) {
                missingReason = "it was not generated on this PC";
                return null;
            }

            string masterFilePath = Path.Combine(_mastersFolder, entry.FileName);
            FileInfo masterFile = new FileInfo(masterFilePath);
            if (!masterFile.Exists) {
                // No folder path here, since this reason goes to the server as the frame's skip_reason.
                missingReason = $"its file {entry.FileName} is no longer in the masters folder";
                return null;
            }

            bool isFileUnchanged = masterFile.Length == entry.FileLengthBytes
                                   && masterFile.LastWriteTimeUtc == entry.LastWriteTimeUtc;
            if (!isFileUnchanged) {
                missingReason = $"its file {entry.FileName} has changed since it was uploaded";
                return null;
            }

            bool hasBothChecksums = master.ChecksumSha256 != null && entry.ChecksumSha256 != null;
            if (hasBothChecksums
                && !string.Equals(master.ChecksumSha256, entry.ChecksumSha256, StringComparison.OrdinalIgnoreCase)) {
                missingReason = $"its file {entry.FileName} does not match the server's copy";
                return null;
            }

            missingReason = null;
            return masterFilePath;
        }

        private List<MasterIndexEntry> ReadEntries() {
            if (!File.Exists(IndexFilePath)) {
                return new List<MasterIndexEntry>();
            }
            string json = File.ReadAllText(IndexFilePath);
            List<MasterIndexEntry>? entries = JsonConvert.DeserializeObject<List<MasterIndexEntry>>(json);
            return entries ?? new List<MasterIndexEntry>();
        }

        // Written beside the index and swapped in, so a crash mid-write leaves the old index intact.
        private void WriteEntries(List<MasterIndexEntry> entries) {
            string temporaryPath = IndexFilePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonConvert.SerializeObject(entries, Formatting.Indented));
            if (File.Exists(IndexFilePath)) {
                File.Replace(temporaryPath, IndexFilePath, destinationBackupFileName: null);
            } else {
                File.Move(temporaryPath, IndexFilePath);
            }
        }
    }
}
