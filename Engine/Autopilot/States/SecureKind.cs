namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States
{
    /// <summary>
    /// Why a secure is running, which decides what it does around the park and the close. Each instance
    /// in AutopilotStates names its kind, so the wiring reads as a sentence instead of a pair of bools.
    /// </summary>
    internal enum SecureKind
    {
        /// <summary>
        /// Unsafe conditions, or a roof that isn't ours closing. The night may resume, so the flat panel
        /// is left alone and no end reason is recorded.
        /// </summary>
        Emergency,

        /// <summary>
        /// The observing window has closed. The flat panel is closed if the mode says so, and no reason is
        /// recorded, since a routine end is not a fault.
        /// </summary>
        EndOfNight,

        /// <summary>
        /// The night is ending badly. The flat panel is closed if the mode says so, and the reason is
        /// recorded for the session-end email.
        /// </summary>
        Fault,
    }
}
