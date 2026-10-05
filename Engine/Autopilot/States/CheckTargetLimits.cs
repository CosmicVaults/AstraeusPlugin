using NINA.Astrometry;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    /// <summary>
    /// Checks the target is inside the mount limits now and over its whole window, before the slew and
    /// again before the frame. The server applies the same rules, so a refusal here only happens when the
    /// two sides disagree.
    /// </summary>
    internal sealed class CheckTargetLimits(string name, Context context, Func<AutopilotNextResponse?, TimeSpan> window)
        : StateBase(name) {
        // A refusal is reported to the server, and the roof is open for that round trip.
        protected internal override bool ShouldWatchSafety => true;

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {
            if (!context.IsMountLimitEnabled || context.Mount == null) {
                return AutopilotStateResult.Yes;
            }

            AutopilotNextResponse? target = context.CurrentTarget;
            if (target?.Ra is not double ra || target.Dec is not double dec) {
                context.Log("No coordinates provided, so skipping limit check.");
                return AutopilotStateResult.Yes;
            }

            Coordinates coordinates = new Coordinates(ra, dec, Epoch.J2000, Coordinates.RAType.Degrees);

            // Shared with the flip decision so both look over the same window. The meridian is left out
            // when a flip is available, since refusing here would skip the target before the flip ran.
            TimeSpan lookAhead = window(target);
            string? breach = context.Mount.DescribeTrackingBreach(
                coordinates, lookAhead, shouldIncludeMeridian: !context.CanFlipForCurrentTarget);
            if (breach == null) {
                return AutopilotStateResult.Yes;
            }

            // Past the server's margin the target ran into the limit while setup went on, so nothing is
            // misconfigured and the server schedules it again.
            if (context.HasServerWindowExpired) {
                double setupMinutes = (DateTime.UtcNow - context.TargetAssignedAtUtc).TotalMinutes;
                context.LogWarning($"Skipping {target.TargetName}: {breach}. Setup took {setupMinutes:F1} min " +
                                   "since the server assigned it, more than the " +
                                   $"{Context.LimitLookAheadMarginSeconds / 60} min it allows, so the server will " +
                                   "schedule it again.");
            } else {
                string label = target.ImageId is int imageId
                    ? $"{target.TargetName} (image {imageId})"
                    : $"{target.TargetName}";
                context.LogError($"Can't slew to {label}: {breach}, so skipping. Note: " +
                                 ExplainRefusal(coordinates, lookAhead));
            }
            context.NextPollDelaySeconds = Context.DefaultRetryDelaySeconds;

            // Reported as a failed frame so the requests table shows why. Past the server's setup
            // margin it's deferred instead, as a timing miss for the server to re-judge.
            if (target.ImageId is int failedImageId) {
                await context.Observatory.AstraeusWebClient.PostAutopilotFailAsync(
                    failedImageId, $"Can't slew to {target.TargetName}: {breach}", null,
                    isDeferred: context.HasServerWindowExpired);
            }
            return AutopilotStateResult.No;
        }

        /// <summary>
        /// Tells whoever reads the log at 2am where to look. Only the meridian has a local fix. A mount that
        /// never reports a side of pier looks just like wrong site coordinates, which sends them the wrong way.
        /// </summary>
        private string ExplainRefusal(Coordinates coordinates, TimeSpan window) {
            const string elsewhere = "the server applies the same rules, so check the site coordinates " +
                                     "and horizon sync.";
            // If altitude or horizon broke, no flip would help and the meridian isn't worth mentioning.
            // Re-running the look-ahead without the meridian tells us which it was.
            Mount mount = context.Mount!;
            if (mount.DescribeTrackingBreach(coordinates, window, shouldIncludeMeridian: false) != null) {
                return elsewhere;
            }
            if (!context.IsMeridianFlipEnabled) {
                return "a meridian flip would clear this, but meridian flips are off (Astraeus Settings → " +
                       "Meridian, or the plugin's Autopilot options).";
            }
            if (!mount.IsPierSideKnown) {
                return "a meridian flip would clear this, but the mount reports its side of pier as " +
                       $"{mount.SideOfPier}. Check the driver's side-of-pier support.";
            }
            return elsewhere;
        }
    }
}
