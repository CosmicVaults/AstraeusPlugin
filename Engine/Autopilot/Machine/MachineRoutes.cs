namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot
{
    /// <summary>
    /// Where a run starts, where a watcher's result goes when the state has no edge of its own, and
    /// the two exits the machine runs outside the loop. The wiring builds it, so the machine can't
    /// start without every route named.
    /// </summary>
    internal readonly record struct MachineRoutes(
        StateBase Entry,
        TransitionsTable Defaults,
        StateBase SecureForFault,
        StateBase StopOnSwitchOff);
}