namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot {
    /// <summary>Serialised by name, and the web app stores those names.</summary>
    public enum FlatPanelMode {
        /// <summary>Leave the flat panel alone. It isn't even connected at dusk.</summary>
        Ignore,
        /// <summary>Whenever the roof opens, make sure the light is off and the cover open. Nothing at dawn.</summary>
        EnsureOpen,
        /// <summary>
        /// Like EnsureOpen, and also close the cover when the night ends, at dawn or on a fault stop. A
        /// bad-weather close leaves it open so the night can resume.
        /// </summary>
        OpenAndClose
    }
}
