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
            try {
                if (context.Camera is { IsConnected: true } camera) {
                    camera.StopExposure();
                }
            } catch (Exception ex) {
                context.LogWarning($"Could not stop the exposure while shutting down: {ex.Message}");
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
                if (context.Mount is { IsConnected: true, IsResting: false } mount
                    && !await mount.StopMovementAsync()) {
                    context.LogWarning("Could not stop tracking while shutting down. Check the mount.");
                }
            } catch (Exception ex) {
                context.LogWarning($"Could not stop tracking while shutting down: {ex.Message}");
            }

            // After the hardware, since this web call can take its whole ten-second timeout and N.I.N.A.
            // closing only waits so long for the machine. The mount is the part that must not be left running.
            try {
                await context.ReportStartedImageFailedAsync("cycle aborted: the autopilot was stopped");
            } catch (Exception ex) {
                context.LogWarning($"Could not report the interrupted image to the server: {ex.Message}");
            }

            bool isRoofOurs = context.RoofMode == RoofMode.Operate || context.HasOpenedRoofTonight;
            // We already parked and closed earlier (dawn or weather), and it still reads that way now.
            // Nothing for anyone to do, so this isn't a warning.
            if (context.IsStillSecured) {
                string securedAt = context.SessionSecuredAtUtc is DateTime securedAtUtc
                    ? $" at {securedAtUtc.ToLocalTime():HH:mm}" : "";
                string roof = isRoofOurs ? "the roof closed" : "the roof left to whoever operates it";
                context.Log($"Autopilot stopped: the mount was parked and {roof}{securedAt}, " +
                            "and both are as they were left.");
                return;
            }

            bool isRoofOpen = context.Dome is { IsConnected: true } dome && !dome.IsShutterClosed();
            List<string> unsecured = new();
            if (context.Mount is not { IsConnected: true } connectedMount) unsecured.Add("the mount not connected");
            else if (!connectedMount.IsParked) unsecured.Add("the mount NOT parked");
            if (isRoofOpen) {
                unsecured.Add("the roof NOT closed");
            } else if (isRoofOurs && context.Dome is not { IsConnected: true } && IsRoofInUseTonight()) {
                // Unread isn't closed, so it's named like an open roof rather than left out.
                unsecured.Add("the roof state unknown (dome not connected)");
            }

            if (unsecured.Count == 0) {
                string roof = context.Dome is { IsConnected: true } ? " and the roof closed" : "";
                context.Log($"Autopilot stopped with the mount parked{roof}.");
                return;
            }

            // If the roof is open and nothing else will close it, say so here too. The session-end
            // email has it, but the log is what someone at the observatory reads.
            string roofNote = isRoofOpen && !context.IsRoofClosedOnUnsafe
                ? " N.I.N.A.'s 'Close on unsafe' is off, so nothing will close the roof if conditions turn."
                : "";
            context.LogWarning(
                $"Autopilot stopped with {string.Join(" and ", unsecured)}. Secure the observatory if you " +
                "are done." + roofNote);
        }

        // We opened the roof, or tonight's connect chain has run. A daytime switch-off before the dome
        // is connected says nothing about a roof the autopilot hasn't touched.
        private bool IsRoofInUseTonight() =>
            context.HasOpenedRoofTonight || (context.IsStartupComplete && context.SessionStartedUtc != null);

        /// <summary>
        /// Parks the mount and closes the roof after a state faults, which skips the normal transitions.
        /// Runs on a fresh token with its own timeout. Park and close are idempotent, so a repeat is harmless.
        /// </summary>
        private async Task SecureOnExitAsync(StateBase secure, string reason) {
            context.LogWarning($"Securing the observatory because {reason}.");
            using CancellationTokenSource secureCancellation =
                new CancellationTokenSource(SecureOnExitTimeout, context.Observatory.Clock);
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

        /// <summary>
        /// An abort a watcher asked for, plus the breach it read for a mount limit. BlindReason is set when
        /// it's the watcher's fail-safe.
        /// </summary>
        internal sealed record Tripped(AutopilotStateResult Result, string? Breach = null, string? BlindReason = null);

        /// <summary>One poll of a watcher: clear, an abort, or blind (no reading, with why).</summary>
        internal readonly record struct WatchReading(Tripped? Trip = null, string? BlindReason = null) {
            public static WatchReading Clear => default;
            public static WatchReading Of(Tripped? trip) => new(trip);
            public static WatchReading Blind(string reason) => new(BlindReason: reason);
        }

        /// <summary>
        /// An unbroken run of blind readings from one kind of watcher. A good reading, or more than
        /// WatcherReadingGap without a blind one, ends it.
        /// </summary>
        internal sealed class BlindSpell(string watcher, AutopilotStateResult failSafe, string failSafeAction) {
            private DateTime? _startedUtc;
            private DateTime _lastBlindUtc;

            public string Watcher => watcher;
            public AutopilotStateResult FailSafe => failSafe;
            public string FailSafeAction => failSafeAction;

            /// <summary>Counts a blind reading and returns how long the spell has lasted.</summary>
            public TimeSpan Observe(DateTime nowUtc, out bool isNew) {
                isNew = _startedUtc is null || nowUtc - _lastBlindUtc > Context.WatcherReadingGap;
                if (isNew) _startedUtc = nowUtc;
                _lastBlindUtc = nowUtc;
                return nowUtc - _startedUtc.GetValueOrDefault(nowUtc);
            }

            /// <summary>True if a spell was running.</summary>
            public bool End() {
                bool wasBlind = _startedUtc is not null;
                _startedUtc = null;
                return wasBlind;
            }
        }

        // Kept here because a state's watchers end with it. Only one watcher of each kind runs at a time.
        private readonly BlindSpell _windowSpell = new("observing window", AutopilotStateResult.WindowClosed,
            "end the night as if the observing window had closed");
        private readonly BlindSpell _safetySpell = new("safety", AutopilotStateResult.Unsafe,
            "secure as if conditions were unsafe");
        private readonly BlindSpell _limitsSpell = new("mount limit", AutopilotStateResult.LimitReached,
            "stop, park, close the roof and switch off");
        private readonly BlindSpell _roofSpell = new("roof", AutopilotStateResult.RoofClosed,
            "stop as if the roof had closed");

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
                watchers.Add(WatchAsync(() => WatchReading.Of(CheckObservingWindow()), _windowSpell, trip,
                    cycleCancellation));
            }
            if (state.ShouldWatchSafety) {
                watchers.Add(WatchAsync(() => WatchReading.Of(CheckSafety()), _safetySpell, trip,
                    cycleCancellation));
            }
            if (state.ShouldWatchRoof) {
                watchers.Add(WatchAsync(() => WatchReading.Of(CheckRoof()), _roofSpell, trip,
                    cycleCancellation));
            }
            if (state.ShouldWatchMountLimits) {
                watchers.Add(WatchAsync(CheckMountLimits, _limitsSpell, trip, cycleCancellation));
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
            string? blind = tripped.BlindReason;
            int grace = Context.WatcherBlindGraceSeconds;
            switch (tripped.Result) {
                case AutopilotStateResult.Unsafe:
                    context.LogWarning(blind == null
                        ? $"Safety conditions changed ({context.SafetyMonitorName} reports unsafe). " +
                          "Aborting current action."
                        : $"The safety watcher could not read its devices for {grace} s ({blind}), so " +
                          "treating conditions as unsafe. Aborting current action.");
                    break;
                case AutopilotStateResult.RoofClosed:
                    context.LogWarning(blind == null
                        ? $"The roof is no longer open (shutter {context.Dome?.ShutterStatusName ?? "unknown"}). " +
                          "Aborting current action."
                        : $"The roof watcher could not read the dome for {grace} s ({blind}), so treating the " +
                          "roof as closed. Aborting current action.");
                    break;
                case AutopilotStateResult.LimitReached:
                    string target = context.CurrentTarget?.TargetName ?? "the current target";
                    context.LastLimitBreach = blind == null
                        ? $"Mount limit reached on {target}: {tripped.Breach ?? "outside the configured limits"}"
                        : $"Mount position unknown on {target} for {grace} s ({blind})";
                    context.LogError(
                        $"{context.LastLimitBreach} during {state.Name}. Parking, closing the roof and " +
                        "disabling the autopilot.");
                    break;
                default:
                    context.LogWarning(blind == null
                        ? "The observing window has closed. Aborting current action."
                        : $"The observing window could not be worked out for {grace} s ({blind}), so " +
                          "treating it as closed. Aborting current action.");
                    break;
            }
            return tripped.Result;
        }

        internal Tripped? CheckSafety() =>
            context.IsSafe() ? null : new Tripped(AutopilotStateResult.Unsafe);

        // A roof that closed because it turned unsafe takes the unsafe path.
        internal Tripped? CheckRoof() {
            if (!context.IsRoofClosedUnexpectedly()) return null;
            return new Tripped(context.IsSafe() ? AutopilotStateResult.RoofClosed : AutopilotStateResult.Unsafe);
        }

        // Unknown first, since the breach check reads an unknown position as clear.
        internal WatchReading CheckMountLimits() {
            if (context.DescribeUnknownMountPosition() is string unknown) return WatchReading.Blind(unknown);
            return context.IsWithinLimits()
                ? WatchReading.Clear
                : WatchReading.Of(new Tripped(AutopilotStateResult.LimitReached, context.CurrentLimitBreach()));
        }

        private Tripped? CheckObservingWindow() =>
            context.IsOutsideObservingWindow() ? new Tripped(AutopilotStateResult.WindowClosed) : null;

        /// <summary>
        /// Polls a check until it trips or the state ends. A check that throws counts as blind, and a blind
        /// spell lasting WatcherBlindGraceSeconds trips the watcher's fail-safe.
        /// </summary>
        private async Task WatchAsync(Func<WatchReading> check, BlindSpell spell, WatchTrip trip,
            CancellationTokenSource cancellationSource) {
            while (!cancellationSource.IsCancellationRequested) {
                WatchReading reading;
                try {
                    reading = check();
                } catch (Exception ex) {
                    reading = WatchReading.Blind($"its check failed: {ex.Message}");
                }

                Tripped? tripped = reading.Trip;
                if (reading.BlindReason is string reason) {
                    tripped = CountBlindReading(spell, reason);
                } else if (spell.End()) {
                    context.Log($"The {spell.Watcher} watcher can read its device again.");
                }

                if (tripped != null) {
                    trip.Record(tripped);
                    await cancellationSource.CancelAsync();
                    return;
                }

                try {
                    await Task.Delay(TimeSpan.FromSeconds(Context.SafetyWatchIntervalSeconds),
                        context.Observatory.Clock, cancellationSource.Token);
                } catch (OperationCanceledException) {
                    return;
                }
            }
        }

        /// <summary>Warns when a spell starts. Returns the fail-safe once it has lasted the grace.</summary>
        private Tripped? CountBlindReading(BlindSpell spell, string reason) {
            TimeSpan blindFor = spell.Observe(context.UtcNow, out bool isNew);
            if (isNew) {
                context.LogWarning(
                    $"The {spell.Watcher} watcher cannot read its device ({reason}). If that lasts " +
                    $"{Context.WatcherBlindGraceSeconds} s the autopilot will {spell.FailSafeAction}.");
            }
            if (blindFor < TimeSpan.FromSeconds(Context.WatcherBlindGraceSeconds)) return null;
            spell.End();
            return new Tripped(spell.FailSafe, BlindReason: reason);
        }
    }
}
