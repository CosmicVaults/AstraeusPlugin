using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Profile.Interfaces;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    /// <summary>
    /// Flips the mount through N.I.N.A.'s own workflow. Completed when the mount reads back on the side the
    /// frame needs, whatever N.I.N.A. reported. Failed refuses the frame, and since this is before /start/
    /// nothing needs un-reserving.
    /// </summary>
    internal sealed class RunMeridianFlip(string name, Context context) : StateBase(name) {
        protected internal override bool ShouldWatchSafety => true;

        // Not ShouldWatchMountLimits. The mount is at or near the meridian limit on the pre-flip side,
        // which is why we're flipping, so watching would abort the flip that fixes it.

        public override Task OnEntryAsync(CancellationToken cancellationToken) {
            // A flip is a slew and a settle, which the dashboard already shows. The log carries the detail.
            context.AutopilotStatus = AutopilotStatus.Slewing;
            return Task.CompletedTask;
        }

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {
            AutopilotNextResponse? target = context.CurrentTarget;
            if (!await context.EnsureConnectedAsync(context.Mount, cancellationToken)
                || context.Mount is not { } mount) {
                return await FailAsync("mount is not connected");
            }
            if (target?.Ra is not double ra || target.Dec is not double dec) {
                return await FailAsync("the assigned target carries no coordinates to flip to");
            }

            Coordinates coordinates = new Coordinates(ra, dec, Epoch.J2000, Coordinates.RAType.Degrees);
            PierSide sideBefore = mount.SideOfPier;

            PierSide wantedSide = context.RequiredPierSide ?? Opposite(sideBefore);

            if (wantedSide == sideBefore) {
                // Only reached when the server pinned the side the mount is already on and the check
                // still objected. The decision state logged it, and this turns it into a reason the
                // observer can read in the project's History.
                return await FailAsync(
                    $"the server requires this frame on {wantedSide} and the mount is already there, but " +
                    "the exposure would cross this observatory's meridian limit",
                    isDeferred: context.HasServerWindowExpired);
            }
            if (!context.IsMeridianFlipEnabled) {
                return await FailAsync(
                    $"the server requires this frame on {wantedSide} and the mount is on {sideBefore}, but " +
                    "Enable Meridian Flips is off, so the mount cannot be moved to it");
            }

            // One flip per frame. A second request means the exposure fits on neither side, a pinned side
            // the geometry disagrees with, or a driver that slewed back to the starting side after the
            // flip. Flipping again would walk the mount back and forth all night, so we refuse the frame.
            if (context.HasFlippedThisFrame) {
                return await FailAsync(
                    $"the mount already flipped for this frame and the check still wants it off {sideBefore}, " +
                    "so the frame is refused rather than flipped back",
                    isDeferred: context.HasServerWindowExpired);
            }

            // Wall clock over the whole flip, since that's what an observer matches against a gap in the
            // night's frames.
            Stopwatch elapsed = Stopwatch.StartNew();

            // Waits for N.I.N.A.'s minutes-after-meridian, flips, and reads the pier side back.
            MeridianFlipOutcome outcome = await mount.FlipAsync(coordinates, wantedSide, cancellationToken);
            if (!outcome.HasReachedTargetSide) {
                return await FailAsync(outcome.FailReason ?? $"mount did not reach {wantedSide}",
                    isDeferred: outcome.IsDeferred);
            }
            bool isReportedComplete = outcome.IsReportedComplete;

            // The mount is on the side it was asked for, so the flip happened. What N.I.N.A. managed
            // afterwards doesn't decide this frame's fate.
            context.HasFlippedThisFrame = true;
            IMeridianFlipSettings meridianFlipSettings =
                context.Observatory.Settings.ProfileService.ActiveProfile.MeridianFlipSettings;
            // N.I.N.A.'s own re-centre stands, unless it failed or a sky angle needs the rotator re-synced.
            bool needsRotatorResync = context.Rotator is { IsConnected: true } && context.RequestedSkyAngle() != null;
            bool doesNinaRecenterStand = isReportedComplete && meridianFlipSettings.Recenter && !needsRotatorResync;
            context.IsRecenterRequired = !doesNinaRecenterStand;
            if (isReportedComplete && meridianFlipSettings.AutoFocusAfterFlip) context.RecordAutofocus();

            if (!isReportedComplete) {
                // A warning, since the frame isn't what went wrong. The usual cause is a plate solver never
                // set up in the N.I.N.A. profile, so the message says where to look.
                context.LogWarning(
                    $"N.I.N.A. reported the meridian flip for {target.TargetName} as failed, but the mount " +
                    $"is on {mount.SideOfPier} as asked; N.I.N.A.'s log names the step that failed, usually " +
                    "the re-centre plate solve. The frame is kept and re-centred before the exposure.");
            }

            // One line with everything needed to reconstruct the flip. It goes to N.I.N.A.'s log and the
            // dashboard but not ReportEvent, the email channel, since flips are routine and mailing each
            // one would teach observers to ignore it.
            string flipResult = isReportedComplete ? "complete" : "confirmed by the pier side alone";
            string recenterNote = context.IsRecenterRequired
                ? "; re-centring before the frame."
                : "; N.I.N.A.'s re-centre stands.";
            context.Log(
                $"Meridian flip {flipResult} " +
                $"for {target.TargetName} in {Context.FormatElapsed(elapsed)}: " +
                $"{sideBefore} -> {mount.SideOfPier}, hour angle now " +
                $"{mount.HoursPastMeridianFor(coordinates) * 60:F1} min against a limit of " +
                $"{mount.DescribeMeridianLimit()}" +
                recenterNote);
            return AutopilotStateResult.Completed;
        }

        private static PierSide Opposite(PierSide side)
            => side == PierSide.pierWest ? PierSide.pierEast : PierSide.pierWest;

        /// <summary>The reason is shown verbatim in the project's History, so word it for there too.</summary>
        private async Task<AutopilotStateResult> FailAsync(string reason, bool isDeferred = false) {
            string message = $"Meridian flip failed: {reason}";
            AutopilotNextResponse? target = context.CurrentTarget;
            context.LogError(
                $"{message}, so not taking the frame on {target?.TargetName ?? "the current target"}.");
            context.NextPollDelaySeconds = Context.DefaultRetryDelaySeconds;

            if (target?.ImageId is int imageId) {
                // No client_ref, since /start/ was never called for this image. PostAutopilotFailAsync
                // swallows its own errors.
                await context.Observatory.AstraeusWebClient.PostAutopilotFailAsync(imageId, message, null, isDeferred);
            }

            // So the dashboard stops showing a target this frame was abandoned on.
            context.CurrentTarget = null;

            // Check the full limit, meridian included, right away. A flip that failed after passing the
            // meridian leaves the mount tracking into the limit, and nothing watches for that before the
            // next slew. CurrentLimitBreach would waive the meridian because a flip is coming, and with a
            // null target it would say a flip is available and hide the breach.
            if (context.Mount?.DescribeCurrentPositionBreach(shouldIncludeMeridian: true) is string breach) {
                context.LogError(
                    $"Mount is {breach} after the failed flip, so stopping tracking. The roof is left as " +
                    "it is and the autopilot keeps polling for other targets.");
                if (context.Mount is { } mountToStop && !await mountToStop.StopMovementAsync()) {
                    context.LogWarning("Could not stop tracking. Check the mount.");
                }
            }
            return AutopilotStateResult.Failed;
        }
    }
}
