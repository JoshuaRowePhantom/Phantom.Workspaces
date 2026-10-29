using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Services.TabCommands;

public interface ITabCommandErrorSink
{
    void ReportFailure(TabCommandContext context, TabCommandDescriptor command, Exception exception);
}

internal sealed class NoOpTabCommandErrorSink : ITabCommandErrorSink
{
    internal static NoOpTabCommandErrorSink Instance { get; } = new();

    private NoOpTabCommandErrorSink()
    {
    }

    public void ReportFailure(TabCommandContext context, TabCommandDescriptor command, Exception exception)
    {
    }
}

public sealed class TabCommandInvoker
{
    private readonly object gate = new();
    private readonly HashSet<(WorkspaceDocument Document, string CommandId)> inFlight = [];
    private readonly ITabCommandErrorSink errorSink;

    public TabCommandInvoker(ITabCommandErrorSink? errorSink = null)
    {
        this.errorSink = errorSink ?? NoOpTabCommandErrorSink.Instance;
    }

    public bool IsInFlight(TabCommandContext context, string commandId)
    {
        lock (this.gate)
        {
            return this.inFlight.Contains((context.Document, commandId));
        }
    }

    public async Task<TabCommandResult> InvokeAsync(
        TabCommandDescriptor command,
        TabCommandContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        if (!context.IsStillValid())
        {
            return TabCommandResult.Rejected("The tab target is no longer valid.");
        }

        var availability = command.Availability?.Invoke(context) ?? TabCommandAvailability.Enabled;
        if (!availability.IsVisible)
        {
            return TabCommandResult.Rejected("The command is hidden for this target.");
        }

        if (!availability.IsEnabled)
        {
            return TabCommandResult.Rejected(
                availability.DisabledReason ?? "The command is not enabled for this target.");
        }

        if (!this.TryEnter(context.Document, command.Id))
        {
            return TabCommandResult.AlreadyRunning();
        }

        try
        {
            return await command.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return TabCommandResult.Cancelled();
        }
        catch (Exception ex)
        {
            this.errorSink.ReportFailure(context, command, ex);
            return TabCommandResult.Failed(ex);
        }
        finally
        {
            this.Exit(context.Document, command.Id);
        }
    }

    private bool TryEnter(WorkspaceDocument document, string commandId)
    {
        lock (this.gate)
        {
            return this.inFlight.Add((document, commandId));
        }
    }

    private void Exit(WorkspaceDocument document, string commandId)
    {
        lock (this.gate)
        {
            this.inFlight.Remove((document, commandId));
        }
    }
}
