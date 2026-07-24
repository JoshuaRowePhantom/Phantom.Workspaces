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
/// Handles <see cref="Shortcut.Open"/> on a <c>shell</c> entity: reads the entity's saved shell
/// configuration (<c>mode</c>, <c>command</c>, <c>command-arguments</c>, <c>working-directory</c>,
/// <c>environment</c>) and starts/attaches the interactive terminal via the trusted executor,
/// presenting it as a <see cref="ShellTabViewModel"/>. Registered ahead of
/// <see cref="OpenEntityShortcutHandler"/> so shell entities open a live terminal instead of the
/// generic entity card.
/// </summary>
public sealed class OpenShellEntityShortcutHandler : ShortcutHandler
{
    private readonly ITrustedExecutorSelector? executorSelector;
    private readonly Func<string, ShellEntityOpenSpec, CancellationToken, Task<ITerminalSession>>? sessionOpener;

    /// <summary>Production constructor: uses the supplied <see cref="ITrustedExecutorSelector"/> to route shell sessions to the correct executor.</summary>
    public OpenShellEntityShortcutHandler(ITrustedExecutorSelector executorSelector)
    {
        ArgumentNullException.ThrowIfNull(executorSelector);
        this.executorSelector = executorSelector;
    }

    /// <summary>Test constructor: injects a custom session-opener so no real PTY is spawned.</summary>
    internal OpenShellEntityShortcutHandler(
        Func<string, ShellEntityOpenSpec, CancellationToken, Task<ITerminalSession>> sessionOpener)
    {
        this.sessionOpener = sessionOpener;
    }

    public override ValueTask<bool> ShouldApplyTo(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        return ValueTask.FromResult(shortcut == Shortcut.Open
            && entityViewModel.IsEntityType("shell"));
    }

    public override async Task<bool> Handle(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        var spec = ReadShellSpec(entityViewModel);
        var targetClientInstance = ResolveTargetClientInstance(mainWindowViewModel, entityViewModel);

        var session = await this.OpenSessionAsync(targetClientInstance, spec, CancellationToken.None);

        var tab = new ShellTabViewModel(session)
        {
            // Stable per-entity id so opening the same shell entity twice reuses the existing tab.
            Id = $"shell-entity-{entityViewModel.EntityId}",
            Title = $"{spec.Command} — {entityViewModel.DisplayName}",
        };

        await mainWindowViewModel.OpenTabAsync(tab);
        return true;
    }

    /// <summary>
    /// #1129: Restore-aware factory used by the workspace-open/restore path so shell
    /// entities open a live terminal (the same <see cref="ShellTabViewModel"/> the
    /// top-level Open shortcut produces) instead of the generic entity card, preserving
    /// the persisted tab-id / title / dock region so the restored dock layout is stable.
    /// </summary>
    public override async Task<WorkspaceTabViewModel?> TryCreateTabForRestoreAsync(
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel entityViewModel,
        string? tabId,
        string? title,
        string? dockRegion)
    {
        var spec = ReadShellSpec(entityViewModel);
        var targetClientInstance = ResolveTargetClientInstance(mainWindowViewModel, entityViewModel);

        var session = await this.OpenSessionAsync(targetClientInstance, spec, CancellationToken.None);

        return new ShellTabViewModel(session)
        {
            Id = tabId ?? $"shell-entity-{entityViewModel.EntityId}",
            Title = title ?? $"{spec.Command} — {entityViewModel.DisplayName}",
            DockRegion = dockRegion ?? "full",
        };
    }

    private static ShellEntityOpenSpec ReadShellSpec(SubscribedEntityViewModel entityViewModel)
    {
        string? mode = null;
        string? command = null;
        IReadOnlyList<string> arguments = [];
        string? workingDirectory = null;
        IReadOnlyDictionary<string, string>? environment = null;

        if (entityViewModel.Data is JsonElement data)
        {
            if (data.TryGetProperty("mode", out var modeElement)
                && modeElement.ValueKind == JsonValueKind.String)
            {
                mode = modeElement.GetString();
            }

            if (data.TryGetProperty("command", out var commandElement)
                && commandElement.ValueKind == JsonValueKind.String)
            {
                var value = commandElement.GetString();
                if (!string.IsNullOrEmpty(value))
                {
                    command = value;
                }
            }

            if (data.TryGetProperty("command-arguments", out var argsElement)
                && argsElement.ValueKind == JsonValueKind.Array)
            {
                arguments = argsElement.EnumerateArray()
                    .Where(static a => a.ValueKind == JsonValueKind.String)
                    .Select(static a => a.GetString()!)
                    .ToArray();
            }

            if (data.TryGetProperty("working-directory", out var wdElement)
                && wdElement.ValueKind == JsonValueKind.String)
            {
                var value = wdElement.GetString();
                if (!string.IsNullOrEmpty(value))
                {
                    workingDirectory = value;
                }
            }

            if (data.TryGetProperty("environment", out var envElement)
                && envElement.ValueKind == JsonValueKind.Object)
            {
                var env = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var property in envElement.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        env[property.Name] = property.Value.GetString()!;
                    }
                }

                if (env.Count > 0)
                {
                    environment = env;
                }
            }
        }

        return new ShellEntityOpenSpec
        {
            Mode = string.IsNullOrEmpty(mode) ? "pty" : mode!,
            Command = command ?? GetDefaultCommand(),
            CommandArguments = arguments,
            WorkingDirectory = workingDirectory,
            Environment = environment,
        };
    }

    private static string ResolveTargetClientInstance(
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel entityViewModel)
    {
        // Shell entities are opened on the local user-computer-profile by default. Cross-profile
        // routing for shell entities is out of scope for this handler; the profile-scoped shell
        // handler covers that case for entities directly attached to a profile.
        _ = mainWindowViewModel;
        _ = entityViewModel;
        return TrustProfile.LocalClientInstance;
    }

    private Task<ITerminalSession> OpenSessionAsync(
        string targetClientInstance,
        ShellEntityOpenSpec spec,
        CancellationToken ct)
    {
        if (this.sessionOpener is not null)
        {
            return this.sessionOpener(targetClientInstance, spec, ct);
        }

        var payloadNode = new JsonObject
        {
            ["mode"] = spec.Mode,
            ["command"] = spec.Command,
        };

        if (spec.CommandArguments.Count > 0)
        {
            var argsArray = new JsonArray();
            foreach (var argument in spec.CommandArguments)
            {
                argsArray.Add(argument);
            }
            payloadNode["command-arguments"] = argsArray;
        }

        if (spec.WorkingDirectory is not null)
        {
            payloadNode["working-directory"] = spec.WorkingDirectory;
        }

        if (spec.Environment is not null && spec.Environment.Count > 0)
        {
            var envObject = new JsonObject();
            foreach (var (key, value) in spec.Environment)
            {
                envObject[key] = value;
            }
            payloadNode["environment"] = envObject;
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

/// <summary>
/// Typed view of the shell entity's saved configuration used to build the shell open payload.
/// Consumed by <see cref="OpenShellEntityShortcutHandler"/>'s test-injected session opener.
/// </summary>
public sealed record ShellEntityOpenSpec
{
    public required string Mode { get; init; }
    public required string Command { get; init; }
    public IReadOnlyList<string> CommandArguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
}
