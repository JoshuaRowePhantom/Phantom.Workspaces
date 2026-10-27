using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;

namespace Phantom.Workspaces.ViewModels;

public enum AgentSessionCloseDecision
{
    Cancel,
    Terminate,
    RunInBackground,
}

internal interface IAgentSessionCloseDecisionProvider
{
    Task<AgentSessionCloseDecision> ChooseAsync(string sessionName, CancellationToken cancellationToken);
}

internal sealed class AgentSessionCloseDecisionProvider : IAgentSessionCloseDecisionProvider
{
    public async Task<AgentSessionCloseDecision> ChooseAsync(
        string sessionName,
        CancellationToken cancellationToken)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
            return AgentSessionCloseDecision.Cancel;

        var completion = new TaskCompletionSource<AgentSessionCloseDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminate = new Button { Content = "Terminate" };
        var background = new Button { Content = "Run in background" };
        var cancel = new Button { Content = "Cancel" };
        var window = new Window
        {
            Title = "Close active agent session",
            Width = 480,
            Height = 190,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = $"“{sessionName}” has active or pending work.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { terminate, background, cancel } },
                },
            },
        };
        void Complete(AgentSessionCloseDecision decision)
        {
            completion.TrySetResult(decision);
            window.Close();
        }
        terminate.Click += (_, _) => Complete(AgentSessionCloseDecision.Terminate);
        background.Click += (_, _) => Complete(AgentSessionCloseDecision.RunInBackground);
        cancel.Click += (_, _) => Complete(AgentSessionCloseDecision.Cancel);
        window.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape)
            {
                args.Handled = true;
                Complete(AgentSessionCloseDecision.Cancel);
            }
        };
        window.Closed += (_, _) => completion.TrySetResult(AgentSessionCloseDecision.Cancel);
        using var registration = cancellationToken.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(window.Close));
        _ = window.ShowDialog(owner);
        return await completion.Task.WaitAsync(cancellationToken);
    }
}
