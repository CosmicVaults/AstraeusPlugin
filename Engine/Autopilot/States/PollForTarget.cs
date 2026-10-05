using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    internal sealed class PollForTarget(string name, Context context) : StateBase(name) {
        protected internal override bool ShouldWatchSafety => true;
        // The mount is still tracking the last target while the next one is asked for.
        protected internal override bool ShouldWatchMountLimits => true;

        // The empty answer already logged in this stretch of polling, or null. The dashboard card shows
        // the newest autopilot line, so saying it once covers the whole wait.
        private string? _loggedReason;
        // Stands in for a reason while the server cannot be reached, so an outage is said once too.
        private const string Unreachable = "(unreachable)";

        public override Task OnEntryAsync(CancellationToken cancellationToken) {
            // The retry loop back here (stop the mount, wait, poll) never changes the status. Any other
            // way in sets its own status and logs its own lines, so the next empty answer is news again.
            if (context.AutopilotStatus != AutopilotStatus.Polling) _loggedReason = null;
            context.CurrentTarget = null;
            context.IsCurrentTargetStarted = false;
            context.CurrentClientRef = null;
            context.FrameWindowEndsAtUtc = null;
            // These belong to the frame in hand, not the mount. A dispatch aborted before its exposure
            // (say between the flip and the slew) mustn't make the next one re-centre or carry a spent
            // flip into it.
            context.IsRecenterRequired = false;
            context.HasFlippedThisFrame = false;
            context.HasFilterChangedThisFrame = false;
            // Clear the camera's project/target image-pattern values between polls so an idle-time
            // or manual capture doesn't inherit the previous target's name.
            if (context.Camera != null) {
                context.Camera.CurrentProjectName = null;
                context.Camera.CurrentTargetName = null;
            }
            context.AutopilotStatus = AutopilotStatus.Polling;
            return Task.CompletedTask;
        }

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {
            string? currentFilter = context.FilterWheel?.GetCurrentFilter()?.Name;
            AutopilotNextResponse? next = await context.Observatory.AstraeusWebClient.GetAutopilotNextAsync(
                currentFilter, context.SideOfPierForDispatch, context.TrackingProjectIdForDispatch,
                context.TelescopePositionForDispatch);
            if (next == null && context.Observatory.IsUpdateRequired) {
                context.LogError("The server no longer supports this version of Astraeus, so the autopilot is " +
                                 "securing the observatory and switching off.");
                return AutopilotStateResult.UpdateRequired;
            }
            if (next == null) {
                context.NextPollDelaySeconds = Context.DefaultRetryDelaySeconds;
                if (_loggedReason != Unreachable) {
                    _loggedReason = Unreachable;
                    context.LogWarning("Unable to reach server for new target. " +
                                       $"Retrying every {Context.DefaultRetryDelaySeconds} s.");
                }
                return AutopilotStateResult.Failed;
            }
            if (next.ImageId == null) {
                context.NextPollDelaySeconds = next.RetryAfter ?? Context.DefaultRetryDelaySeconds;
                string reason = next.Reason ?? "no reason given";
                if (reason != _loggedReason) {
                    _loggedReason = reason;
                    context.Log(DescribeNoWork(reason));
                }
                return AutopilotStateResult.Failed;
            }

            _loggedReason = null;
            context.CurrentTarget = next;
            context.TargetAssignedAtUtc = System.DateTime.UtcNow;
            // The last "nothing ready" RetryAfter belongs to that answer. Left standing, a later "try
            // again shortly" edge (a failed /start/, say) would sleep for it.
            context.NextPollDelaySeconds = Context.DefaultRetryDelaySeconds;
            if (next.RequiredPierSide is string requiredSide && context.RequiredPierSide is null) {
                context.LogWarning($"Server sent required_pier_side \"{requiredSide}\", " +
                                   "which is not pierEast or pierWest, so ignoring it.");
            }
            // Put the project and target on the camera so the $$...PROJECTNAME$$ and $$...TARGETNAME$$
            // file-name patterns resolve for the frames about to be captured.
            if (context.Camera != null) {
                context.Camera.CurrentProjectName = next.ProjectName;
                context.Camera.CurrentTargetName = next.TargetName;
            }
            // The server counts delivered frames, so this is the clearest sign in the log of how much of
            // a project is done. A cadence run fills its window and has no total.
            int frameNumber = (next.CapturesComplete ?? 0) + 1;
            string progress = next.CapturesTotal is int total
                ? $"frame {frameNumber} of {total}"
                : $"frame {frameNumber}";
            string filter = next.FilterName is { Length: > 0 } filterName ? $", {filterName}" : "";
            context.Log($"Next: {next.TargetName}, {next.Exposure}s{filter} ({progress}).");
            return AutopilotStateResult.Completed;
        }

        // The server's no-work reasons in plain words. An unknown one is shown raw, so a newer server
        // still says why.
        private static string DescribeNoWork(string reason) => reason switch {
            "no_visible_targets" => "No visible targets. Waiting...",
            "wrong_pier_side" => "No targets reachable on this side of the pier. Waiting...",
            "no_pending_requests" => "Nothing scheduled to image right now. Waiting...",
            "interval_not_elapsed" => "Waiting for a repeat interval to elapse...",
            "fixed_time_imminent" => "Waiting for a fixed-time window to open...",
            "plan_required" => "Autopilot is not part of the current plan. Waiting...",
            "setup_incomplete" => "Autopilot setup is not finished. Waiting...",
            "location_not_configured" => "Observatory location is not set. Waiting...",
            _ => $"No image ready ({reason}). Waiting..."
        };
    }
}