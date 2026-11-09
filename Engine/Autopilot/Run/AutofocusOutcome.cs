using System;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot
{
    internal sealed record AutofocusOutcome(DateTime AtUtc, bool IsSuccess, int? Position);
}
