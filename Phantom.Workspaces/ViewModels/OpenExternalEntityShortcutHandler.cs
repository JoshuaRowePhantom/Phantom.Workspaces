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



