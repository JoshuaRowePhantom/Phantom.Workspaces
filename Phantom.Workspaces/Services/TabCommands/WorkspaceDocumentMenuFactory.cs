using System.Collections;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Dock.Avalonia.Controls;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Services.TabCommands;

internal sealed class WorkspaceDocumentMenuFactory
{
    private static readonly object ContributionMarker = new();
    private readonly Dictionary<ContextMenu, ActiveSubscription> activeSubscriptions = [];

    internal void Apply(ContextMenu menu, DocumentTabStripItem tabItem)
    {
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(tabItem);

        this.Clear(menu);

        if (tabItem.DataContext is not WorkspaceDocument document)
        {
            return;
        }

        if (!TryResolveMainWindow(document, tabItem, out var mainWindow))
        {
            return;
        }

        if (!TabCommandContext.TryCreateForExplicitTarget(mainWindow, document, out var context)
            || context is null)
        {
            return;
        }

        var provider = context.Tab.TabCommands;
        var descriptors = provider.GetCommands(context)
            .Select(descriptor => (Descriptor: descriptor, Availability: descriptor.Availability?.Invoke(context) ?? TabCommandAvailability.Enabled))
            .Where(entry => entry.Availability.IsVisible)
            .OrderBy(entry => entry.Descriptor.Group, StringComparer.Ordinal)
            .ThenBy(entry => entry.Descriptor.Order)
            .ThenBy(entry => entry.Descriptor.Id, StringComparer.Ordinal)
            .ToArray();

        if (descriptors.Length == 0)
        {
            return;
        }

        if (menu.Items is not IList items)
        {
            return;
        }

        var contributedItems = new List<object>();
        var hasVisibleDefaults = items.Cast<object>().OfType<Control>().Any(control => control.IsVisible);
        if (hasVisibleDefaults)
        {
            contributedItems.Add(CreateContributionSeparator());
        }

        string? previousGroup = null;
        foreach (var entry in descriptors)
        {
            // Normalize contribution-group separators after visibility filtering: a single
            // separator between distinct contribution groups, none leading/trailing/doubled.
            if (previousGroup is not null
                && !string.Equals(previousGroup, entry.Descriptor.Group, StringComparison.Ordinal))
            {
                contributedItems.Add(CreateContributionSeparator());
            }

            previousGroup = entry.Descriptor.Group;

            var menuItem = new MenuItem
            {
                Header = entry.Descriptor.Header,
                IsEnabled = entry.Availability.IsEnabled && !context.Invoker.IsInFlight(context, entry.Descriptor.Id),
                Tag = ContributionMarker,
                Command = new AsyncRelayCommand(
                    async _ =>
                    {
                        await context.Invoker.InvokeAsync(entry.Descriptor, context, CancellationToken.None).ConfigureAwait(false);
                        Dispatcher.UIThread.Post(
                            () =>
                            {
                                if (menu.IsOpen)
                                {
                                    this.Apply(menu, tabItem);
                                }
                            },
                            DispatcherPriority.Input);
                    },
                    _ => true,
                    allowConcurrentExecutions: false),
            };

            var accessibleName = entry.Descriptor.AccessibleLabel ?? entry.Descriptor.Header;
            AutomationProperties.SetName(menuItem, accessibleName);
            if (!entry.Availability.IsEnabled && !string.IsNullOrWhiteSpace(entry.Availability.DisabledReason))
            {
                ToolTip.SetTip(menuItem, entry.Availability.DisabledReason);
            }

            contributedItems.Add(menuItem);
        }

        foreach (var contributed in contributedItems)
        {
            _ = items.Add(contributed);
        }

        EventHandler? commandsChanged = null;
        commandsChanged = (_, _) =>
        {
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (menu.IsOpen)
                    {
                        this.Apply(menu, tabItem);
                    }
                },
                DispatcherPriority.Input);
        };

        provider.CommandsChanged += commandsChanged;
        this.activeSubscriptions[menu] = new ActiveSubscription(provider, commandsChanged);
    }

    internal void Clear(ContextMenu menu)
    {
        ArgumentNullException.ThrowIfNull(menu);
        if (this.activeSubscriptions.TryGetValue(menu, out var subscription))
        {
            subscription.Provider.CommandsChanged -= subscription.Handler;
            this.activeSubscriptions.Remove(menu);
        }

        if (menu.Items is not IList items)
        {
            return;
        }

        for (var index = items.Count - 1; index >= 0; index--)
        {
            if (items[index] is Separator separator && Equals(separator.Tag, ContributionMarker))
            {
                items.RemoveAt(index);
                continue;
            }

            if (items[index] is MenuItem menuItem && Equals(menuItem.Tag, ContributionMarker))
            {
                items.RemoveAt(index);
            }
        }
    }

    private static Separator CreateContributionSeparator() =>
        new()
        {
            Tag = ContributionMarker,
        };

    private static bool TryResolveMainWindow(
        WorkspaceDocument document,
        DocumentTabStripItem tabItem,
        out MainWindowViewModel mainWindow)
    {
        mainWindow = null!;
        for (var current = document.Owner; current is not null; current = current.Owner)
        {
            if (current is { Context: MainWindowViewModel vm })
            {
                mainWindow = vm;
                return true;
            }
        }

        if (TopLevel.GetTopLevel(tabItem) is { DataContext: MainWindowViewModel topLevelVm })
        {
            mainWindow = topLevelVm;
            return true;
        }

        return false;
    }

    private sealed record ActiveSubscription(ITabCommandProvider Provider, EventHandler Handler);
}
