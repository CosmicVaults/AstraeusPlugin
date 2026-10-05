namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot {
    public enum AutopilotStateResult {
        Completed,
        Failed,          // recoverable failure, and the table decides where to go
        Unsafe,          // injected by the safety watcher, never returned by ExecuteAsync
        LimitReached,    // injected by the mount-limit watcher, never returned by ExecuteAsync
        WindowClosed,    // injected by the daylight watcher when the observing window closes
        RoofClosed,      // injected by the safety watcher when the roof stops reading open mid-imaging
        Yes,
        No,
        Pass,            // a three-way decision's "doesn't apply" branch (see DomeControlSettingCheck)
        Disabled,        // a state ended the run, so the machine stops here
        Resumed,         // the cause of an abort cleared by itself, so imaging carries on
        UpdateRequired,  // the server refused this plugin version, so the night ends
    }
}