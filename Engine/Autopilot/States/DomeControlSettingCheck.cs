using System;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Autopilot.States {
    internal sealed class DomeControlSettingCheck(string name, Context context) : StateBase(name) {
        
        public override Task<AutopilotStateResult> ExecuteAsync(CancellationToken cancellationToken) {
            return context.RoofMode switch {
                RoofMode.Ignore => Task.FromResult(AutopilotStateResult.Pass),
                RoofMode.WaitForOpen => Task.FromResult(AutopilotStateResult.No),
                RoofMode.Operate => Task.FromResult(AutopilotStateResult.Yes),
                _ => throw new ArgumentOutOfRangeException()
            };
        }
    }
}