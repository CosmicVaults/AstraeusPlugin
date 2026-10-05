using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot {
    /// <summary>
    /// When the autopilot may move the flat panel cover, and the end-of-night close. On some rigs the
    /// cover swings through space a closed roof takes up, so by default it only moves while the roof
    /// reads Open. A bad-weather secure leaves the cover alone, since the night may resume.
    /// </summary>
    internal static class FlatPanelControl {
        public static bool IsOperable(Context context) =>
            context.ShouldOperateFlatPanelWithRoofClosed ||
            context.RoofMode == RoofMode.Ignore ||
            context.Dome?.IsShutterOpen() == true;

        /// <summary>
        /// A panel that has dropped still counts, since the close reconnects it or reports that it couldn't.
        /// </summary>
        private static bool IsCloseWanted(Context context) =>
            context.FlatPanelMode == FlatPanelMode.OpenAndClose &&
            context.FlatPanel is { } panel && (panel.IsConnected || panel.HasDefaultDevice);

        /// <summary>
        /// Closes a cover that can't move under a closed roof while the roof is still open, and returns
        /// whether it tried. A failed close is reported and the roof closes anyway, since holding the
        /// roof open for the panel would be worse.
        /// </summary>
        public static async Task<bool> CloseBeforeRoofAsync(Context context, string reason,
            CancellationToken cancellationToken) {
            if (context.ShouldOperateFlatPanelWithRoofClosed) return false;
            if (!IsCloseWanted(context) || !IsOperable(context)) return false;
            await CloseAsync(context, reason, cancellationToken);
            return true;
        }

        /// <summary>
        /// End-of-night step after the roof. Closes a cover that's still open if it may move, otherwise
        /// reports it left open.
        /// </summary>
        /// <param name="hasAlreadyTried">
        /// True when this stand-down already tried the cover. That attempt reported its outcome, and a
        /// stand-down spends only one timeout on the panel.
        /// </param>
        public static async Task CloseAfterRoofAsync(Context context, string reason, bool hasAlreadyTried,
            CancellationToken cancellationToken) {
            if (hasAlreadyTried || !IsCloseWanted(context)) return;
            await CloseAsync(context, reason, cancellationToken);
        }

        private static async Task CloseAsync(Context context, string reason, CancellationToken cancellationToken) {
            FlatPanel panel = context.FlatPanel!;
            if (!await context.EnsureConnectedAsync(panel, cancellationToken)) {
                ReportLeftOpen(context, reason, "the flat panel is not connected");
                return;
            }
            if (!panel.HasCover || panel.IsCoverClosed()) return;
            if (!IsOperable(context)) {
                ReportLeftOpen(context, reason,
                    "the roof is already closed and 'Can operate with roof closed' is off");
                return;
            }

            context.AutopilotStatus = AutopilotStatus.ClosingFlatPanel;
            context.Log($"Closing the flat panel ({reason}).");
            Stopwatch watch = Stopwatch.StartNew();
            if (await panel.CloseCoverAsync(cancellationToken)) {
                context.Log($"Flat panel closed in {Context.FormatElapsed(watch)}.");
                return;
            }
            ReportLeftOpen(context, reason, $"the cover did not read closed after {Context.FormatElapsed(watch)}");
        }

        // Only a log warning. A cover left open for the day is worth knowing about but not worth
        // waking anyone for.
        private static void ReportLeftOpen(Context context, string reason, string why) {
            string cover = context.FlatPanel?.CoverStateName ?? "none";
            context.LogWarning($"Flat panel cover not closed ({reason}): {why}. Cover {cover}. Close it by hand.");
        }
    }
}
