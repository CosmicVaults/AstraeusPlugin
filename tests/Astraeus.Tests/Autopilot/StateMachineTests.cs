using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot;
using CosmicVaults.NINA.Astraeus.Tests.Helpers;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.Autopilot {
    [TestFixture]
    public partial class StateMachineTests {
        // A cap on runs that should end at once, so a regression fails instead of hanging the run.
        private static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);

        private List<string> _trace = new();
        private Context _context = null!;
        private FakeState _unsafeRoute = null!;
        private FakeState _limitRoute = null!;
        private FakeState _windowRoute = null!;
        private FakeState _roofRoute = null!;
        private FakeState _faultRoute = null!;
        private StateMachine.AbortRoutes _abort;

        [SetUp]
        public void CreateTheMachinesParts() {
            _trace = new List<string>();
            _context = new FakeNina().CreateContext();
            // Each route ends the run, so a test can tell which one the machine took.
            _unsafeRoute = new FakeState("Unsafe route", _trace, AutopilotStateResult.Disabled);
            _limitRoute = new FakeState("Limit route", _trace, AutopilotStateResult.Disabled);
            _windowRoute = new FakeState("Window route", _trace, AutopilotStateResult.Disabled);
            _roofRoute = new FakeState("Roof route", _trace, AutopilotStateResult.Disabled);
            _faultRoute = new FakeState("Fault route", _trace, AutopilotStateResult.Disabled);
            _abort = new StateMachine.AbortRoutes(_unsafeRoute, _limitRoute, _windowRoute, _roofRoute, _faultRoute);
        }

        // The ordinary night is a chain of wired transitions, each state entered, run and exited in turn.
        [Test]
        public async Task TheMachineFollowsTheWiredTransitionsInOrder() {
            FakeState first = new FakeState("First", _trace, AutopilotStateResult.Completed);
            FakeState second = new FakeState("Second", _trace, AutopilotStateResult.Completed);
            FakeState last = new FakeState("Last", _trace, AutopilotStateResult.Disabled);
            first.OnCompleted(second);
            second.OnCompleted(last);

            await new StateMachine(_context).RunAsync(first, _abort, CancellationToken.None).WaitAsync(Soon);

            Assert.That(_trace, Is.EqualTo(new[] {
                "First: entry", "First: execute", "First: exit",
                "Second: entry", "Second: execute", "Second: exit",
                "Last: entry", "Last: execute", "Last: exit",
            }));
        }

        // Disabled is the wired shutdown path. It must stop the machine dead, not look for a transition it won't find.
        [Test]
        public async Task DisabledEndsTheRunWithoutLookingForATransition() {
            FakeState only = new FakeState("Only", _trace, AutopilotStateResult.Disabled);

            await new StateMachine(_context).RunAsync(only, _abort, CancellationToken.None).WaitAsync(Soon);

            Assert.That(_trace, Is.EqualTo(new[] { "Only: entry", "Only: execute", "Only: exit" }));
            Assert.That(_faultRoute.Executions, Is.Zero);
        }

        // A watcher's result goes to its own abort route when the state has no transition for it, so Unsafe can never land on the fault secure.
        [TestCase(AutopilotStateResult.Unsafe, "Unsafe route")]
        [TestCase(AutopilotStateResult.LimitReached, "Limit route")]
        [TestCase(AutopilotStateResult.WindowClosed, "Window route")]
        [TestCase(AutopilotStateResult.RoofClosed, "Roof route")]
        public async Task AnInjectedResultWithNoTransitionTakesItsAbortRoute(AutopilotStateResult result, string expectedRoute) {
            FakeState state = new FakeState("State", _trace, result);

            await new StateMachine(_context).RunAsync(state, _abort, CancellationToken.None).WaitAsync(Soon);

            Assert.That(_trace, Does.Contain($"{expectedRoute}: execute"));
            Assert.That(_trace.FindAll(entry => entry.EndsWith(": execute")), Has.Count.EqualTo(2));
        }

        // A state's own transition wins over the abort route, which is how a state handles a watcher's result itself.
        [Test]
        public async Task AStatesOwnTransitionWinsOverTheAbortRoute() {
            FakeState state = new FakeState("State", _trace, AutopilotStateResult.Unsafe);
            FakeState handler = new FakeState("Handler", _trace, AutopilotStateResult.Disabled);
            state.On(AutopilotStateResult.Unsafe, handler);

            await new StateMachine(_context).RunAsync(state, _abort, CancellationToken.None).WaitAsync(Soon);

            Assert.That(_trace, Does.Contain("Handler: execute"));
            Assert.That(_unsafeRoute.Executions, Is.Zero);
        }

        // A result nobody wired is a bug in the graph. It must secure through the fault route and record why, not walk on or hang.
        [Test]
        public async Task AResultWithNoTransitionIsAFaultThatSecures() {
            FakeState state = new FakeState("Loose end", _trace, AutopilotStateResult.Completed);

            await new StateMachine(_context).RunAsync(state, _abort, CancellationToken.None).WaitAsync(Soon);

            Assert.That(_faultRoute.Executions, Is.EqualTo(1));
            Assert.That(_context.SessionEndReason, Does.Contain("No transition from Loose end on Completed"));
        }

        // A state that throws takes the fault route with the error kept for the session-end email, and its exit still runs.
        [Test]
        public async Task AStateThatThrowsSecuresThroughTheFaultRouteAndStillExits() {
            FakeState state = new FakeState("Broken", _trace, _ => throw new InvalidOperationException("driver exploded"));

            await new StateMachine(_context).RunAsync(state, _abort, CancellationToken.None).WaitAsync(Soon);

            Assert.That(_trace, Does.Contain("Broken: exit"));
            Assert.That(_faultRoute.Executions, Is.EqualTo(1));
            Assert.That(_context.SessionEndReason, Does.Contain("driver exploded"));
        }

        // Switching the autopilot off is a manual stop. The machine must not secure, since someone is taking over the observatory.
        [Test]
        public async Task CancellationBeforeTheFirstStateStopsWithoutSecuring() {
            FakeState state = new FakeState("Never run", _trace, AutopilotStateResult.Completed);
            using CancellationTokenSource stop = new CancellationTokenSource();
            stop.Cancel();

            await new StateMachine(_context).RunAsync(state, _abort, stop.Token).WaitAsync(Soon);

            Assert.That(_trace, Is.Empty);
            Assert.That(_faultRoute.Executions, Is.Zero);
        }

        // A stop mid-state ends that state through its exit and nothing more. The fault route is for errors, not the observer's switch.
        [Test]
        public async Task CancellationDuringAStateExitsItAndStopsWithoutSecuring() {
            using CancellationTokenSource stop = new CancellationTokenSource();
            FakeState waiting = new FakeState("Waiting", _trace, async token => {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return AutopilotStateResult.Completed;
            });
            Task run = new StateMachine(_context).RunAsync(waiting, _abort, stop.Token);

            stop.Cancel();
            await run.WaitAsync(Soon);

            Assert.That(_trace, Is.EqualTo(new[] { "Waiting: entry", "Waiting: execute", "Waiting: exit" }));
            Assert.That(_faultRoute.Executions, Is.Zero);
            Assert.That(_context.SessionEndReason, Is.Null);
        }
    }
}