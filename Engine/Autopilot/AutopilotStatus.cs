namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot {
    public enum AutopilotStatus {
        Disabled,
        Idle,
        RunningStartup,
        RunningShutdown,
        WaitingForNight,
        Polling,
        WaitingSafety, 
        WaitingEquipment, 
        WaitingRoof, 
        OpeningRoof, 
        ClosingRoof, 
        Slewing, 
        PlateSolving, 
        Rotating,
        ChangingFilter,
        Focusing,
        Guiding,
        Dithering,
        Capturing,
        Reporting,
        Calibrating,
        Parking,
        OpeningFlatPanel,
        ClosingFlatPanel
    }
}