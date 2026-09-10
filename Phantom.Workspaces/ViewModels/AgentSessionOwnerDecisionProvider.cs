using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Threading;
using Phantom.Workspaces.Llm.Remote;

namespace Phantom.Workspaces.ViewModels;

internal enum AgentSessionOwnerDecision
{
    ConnectOnOwner,
    ResumeLocally,
}

internal readonly record struct AgentSessionOwnerDecisionContext(
    AgentSessionRemoteStatus Status,
    string OwningProfileEntityId);

internal interface IAgentSessionOwnerDecisionProvider
{
    Task<AgentSessionOwnerDecision> ChooseAsync(
        AgentSessionOwnerDecisionContext context,
        CancellationToken ct);
}

internal sealed class AgentSessionOwnerDecisionProvider : IAgentSessionOwnerDecisionProvider
{
    public async Task<AgentSessionOwnerDecision> ChooseAsync(
        AgentSessionOwnerDecisionContext context,
        CancellationToken ct)
    {
        if (Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
            return AgentSessionOwnerDecision.ConnectOnOwner;

        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var completion = new TaskCompletionSource<AgentSessionOwnerDecision>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var status = context.Status switch
            {
                AgentSessionRemoteStatus.Running => "Running",
                AgentSessionRemoteStatus.NotRunning => "Not running",
                _ => "Unavailable",
            };
            var connect = new Button { Content = "Connect on owning profile" };
            var resume = new Button { Content = "Resume locally" };
            var window = new Window
            {
                Title = "Open agent session",
                Width = 440,
                Height = 190,
                CanResize = false,
                Content = new StackPanel
                {
                    Margin = new Thickness(20),
                    Spacing = 16,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"Owning profile status: {status}",
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        },
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 12,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Children = { connect, resume },
                        },
                    },
                },
            };
            connect.Click += (_, _) =>
            {
                completion.TrySetResult(AgentSessionOwnerDecision.ConnectOnOwner);
                window.Close();
            };
            resume.Click += (_, _) =>
            {
                completion.TrySetResult(AgentSessionOwnerDecision.ResumeLocally);
                window.Close();
            };
            window.Closed += (_, _) =>
                completion.TrySetResult(AgentSessionOwnerDecision.ConnectOnOwner);
            using var registration = ct.Register(() => Dispatcher.UIThread.Post(window.Close));
            _ = window.ShowDialog(owner);
            return await completion.Task.WaitAsync(ct);
        });
    }
}
