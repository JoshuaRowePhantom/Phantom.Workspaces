using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Services.TabCommands;

public readonly record struct AgentSessionRestartAvailability
{
    public required bool IsAvailable { get; init; }

    public string? DisabledReason { get; init; }
}

public enum AgentSessionRestartOutcome
{
    Succeeded,
    Cancelled,
    TargetChanged,
    Failed,
}

public sealed record AgentSessionRestartResult
{
    public required AgentSessionRestartOutcome Outcome { get; init; }

    public string? Message { get; init; }
}

public sealed record AgentSessionRestartRequest
{
    public required string SessionId { get; init; }

    public required IAgentChat ExpectedChat { get; init; }

    public required RunningAgentChatLease? ExpectedLease { get; init; }

    public required Guid CommandId { get; init; }

    /// <summary>Owner identity the restart was authorized against (the frozen session owner).</summary>
    public required string ExpectedOwnerId { get; init; }

    /// <summary>Ownership generation frozen before consent; a newer generation means the runtime was replaced.</summary>
    public required long OwnershipGeneration { get; init; }

    /// <summary>Runtime epoch/entry identity frozen before consent; a different epoch means a fresh runtime.</summary>
    public required Guid RuntimeEpoch { get; init; }
}

public interface IAgentSessionRestartService
{
    AgentSessionRestartAvailability GetAvailability(
        AgentSessionWorkspaceTabViewModel tab,
        TabCommandContext context);

    Task<AgentSessionRestartResult> RestartAsync(
        AgentSessionRestartRequest request,
        CancellationToken cancellationToken);
}

internal sealed class UnavailableAgentSessionRestartService : IAgentSessionRestartService
{
    internal static UnavailableAgentSessionRestartService Instance { get; } = new();

    private UnavailableAgentSessionRestartService()
    {
    }

    public AgentSessionRestartAvailability GetAvailability(
        AgentSessionWorkspaceTabViewModel tab,
        TabCommandContext context)
        => new()
        {
            IsAvailable = false,
            DisabledReason = "Restart is unavailable until lifecycle restart support is enabled.",
        };

    public Task<AgentSessionRestartResult> RestartAsync(
        AgentSessionRestartRequest request,
        CancellationToken cancellationToken)
        => Task.FromResult(new AgentSessionRestartResult
        {
            Outcome = AgentSessionRestartOutcome.Failed,
            Message = "Restart is unavailable until lifecycle restart support is enabled.",
        });
}

public interface IAgentSessionRestartConsentProvider
{
    Task<bool> ConfirmAsync(string sessionName, CancellationToken cancellationToken);
}

internal sealed class AgentSessionRestartConsentProvider : IAgentSessionRestartConsentProvider
{
    public async Task<bool> ConfirmAsync(string sessionName, CancellationToken cancellationToken)
    {
        if (Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            return false;
        }

        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var restart = new Button { Content = "Restart session" };
            var cancel = new Button { Content = "Cancel" };
            var window = new Window
            {
                Title = "Restart agent session",
                Width = 500,
                Height = 200,
                CanResize = false,
                Content = new StackPanel
                {
                    Margin = new Thickness(20),
                    Spacing = 16,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"“{sessionName}” has active or pending work. Restarting will interrupt this run.",
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        },
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 12,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Children = { restart, cancel },
                        },
                    },
                },
            };

            void Complete(bool accepted)
            {
                completion.TrySetResult(accepted);
                window.Close();
            }

            restart.Click += (_, _) => Complete(true);
            cancel.Click += (_, _) => Complete(false);
            window.KeyDown += (_, args) =>
            {
                if (args.Key != Key.Escape)
                {
                    return;
                }

                args.Handled = true;
                Complete(false);
            };
            window.Closed += (_, _) => completion.TrySetResult(false);
            using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(window.Close));
            _ = window.ShowDialog(owner);
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }
}
