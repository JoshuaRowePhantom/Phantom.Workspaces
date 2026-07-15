using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm.Shell;
using Phantom.Workspaces.Llm.Trust;

namespace Phantom.Workspaces.ViewModels;

public sealed class StartShellOnProfileShortcutHandler : ShortcutHandler
{
    private readonly ITrustedExecutorSelector? executorSelector;
    private readonly Func<string, string?, CancellationToken, Task<ITerminalSession>>? sessionOpener;

    /// <summary>Production constructor: uses the supplied <see cref="ITrustedExecutorSelector"/> to route shell sessions to the correct executor.</summary>
    public StartShellOnProfileShortcutHandler(ITrustedExecutorSelector executorSelector)
    {
        ArgumentNullException.ThrowIfNull(executorSelector);
        this.executorSelector = executorSelector;
    }

    /// <summary>Test constructor: injects a custom session-opener so no real PTY is spawned.</summary>
    internal StartShellOnProfileShortcutHandler(
        Func<string, string?, CancellationToken, Task<ITerminalSession>> sessionOpener)
    {
        this.sessionOpener = sessionOpener;
    }

    public override ValueTask<bool> ShouldApplyTo(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        return ValueTask.FromResult(shortcut == Shortcut.StartShell
            && entityViewModel.IsEntityType("user-computer-profile"));
    }

    public override async Task<bool> Handle(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        var localProfileId = mainWindowViewModel.EntityBroker.EntityRepository
            .WorkspaceEntitySession.UserComputerProfileEntityId;

        var targetClientInstance = entityViewModel.EntityId == localProfileId
            ? TrustProfile.LocalClientInstance
            : entityViewModel.EntityId.ToString();

        var workingDirectory = entityViewModel.Data is JsonElement entityData
            && entityData.TryGetProperty("home-directory", out var homeDirElement)
            && homeDirElement.ValueKind == JsonValueKind.String
            ? homeDirElement.GetString()
            : null;

        var session = await this.OpenSessionAsync(targetClientInstance, workingDirectory, CancellationToken.None);

        var command = GetDefaultCommand();
        var tab = new ShellTabViewModel(session)
        {
            Id = $"shell-{entityViewModel.EntityId}-{Guid.NewGuid():N}",
            Title = $"{command} — {entityViewModel.DisplayName}",
        };

        await mainWindowViewModel.OpenTabAsync(tab);
        return true;
    }

    private Task<ITerminalSession> OpenSessionAsync(string targetClientInstance, string? workingDirectory, CancellationToken ct)
    {
        if (this.sessionOpener is not null)
        {
            return this.sessionOpener(targetClientInstance, workingDirectory, ct);
        }

        var command = GetDefaultCommand();
        var payloadNode = new JsonObject
        {
            ["mode"] = "pty",
            ["command"] = command,
        };

        if (workingDirectory is not null)
        {
            payloadNode["working-directory"] = workingDirectory;
        }

        using var payloadDocument = JsonDocument.Parse(payloadNode.ToJsonString());

        var request = new TrustedStreamRequest
        {
            TargetClientInstance = targetClientInstance,
            StreamKind = "shell",
            OpenPayload = payloadDocument.RootElement.Clone(),
        };

        return OpenSessionAsync(request, targetClientInstance, ct);
    }

    private Task<ITerminalSession> OpenSessionAsync(
        TrustedStreamRequest request,
        string targetClientInstance,
        CancellationToken ct)
    {
        var trustProfile = new TrustProfile
        {
            HostingWorkspacesClientInstances = [TrustProfile.WildcardClientInstance],
        };
        var executor = this.executorSelector!.SelectExecutor(trustProfile, targetClientInstance);
        return OpenStreamSessionAsync(executor, request, ct);
    }

    private static async Task<ITerminalSession> OpenStreamSessionAsync(
        ITrustedExecutor executor,
        TrustedStreamRequest request,
        CancellationToken ct)
    {
        var stream = await executor.OpenStreamAsync(request, ct);
        return new StreamTerminalSession(stream);
    }

    private static string GetDefaultCommand()
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "pwsh" : "bash";

    /// <summary>
    /// A thin <see cref="ITerminalSession"/> over the <see cref="StreamMessageChannelStream"/>
    /// returned by <see cref="ITrustedExecutor.OpenStreamAsync"/>. Resize and signal are
    /// forwarded as out-of-band control frames via
    /// <see cref="StreamMessageChannelStream.SendControlAsync"/>.
    /// </summary>
    private sealed class StreamTerminalSession : ITerminalSession
    {
        private readonly StreamMessageChannelStream channelStream;

        public StreamTerminalSession(Stream stream)
        {
            this.channelStream = (StreamMessageChannelStream)stream;
        }

        public Stream Stream => this.channelStream;

        public ValueTask ResizeAsync(int columns, int rows, CancellationToken ct)
            => this.channelStream.SendControlAsync(
                new StreamControlMessage
                {
                    Type = StreamControlMessage.Types.Resize,
                    Columns = columns,
                    Rows = rows,
                },
                ct);

        public ValueTask SignalAsync(string signal, CancellationToken ct)
            => this.channelStream.SendControlAsync(
                new StreamControlMessage
                {
                    Type = StreamControlMessage.Types.Signal,
                    Signal = signal,
                },
                ct);

        public Task<int> WaitForExitAsync()
            => this.channelStream.Completion.ContinueWith(
                static _ => 0,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

        public ValueTask DisposeAsync() => this.channelStream.DisposeAsync();
    }
}



