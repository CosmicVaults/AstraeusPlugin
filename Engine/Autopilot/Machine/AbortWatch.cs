using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot
{
    /// <summary>
    /// The abort watchers: one row per Aborts flag, in priority order, polled on one task every
    /// SafetyWatchIntervalSeconds while a state runs. The first row to trip ends the state's work and
    /// decides its result. The blind spells live here too, since a spell belongs to the watcher and has
    /// to carry across consecutive states.
    /// </summary>
    internal sealed class AbortWatch
    {
        /// <summary>
        /// An abort a watcher asked for, plus the breach it read for a mount limit. BlindReason is set when
        /// it's the watcher's fail-safe.
        /// </summary>
        internal sealed record Tripped(AutopilotStateResult Result, string? Breach = null, string? BlindReason = null);

        /// <summary>One poll of a watcher: clear, an abort, or blind (no reading, with why).</summary>
        internal readonly record struct WatchReading(Tripped? Trip = null, string? BlindReason = null)
        {
            public static WatchReading Clear => default;
            public static WatchReading Of(Tripped? trip) => new(trip);
            public static WatchReading Blind(string reason) => new(BlindReason: reason);
        }

        // One watcher: the flag a state opts into, its check, and its blind spell.
        private sealed record Row(Aborts Abort, Func<WatchReading> Check, BlindSpell Spell);

        private readonly Context _context;
        private readonly Row[] _rows;

        public AbortWatch(Context context)
        {
            _context = context;
            // Priority when two trip in the same poll: unsafe first, since it is the one that parks and
            // closes, then the roof, then the mount limits, and the window last since dawn is never urgent.
            _rows = [
                new Row(Aborts.Unsafe, () => WatchReading.Of(CheckSafety()),
                    new BlindSpell("safety", AutopilotStateResult.Unsafe, "secure as if conditions were unsafe")),
                new Row(Aborts.RoofClosed, () => WatchReading.Of(CheckRoof()),
                    new BlindSpell("roof", AutopilotStateResult.RoofClosed, "stop as if the roof had closed")),
                new Row(Aborts.LimitReached, CheckMountLimits,
                    new BlindSpell("mount limit", AutopilotStateResult.LimitReached,
                        "stop, park, close the roof and switch off")),
                new Row(Aborts.WindowClosed, () => WatchReading.Of(CheckObservingWindow()),
                    new BlindSpell("observing window", AutopilotStateResult.WindowClosed,
                        "end the night as if the observing window had closed")),
            ];
        }

        /// <summary>
        /// Runs a state's work under the watchers it opts into. They share one token linked to the
        /// machine's, so the first row to trip stops the work, and that trip decides the result.
        /// </summary>
        public async Task<AutopilotStateResult> RunAsync(StateBase state, CancellationToken machineToken)
        {
            using CancellationTokenSource cycle = CancellationTokenSource.CreateLinkedTokenSource(machineToken);
            Task<Tripped?> poll = PollAsync(state.AbortsOn, cycle);

            AutopilotStateResult result = default;
            Exception? error = null;
            try
            {
                result = await state.ExecuteAsync(cycle.Token);
            }
            catch (Exception ex)
            {
                error = ex;
            }
            await cycle.CancelAsync();
            // Read once, after the join, so the trip is whatever fired while the state ran and is never
            // worked out again after the state unwound. Conditions may have changed by then.
            Tripped? tripped = await poll;

            // A state that never checks the token, like a decision or a web call, returns normally even
            // if a watcher fired. The abort still stands, or the machine would walk on into the next
            // state. Disabled is exempt since it has already ended the run, and the machine's own
            // cancellation beats a trip. A state that threw while unwinding takes the trip's path too.
            if (tripped is not null && !machineToken.IsCancellationRequested
                && (error is not null || result != AutopilotStateResult.Disabled))
            {
                if (error is not null and not OperationCanceledException)
                {
                    _context.LogDebug($"{state.Name} threw while a watcher was aborting it: {error.Message}");
                }
                return LogTrip(state, tripped);
            }
            if (error is not null) ExceptionDispatchInfo.Throw(error);
            return result;
        }

        /// <summary>
        /// Polls the rows the state asked for, in priority order, every SafetyWatchIntervalSeconds on the
        /// observatory's clock, until one trips or the state ends. The first reading is taken as the state
        /// starts, so a roof already closing aborts a state that finishes at once.
        /// </summary>
        private async Task<Tripped?> PollAsync(Aborts abortsOn, CancellationTokenSource cycle)
        {
            if (abortsOn == Aborts.None) return null;
            while (!cycle.IsCancellationRequested)
            {
                foreach (Row row in _rows)
                {
                    if (!abortsOn.HasFlag(row.Abort)) continue;
                    if (Read(row) is Tripped tripped)
                    {
                        await cycle.CancelAsync();
                        return tripped;
                    }
                }
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(AutopilotConstants.SafetyWatchIntervalSeconds),
                        _context.Observatory.Clock, cycle.Token);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }
            return null;
        }

        // One reading of one row. A check that throws is a blind reading, not a fault, so the watcher never
        // dies silently. A blind reading counts towards the row's fail-safe, and a good one ends any spell.
        private Tripped? Read(Row row)
        {
            WatchReading reading;
            try
            {
                reading = row.Check();
            }
            catch (Exception ex)
            {
                reading = WatchReading.Blind($"its check failed: {ex.Message}");
            }
            if (reading.BlindReason is string reason) return CountBlindReading(row.Spell, reason);
            if (row.Spell.End()) _context.Log($"The {row.Spell.Watcher} watcher can read its device again.");
            return reading.Trip;
        }

        /// <summary>Warns when a spell starts. Returns the fail-safe once it has lasted the grace.</summary>
        private Tripped? CountBlindReading(BlindSpell spell, string reason)
        {
            TimeSpan blindFor = spell.Observe(_context.UtcNow, out bool isNew);
            if (isNew)
            {
                _context.LogWarning(
                    $"The {spell.Watcher} watcher cannot read its device ({reason}). If that lasts " +
                    $"{AutopilotConstants.WatcherBlindGraceSeconds} s the autopilot will {spell.FailSafeAction}.");
            }
            if (blindFor < TimeSpan.FromSeconds(AutopilotConstants.WatcherBlindGraceSeconds)) return null;
            spell.End();
            return new Tripped(spell.FailSafe, BlindReason: reason);
        }

        // Logs why the state was aborted, with its own wording when a watcher's fail-safe fired. For a
        // limit it also records LastLimitBreach, which the limit secure reports.
        private AutopilotStateResult LogTrip(StateBase state, Tripped tripped)
        {
            string? blind = tripped.BlindReason;
            int grace = AutopilotConstants.WatcherBlindGraceSeconds;
            switch (tripped.Result)
            {
                case AutopilotStateResult.Unsafe:
                    _context.LogWarning(blind == null
                        ? $"Safety conditions changed ({_context.SafetyMonitorName} reports unsafe). " +
                          "Aborting current action."
                        : $"The safety watcher could not read its devices for {grace} s ({blind}), so " +
                          "treating conditions as unsafe. Aborting current action.");
                    break;
                case AutopilotStateResult.RoofClosed:
                    _context.LogWarning(blind == null
                        ? $"The roof is no longer open (shutter {_context.Dome?.ShutterStatusName ?? "unknown"}). " +
                          "Aborting current action."
                        : $"The roof watcher could not read the dome for {grace} s ({blind}), so treating the " +
                          "roof as closed. Aborting current action.");
                    break;
                case AutopilotStateResult.LimitReached:
                    string target = _context.Frame.Target?.TargetName ?? "the current target";
                    _context.LastLimitBreach = blind == null
                        ? $"Mount limit reached on {target}: {tripped.Breach ?? "outside the configured limits"}"
                        : $"Mount position unknown on {target} for {grace} s ({blind})";
                    _context.LogError(
                        $"{_context.LastLimitBreach} during {state.Name}. Parking, closing the roof and " +
                        "disabling the autopilot.");
                    break;
                default:
                    _context.LogWarning(blind == null
                        ? "The observing window has closed. Aborting current action."
                        : $"The observing window could not be worked out for {grace} s ({blind}), so " +
                          "treating it as closed. Aborting current action.");
                    break;
            }
            return tripped.Result;
        }

        ///////////// The checks /////////////

        internal Tripped? CheckSafety() =>
            _context.IsSafe() ? null : new Tripped(AutopilotStateResult.Unsafe);

        // A roof that closed because it turned unsafe takes the unsafe path.
        internal Tripped? CheckRoof()
        {
            if (!_context.IsRoofClosedUnexpectedly()) return null;
            return new Tripped(_context.IsSafe() ? AutopilotStateResult.RoofClosed : AutopilotStateResult.Unsafe);
        }

        // Unknown first, since the breach check reads an unknown position as clear.
        internal WatchReading CheckMountLimits()
        {
            if (_context.DescribeUnknownMountPosition() is string unknown) return WatchReading.Blind(unknown);
            return IsWithinLimits()
                ? WatchReading.Clear
                : WatchReading.Of(new Tripped(AutopilotStateResult.LimitReached, CurrentLimitBreach()));
        }

        private Tripped? CheckObservingWindow() =>
            _context.IsOutsideObservingWindow() ? new Tripped(AutopilotStateResult.WindowClosed) : null;

        ///////////// The limit check and its meridian waiver /////////////

        // How long the meridian waiver holds past the frame's window. Long enough for the report and
        // calibration to reach the next poll's flip, short enough to bound a stall.
        private static readonly TimeSpan MeridianWaiverGrace = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Whether the mount is inside its limits now. True when limits are off, it's parked, there's no
        /// mount, or its position can't be read, which CheckMountLimits asks about first. The meridian is
        /// left out while a flip is coming, or the watcher would end the night a minute before the flip
        /// fixes the breach.
        /// </summary>
        private bool IsWithinLimits() =>
            _context.Mount?.IsCurrentPositionWithinLimits(shouldIncludeMeridian: !IsMeridianWaived) ?? true;

        // The flip waiver, time-limited so a stalled frame can't leave the meridian unwatched for the rest
        // of the night.
        private bool IsMeridianWaived
        {
            get
            {
                if (!_context.CanFlipForCurrentTarget) return false;
                if (_context.Frame.WindowEndsAtUtc is not DateTime windowEnd) return true;
                return _context.UtcNow < windowEnd + MeridianWaiverGrace;
            }
        }

        /// <summary>
        /// Which limit the mount breaks right now, worded for the log. Null when it's clear, limits are
        /// off or there's no mount, and when parked, since parking is the watcher's own remedy. Scoped
        /// like IsWithinLimits, so the watcher reports the reason it tripped.
        /// </summary>
        private string? CurrentLimitBreach() =>
            _context.Mount?.DescribeCurrentPositionBreach(shouldIncludeMeridian: !IsMeridianWaived);
    }
}
