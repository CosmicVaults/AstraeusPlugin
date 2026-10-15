using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CosmicVaults.NINA.Astraeus.Engine;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot;
using NINA.Astrometry;
using NINA.Equipment.Interfaces;

namespace CosmicVaults.NINA.Astraeus.Tests.Helpers {
    /// <summary>
    /// A night under way on the real state machine, with the real abort routes and every state they lead to, driven by
    /// the clock. The imaging chain is one state playing "exposing". The night starts at 22:00 with the roof open, the
    /// mount tracking at RA 6 h 40 min, the guider and camera connected, conditions safe, the session started, and
    /// drivers that park, close and open at once.
    /// </summary>
    internal sealed class NightScenario {
        private static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);
        private readonly RoofMode _roofMode;
        private readonly CancellationTokenSource _switch = new();

        public FakeNina Nina { get; } = new FakeNina();
        public Context Context { get; }
        public List<string> Trace { get; } = new();
        public Task Run { get; private set; } = Task.CompletedTask;

        public NightScenario(RoofMode roofMode = RoofMode.Operate) {
            _roofMode = roofMode;
            Nina.SetTypicalNight(new DateTime(2026, 3, 1, 22, 0, 0));
            Nina.Settings.SetAutopilotRoofMode(roofMode);
            Nina.Settings.SetSafeSettleSeconds(0);
            Nina.SetRoof(ShutterState.ShutterOpen);
            Nina.SetSafety(true);
            Nina.SetMount(isConnected: true, isTracking: true);
            // J2000, since the target poll converts the mount's position to J2000 and a JNOW position would need the
            // native precession library the test box doesn't have.
            Nina.TelescopeInfo.Coordinates = new Coordinates(100, 0, Epoch.J2000, Coordinates.RAType.Degrees);
            // Sidereal time comes from a native library the test box doesn't have. Pinned at the mount's RA, so the
            // meridian is never the limit a scenario trips.
            Nina.Mount.LocalSiderealTimeNow = _ => 100.0 / 15;
            Nina.SetGuider(isConnected: true);
            Nina.SetCamera(isConnected: true);
            Nina.MountParksAtOnce();
            Nina.RoofClosesAtOnce();
            Nina.RoofOpensAtOnce();
            Context = Nina.CreateContext();
        }

        /// <summary>
        /// Starts the machine on the exposure, or on the given state, with the real routes into the real safety states.
        /// The session is opened first, since that resets the night's flags, and the flags are then set as a night in
        /// progress has them.
        /// </summary>
        public async Task StartAsync(StateBase? initial = null) {
            await Context.StartSessionAsync();
            Context.IsStartupComplete = true;
            Context.IsRoofExpectedOpen = _roofMode != RoofMode.Ignore;
            Context.IsRoofUnderAutopilotControl = _roofMode == RoofMode.Operate;
            Context.HasOpenedRoofTonight = _roofMode == RoofMode.Operate;
            AutopilotStates states = new AutopilotStates(Context);
            states.WireTransitions();
            Run = new StateMachine(Context).RunAsync(initial ?? new ExposingState("Exposing", Trace), states.CreateAbortRoutes(),
                _switch.Token);
        }

        /// <summary>Moves the clock in steps until the condition holds, the machine ends or the budget is spent.</summary>
        public async Task<bool> AdvanceUntilAsync(Func<bool> condition, TimeSpan step, TimeSpan budget) {
            TimeSpan advanced = TimeSpan.Zero;
            while (!condition() && !Run.IsCompleted && advanced < budget) {
                Nina.Clock.Advance(step);
                advanced += step;
                await Task.Delay(1);
            }
            return condition();
        }

        /// <summary>Switches the autopilot off, as the observer would, and waits for the machine to wind down.</summary>
        public async Task SwitchOffAsync() {
            _switch.Cancel();
            await Run.WaitAsync(Soon);
        }

        /// <summary>The alerts that would email someone, leaving out the session's own start and end.</summary>
        public List<Observatory.ReportedEvent> CriticalAlerts =>
            Nina.Alerts.Where(alert => alert.Kind == "critical_error").ToList();

        /// <summary>Whether the night has got back to asking the server for a target.</summary>
        public bool HasAskedForATarget => Nina.Server.Requests.Any(request => request.Path == "/api/autopilot/next/");
    }
}