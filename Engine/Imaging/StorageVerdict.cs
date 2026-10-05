namespace CosmicVaults.NINA.Astraeus.Engine.Imaging {
    internal enum StorageVerdict {
        /// <summary>There was headroom, or the server couldn't be asked, so send it.</summary>
        Upload,

        /// <summary>Full. The frame stays on this PC and imaging carries on.</summary>
        Skip,

        /// <summary>Full, and this account asks for the autopilot to stop when it is.</summary>
        Pause,
    }
}
