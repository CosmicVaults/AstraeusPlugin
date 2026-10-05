using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot;
using CosmicVaults.NINA.Astraeus.Engine.Calibration;
using CosmicVaults.NINA.Astraeus.Engine.Utility;

namespace CosmicVaults.NINA.Astraeus.Engine.Imaging {
    /// <summary>
    /// Uploads a captured frame (the calibrated copy, the raw, or both) and its preview to R2, then
    /// links them to their CapturedImage with the calibration report. Only CaptureUploadQueue calls
    /// this. Never throws except on cancellation.
    /// </summary>
    internal sealed class CaptureUploader(Observatory observatory, string logDevice, LogCategory logCategory) {
        // The confirm has to happen, or the server fails the upload after ~30 minutes. Retrying it is
        // cheap and idempotent since the bytes are already in R2. PUTs aren't retried here because the
        // queue restarts the chain. The exception is the calibrated frame, since a failed PUT there
        // swaps the raw in for good, so it gets one more try.
        private const int MaxConfirmAttempts = 3;
        private static readonly TimeSpan CalibratedPutRetryDelay = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ConfirmRetryBackoffStep = TimeSpan.FromSeconds(5);

        // What the last upload put in R2, kept while it is unconfirmed so a retry can confirm it
        // without sending the bytes again.
        private PendingConfirm? _pendingConfirm;

        /// <summary>
        /// Failed is worth another try, ConfirmFailed only needs RetryConfirmAsync, and StorageFull isn't
        /// worth retrying. The confirm call ignores cancellation, since it's quick and finishing it saves a
        /// re-upload.
        /// </summary>
        public async Task<UploadOutcome> UploadAsync(PendingUpload upload, string r2Filename,
            CancellationToken cancellationToken) {
            _pendingConfirm = null;
            AstraeusWebClient client = observatory.AstraeusWebClient;
            int captureId = upload.CaptureId;
            // The observer knows a frame by its file, never by the server's capture id.
            string frameName = Path.GetFileName(upload.FilePath);
            CalibrationProvenance? provenance = upload.Provenance;

            // 1) The calibrated frame, when CalibrateFrame produced one.
            string? calibratedKey = null;
            if (upload.CalibratedFilePath is { } calibratedPath && File.Exists(calibratedPath)) {
                string calibratedName = ObjectKey.ReplaceFileName(r2Filename, Path.GetFileName(calibratedPath));
                (string? key, bool isStorageFull) = await PutFileAsync(captureId, frameName, calibratedName,
                    calibratedPath, "calibrated copy", cancellationToken);
                if (isStorageFull) return ReportStorageFull(frameName);
                if (key is null) {
                    LogWarning($"Retrying the upload of {frameName}'s calibrated copy once.");
                    await Task.Delay(CalibratedPutRetryDelay, cancellationToken);
                    (key, isStorageFull) = await PutFileAsync(captureId, frameName, calibratedName,
                        calibratedPath, "calibrated copy", cancellationToken);
                    if (isStorageFull) return ReportStorageFull(frameName);
                }
                calibratedKey = key;
                // The report must describe what is in R2, not what happened on disk.
                if (calibratedKey is null && provenance is { Status: "completed" })
                    provenance = provenance.AsFailed("The calibrated frame could not be uploaded.");
            }

            // 2) The raw, when kept, or as a stand-in when the calibrated copy didn't make it.
            string? rawKey = null;
            if (upload.ShouldKeepRaw || calibratedKey is null) {
                if (!upload.ShouldKeepRaw)
                    LogWarning($"Uploading {frameName} uncalibrated: there is no calibrated copy to send.");
                (string? key, bool isStorageFull) = await PutFileAsync(captureId, frameName, r2Filename,
                    upload.FilePath, "frame", cancellationToken);
                if (isStorageFull) return ReportStorageFull(frameName);
                rawKey = key;
            }
            if (rawKey is null && calibratedKey is null) {
                LogWarning($"{frameName} did not reach cloud storage; the upload is abandoned.");
                return UploadOutcome.Failed;
            }

            // 3) Preview thumbnail beside it, best effort, same key with a .jpg extension. A frame is
            //    already up by now, so if storage is full the card just goes without a thumbnail.
            string? previewKey = null;
            if (upload.PreviewJpeg is { Length: > 0 } previewBytes) {
                string previewFilename = ObjectKey.WithExtension(r2Filename, ".jpg");
                (string? UploadUrl, string? R2Key, bool IsStorageFull) preview =
                    await client.PostR2UploadUrlAsync(captureId, previewFilename, cancellationToken);
                if (preview.UploadUrl is { } previewUrl && preview.R2Key is { } previewR2Key
                    && await client.PutR2BytesAsync(previewUrl, previewBytes, "image/jpeg", cancellationToken))
                    previewKey = previewR2Key;
                else
                    LogWarning($"The preview of {frameName} did not upload; its library card will have no " +
                               "thumbnail.");
            }

            // 4) Link what made it up. This has to happen once bytes are in R2, hence the retry.
            string linked = string.Join(", ", new[] {
                rawKey != null ? "frame" : null,
                calibratedKey != null ? "calibrated frame" : null,
                previewKey != null ? "preview" : null,
            }.Where(part => part != null));
            PendingConfirm pendingConfirm = new PendingConfirm(captureId, frameName, rawKey, calibratedKey,
                previewKey, provenance, linked);
            _pendingConfirm = pendingConfirm;
            return await ConfirmAsync(pendingConfirm, cancellationToken);
        }

        /// <summary>
        /// Confirms again what the last UploadAsync put in R2, after it returned ConfirmFailed. Nothing
        /// is uploaded again.
        /// </summary>
        public Task<UploadOutcome> RetryConfirmAsync(CancellationToken cancellationToken) {
            if (_pendingConfirm is not { } pendingConfirm) return Task.FromResult(UploadOutcome.Failed);
            return ConfirmAsync(pendingConfirm, cancellationToken);
        }

        /// <summary>
        /// Returns ConfirmFailed if the server never took it, and the caller decides whether to try again.
        /// </summary>
        private async Task<UploadOutcome> ConfirmAsync(PendingConfirm confirm, CancellationToken cancellationToken) {
            AstraeusWebClient client = observatory.AstraeusWebClient;
            for (int attempt = 1; attempt <= MaxConfirmAttempts; attempt++) {
                bool isConfirmed = await client.PostUploadCompleteAsync(confirm.CaptureId, confirm.RawKey,
                    confirm.CalibratedKey, confirm.PreviewKey, confirm.Provenance);
                if (isConfirmed) {
                    // Silent on success: the library tile is the observer's confirmation.
                    LogDebug($"Linked R2 objects to capture {confirm.CaptureId} ({confirm.Linked}).");
                    _pendingConfirm = null;
                    return UploadOutcome.Confirmed;
                }
                if (attempt == MaxConfirmAttempts) break;
                LogWarning($"{confirm.FrameName} is uploaded but not yet in your image library " +
                           $"(attempt {attempt} of {MaxConfirmAttempts}); retrying.");
                try {
                    await Task.Delay(ConfirmRetryBackoffStep * attempt, cancellationToken);
                } catch (OperationCanceledException) {
                    return UploadOutcome.ConfirmFailed;
                }
            }
            return UploadOutcome.ConfirmFailed;
        }

        /// <summary>
        /// Presigns and PUTs a file and returns its object key, or null with the reason logged.
        /// IsStorageFull means the presign got a 507, so the caller drops the whole upload since every
        /// other object would be refused too.
        /// </summary>
        private async Task<(string? Key, bool IsStorageFull)> PutFileAsync(int captureId, string frameName,
            string r2Filename, string filePath, string objectDescription, CancellationToken cancellationToken) {
            AstraeusWebClient client = observatory.AstraeusWebClient;
            string subject = objectDescription == "frame" ? frameName : $"{frameName}'s {objectDescription}";
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) {
                LogWarning($"{subject} is not on disk, so it was not uploaded.");
                return (null, false);
            }
            (string? UploadUrl, string? R2Key, bool IsStorageFull) target =
                await client.PostR2UploadUrlAsync(captureId, r2Filename, cancellationToken);
            if (target.IsStorageFull) return (null, true);
            if (target.UploadUrl is not { } uploadUrl || target.R2Key is not { } r2Key) {
                LogWarning($"The server gave {subject} no upload slot.");
                return (null, false);
            }
            if (!await client.PutR2FileAsync(uploadUrl, filePath, cancellationToken)) {
                LogWarning($"{subject} did not reach cloud storage.");
                return (null, false);
            }
            return (r2Key, false);
        }

        /// <summary>
        /// For a presign refused because the account is over quota. Nothing is confirmed, so the frame stays
        /// on disk and the server's own upload timeout re-offers the exposure.
        /// </summary>
        private UploadOutcome ReportStorageFull(string frameName) {
            LogWarning($"{frameName} was not uploaded: your cloud storage is full. The frame stays on this " +
                       "machine and the server will re-offer the exposure.");
            return UploadOutcome.StorageFull;
        }

        private void LogDebug(string message) => observatory.LogDebug(message, logDevice, logCategory);
        private void LogWarning(string message) => observatory.LogWarning(message, logDevice, logCategory);

        private sealed record PendingConfirm(int CaptureId, string FrameName, string? RawKey,
            string? CalibratedKey, string? PreviewKey, CalibrationProvenance? Provenance, string Linked);
    }
}
