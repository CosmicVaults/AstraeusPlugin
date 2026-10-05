using System;
using System.Threading;
using System.Threading.Tasks;

namespace CosmicVaults.NINA.Astraeus.Engine.Utility {
    internal static class TaskFaults {
        /// <summary>For a task nobody awaits, hands its exception to onFault so it isn't left unobserved.</summary>
        public static void ObserveFaults(this Task task, Action<Exception> onFault) {
            task.ContinueWith(
                faultedTask => onFault(faultedTask.Exception!.GetBaseException()),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
