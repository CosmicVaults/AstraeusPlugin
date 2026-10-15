using System;
using NINA.Astrometry.Body;
using NINA.Astrometry.RiseAndSet;

namespace CosmicVaults.NINA.Astraeus.Tests.Helpers {
    /// <summary>A set and rise the test chose, in place of the one N.I.N.A. calculates from the sun.</summary>
    internal sealed class FixedRiseAndSet(DateTime? set, DateTime? rise) : RiseAndSetEvent(DateTime.MinValue, 0, 0) {
        public override DateTime? Set => set;
        public override DateTime? Rise => rise;

        protected override double AdjustAltitude(BasicBody body) => throw new NotSupportedException();
        protected override BasicBody GetBody(DateTime date) => throw new NotSupportedException();
    }
}