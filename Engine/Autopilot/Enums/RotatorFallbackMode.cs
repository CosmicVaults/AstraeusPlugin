namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot {
    /// <summary>
    /// What the autopilot does with a connected rotator when the assigned target carries no
    /// position angle of its own.
    /// </summary>
    public enum RotatorFallbackMode {
        None,
        SkyPositionAngle,
        MechanicalAngle
    }
}