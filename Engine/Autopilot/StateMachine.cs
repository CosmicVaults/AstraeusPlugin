using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot {
    internal sealed class StateMachine(Context context) {

        /// <summary>
        /// Where the machine goes when a watcher aborts a state, or a state faults. No state returns
        /// these results itself, so the machine owns their routing and can't start without them.
        /// </summary>
        internal readonly record struct AbortRoutes(StateBase Unsafe, StateBase LimitReached, StateBase WindowClosed,
            StateBase RoofClosed, StateBase Fault);

        /// <summary>
        /// A backstop for the exit secure. SecureObservatory times itself out on Context.SecureTimeout and
        /// reports it, so this sits a minute past that and should never be the one to fire.
        /// </summary>
        private static readonly TimeSpan SecureOnExitTimeout = Context.SecureTimeout + TimeSpan.FromMinutes(1);

        /// <summary>
        /// Runs the machine until the token is cancelled (autopilot or plugin disabled, or N.I.N.A.
        /// closing) or a state faults.
        /// </summary>
        public async Task RunAsync(StateBase initialState, AbortRoutes abort,
            CancellationToken cancellationToken) {
            StateBase state = initialState;

            while (!cancellationToken.IsCancellationRequested) {
                try {
                    await state.OnEntryAsync(cancellationToken);
                    AutopilotStateResult result;
                    try {
                        result = await ExecuteWithWatchAsync(state, cancellationToken);
                    } finally {
                        await state.OnExitAsync();
                    }
                    // Disabled is the wired shutdown path and has already secured on the way here.
                    if (result == AutopilotStateResult.Disabled) return;
                    state = Next(state, result, abort);
                } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                    await StopImagingOnExitAsync();
                    return;
                } catch (Exception ex) when (cancellationToken.IsCancellationRequested) {
                    // A driver throwing while a manual stop unwinds is still a manual stop, so the
                    // observatory is left as found, not parked or closed.
                    context.LogWarning($"Error while the autopilot was stopping: {ex.Message}");
                    await StopImagingOnExitAsync();
                    return;
                } catch (Exception ex) {
                    // This also catches a cancellation that neither the stop nor a watcher caused. That
                    // one is the state's own, so it's treated as a fault, not as a stop or as dawn.
                    context.LogError($"Unexpected error in autopilot machine: {ex.Message}");
                    context.LogDebug($"Autopilot machine exception detail: {ex}");
                    // Only recorded here. The machine teardown sends the session's one
                    // "autopilot failed" alert, and this is its cause.
                    context.SessionEndReason = $"Unexpected error in the autopilot machine.\n{ex.Message}";
                    await SecureOnExitAsync(abort.Fault, $"of an unexpected error ({ex.Message})");
                    return;
                }
            }

            // Cancelled between states, which is handled the same way.
            await StopImagingOnExitAsync();
        }

        /// <summary>
        /// Winds down after a deliberate stop. Stops the exposure, guiding and tracking, and nothing else,
        /// since someone switching off is usually taking manual control. The session-end email warns them
        /// instead of a separate alert, which on every switch-off would teach people to ignore alerts.
        /// </summary>
        private async Task StopImagingOnExitAsync() {
            // The machine's token is already cancelled, so nothing here uses it. Each call is either
            // synchronous or quick and runs on CancellationToken.None.
            try {
                if (context.Camera is { IsConnected: true } camera) {
                    camera.StopExposure();
                }
            } catch (Exception ex) {
                context.LogWarning($"Could not stop the exposure while shutting down: {ex.Message}");
            }

            try {
                await context.ReportStartedImageFailedAsync("cycle aborted: the autopilot was stopped");
            } catch (Exception ex) {
                context.LogWarning($"Could not report the interrupted image to the server: {ex.Message}");
            }

            // Stop guiding before tracking, or the guider chases the drifting sky with corrections
            // that go nowhere. The guider times out its own stop, so a silent one can't block the mount.
            try {
                if (context.Guider is { IsConnected: true } guider
                    && await guider.StopGuidingAsync(CancellationToken.None) == GuidingStopOutcome.NotConfirmed) {
                    context.LogWarning("Could not stop guiding while shutting down. Check the guider.");
                }
            } catch (Exception ex) {
                context.LogWarning($"Could not stop guiding while shutting down: {ex.Message}");
            }

            try {
                if (context.Mount is { IsConnected: true } mount && !await mount.StopMovementAsync()) {
                    context.LogWarning("Could not stop tracking while shutting down. Check the mount.");
                }
            } catch (Exception ex) {
                context.LogWarning($"Could not stop tracking while shutting down: {ex.Message}");
            }

            // We already parked and closed earlier (dawn or weather), and nothing has moved since or
            // the flag would have been cleared. Nothing for anyone to do, so this isn't a warning.
            if (context.IsSessionSecured) {
                string securedAt = context.SessionSecuredAtUtc is DateTime securedAtUtc
                    ? $" at {securedAtUtc.ToLocalTime():HH:mm}" : "";
                string roof = context.RoofMode == RoofMode.Operate
                    ? "the roof closed" : "the roof left to whoever operates it";
                context.Log($"Autopilot stopped: the mount was parked and {roof}{securedAt}, " +
                            "and both are as they were left.");
                return;
            }

            // If the roof is open and nothing else will close it, say so here too. The session-end
            // email has it, but the log is what someone at the observatory reads.
            string roofNote = context.Dome is { IsConnected: true } dome && !dome.IsShutterClosed()
                              && !context.IsRoofClosedOnUnsafe
                ? " N.I.N.A.'s 'Close on unsafe' is off, so nothing will close the roof if conditions turn."
                : "";
            context.LogWarning(
                "Autopilot stopped with the mount NOT parked and the roof NOT closed. Secure the " +
                "observatory if you are done." + roofNote);
        }
        /// <summary>
        /// Parks the mount and closes the roof after a state faults, which skips the normal transitions.
        /// Runs on a fresh token with its own timeout. Park and close are idempotent, so a repeat is harmless.
        /// </summary>
        private async Task SecureOnExitAsync(StateBase secure, string reason) {
            context.LogWarning($"Securing the observatory because {reason}.");
            using CancellationTokenSource secureCancellation = new CancellationTokenSource(SecureOnExitTimeout);
            try {
                await secure.OnEntryAsync(secureCancellation.Token);
                try {
                    await secure.ExecuteAsync(secureCancellation.Token);
                } finally {
                    await secure.OnExitAsync();
                }
            // Both first lines are fixed, with the details below. The server keys its cooldown on the
            // first line, so a driver's wording in it would make every repeat look like a new fault.
            } catch (OperationCanceledException) {
                context.ReportCritical(
                    "Securing the observatory did not finish in time. Check the mount and roof manually.",
                    new { reason, timeout = $"{SecureOnExitTimeout.TotalMinutes:F0} minutes" });
            } catch (Exception ex) {
                context.ReportCritical(
                    "Securing the observatory failed. Check the mount and roof manually.\n" + ex.Message,
                    new { reason, error = ex.Message });
            }
        }

        /// <summary>
        /// The state's own transition wins, then the abort route for a result the machine injects.
        /// Anything else with no transition is a wiring bug and throws.
        /// </summary>
        private static StateBase Next(StateBase current, AutopilotStateResult result, AbortRoutes abort) {
            if (current.TryNext(result, out StateBase next)) return next;
            return result switch {
                AutopilotStateResult.Unsafe       => abort.Unsafe,
                AutopilotStateResult.LimitReached => abort.LimitReached,
                AutopilotStateResult.WindowClosed => abort.WindowClosed,
                AutopilotStateResult.RoofClosed   => abort.RoofClosed,
                _ => throw new InvalidOperationException($"No transition from {current.Name} on {result}."),
            };
        }

        /// <summary>An abort a watcher asked for, plus the breach it read for a mount limit.</summary>
        private sealed record Tripped(AutopilotStateResult Result, string? Breach = null);

        /// <summary>
        /// The first abort any watcher asked for during one state's run. Recorded when the watcher fires,
        /// since unwinding a slew or exposure takes seconds, and a monitor reading Safe again by then would
        /// fall through to "daylight" and restart the night.
        /// </summary>
        private sealed class WatchTrip {
            private Tripped? _tripped;
            public Tripped? Value => Volatile.Read(ref _tripped);
            public void Record(Tripped tripped) => Interlocked.CompareExchange(ref _tripped, tripped, null);
        }

        /// <summary>
        /// Runs a state's work under the abort watchers. They share one linked token, so any of them stops
        /// the work, and the first to fire decides the abort.
        /// </summary>
        private async Task<AutopilotStateResult> ExecuteWithWatchAsync(StateBase state,
            CancellationToken cancellationToken) {
            using CancellationTokenSource cycleCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            WatchTrip trip = new WatchTrip();

            List<Task> watchers = new List<Task>();
            if (state.ShouldWatchObservingWindow) {
                watchers.Add(WatchAsync(CheckObservingWindow, trip, cycleCancellation));
            }
            if (state.ShouldWatchSafety) {
                watchers.Add(WatchAsync(CheckSafety, trip, cycleCancellation));
            }
            if (state.ShouldWatchMountLimits) {
                watchers.Add(WatchAsync(CheckMountLimits, trip, cycleCancellation));
            }

            try {
                AutopilotStateResult result = await state.ExecuteAsync(cycleCancellation.Token);
                // A state that never checks the token, like a decision or a web call, returns normally
                // even if a watcher fired. The abort still stands, or the machine would walk on into the
                // next state. Disabled is exempt since it has already ended the run.
                if (result != AutopilotStateResult.Disabled && !cancellationToken.IsCancellationRequested
                    && trip.Value is { } tripped) {
                    return ResolveTrip(state, tripped);
                }
                return result;
            } catch (Exception ex) when (trip.Value is not null && !cancellationToken.IsCancellationRequested) {
                // A watcher cancelled the cycle, so whatever the state threw while unwinding takes the
                // watcher's path, not the fault path. With no trip recorded the exception propagates.
                if (ex is not OperationCanceledException) {
                    context.LogDebug($"{state.Name} threw while a watcher was aborting it: {ex.Message}");
                }
                return ResolveTrip(state, trip.Value!);
            } finally {
                await cycleCancellation.CancelAsync();
                await Task.WhenAll(watchers);
            }
        }

        private AutopilotStateResult ResolveTrip(StateBase state, Tripped tripped) {
            switch (tripped.Result) {
                case AutopilotStateResult.Unsafe:
                    context.LogWarning($"Safety conditions changed ({context.SafetyMonitorName} reports unsafe). " +
                                       "Aborting current action.");
                    break;
                case AutopilotStateResult.RoofClosed:
                    context.LogWarning(
                        $"The roof is no longer open (shutter {context.Dome?.ShutterStatusName ?? "unknown"}). " +
                        "Aborting current action.");
                    break;
                case AutopilotStateResult.LimitReached:
                    string target = context.CurrentTarget?.TargetName ?? "the current target";
                    context.LastLimitBreach =
                        $"Mount limit reached on {target}: {tripped.Breach ?? "outside the configured limits"}";
                    context.LogError(
                        $"{context.LastLimitBreach} during {state.Name}. Parking, closing the roof and " +
                        "disabling the autopilot.");
                    break;
                default:
                    context.LogWarning("The observing window has closed. Aborting current action.");
                    break;
            }
            return tripped.Result;
        }

        /// <summary>
        /// Unsafe is checked first, since rain usually trips both and the unsafe path is the one that
        /// parks and closes.
        /// </summary>
        private Tripped? CheckSafety() {
            if (!context.IsSafe()) return new Tripped(AutopilotStateResult.Unsafe);
            if (context.IsRoofClosedUnexpectedly()) return new Tripped(AutopilotStateResult.RoofClosed);
            return null;
        }

        private Tripped? CheckMountLimits() =>
            context.IsWithinLimits()
                ? null
                : new Tripped(AutopilotStateResult.LimitReached, context.CurrentLimitBreach());

        private Tripped? CheckObservingWindow() =>
            context.IsOutsideObservingWindow() ? new Tripped(AutopilotStateResult.WindowClosed) : null;

        /// <summary>
        /// A check that throws is logged once and polled again, since a dead watcher would leave the
        /// state unwatched.
        /// </summary>
        private async Task WatchAsync(Func<Tripped?> check, WatchTrip trip,
            CancellationTokenSource cancellationSource) {
            bool hasReportedCheckFailure = false;
            while (!cancellationSource.IsCancellationRequested) {
                Tripped? tripped = null;
                try {
                    tripped = check();
                } catch (Exception ex) {
                    if (!hasReportedCheckFailure) {
                        context.LogWarning($"A safety watcher could not read its device: {ex.Message}");
                        hasReportedCheckFailure = true;
                    }
                }
                if (tripped != null) {
                    trip.Record(tripped);
                    await cancellationSource.CancelAsync();
                    return;
                }

                try {
                    await Task.Delay(TimeSpan.FromSeconds(Context.SafetyWatchIntervalSeconds),
                        cancellationSource.Token);
                } catch (OperationCanceledException) {
                    return;
                }
            }
        }
    }
}
