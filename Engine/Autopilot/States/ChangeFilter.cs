using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    /// <summary>
    /// Failed refuses the frame when the filter can't be selected, so nothing is taken through the
    /// wrong filter.
    /// </summary>
    internal sealed class ChangeFilter(string name, Context context) : StateBase(name) {
        // The mount tracks throughout, and the hour-angle limit may not be set in the mount's own driver,
        // so every tracking state needs the watcher, not only the ones that move it.
        protected internal override bool ShouldWatchSafety => true;
        protected internal override bool ShouldWatchMountLimits => true;

        public override async Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {
            AutopilotNextResponse? target = context.CurrentTarget;
            string? filterName = target?.FilterName;
            if (string.IsNullOrWhiteSpace(filterName)) {
                context.Log("No filter specified, so skipping filter change.");
                return AutopilotStateResult.Completed;
            }

            FilterWheel? filterWheel = context.FilterWheel;
            bool isFilterWheelInUse = filterWheel != null && (filterWheel.IsConnected || filterWheel.HasDefaultDevice);
            if (!isFilterWheelInUse) {
                context.Log("No filter wheel in use, so skipping filter change.");
                return AutopilotStateResult.Completed;
            }

            if (!await context.EnsureConnectedAsync(filterWheel, cancellationToken)) {
                await context.RefuseCurrentFrameAsync(
                    $"The filter wheel is not connected, so filter '{filterName}' cannot be selected");
                return AutopilotStateResult.Failed;
            }

            context.AutopilotStatus = AutopilotStatus.ChangingFilter;
            context.Log($"Selecting filter '{filterName}'.");
            int? positionBefore = filterWheel!.GetCurrentFilter()?.Position;
            bool isFilterSelected = await filterWheel!.ChangeFilterByNameAsync(filterName, cancellationToken);
            if (!isFilterSelected) {
                await context.RefuseCurrentFrameAsync(
                    $"Filter '{filterName}' could not be selected " +
                    "(see the filter wheel log for the available filters)");
                return AutopilotStateResult.Failed;
            }
            // Not cleared here. A frame that comes back after a flip finds its filter already in place,
            // and must still remember that it changed it the first time.
            if (filterWheel!.GetCurrentFilter()?.Position != positionBefore) context.HasFilterChangedThisFrame = true;
            return AutopilotStateResult.Completed;
        }
    }
}
