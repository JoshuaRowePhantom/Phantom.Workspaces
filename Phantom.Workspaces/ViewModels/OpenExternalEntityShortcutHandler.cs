using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Shortcut handler for opening external entities in embedded browser tabs.
/// </summary>
public sealed class OpenExternalEntityShortcutHandler : ShortcutHandler
{
    public override ValueTask<bool> ShouldApplyTo(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        return ValueTask.FromResult(shortcut == Shortcut.Open
               && entityViewModel.IsEntityType("external"));
    }

    public override async Task<bool> Handle(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        // Parse URLs from external entity
        var urls = ParseUrls(entityViewModel);
        if (urls.Count == 0)
        {
            return false;
        }

        // Open the first/default URL in a web view
        var urlKey = urls.ContainsKey("default") ? "default" : urls.Keys.First();
        var url = urls[urlKey];
        var tab = CreateWebTab(mainWindowViewModel, entityViewModel, url, urlKey);

        // Open tab
        await mainWindowViewModel.OpenTabAsync(tab);

        return true;
    }

    /// <summary>
    /// #1129: Restore-aware factory used by the workspace-open/restore path so external
    /// entities open in an embedded browser (mirrors the old ad-hoc branch in
    /// <c>MainWindowViewModel.CreateTabFromEntityAsync</c>) instead of the generic entity
    /// card, while preserving the saved tab-id / title / dock region.
    /// </summary>
    public override Task<WorkspaceTabViewModel?> TryCreateTabForRestoreAsync(
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel entityViewModel,
        string? tabId,
        string? title,
        string? dockRegion)
    {
        var urls = ParseUrls(entityViewModel);
        if (urls.Count == 0)
        {
            return Task.FromResult<WorkspaceTabViewModel?>(null);
        }

        var urlKey = urls.ContainsKey("default") ? "default" : urls.Keys.First();
        var entityUrl = urls[urlKey];
        var isDefault = string.Equals(urlKey, "default", StringComparison.OrdinalIgnoreCase);
        var explicitTitle = string.IsNullOrEmpty(title) ? null : title;

        WorkspaceTabViewModel tab = new WebViewModel(
            entityUrl,
            mainWindowViewModel,
            titleFixed: explicitTitle is not null || !isDefault)
        {
            Id = tabId ?? $"web-{entityViewModel.EntityId}",
            Title = explicitTitle ?? (isDefault ? entityViewModel.DisplayName : urlKey),
            DockRegion = dockRegion ?? "full",
        };
        return Task.FromResult<WorkspaceTabViewModel?>(tab);
    }

    private static WebViewModel CreateWebTab(
        IWorkspaceTabService tabService,
        SubscribedEntityViewModel entity,
        string url,
        string urlKey)
    {
        var isDefault = string.Equals(urlKey, "default", StringComparison.OrdinalIgnoreCase);
        return new WebViewModel(url, tabService, titleFixed: !isDefault)
        {
            Id = $"web-{entity.EntityId}-{urlKey}",
            Title = isDefault ? entity.DisplayName : urlKey,
        };
    }

    public static Dictionary<string, string> ParseUrls(SubscribedEntityViewModel entity)
    {
        var urls = new Dictionary<string, string>();

        if (entity.Data.HasValue && entity.Data.Value.TryGetProperty("urls", out var urlsProperty))
        {
            try
            {
                var parsedUrls = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    urlsProperty.GetRawText());
                if (parsedUrls is not null)
                {
                    foreach (var kvp in parsedUrls)
                    {
                        urls[kvp.Key] = kvp.Value;
                    }
                }
            }
            catch (JsonException)
            {
                // Invalid URLs format, return empty
            }
        }

        return urls;
    }
}



