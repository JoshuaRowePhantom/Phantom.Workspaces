using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Dock.Avalonia.Controls;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.ViewModels;
using Xunit;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Tests;

/// <summary>
/// Regression tests for #1119: the top-level workspace <see cref="DockControl"/>
/// must have <c>AutoCreateDataTemplates=false</c> and carry the Dock DataTemplates
/// directly, so Dock.Avalonia's tab-strip rendering scope can resolve the custom
/// <see cref="WorkspacesPaneDock"/> header template (and glyph indicator templates)
/// that render the aggregated pulsating-brain / exclamation glyphs on outer tabs.
/// </summary>
public sealed class MainWindowDockTemplateTests
{
    [AvaloniaFact(Timeout = 15_000)]
    public void MainWindowDockControl_TopLevelDockControl_DisablesAutoCreateDataTemplates()
    {
        var topLevelDockControl = GetTopLevelDockControl();

        Assert.False(topLevelDockControl.AutoCreateDataTemplates);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void MainWindowDockControl_TopLevelDockControl_HasWorkspacesPaneDockTemplate()
    {
        var topLevelDockControl = GetTopLevelDockControl();

        var paneDock = new WorkspacesPaneDock();
        var matching = topLevelDockControl.DataTemplates
            .OfType<IDataTemplate>()
            .FirstOrDefault(t => t.Match(paneDock));

        Assert.NotNull(matching);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void MainWindowDockControl_TopLevelDockControl_HasTabHeaderGlyphTemplates()
    {
        var topLevelDockControl = GetTopLevelDockControl();

        var tabHeader = new TabHeaderViewModel { Title = "T" };
        var running = new AgentRunningIndicatorTabHeaderItemViewModel();
        var notification = new NotificationIndicatorTabHeaderItemViewModel();

        Assert.NotNull(topLevelDockControl.DataTemplates
            .OfType<IDataTemplate>()
            .FirstOrDefault(t => t.Match(tabHeader)));
        Assert.NotNull(topLevelDockControl.DataTemplates
            .OfType<IDataTemplate>()
            .FirstOrDefault(t => t.Match(running)));
        Assert.NotNull(topLevelDockControl.DataTemplates
            .OfType<IDataTemplate>()
            .FirstOrDefault(t => t.Match(notification)));
    }

    private static DockControl GetTopLevelDockControl()
    {
        var viewModel = CreateTestMainWindowViewModel();
        var window = new MainWindow(viewModel);

        return window.GetLogicalDescendants()
            .OfType<DockControl>()
            .First(dc => dc.Name == "TopLevelDockControl");
    }

    private static MainWindowViewModel CreateTestMainWindowViewModel()
    {
        return new MainWindowViewModel(
            new UnknownRepositorySource(),
            new WorkspacesConfiguration { SkipStartupWorkspace = true },
            new ProfileStore(CreateTempProfileStorePath()),
            applicationServices: null);
    }

    private static string CreateTempProfileStorePath()
    {
        return System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "Phantom.Workspaces.Tests",
            System.Guid.NewGuid().ToString("N"),
            "profile.json");
    }
}

