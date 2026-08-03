using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Services.Notifications;
using Phantom.Workspaces.Tools;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Handles <see cref="Shortcut.VsCode"/> on filesystem entities (git-worktree, filesystem-path).
/// For local entities, runs <c>code &lt;path&gt;</c> through the shared
/// <see cref="VsCodeCliInvoker"/> so stdout, stderr, and exit code are always logged and
/// surfaced to the user on failure. For remote entities with a vscode-tunnel, opens
/// <c>vscode://vscode-remote/tunnel+&lt;tunnel-name&gt;/&lt;path&gt;</c> via shell execute and
/// logs / notifies on launch failure.
/// </summary>
public sealed class OpenInVsCodeShortcutHandler : ShortcutHandler
{
    private readonly Func<string> cliLocator;
    private readonly Func<RunProcessParameters, CancellationToken, Task<ProcessResult>>? processRunner;
    private readonly Func<string, Task>? urlLauncher;
    private readonly Func<MainWindowViewModel, SubscribedEntityViewModel, Task<string?>>? remoteTunnelNameResolver;
    private readonly ILogger logger;

    /// <summary>Production constructor: uses default VS Code CLI locator and process runner.</summary>
    public OpenInVsCodeShortcutHandler()
    {
        this.cliLocator = VsCodeCliLocator.ResolveDefaultCliPath;
        this.processRunner = null;
        this.urlLauncher = null;
        this.remoteTunnelNameResolver = null;
        this.logger = NullLogger.Instance;
    }

    /// <summary>Test constructor: injects custom locator, process runner, and URL launcher.</summary>
    internal OpenInVsCodeShortcutHandler(
        Func<string> cliLocator,
        Func<RunProcessParameters, CancellationToken, Task<ProcessResult>>? processRunner,
        Func<string, Task>? urlLauncher,
        ILogger? logger = null,
        Func<MainWindowViewModel, SubscribedEntityViewModel, Task<string?>>? remoteTunnelNameResolver = null)
    {
        this.cliLocator = cliLocator;
        this.processRunner = processRunner;
        this.urlLauncher = urlLauncher;
        this.remoteTunnelNameResolver = remoteTunnelNameResolver;
        this.logger = logger ?? NullLogger.Instance;
    }

    public override async ValueTask<bool> ShouldApplyTo(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        if (shortcut != Shortcut.VsCode)
        {
            return false;
        }

        var path = TryGetPath(entityViewModel);
        if (path is null)
        {
            return false;
        }

        var owningProfile = await FindOwningProfileAsync(mainWindowViewModel, entityViewModel);
        var localProfileId = mainWindowViewModel.EntityBroker.EntityRepository
            .WorkspaceEntitySession.UserComputerProfileEntityId;

        var isLocal = owningProfile is null || owningProfile.EntityId == localProfileId;
        if (isLocal)
        {
            return true;
        }

        if (owningProfile is null)
        {
            return false;
        }

        var tunnelEntity = await TryFindVsCodeTunnelAsync(mainWindowViewModel, owningProfile);
        return tunnelEntity is not null;
    }

    public override async Task<bool> Handle(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        var path = TryGetPath(entityViewModel);
        if (path is null)
        {
            return false;
        }

        if (this.remoteTunnelNameResolver is not null)
        {
            var resolvedTunnelName = await this.remoteTunnelNameResolver(mainWindowViewModel, entityViewModel);
            if (resolvedTunnelName is null)
            {
                return await HandleLocalEntityAsync(mainWindowViewModel, path);
            }

            return await LaunchRemoteVsCodeUriAsync(mainWindowViewModel, resolvedTunnelName, path);
        }

        var owningProfile = await FindOwningProfileAsync(mainWindowViewModel, entityViewModel);
        var localProfileId = mainWindowViewModel.EntityBroker.EntityRepository
            .WorkspaceEntitySession.UserComputerProfileEntityId;

        var isLocal = owningProfile is null || owningProfile.EntityId == localProfileId;

        if (isLocal)
        {
            return await HandleLocalEntityAsync(mainWindowViewModel, path);
        }
        else
        {
            if (owningProfile is null)
            {
                return false;
            }

            return await HandleRemoteEntityAsync(mainWindowViewModel, owningProfile, path);
        }
    }

    private async Task<bool> HandleLocalEntityAsync(MainWindowViewModel mainWindowViewModel, string path)
    {
        string cliPath;
        try
        {
            cliPath = this.cliLocator();
        }
        catch (Win32Exception)
        {
            mainWindowViewModel.NotificationService.Notify(
                new Notification(
                    new TabDescriptor
                    {
                        TabId = $"vscode-cli:{path}",
                        TabTitle = "VS Code",
                    },
                    "VS Code CLI not found",
                    "VS Code CLI ('code') was not found on PATH. Install VS Code and ensure 'code' is on your PATH.",
                    DateTime.UtcNow,
                    RunningState.Idle,
                    NotificationState.Interesting));
            return false;
        }

        var invoker = new VsCodeCliInvoker(
            notificationService: mainWindowViewModel.NotificationService,
            logger: this.logger,
            processRunner: this.processRunner);

        try
        {
            await invoker.RunAsync(
                cliPath,
                path,
                operationDescription: $"open {path}",
                VsCodeCliReporting.LogAndReportOnFailure,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Win32Exception)
        {
            // Invoker already logged and notified via TryNotify on Win32Exception.
            return false;
        }
    }

    private async Task<bool> HandleRemoteEntityAsync(
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel owningProfile,
        string path)
    {
        var tunnelEntity = await TryFindVsCodeTunnelAsync(mainWindowViewModel, owningProfile);
        if (tunnelEntity is null)
        {
            return false;
        }

        var tunnelName = ReadTunnelName(tunnelEntity);
        if (tunnelName is null)
        {
            return false;
        }

        return await LaunchRemoteVsCodeUriAsync(mainWindowViewModel, tunnelName, path);
    }

    private async Task<bool> LaunchRemoteVsCodeUriAsync(
        MainWindowViewModel mainWindowViewModel,
        string tunnelName,
        string path)
    {
        var url = $"vscode://vscode-remote/tunnel+{tunnelName}{path}";

        try
        {
            if (this.urlLauncher is not null)
            {
                await this.urlLauncher(url).ConfigureAwait(false);
            }
            else
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }

            return true;
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Failed to launch VS Code remote URI '{Url}': {Message}", url, ex.Message);
            mainWindowViewModel.NotificationService.Notify(
                new Notification(
                    new TabDescriptor
                    {
                        TabId = $"vscode-cli:remote:{tunnelName}",
                        TabTitle = "VS Code",
                    },
                    "VS Code remote launch failed",
                    $"Could not open '{url}': {ex.Message}",
                    DateTime.UtcNow,
                    RunningState.Idle,
                    NotificationState.Interesting));
            return false;
        }
    }

    private static string? TryGetPath(SubscribedEntityViewModel entityViewModel)
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

        return null;
    }

    private static async Task<SubscribedEntityViewModel?> FindOwningProfileAsync(
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel entityViewModel)
    {
        if (entityViewModel.IsEntityType("user-computer-profile"))
        {
            return entityViewModel;
        }

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

    private static async Task<SubscribedEntityViewModel?> TryFindVsCodeTunnelAsync(
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel owningProfile)
    {
        if (owningProfile.Data is not JsonElement profileData
            || !profileData.TryGetProperty("names", out var namesElement)
            || namesElement.ValueKind != JsonValueKind.Array
            || namesElement.GetArrayLength() == 0)
        {
            return null;
        }

        var primaryNameElement = namesElement[0];
        if (primaryNameElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var nameParts = primaryNameElement.EnumerateArray()
            .Where(static e => e.ValueKind == JsonValueKind.String)
            .Select(static e => e.GetString()!)
            .ToArray();

        string? userSegment = null;
        for (int i = 0; i < nameParts.Length - 1; i++)
        {
            if (nameParts[i] is "username" or "user-computer-profile")
            {
                userSegment = nameParts[i + 1];
                break;
            }
        }

        if (userSegment is null)
        {
            return null;
        }

        var tunnelName = new EntityName([userSegment, "vscode-tunnel"]);
        var request = new GetEntityRequest { EntityName = tunnelName };

        var entities = await mainWindowViewModel.EntityBroker.GetEntitiesAsync([request]);
        return entities.FirstOrDefault(e => e.IsEntityType("vscode-tunnel"));
    }

    private static string? ReadTunnelName(SubscribedEntityViewModel tunnelEntity)
    {
        if (tunnelEntity.Data is not JsonElement data)
        {
            return null;
        }

        if (data.TryGetProperty("tunnel-name", out var nameElement)
            && nameElement.ValueKind == JsonValueKind.String)
        {
            return nameElement.GetString();
        }

        return null;
    }
}
