using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm.Shell;
using Phantom.Workspaces.Llm.Trust;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Handles <see cref="Shortcut.StartShell"/> on any entity that exposes a filesystem path via a
/// <c>path</c> field (e.g. <c>git</c>, <c>git-worktree</c>, <c>filesystem-path</c>) or a
/// <c>home-directory</c> field (e.g. <c>user-computer-profile</c>). The path is pre-filled as
/// the shell's working directory. The owning user-computer-profile is derived from the entity's
/// name hierarchy to route the shell to the correct machine.
/// </summary>
public sealed class StartShellFromEntityShortcutHandler : ShortcutHandler
{
    private readonly ITrustedExecutorSelector? executorSelector;
    private readonly Func<string, string?, CancellationToken, Task<ITerminalSession>>? sessionOpener;

    /// <summary>Production constructor: uses the supplied <see cref="ITrustedExecutorSelector"/> to route shell sessions to the correct executor.</summary>
    public StartShellFromEntityShortcutHandler(ITrustedExecutorSelector executorSelector)
    {
        ArgumentNullException.ThrowIfNull(executorSelector);
        this.executorSelector = executorSelector;
    }

    /// <summary>Test constructor: injects a custom session-opener so no real PTY is spawned.</summary>
    internal StartShellFromEntityShortcutHandler(
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
            && TryGetWorkingDirectory(entityViewModel) is not null);
    }

    public override async Task<bool> Handle(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        var workingDirectory = TryGetWorkingDirectory(entityViewModel);
        var targetClientInstance = await ResolveTargetClientInstanceAsync(mainWindowViewModel, entityViewModel);

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

    /// <summary>
    /// Returns the working directory for the entity: the <c>path</c> field takes priority over
    /// <c>home-directory</c>. Returns <see langword="null"/> when neither is present (i.e. this
    /// handler does not apply to the entity).
    /// </summary>
    private static string? TryGetWorkingDirectory(SubscribedEntityViewModel entityViewModel)
    {
        if (entityViewModel.Data is not JsonElement data)
        {
            return null;
        }

        if (data.TryGetProperty("path", out var pathElement)
            && pathElement.ValueKind == JsonValueKind.String)
        {
            var path = pathElement.GetString();
            if (!string.IsNullOrEmpty(path))
            {
                return path;
            }
        }

        if (data.TryGetProperty("home-directory", out var homeDirElement)
            && homeDirElement.ValueKind == JsonValueKind.String)
        {
            var homeDir = homeDirElement.GetString();
            if (!string.IsNullOrEmpty(homeDir))
            {
                return homeDir;
            }
        }

        return null;
    }

    private static async Task<string> ResolveTargetClientInstanceAsync(
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel entityViewModel)
    {
        var localProfileId = mainWindowViewModel.EntityBroker.EntityRepository
            .WorkspaceEntitySession.UserComputerProfileEntityId;

        if (entityViewModel.IsEntityType("user-computer-profile"))
        {
            return entityViewModel.EntityId == localProfileId
                ? TrustProfile.LocalClientInstance
                : entityViewModel.EntityId.ToString();
        }

        // For path-based entities (git, git-worktree, filesystem-path, …) derive the owning
        // user-computer-profile from the entity's name hierarchy. The git-workspace discovery
        // tool writes the owning profile's primary name as a secondary name on each entity it
        // discovers, so querying by those names and filtering for user-computer-profile yields
        // the profile entity that owns this filesystem entity.
        var profileEntity = await FindOwningProfileAsync(mainWindowViewModel, entityViewModel);
        if (profileEntity is null)
        {
            return TrustProfile.LocalClientInstance;
        }

        return profileEntity.EntityId == localProfileId
            ? TrustProfile.LocalClientInstance
            : profileEntity.EntityId.ToString();
    }

    private static async Task<SubscribedEntityViewModel?> FindOwningProfileAsync(
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel entityViewModel)
    {
        if (entityViewModel.Data is not JsonElement data
            || !data.TryGetProperty("names", out var namesElement)
            || namesElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var nameRequests = ReadEntityNameRequests(namesElement);
        if (nameRequests.Count == 0)
        {
            return null;
        }

        var entities = await mainWindowViewModel.EntityBroker.GetEntitiesAsync(nameRequests);
        return entities.FirstOrDefault(e => e.IsEntityType("user-computer-profile"));
    }

    private static IReadOnlyCollection<GetEntityRequest> ReadEntityNameRequests(JsonElement namesElement)
    {
        var requests = new List<GetEntityRequest>();
        foreach (var nameArray in namesElement.EnumerateArray())
        {
            if (nameArray.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var parts = nameArray.EnumerateArray()
                .Where(static part => part.ValueKind == JsonValueKind.String)
                .Select(static part => part.GetString())
                .Where(static part => !string.IsNullOrEmpty(part))
                .Cast<string>()
                .ToArray();

            if (parts.Length > 0)
            {
                requests.Add(new GetEntityRequest { EntityName = new EntityName(parts) });
            }
        }

        return requests;
    }

    private Task<ITerminalSession> OpenSessionAsync(
        string targetClientInstance,
        string? workingDirectory,
        CancellationToken ct)
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

        return OpenStreamSessionAsync(request, targetClientInstance, ct);
    }

    private Task<ITerminalSession> OpenStreamSessionAsync(
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


