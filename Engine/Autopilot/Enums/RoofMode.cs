namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot {
    public enum RoofMode {
        /// <summary>Act as if there's no roof. Never connect to, open, close or wait on the dome.</summary>
        Ignore,
        /// <summary>Don't operate the roof. Wait until it's open before imaging.</summary>
        WaitForOpen,
        /// <summary>Connect to the dome and open/close the roof automatically.</summary>
        Operate
    }
}
