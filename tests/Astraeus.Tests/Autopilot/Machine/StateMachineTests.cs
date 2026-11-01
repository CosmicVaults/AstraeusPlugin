using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot;
using CosmicVaults.NINA.Astraeus.Tests.Helpers;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.Autopilot
{
    [TestFixture]
    public partial class StateMachineTests
    {
        // A cap on runs that should end at once, so a regression fails instead of hanging the run.
        private static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);

        private List<string> _trace = new();
        private Context _context = null!;
        private FakeState _unsafeRoute = null!;
        private FakeState _limitRoute = null!;
        private FakeState _windowRoute = null!;
        private FakeState _roofRoute = null!;
        private FakeState _faultRoute = null!;
        private FakeState _stopRoute = null!;
        private MachineRoutes _routes;

        [SetUp]
        public void CreateTheMachinesParts()
        {
            _trace = new List<string>();
            _context = new FakeNina().CreateContext();
            // Each default route ends the run, so a test can tell which one the machine took. The two exits
            // run outside the loop, so what they return does not matter.
            _unsafeRoute = new FakeState("Unsafe route", _trace, AutopilotStateResult.Disabled);
            _limitRoute = new FakeState("Limit route", _trace, AutopilotStateResult.Disabled);
            _windowRoute = new FakeState("Window route", _trace, AutopilotStateResult.Disabled);
            _roofRoute = new FakeState("Roof route", _trace, AutopilotStateResult.Disabled);
            _faultRoute = new FakeState("Fault route", _trace, AutopilotStateResult.Disabled);
            _stopRoute = new FakeState("Stop route", _trace, AutopilotStateResult.Completed);
            TransitionsTable defaults = new TransitionsTable()
                .On(AutopilotStateResult.Unsafe, _unsafeRoute)
                .On(AutopilotStateResult.RoofClosed, _roofRoute)
                .On(AutopilotStateResult.LimitReached, _limitRoute)
                .On(AutopilotStateResult.WindowClosed, _windowRoute);
            // Each test supplies its own entry state.
            _routes = new MachineRoutes(Entry: null!, defaults, _faultRoute, _stopRoute);
        }

        private Task RunAsync(StateBase first, CancellationToken cancellationToken = default) =>
            new StateMachine(_context).RunAsync(_routes with { Entry = first }, cancellationToken);

        // The ordinary night is a chain of wired transitions, each state entered, run and exited in turn.
        [Test]
        public async Task TheMachineFollowsTheWiredTransitionsInOrder()
        {
            FakeState first = new FakeState("First", _trace, AutopilotStateResult.Completed);
            FakeState second = new FakeState("Second", _trace, AutopilotStateResult.Completed);
            FakeState last = new FakeState("Last", _trace, AutopilotStateResult.Disabled);
            first.OnCompleted(second);
            second.OnCompleted(last);

            await RunAsync(first).WaitAsync(Soon);

            Assert.That(_trace, Is.EqualTo(new[] {
                "First: entry", "First: execute", "First: exit",
                "Second: entry", "Second: execute", "Second: exit",
                "Last: entry", "Last: execute", "Last: exit",
            }));
        }

        // Disabled is the wired shutdown path. It must stop the machine dead: no transition looked up, and neither exit run.
        [Test]
        public async Task DisabledEndsTheRunWithoutLookingForATransition()
        {
            FakeState only = new FakeState("Only", _trace, AutopilotStateResult.Disabled);

            await RunAsync(only).WaitAsync(Soon);

            Assert.That(_trace, Is.EqualTo(new[] { "Only: entry", "Only: execute", "Only: exit" }));
            Assert.That(_faultRoute.Executions, Is.Zero);
            Assert.That(_stopRoute.Executions, Is.Zero);
        }

        // A watcher's result goes to the wiring's default for it when the state has no transition, so Unsafe can never land on the fault secure.
        [TestCase(AutopilotStateResult.Unsafe, "Unsafe route")]
        [TestCase(AutopilotStateResult.LimitReached, "Limit route")]
        [TestCase(AutopilotStateResult.WindowClosed, "Window route")]
        [TestCase(AutopilotStateResult.RoofClosed, "Roof route")]
        public async Task AnInjectedResultWithNoTransitionTakesItsAbortRoute(AutopilotStateResult result, string expectedRoute)
        {
            FakeState state = new FakeState("State", _trace, result);

            await RunAsync(state).WaitAsync(Soon);

            Assert.That(_trace, Does.Contain($"{expectedRoute}: execute"));
            Assert.That(_trace.FindAll(entry => entry.EndsWith(": execute")), Has.Count.EqualTo(2));
        }

        // A state's own transition wins over the default, which is how a state handles a watcher's result itself.
        [Test]
        public async Task AStatesOwnTransitionWinsOverTheAbortRoute()
        {
            FakeState state = new FakeState("State", _trace, AutopilotStateResult.Unsafe);
            FakeState handler = new FakeState("Handler", _trace, AutopilotStateResult.Disabled);
            state.On(AutopilotStateResult.Unsafe, handler);

            await RunAsync(state).WaitAsync(Soon);

            Assert.That(_trace, Does.Contain("Handler: execute"));
            Assert.That(_unsafeRoute.Executions, Is.Zero);
        }

        // A result nobody wired is a bug in the graph. It must secure through the fault exit and record why, not walk on or hang.
        [Test]
        public async Task AResultWithNoTransitionIsAFaultThatSecures()
        {
            FakeState state = new FakeState("Loose end", _trace, AutopilotStateResult.Completed);

            await RunAsync(state).WaitAsync(Soon);

            Assert.That(_faultRoute.Executions, Is.EqualTo(1));
            Assert.That(_context.Night.EndReason, Does.Contain("No transition from Loose end on Completed"));
        }

        // A state that throws takes the fault exit with the error kept for the session-end email, and its exit still runs.
        [Test]
        public async Task AStateThatThrowsSecuresThroughTheFaultRouteAndStillExits()
        {
            FakeState state = new FakeState("Broken", _trace, _ => throw new InvalidOperationException("driver exploded"));

            await RunAsync(state).WaitAsync(Soon);

            Assert.That(_trace, Does.Contain("Broken: exit"));
            Assert.That(_faultRoute.Executions, Is.EqualTo(1));
            Assert.That(_context.Night.EndReason, Does.Contain("driver exploded"));
        }

        // Switching the autopilot off is a manual stop. The machine runs the stop exit and nothing else: it must not secure,
        // since someone is taking over the observatory.
        [Test]
        public async Task CancellationBeforeTheFirstStateStopsWithoutSecuring()
        {
            FakeState state = new FakeState("Never run", _trace, AutopilotStateResult.Completed);
            using CancellationTokenSource stop = new CancellationTokenSource();
            stop.Cancel();

            await RunAsync(state, stop.Token).WaitAsync(Soon);

            Assert.That(_trace, Is.EqualTo(new[] { "Stop route: entry", "Stop route: execute", "Stop route: exit" }));
            Assert.That(_faultRoute.Executions, Is.Zero);
        }

        // A stop mid-state ends that state through its exit, then runs the stop exit and nothing more. The fault exit is for
        // errors, not the observer's switch.
        [Test]
        public async Task CancellationDuringAStateExitsItAndStopsWithoutSecuring()
        {
            using CancellationTokenSource stop = new CancellationTokenSource();
            FakeState waiting = new FakeState("Waiting", _trace, async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return AutopilotStateResult.Completed;
            });
            Task run = RunAsync(waiting, stop.Token);

            stop.Cancel();
            await run.WaitAsync(Soon);

            Assert.That(_trace, Is.EqualTo(new[] {
                "Waiting: entry", "Waiting: execute", "Waiting: exit",
                "Stop route: entry", "Stop route: execute", "Stop route: exit",
            }));
            Assert.That(_faultRoute.Executions, Is.Zero);
            Assert.That(_context.Night.EndReason, Is.Null);
        }
    }
}
