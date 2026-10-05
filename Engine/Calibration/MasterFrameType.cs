namespace CosmicVaults.NINA.Astraeus.Engine.Calibration {
    /// <summary>
    /// From a file's FITS/XISF IMAGETYP header. Darks and bias don't depend on the filter. Flats are
    /// per filter.
    /// </summary>
    public enum MasterFrameType {
        Dark,
        Bias,
        Flat,
        DarkFlat
    }
}
