using NINA.Astrometry;
using NINA.Core.Enum;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    /// <summary>
    /// Decides whether the mount must flip, once before the slew and again before the exposure. The server
    /// can't see the side of pier, so the geometry is worked out here. A target past the limit stays past
    /// it all night, so a rule on hour angle alone would flip every frame. Side of pier is what tells
    /// "not yet flipped" from "flipped forty minutes ago".
    /// </summary>
    internal sealed class IsMeridianFlipRequired(string name, Context context,
        Func<AutopilotNextResponse?, TimeSpan> window)
        : DecisionState(name) {

        // What was last said, so an unchanged verdict isn't repeated at INFO every frame. The state
        // lives for one autopilot run, which is the lifetime the de-duplication needs.
        private string? _lastVerdictKey;

        protected override Task<bool> DecideAsync(CancellationToken cancellationToken) {
            AutopilotNextResponse? target = context.CurrentTarget;
            if (target?.Ra is not double ra || target.Dec is not double dec) return Task.FromResult(false);

            // Not a German equatorial, or one that won't report its side (Mount's westward test refuses
            // instead), so nothing to decide. Only worth a line when the server pinned a side, since
            // then it believes something about this observatory that isn't true.
            if (context.Mount is not { IsGermanEquatorial: true, IsPierSideKnown: true } mount) {
                if (context.RequiredPierSide is PierSide unusableSide) {
                    Announce($"{target.TargetName}|unusable-pin", isWarning: true,
                        $"Server requires {target.TargetName} on {unusableSide}, but this mount reports " +
                        $"alignment {context.Mount?.SideOfPier.ToString() ?? "unknown"} and no usable " +
                        "side of pier, so the pin cannot be honoured and is being ignored. The mount's " +
                        "own limit still applies.");
                }
                return Task.FromResult(false);
            }

            // When parked the side reported is the park position's. The mount won't flip from park
            // anyway, and the normal path unparks and slews to the driver's choice of side before the
            // pre-exposure check asks again.
            if (mount.IsParked) return Task.FromResult(false);

            Coordinates coordinates = new Coordinates(ra, dec, Epoch.J2000, Coordinates.RAType.Degrees);
            PierSide side = mount.SideOfPier;
            double hoursPastMeridian = mount.HoursPastMeridianFor(coordinates);
            bool willBreachOnCurrentSide = mount.WouldBreachMeridianOnCurrentSide(coordinates, window(target));

            // A pinned side settles it. The server uses it to keep a non-flipping project's frames
            // consistent, or to say the mount can't change side, and our geometry gets no vote.
            if (context.RequiredPierSide is PierSide requiredSide) {
                if (requiredSide != side) {
                    Verdict(target, side, hoursPastMeridian, $"pinned-{requiredSide}",
                        $"The server requires this frame on {requiredSide} and the mount is on {side}.");
                    return Task.FromResult(true);
                }

                // Already on the pinned side. The server guarantees it holds for the whole exposure, so
                // if our geometry disagrees the two sides have diverged and that's a fault. Returning
                // true lets RunMeridianFlip report it with a reason that reaches the project's History.
                if (willBreachOnCurrentSide) {
                    context.LogError(
                        $"Refusing {target.TargetName}: the server requires {requiredSide}, but the exposure " +
                        $"would cross this observatory's meridian limit there (hour angle " +
                        $"{hoursPastMeridian * 60:F1} min, limit {mount.DescribeMeridianLimit()}).");
                    return Task.FromResult(true);
                }
                return Task.FromResult(false);
            }

            // Not pinned, so ours to decide if the observer allows flips. With flips off the limit
            // refuses instead, and CheckTargetLimits explains why.
            if (!context.IsMeridianFlipEnabled) return Task.FromResult(false);

            if (!willBreachOnCurrentSide) {
                // The limit is far off, or the mount is already on the side this target needs (true
                // for the rest of the night once flipped). Only worth a line when the limit is in view.
                if (mount.SecondsToMeridianLimit(coordinates) is double secondsToLimit
                    && secondsToLimit <= window(target).TotalSeconds) {
                    Verdict(target, side, hoursPastMeridian, "settled",
                        "No flip, since the mount is already on the side of the pier this target needs.");
                }
                return Task.FromResult(false);
            }

            Verdict(target, side, hoursPastMeridian, "flipping",
                DescribeFlipReason(mount, coordinates, hoursPastMeridian, window(target)));
            return Task.FromResult(true);
        }

        private static string DescribeFlipReason(Mount mount, Coordinates coordinates,
            double hoursPastMeridian, TimeSpan lookAhead) {
            if (mount.WouldBreachMeridianOnCurrentSide(coordinates, TimeSpan.Zero)) {
                return hoursPastMeridian < 0
                    ? "Flipping because the target is east of the meridian, which this side of the pier cannot reach."
                    : "Flipping because the target is already past the limit for this side of the pier.";
            }
            return $"Flipping because staying on this side would cross the limit within the next " +
                   $"{lookAhead.TotalMinutes:F1} min (this frame and its setup margin).";
        }

        private void Verdict(AutopilotNextResponse target, PierSide side, double hoursPastMeridian,
            string verdict, string explanation) {
            string limit = context.Mount?.DescribeMeridianLimit() ?? "the meridian limit";
            string message =
                $"Meridian flip check for {target.TargetName} ({Name}): hour angle {hoursPastMeridian * 60:F1} min " +
                $"against a limit of {limit}, mount on {side}. {explanation}";

            // Keyed on target, side and verdict, not the wording, so drifting numbers don't count as a
            // new decision.
            Announce($"{target.TargetName}|{side}|{verdict}", isWarning: false, message);
        }

        private void Announce(string key, bool isWarning, string message) {
            if (key == _lastVerdictKey) {
                context.LogDebug(message);
                return;
            }
            _lastVerdictKey = key;
            if (isWarning) {
                context.LogWarning(message);
            } else {
                context.Log(message);
            }
        }
    }
}
