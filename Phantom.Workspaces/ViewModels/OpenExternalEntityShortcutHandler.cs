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
    public override bool ShouldApplyTo(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        return shortcut == Shortcut.Open
               && entityViewModel.IsEntityType("external");
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
        var url = urls.ContainsKey("default") ? urls["default"] : urls.First().Value;
        var tab = CreateWebTab(mainWindowViewModel, entityViewModel, url);

        // Open tab
        await mainWindowViewModel.OpenTabAsync(tab);

        return true;
    }

    private static WebViewModel CreateWebTab(
        IWorkspaceTabService tabService,
        SubscribedEntityViewModel entity,
        string url)
    {
        return new WebViewModel(url, tabService)
        {
            Id = $"web-{entity.EntityId}",
            Title = entity.DisplayName,
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

