namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot {
    public enum RoofMode {
        /// <summary>
        /// Shown as "No roof". Act as if there's no roof: never connect to, open, close, wait on or check the
        /// dome, so the mount unparks, slews and parks whatever any roof is doing. Only for a pier with
        /// nothing overhead. The name stays Ignore, since it's stored and sent to the server by name.
        /// </summary>
        Ignore,
        /// <summary>Don't operate the roof. Wait until it's open before imaging.</summary>
        WaitForOpen,
        /// <summary>Connect to the dome and open/close the roof automatically.</summary>
        Operate
    }
}
