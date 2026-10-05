namespace CosmicVaults.NINA.Astraeus.Engine.Imaging {
    internal enum UploadOutcome {
        Confirmed,

        /// <summary>
        /// Something along the way failed, but nothing says the account can't upload, so the frame is
        /// worth another try.
        /// </summary>
        Failed,

        /// <summary>
        /// The bytes are in R2 but the server never confirmed them. Only the confirm needs retrying,
        /// through CaptureUploader.RetryConfirmAsync, not the transfer.
        /// </summary>
        ConfirmFailed,

        /// <summary>
        /// The account is over quota. A retry would take three more round trips to hear the same
        /// thing, so the queue drops the frame at once.
        /// </summary>
        StorageFull,
    }
}
