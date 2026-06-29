using System.Collections.Generic;

namespace Phantom.Workspaces.ViewModels;

public static class StatusItemAggregator
{
    /// <summary>
    /// Computes the aggregate RunningStatus and ErrorStatus from a snapshot of sources.
    /// RunningStatus is Running if any source is Running.
    /// ErrorStatus is the worst across sources: Error > Successful > None.
    /// </summary>
    public static void UpdateFrom(StatusItem target, IEnumerable<IStatusItem> sources)
    {
        var running = RunningStatus.Idle;
        var error = ErrorStatus.None;
        foreach (var source in sources)
        {
            if (source.RunningStatus == RunningStatus.Running)
                running = RunningStatus.Running;
            if (source.ErrorStatus > error)
                error = source.ErrorStatus;
        }
        target.RunningStatus = running;
        target.ErrorStatus = error;
    }
}
