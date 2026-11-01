using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CosmicVaults.NINA.Astraeus.Engine.Autopilot;
using CosmicVaults.NINA.Astraeus.Tests.Helpers;
using NUnit.Framework;

namespace CosmicVaults.NINA.Astraeus.Tests.Autopilot
{
    [TestFixture]
    public class AutopilotStatesTests
    {
        private AutopilotStates _states = null!;
        private Dictionary<string, StateBase> _byProperty = new();

        [SetUp]
        public void BuildAndWireTheGraph()
        {
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
            state.Transitions.TryGetNext(result, out StateBase? next) ? next : null;

        private static StateBase? DefaultOf(MachineRoutes routes, AutopilotStateResult result) =>
            routes.Defaults.TryGetNext(result, out StateBase? next) ? next : null;

        // The night starts by waiting for dusk, each watcher's result lands on the state the plan names, and the two exits are the real
        // stop and the fault secure, so Unsafe can't drift onto a secure that ends the night.
        [Test]
        public void TheEntryTheDefaultsAndTheExitsAreTheOnesTheNightDependsOn()
        {
            MachineRoutes routes = _states.CreateRoutes();

            Assert.Multiple(() =>
            {
                Assert.That(routes.Entry, Is.SameAs(State("WaitForDusk")));
                Assert.That(DefaultOf(routes, AutopilotStateResult.Unsafe), Is.SameAs(State("SecureForSafety")));
                Assert.That(DefaultOf(routes, AutopilotStateResult.LimitReached), Is.SameAs(State("StopTrackingForLimit")));
                Assert.That(DefaultOf(routes, AutopilotStateResult.WindowClosed), Is.SameAs(State("SecureForShutdown")));
                Assert.That(DefaultOf(routes, AutopilotStateResult.RoofClosed), Is.SameAs(State("IsRoofStillOpen")));
                Assert.That(routes.SecureForFault, Is.SameAs(State("SecureForFault")));
                Assert.That(routes.StopOnSwitchOff, Is.SameAs(State("StopOnSwitchOff")));
            });
        }

        // A roof that stops reading open first asks whether the dome merely dropped and came back open, which resumes polling.
        // Otherwise it goes stop, then the emergency secure, then back to waiting, and Operate failing it disables.
        [Test]
        public void ARoofThatClosesIsRoutedThroughTheStillOpenCheckStopSecureAndWait()
        {
            StateBase check = State("IsRoofStillOpen");
            StateBase stop = State("StopForRoofClosed");

            Assert.Multiple(() =>
            {
                Assert.That(NextOf(check, AutopilotStateResult.Yes), Is.SameAs(State("PollForTarget")));
                Assert.That(NextOf(check, AutopilotStateResult.No), Is.SameAs(stop));
                Assert.That(NextOf(stop, AutopilotStateResult.Completed), Is.SameAs(State("SecureForRoofClosed")));
                Assert.That(NextOf(stop, AutopilotStateResult.Failed), Is.SameAs(State("DisableAutopilot")));
                Assert.That(NextOf(State("SecureForRoofClosed"), AutopilotStateResult.Completed), Is.SameAs(State("WaitForSafe")));
            });
        }

        // A captured frame is checked for its pointing and for a blocked light path before it is reported, and a failed capture
        // goes straight to the report, since there is no frame to check. Neither check can be cut short by dawn or the roof.
        [Test]
        public void ACapturedFrameIsCheckedBeforeItIsReported()
        {
            Assert.Multiple(() =>
            {
                Assert.That(NextOf(State("CaptureImage"), AutopilotStateResult.Completed), Is.SameAs(State("CheckFramePointing")));
                Assert.That(NextOf(State("CaptureImage"), AutopilotStateResult.Failed), Is.SameAs(State("ReportResult")));
                Assert.That(NextOf(State("CheckFramePointing"), AutopilotStateResult.Completed), Is.SameAs(State("CheckLightPath")));
                Assert.That(NextOf(State("CheckLightPath"), AutopilotStateResult.Completed), Is.SameAs(State("ReportResult")));
                Assert.That(State("CheckFramePointing").AbortsOn, Is.EqualTo(Aborts.LimitReached));
                Assert.That(State("CheckLightPath").AbortsOn, Is.EqualTo(Aborts.LimitReached));
            });
        }

        // Under Operate the mount is made ready for N.I.N.A. before the open, and a park that can't be had retries like a
        // failed open.
        [Test]
        public void UnderOperateTheMountIsParkedForTheRoofBeforeTheOpen()
        {
            Assert.Multiple(() =>
            {
                Assert.That(NextOf(State("OpenRoofCheck"), AutopilotStateResult.Yes), Is.SameAs(State("ParkMountForRoofOpen")));
                Assert.That(NextOf(State("ParkMountForRoofOpen"), AutopilotStateResult.Completed), Is.SameAs(State("OpenDomeShutter")));
                Assert.That(NextOf(State("ParkMountForRoofOpen"), AutopilotStateResult.Failed), Is.SameAs(State("RetryRoofOpen")));
            });
        }

        // A failed flip may leave the mount tracking into the limit it was flipping away from, so both flips stop tracking
        // before the next poll, as a refused target does.
        [Test]
        public void AFailedFlipStopsTrackingBeforeTheNextPoll()
        {
            Assert.Multiple(() =>
            {
                Assert.That(NextOf(State("RunMeridianFlip"), AutopilotStateResult.Failed), Is.SameAs(State("StopTrackingForRefusal")));
                Assert.That(NextOf(State("RunFlipBeforeFrame"), AutopilotStateResult.Failed), Is.SameAs(State("StopTrackingForRefusal")));
            });
        }

        // Only the two emergency secures return to waiting for safe, so the night can resume. Every other secure ends it.
        [Test]
        public void OnlyTheEmergencySecuresReturnToWaitingForSafe()
        {
            string[] endOfNight = ["SecureForFault", "SecureForLimit", "SecureForStorageFull", "SecureForUpdateRequired",
                "SecureForStartupFailure", "SecureForFlatPanel"];

            Assert.Multiple(() =>
            {
                Assert.That(NextOf(State("SecureForSafety"), AutopilotStateResult.Completed), Is.SameAs(State("WaitForSafe")));
                Assert.That(NextOf(State("SecureForRoofClosed"), AutopilotStateResult.Completed), Is.SameAs(State("WaitForSafe")));
                foreach (string secure in endOfNight)
                {
                    Assert.That(NextOf(State(secure), AutopilotStateResult.Completed), Is.SameAs(State("DisableAutopilot")), secure);
                }
                Assert.That(NextOf(State("SecureForShutdown"), AutopilotStateResult.Completed), Is.SameAs(State("WarmCamera")));
            });
        }

        // A mount limit crossed mid-run stops tracking first, then secures and disables, whichever way the stop went.
        [Test]
        public void ALimitBreachStopsTrackingThenSecuresThenDisables()
        {
            StateBase stopTracking = State("StopTrackingForLimit");

            Assert.Multiple(() =>
            {
                Assert.That(NextOf(stopTracking, AutopilotStateResult.Completed), Is.SameAs(State("SecureForLimit")));
                Assert.That(NextOf(stopTracking, AutopilotStateResult.Failed), Is.SameAs(State("SecureForLimit")));
                Assert.That(NextOf(State("SecureForLimit"), AutopilotStateResult.Completed), Is.SameAs(State("DisableAutopilot")));
            });
        }

        // Every transition and every default edge points at a state the graph owns, so no state can hand the machine an object nothing else knows about.
        [Test]
        public void EveryTransitionTargetIsAStateInTheGraph()
        {
            HashSet<StateBase> graph = _byProperty.Values.ToHashSet();
            MachineRoutes routes = _states.CreateRoutes();

            foreach (StateBase state in graph)
            {
                foreach (AutopilotStateResult result in Enum.GetValues<AutopilotStateResult>())
                {
                    if (NextOf(state, result) is StateBase next)
                    {
                        Assert.That(graph, Does.Contain(next), $"{state.Name} on {result}");
                    }
                }
            }
            foreach (AutopilotStateResult result in Enum.GetValues<AutopilotStateResult>())
            {
                if (DefaultOf(routes, result) is StateBase route)
                {
                    Assert.That(graph, Does.Contain(route), $"default on {result}");
                }
            }
        }

        // Every state is reachable from the entry, a default edge or an exit. One that isn't is dead wiring, or a route somebody forgot to connect.
        [Test]
        public void EveryStateIsReachableFromTheEntryADefaultOrAnExit()
        {
            MachineRoutes routes = _states.CreateRoutes();
            HashSet<StateBase> reached = new();
            Queue<StateBase> pending = new Queue<StateBase>(new[] { routes.Entry, routes.SecureForFault, routes.StopOnSwitchOff });
            foreach (AutopilotStateResult result in Enum.GetValues<AutopilotStateResult>())
            {
                if (DefaultOf(routes, result) is StateBase route) pending.Enqueue(route);
            }
            while (pending.TryDequeue(out StateBase? state))
            {
                if (!reached.Add(state)) continue;
                foreach (AutopilotStateResult result in Enum.GetValues<AutopilotStateResult>())
                {
                    if (NextOf(state, result) is StateBase next) pending.Enqueue(next);
                }
            }

            IEnumerable<string> unreachable = _byProperty.Values.Distinct().Except(reached).Select(state => state.Name);
            Assert.That(unreachable, Is.Empty);
        }

        // The roof watcher runs in every state except the ones that handle the roof themselves or end the run, so a roof closing mid-upload is still caught.
        [Test]
        public void OnlyTheStatesThatHandleTheRoofOptOutOfTheRoofWatcher()
        {
            string[] optedOut = ["SecureObservatory", "StopForRoofClosed", "DisableTracking", "ReportResult", "StopOnSwitchOff",
                "IsRoofStillOpen", "CheckFramePointing", "CheckLightPath"];

            Assert.Multiple(() =>
            {
                foreach (StateBase state in _byProperty.Values.Distinct())
                {
                    bool isExpectedToOptOut = optedOut.Contains(state.GetType().Name);
                    Assert.That(state.AbortsOn.HasFlag(Aborts.RoofClosed), Is.EqualTo(!isExpectedToOptOut), $"{state.Name} ({state.GetType().Name})");
                }
            });
        }
    }
}
