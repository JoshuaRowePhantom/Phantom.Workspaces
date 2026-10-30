using System.Diagnostics;

namespace Phantom.Workspaces.Llm;

/// <summary>Isolates observer failures from an owner-acknowledged operation.</summary>
internal static class InterruptNotifications
{
    internal static void Publish<THandler>(
        THandler? handlers,
        Action<THandler> notify,
        Func<bool>? isCurrent = null)
        where THandler : Delegate
    {
        if (handlers is null)
        {
            return;
        }

        foreach (THandler handler in handlers.GetInvocationList())
        {
            if (isCurrent is not null && !isCurrent())
            {
                return;
            }

            try
            {
                notify(handler);
            }
            catch (Exception exception)
            {
                // Only observer callbacks are isolated; owner exceptions retain their task semantics.
                Trace.TraceError("Interrupt notification observer failed: {0}", exception);
            }
        }
    }
}
