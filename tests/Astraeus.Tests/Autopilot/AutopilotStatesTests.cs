using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot;
using CosmicVaults.NINA.Astraeus.Tests.Helpers;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.Autopilot {
    [TestFixture]
    public class AutopilotStatesTests {
        private AutopilotStates _states = null!;
        private Dictionary<string, StateBase> _byProperty = new();

        [SetUp]
        public void BuildAndWireTheGraph() {
            _states = new AutopilotStates(new FakeNina().CreateContext());
            _states.WireTransitions();
            // The states are private properties, read here by name. That is the real graph, not a copy of it.
            _byProperty = typeof(AutopilotStates)
                .GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(property => typeof(StateBase).IsAssignableFrom(property.PropertyType))
                .ToDictionary(property => property.Name, property => (StateBase)property.GetValue(_states)!);
        }

        private StateBase State(string propertyName) => _byProperty[propertyName];

        private static StateBase? NextOf(StateBase state, AutopilotStateResult result) =>
            state.TryNext(result, out StateBase next) ? next : null;

        // The night starts by waiting for dusk, and each abort lands on the state the plan names, so Unsafe can't drift onto a secure that ends the night.
        [Test]
        public void TheEntryStateAndAbortRoutesAreTheOnesTheNightDependsOn() {
            StateMachine.AbortRoutes routes = _states.CreateAbortRoutes();

            Assert.Multiple(() => {
                Assert.That(_states.EntryState, Is.SameAs(State("WaitForDusk")));
                Assert.That(routes.Unsafe, Is.SameAs(State("SecureForSafety")));
                Assert.That(routes.LimitReached, Is.SameAs(State("StopTrackingForLimit")));
                Assert.That(routes.WindowClosed, Is.SameAs(State("SecureForShutdown")));
                Assert.That(routes.RoofClosed, Is.SameAs(State("StopForRoofClosed")));
                Assert.That(routes.Fault, Is.SameAs(State("SecureForFault")));
            });
        }

        // A roof that stops reading open goes stop, then the emergency secure, then back to waiting. Operate failing it disables, and a roof that was only a blip resumes polling.
        [Test]
        public void ARoofThatClosesIsRoutedThroughStopSecureAndWait() {
            StateBase stop = State("StopForRoofClosed");

            Assert.Multiple(() => {
                Assert.That(NextOf(stop, AutopilotStateResult.Completed), Is.SameAs(State("SecureForRoofClosed")));
                Assert.That(NextOf(stop, AutopilotStateResult.Failed), Is.SameAs(State("DisableAutopilot")));
                Assert.That(NextOf(stop, AutopilotStateResult.Resumed), Is.SameAs(State("PollForTarget")));
                Assert.That(NextOf(State("SecureForRoofClosed"), AutopilotStateResult.Completed), Is.SameAs(State("WaitForSafe")));
            });
        }

        // Only the two emergency secures return to waiting for safe, so the night can resume. Every other secure ends it.
        [Test]
        public void OnlyTheEmergencySecuresReturnToWaitingForSafe() {
            string[] endOfNight = ["SecureForFault", "SecureForLimit", "SecureForStorageFull", "SecureForUpdateRequired",
                "SecureForStartupFailure", "SecureForFlatPanel"];

            Assert.Multiple(() => {
                Assert.That(NextOf(State("SecureForSafety"), AutopilotStateResult.Completed), Is.SameAs(State("WaitForSafe")));
                Assert.That(NextOf(State("SecureForRoofClosed"), AutopilotStateResult.Completed), Is.SameAs(State("WaitForSafe")));
                foreach (string secure in endOfNight) {
                    Assert.That(NextOf(State(secure), AutopilotStateResult.Completed), Is.SameAs(State("DisableAutopilot")), secure);
                }
                Assert.That(NextOf(State("SecureForShutdown"), AutopilotStateResult.Completed), Is.SameAs(State("WarmCamera")));
            });
        }

        // A mount limit crossed mid-run stops tracking first, then secures and disables, whichever way the stop went.
        [Test]
        public void ALimitBreachStopsTrackingThenSecuresThenDisables() {
            StateBase stopTracking = State("StopTrackingForLimit");

            Assert.Multiple(() => {
                Assert.That(NextOf(stopTracking, AutopilotStateResult.Completed), Is.SameAs(State("SecureForLimit")));
                Assert.That(NextOf(stopTracking, AutopilotStateResult.Failed), Is.SameAs(State("SecureForLimit")));
                Assert.That(NextOf(State("SecureForLimit"), AutopilotStateResult.Completed), Is.SameAs(State("DisableAutopilot")));
            });
        }

        // Every transition points at a state the graph owns, so no state can hand the machine an object nothing else knows about.
        [Test]
        public void EveryTransitionTargetIsAStateInTheGraph() {
            HashSet<StateBase> graph = _byProperty.Values.ToHashSet();

            foreach (StateBase state in graph) {
                foreach (AutopilotStateResult result in Enum.GetValues<AutopilotStateResult>()) {
                    if (NextOf(state, result) is StateBase next) {
                        Assert.That(graph, Does.Contain(next), $"{state.Name} on {result}");
                    }
                }
            }
        }

        // Every state is reachable from the entry or an abort route. One that isn't is dead wiring, or a route somebody forgot to connect.
        [Test]
        public void EveryStateIsReachableFromTheEntryOrAnAbortRoute() {
            StateMachine.AbortRoutes routes = _states.CreateAbortRoutes();
            HashSet<StateBase> reached = new();
            Queue<StateBase> pending = new Queue<StateBase>(new[] {
                _states.EntryState, routes.Unsafe, routes.LimitReached, routes.WindowClosed, routes.RoofClosed, routes.Fault,
            });
            while (pending.TryDequeue(out StateBase? state)) {
                if (!reached.Add(state)) continue;
                foreach (AutopilotStateResult result in Enum.GetValues<AutopilotStateResult>()) {
                    if (NextOf(state, result) is StateBase next) pending.Enqueue(next);
                }
            }

            IEnumerable<string> unreachable = _byProperty.Values.Distinct().Except(reached).Select(state => state.Name);
            Assert.That(unreachable, Is.Empty);
        }

        // The roof watcher runs in every state except the ones that handle the roof themselves, so a roof closing mid-upload is still caught.
        [Test]
        public void OnlyTheStatesThatHandleTheRoofOptOutOfTheRoofWatcher() {
            string[] optedOut = ["SecureObservatory", "StopForRoofClosed", "DisableTracking", "ReportResult"];

            Assert.Multiple(() => {
                foreach (StateBase state in _byProperty.Values.Distinct()) {
                    bool isExpectedToOptOut = optedOut.Contains(state.GetType().Name);
                    Assert.That(state.ShouldWatchRoof, Is.EqualTo(!isExpectedToOptOut), $"{state.Name} ({state.GetType().Name})");
                }
            });
        }
    }
}