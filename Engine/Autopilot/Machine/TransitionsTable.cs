using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot
{

    /// <summary>
    /// A holder for the statemachines edges
    /// </summary>
    internal sealed class TransitionsTable
    {
        private readonly Dictionary<AutopilotStateResult, StateBase> _transitions = new();

        /// <summary>
        /// Define a transition of states. Will throw an exception if the same transition is added twice.
        /// </summary>
        public TransitionsTable On(AutopilotStateResult result, StateBase next)
        {
            if (!_transitions.TryAdd(result, next))
            {
                throw new InvalidOperationException($"{result} is already wired to {_transitions[result].Name}.");
            }
            return this;
        }


        public bool TryGetNext(AutopilotStateResult result, [NotNullWhen(true)] out StateBase? next)
        {
            return _transitions.TryGetValue(result, out next);
        }
    }




}