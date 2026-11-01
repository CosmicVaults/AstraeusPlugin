using System;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot
{
    internal sealed class StateMachine(Context context)
    {
        // One for the whole run, so the watchers' blind spells carry across states.
        private readonly AbortWatch _watch = new AbortWatch(context);

        /// <summary>
        /// A backstop for the exit secure. SecureObservatory times itself out on AutopilotConstants.SecureTimeout and
        /// reports it, so this sits a minute past that and should never be the one to fire.
        /// </summary>
        private static readonly TimeSpan SecureOnExitTimeout = AutopilotConstants.SecureTimeout + TimeSpan.FromMinutes(1);

        /// <summary>
        /// Runs states from the entry until one ends the run, the token is cancelled (autopilot or plugin
        /// disabled, or N.I.N.A. closing) or a state faults. The last two leave through an exit state.
        /// </summary>
        public async Task RunAsync(MachineRoutes routes, CancellationToken cancellationToken)
        {
            StateBase state = routes.Entry;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await state.OnEntryAsync(cancellationToken);
                    AutopilotStateResult result;
                    try
                    {
                        result = await _watch.RunAsync(state, cancellationToken);
                    }
                    finally
                    {
                        await state.OnExitAsync();
                    }
                    // Disabled is the wired shutdown path and has already secured on the way here.
                    if (result == AutopilotStateResult.Disabled) return;
                    StateBase next = Next(state, result, routes.Defaults);
                    context.LogDebug($"{state.Name} returned {result}, so the next state is {next.Name}.");
                    state = next;
                }
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested)
            {
                if (ex is not OperationCanceledException)
                {
                    context.LogWarning($"Error while the autopilot was stopping: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                context.LogError($"Unexpected error in autopilot machine: {ex.Message}");
                context.LogDebug($"Autopilot machine exception detail: {ex}");

                context.Night.RecordEndReason($"Unexpected error in the autopilot machine.\n{ex.Message}");
                await SecureOnExitAsync(routes.SecureForFault, $"of an unexpected error ({ex.Message})");
                return;
            }
            // Cancelled, mid-state or between states, which is handled the same way.
            await StopOnExitAsync(routes.StopOnSwitchOff);
        }

        /// <summary>
        /// The deliberate stop, run on no token so it finishes whatever cancelled the run. Never throws,
        /// since nothing runs after it.
        /// </summary>
        private async Task StopOnExitAsync(StateBase stop)
        {
            try
            {
                await RunExitAsync(stop, CancellationToken.None);
            }
            catch (Exception ex)
            {
                context.LogWarning($"Error while the autopilot was stopping: {ex.Message}");
            }
        }

        /// <summary>
        /// Parks the mount and closes the roof after a state faults, which skips the normal transitions.
        /// Runs on a fresh token with its own timeout. Park and close are idempotent, so a repeat is harmless.
        /// </summary>
        private async Task SecureOnExitAsync(StateBase secure, string reason)
        {
            context.LogWarning($"Securing the observatory because {reason}.");
            using CancellationTokenSource secureCancellation =
                new CancellationTokenSource(SecureOnExitTimeout, context.Observatory.Clock);
            try
            {
                await RunExitAsync(secure, secureCancellation.Token);
                // Both first lines are fixed, with the details below. The server keys its cooldown on the
                // first line, so a driver's wording in it would make every repeat look like a new fault.
            }
            catch (OperationCanceledException)
            {
                context.ReportCritical(
                    "Securing the observatory did not finish in time. Check the mount and roof manually.",
                    new { reason, timeout = $"{SecureOnExitTimeout.TotalMinutes:F0} minutes" });
            }
            catch (Exception ex)
            {
                context.ReportCritical(
                    "Securing the observatory failed. Check the mount and roof manually.\n" + ex.Message,
                    new { reason, error = ex.Message });
            }
        }

        /// <summary>
        /// An exit runs as any state does, entry, execute and exit, but under no watchers.
        /// </summary>
        private static async Task RunExitAsync(StateBase exit, CancellationToken cancellationToken)
        {
            await exit.OnEntryAsync(cancellationToken);
            try
            {
                await exit.ExecuteAsync(cancellationToken);
            }
            finally
            {
                await exit.OnExitAsync();
            }
        }

        /// <summary>
        /// The state's own edge wins, then the wiring's defaults for a result the watchers inject.
        /// Anything else with no transition is a wiring bug and throws.
        /// </summary>
        private static StateBase Next(StateBase current, AutopilotStateResult result, TransitionsTable defaults)
        {
            if (current.Transitions.TryGetNext(result, out StateBase? next)) return next;
            if (defaults.TryGetNext(result, out next)) return next;
            throw new InvalidOperationException($"No transition from {current.Name} on {result}.");
        }
    }
}
