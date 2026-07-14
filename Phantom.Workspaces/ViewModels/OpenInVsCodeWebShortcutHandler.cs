using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Handles <see cref="Shortcut.VsCodeWeb"/> on filesystem entities (git-worktree, filesystem-path).
/// Opens VS Code Web in an in-app WebView browser tab via the vscode-tunnel URL.
/// </summary>
public sealed class OpenInVsCodeWebShortcutHandler : ShortcutHandler
{
    private readonly Func<MainWindowViewModel, WorkspaceTabViewModel, Task>? tabOpener;

    /// <summary>Production constructor: uses MainWindowViewModel.OpenTabAsync to open the tab.</summary>
    public OpenInVsCodeWebShortcutHandler()
    {
        this.tabOpener = null;
    }

    /// <summary>Test constructor: injects custom tab opener for testing.</summary>
    internal OpenInVsCodeWebShortcutHandler(
        Func<MainWindowViewModel, WorkspaceTabViewModel, Task>? tabOpener)
    {
        this.tabOpener = tabOpener;
    }

    public override bool ShouldApplyTo(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        if (shortcut != Shortcut.VsCodeWeb)
        {
            return false;
        }

        var path = TryGetPath(entityViewModel);
        return path is not null;
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

        var owningProfile = await FindOwningProfileAsync(mainWindowViewModel, entityViewModel);
        var tunnelEntity = await TryFindVsCodeTunnelAsync(mainWindowViewModel, owningProfile);
        if (tunnelEntity is null)
        {
            return false;
        }

        var tunnelUrl = ReadTunnelUrl(tunnelEntity);
        if (tunnelUrl is null)
        {
            return false;
        }

        var encodedPath = Uri.EscapeDataString(path);
        var url = $"{tunnelUrl}?folder={encodedPath}";

        var tabTitle = $"VS Code Web — {entityViewModel.DisplayName}";
        var tabId = $"vscode-web-{entityViewModel.EntityId}";

        var tab = new WebViewModel(url, mainWindowViewModel, titleFixed: true)
        {
            Id = tabId,
            Title = tabTitle,
        };

        if (this.tabOpener is not null)
        {
            await this.tabOpener(mainWindowViewModel, tab);
        }
        else
        {
            await mainWindowViewModel.OpenTabAsync(tab);
        }

        return true;
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
        SubscribedEntityViewModel? owningProfile)
    {
        if (owningProfile is null)
        {
            var localProfileId = mainWindowViewModel.EntityBroker.EntityRepository
                .WorkspaceEntitySession.UserComputerProfileEntityId;
            var localProfiles = await mainWindowViewModel.EntityBroker.GetEntitiesAsync([localProfileId]);
            owningProfile = localProfiles.FirstOrDefault();
            if (owningProfile is null)
            {
                return null;
            }
        }

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
            if (nameParts[i] == "username")
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

    private static string? ReadTunnelUrl(SubscribedEntityViewModel tunnelEntity)
    {
        if (tunnelEntity.Data is not JsonElement data)
        {
            return null;
        }

        if (data.TryGetProperty("tunnel-url", out var urlElement)
            && urlElement.ValueKind == JsonValueKind.String)
        {
            return urlElement.GetString();
        }

        return null;
    }
}
