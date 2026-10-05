using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.PlateSolving;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    /// <summary>
    /// A rotator only knows its mechanical angle, so for a sky angle we plate solve, sync the rotator and
    /// move until inside tolerance. Runs before CenterOnTarget, as N.I.N.A. does, since rotating shifts
    /// the field.
    /// </summary>
    internal sealed class RotateToTarget(string name, Context context) : StateBase(name) {
        protected internal override bool ShouldWatchSafety => true;
        protected internal override bool ShouldWatchMountLimits => true;

        /// <summary>Solve and rotate attempts before giving up, the same limit N.I.N.A. uses.</summary>
        private const int MaxAttempts = 10;
        private const double RangeAdjustmentLogThresholdDegrees = 0.1;

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {
            await context.EnsureConnectedAsync(context.Rotator, cancellationToken);
            if (context.Rotator is not { IsConnected: true } rotator) {
                context.Log("No rotator connected, so skipping rotation.");
                return AutopilotStateResult.Completed;
            }

            if (context.RequestedMechanicalAngle() is double mechanical) {
                context.AutopilotStatus = AutopilotStatus.Rotating;
                context.Log($"Rotating to mechanical angle {mechanical:F2}°.");
                try {
                    await rotator.MoveMechanicalAsync((float)mechanical, cancellationToken);
                } catch (OperationCanceledException) {
                    throw;
                } catch (Exception ex) {
                    context.LogError($"Rotator move to mechanical {mechanical:F2}° failed: {ex.Message}");
                    return AutopilotStateResult.Failed;
                }
                return AutopilotStateResult.Completed;
            }

            if (context.RequestedSkyAngle() is not double requested) {
                context.Log("No position angle specified, so leaving the rotator where it is.");
                return AutopilotStateResult.Completed;
            }

            if (context.Mount == null) {
                context.LogError("Mount component not available, so cannot solve for rotation.");
                return AutopilotStateResult.Failed;
            }

            context.AutopilotStatus = AutopilotStatus.Rotating;
            context.Log($"Rotating to sky position angle {requested:F2}°.");

            RotatorRangeTypeEnum rangeType =
                context.Observatory.Settings.ProfileService.ActiveProfile.RotatorSettings.RangeType;
            double tolerance = context.RotationToleranceDegrees;

            float targetRotation = (float)requested;
            float rotationDistance = float.MaxValue;
            int attempts = 0;

            try {
                do {
                    PlateSolveResult? solve = await context.Mount.CaptureAndSolveAsync(cancellationToken);
                    if (solve?.Success != true) {
                        context.LogError("Plate solve failed, so cannot determine the current rotation.");
                        return AutopilotStateResult.Failed;
                    }

                    // The solved angle is the camera's sky orientation. Syncing it gives the rotator the
                    // mechanical-to-sky offset it can't know otherwise.
                    float orientation = (float)solve.PositionAngle;
                    rotator.SyncToSkyAngle(orientation);

                    // Clamp the request into the mechanical range the profile allows (FULL/HALF/QUARTER).
                    float previousTarget = targetRotation;
                    targetRotation = rotator.GetTargetPosition(previousTarget);
                    if (Math.Abs(targetRotation - previousTarget) > RangeAdjustmentLogThresholdDegrees) {
                        context.Log($"Target angle {previousTarget:F2}° adjusted to {targetRotation:F2}° " +
                                    "to stay within the rotator's allowed mechanical range.");
                    }

                    rotationDistance = targetRotation - orientation;

                    if (rangeType == RotatorRangeTypeEnum.FULL) {
                        (targetRotation, rotationDistance) = ChooseShorterMove(targetRotation, rotationDistance);
                    }

                    if (IsWithinTolerance(rotationDistance, tolerance)) break;

                    context.Log($"Rotator at {orientation:F2}°, target {targetRotation:F2}°. " +
                                $"Moving {rotationDistance:F2}° (tolerance {tolerance:F2}°).");
                    await rotator.MoveRelativeAsync(rotationDistance, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();

                    if (++attempts >= MaxAttempts) {
                        context.LogError($"Rotator did not reach {requested:F2}° after {MaxAttempts} attempts.");
                        return AutopilotStateResult.Failed;
                    }
                } while (true);
            } catch (OperationCanceledException) {
                throw; // a watcher (safety, mount limit or daylight) aborted, so let the machine handle it
            } catch (Exception ex) {
                // The capture solver throws on failure, and GetTargetPosition throws when the rotator is
                // unsynced. Treat both as recoverable.
                context.LogError($"Rotation to {requested:F2}° threw: {ex.Message}");
                return AutopilotStateResult.Failed;
            }

            context.Log($"Rotator on target at {requested:F2}° (within {tolerance:F2}°).");
            return AutopilotStateResult.Completed;
        }

        // A frame rotated 180° is the same framing, so take whichever is the shorter move.
        private (float TargetRotation, float RotationDistance) ChooseShorterMove(float targetRotation,
            float rotationDistance) {
            float movement = AstroUtil.EuclidianModulus(rotationDistance, 180);
            float oppositeMovement = movement - 180;
            if (movement < Math.Abs(oppositeMovement)) {
                return (targetRotation, movement);
            }
            float equivalentTarget = (float)AstroUtil.EuclidianModulus(targetRotation + 180, 360);
            context.LogDebug($"Using the 180°-equivalent target {equivalentTarget:F2}° (shorter move).");
            return (equivalentTarget, oppositeMovement);
        }

        /// <summary>N.I.N.A.'s own tolerance test.</summary>
        private static bool IsWithinTolerance(double distanceDegrees, double toleranceDegrees) =>
            Angle.ByDegree(distanceDegrees).Equals(Angle.Zero, Angle.ByDegree(toleranceDegrees), true);
    }
}