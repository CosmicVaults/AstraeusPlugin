using System;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot
{
    /// <summary>
    /// Bit flags that specify which abort conditions apply to the particular state.
    /// </summary>
    [Flags]
    internal enum Aborts
    {
        None = 0,
        Unsafe = 1,         // the safety monitor stops reading Safe
        RoofClosed = 2,     // a roof we are imaging under stops reading Open
        LimitReached = 4,   // the mount crosses a pointing or tracking limit, or its position cannot be read
        WindowClosed = 8,   // the observing window closes (dawn)
        All = Unsafe | RoofClosed | LimitReached | WindowClosed,
    }
}
