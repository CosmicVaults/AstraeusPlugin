using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    /// <summary>
    /// Opens the flat panel cover once the roof is open, whatever "operates with roof closed" says, since
    /// moving earlier gains nothing. Fails, stopping the night, when the cover can't be confirmed open.
    /// </summary>
    internal sealed class PrepareFlatPanel(string name, Context context) : StateBase(name) {
        protected internal override bool ShouldWatchSafety => true;

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {
            FlatPanelMode mode = context.FlatPanelMode;
            if (mode == FlatPanelMode.Ignore) return AutopilotStateResult.Completed;

            // Nothing chosen and nothing connected. The connect chain holds the night at dusk until a
            // panel is connected, so this is a panel dropped mid-night with its default gone too.
            if (context.FlatPanel is not { } panel || (!panel.IsConnected && !panel.HasDefaultDevice)) {
                return Fail(mode, BaseComponent.NoDevice, "unknown",
                    "no flat panel is connected or selected as the default device");
            }
            if (!await context.EnsureConnectedAsync(panel, cancellationToken)) {
                return Fail(mode, panel.DefaultDevice ?? "unknown", "disconnected",
                    context.ShouldAutoConnectEquipment
                        ? "it is not connected and would not connect"
                        : "it is not connected and auto-connect is off");
            }

            if (panel.IsLightOn) {
                context.Log("Switching the flat panel light off.");
                // Not a reason to stop, since an open panel's light faces away from the optics.
                if (!await panel.TurnLightOffAsync(cancellationToken)) {
                    context.LogWarning("Flat panel light did not confirm it switched off. Carrying on.");
                }
            }

            if (!panel.HasCover) {
                context.LogDebug("Flat panel has no cover to open.");
                return AutopilotStateResult.Completed;
            }
            if (panel.IsCoverOpen()) {
                context.LogDebug("Flat panel cover already open.");
                return AutopilotStateResult.Completed;
            }
            // Every path here has just seen the roof open, so this only bites if it has since stopped
            // reading open, which the safety watcher will have noticed too.
            if (!FlatPanelControl.IsOperable(context)) {
                return Fail(mode, panel.DeviceId, panel.CoverStateName,
                    "the roof does not read open, and the panel is set not to operate with the roof closed");
            }

            context.AutopilotStatus = AutopilotStatus.OpeningFlatPanel;
            context.Log("Opening the flat panel.");
            Stopwatch watch = Stopwatch.StartNew();
            if (await panel.OpenCoverAsync(cancellationToken)) {
                context.Log($"Flat panel open in {Context.FormatElapsed(watch)}.");
                return AutopilotStateResult.Completed;
            }
            return Fail(mode, panel.DeviceId, panel.CoverStateName,
                $"the cover did not read open after {Context.FormatElapsed(watch)}");
        }

        private AutopilotStateResult Fail(FlatPanelMode mode, string device, string coverState, string why) {
            context.LogError($"Flat panel cover not confirmed open: {why}. Stopping the autopilot.");
            context.SessionEndReason ??= $"The flat panel cover could not be confirmed open ({why}).";
            // Fixed first line, since the server keys its alert cooldown on it.
            context.ReportCritical(
                "Flat panel cover not confirmed open. The autopilot has stopped.",
                new { panel = device, cover = coverState, mode = mode.ToString(), reason = why });
            return AutopilotStateResult.Failed;
        }
    }
}
