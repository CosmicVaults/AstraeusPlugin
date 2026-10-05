using System;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot {
    /// <summary>
    /// One filter's entry from /api/autofocus/status/.
    /// A query for one current_filter gets back just this single entry.
    /// </summary>
    public class AutofocusStatusResponse {
        public string?   Filter                 { get; set; }
        public bool      NeedsAutofocus         { get; set; }
        public string?   Reason                 { get; set; }
        public int?      StartingPosition       { get; set; }
        public string?   StartingPositionSource { get; set; }
    }
}
