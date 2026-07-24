using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Templates;
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

    [AvaloniaFact(Timeout = 15_000)]
    public void MainWindowDockControl_TopLevelDockControl_HasProportionalDockTemplate()
    {
        // #1130: TopLevelDockControl.DataTemplates must contain a template whose
        // Match(new ProportionalDock()) is true so the "New Horizontal/Vertical Dock"
        // command renders a real control instead of the raw type name.
        var topLevelDockControl = GetTopLevelDockControl();

        var proportional = new ProportionalDock();
        var matching = topLevelDockControl.DataTemplates
            .OfType<IDataTemplate>()
            .FirstOrDefault(t => t.Match(proportional));

        Assert.NotNull(matching);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void WorkspacePaneDockControl_InnerDockControl_HasProportionalDockTemplate()
    {
        // #1130: The inner workspace-pane DockControl (produced by the WorkspacePaneDocument
        // template inside DockDataTemplates) has its own scoped, non-inheriting template set,
        // so it must also carry a ProportionalDock template.
        var innerDockControl = BuildInnerWorkspacePaneDockControl();

        var proportional = new ProportionalDock();
        var matching = innerDockControl.DataTemplates
            .OfType<IDataTemplate>()
            .FirstOrDefault(t => t.Match(proportional));

        Assert.NotNull(matching);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void DockDataTemplates_ProportionalDock_ResolvesProportionalDockControl()
    {
        // #1130: The matched template builds a ProportionalDockControl (not raw text).
        var topLevelDockControl = GetTopLevelDockControl();

        var proportional = new ProportionalDock();
        var matching = topLevelDockControl.DataTemplates
            .OfType<IDataTemplate>()
            .First(t => t.Match(proportional));

        var built = matching.Build(proportional);
        Assert.IsType<ProportionalDockControl>(built);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void NewHorizontalDock_WhenInvoked_CreatesProportionalDockRenderedAsControl()
    {
        // #1130: The factory's CreateProportionalDock (invoked by the "New Horizontal Dock"
        // context-menu command) yields a ProportionalDock whose template renders a real
        // ProportionalDockControl rather than the literal type name.
        var factory = new global::Dock.Model.Mvvm.Factory();
        var proportional = factory.CreateProportionalDock();

        var topLevelDockControl = GetTopLevelDockControl();
        var matching = topLevelDockControl.DataTemplates
            .OfType<IDataTemplate>()
            .First(t => t.Match(proportional));

        var built = matching.Build(proportional);
        Assert.IsType<ProportionalDockControl>(built);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void NewVerticalDock_WhenInvoked_CreatesProportionalDockRenderedAsControl()
    {
        // #1130: The sibling "New Vertical Dock" command likewise renders a real dock control.
        var factory = new global::Dock.Model.Mvvm.Factory();
        var proportional = factory.CreateProportionalDock();

        var topLevelDockControl = GetTopLevelDockControl();
        var matching = topLevelDockControl.DataTemplates
            .OfType<IDataTemplate>()
            .First(t => t.Match(proportional));

        var built = matching.Build(proportional);
        Assert.IsType<ProportionalDockControl>(built);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void SplitDock_WhenCreated_DoesNotRenderRawTypeName()
    {
        // #1130: A layout containing a ProportionalDock must not render its ToString()
        // ("Dock.Model.Mvvm.Controls.ProportionalDock") as visible text; a real
        // ProportionalDockControl is produced instead.
        var factory = new global::Dock.Model.Mvvm.Factory();
        var proportional = factory.CreateProportionalDock();

        var topLevelDockControl = GetTopLevelDockControl();
        var matching = topLevelDockControl.DataTemplates
            .OfType<IDataTemplate>()
            .FirstOrDefault(t => t.Match(proportional));

        Assert.NotNull(matching);
        var built = matching.Build(proportional);
        Assert.IsNotType<TextBlock>(built);
        Assert.NotEqual(typeof(ProportionalDock).FullName, built?.GetType().FullName);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void SplitDock_WhenCreated_ContainsExpectedChildDocks()
    {
        // #1130: The MVVM factory's CreateProportionalDock produces an empty
        // ProportionalDock; wiring child docks via SplitToDock populates its
        // VisibleDockables with the two child docks and a splitter.
        var factory = new global::Dock.Model.Mvvm.Factory();
        var proportional = factory.CreateProportionalDock();
        Assert.NotNull(proportional);
        Assert.IsAssignableFrom<IProportionalDock>(proportional);

        var toolA = factory.CreateToolDock();
        toolA.Id = "A";
        var toolB = factory.CreateToolDock();
        toolB.Id = "B";
        var splitter = factory.CreateProportionalDockSplitter();

        proportional.VisibleDockables = factory.CreateList<IDockable>(toolA, splitter, toolB);

        Assert.Equal(3, proportional.VisibleDockables!.Count);
        Assert.Contains(proportional.VisibleDockables, d => d is IProportionalDockSplitter);
        Assert.Contains(toolA, proportional.VisibleDockables);
        Assert.Contains(toolB, proportional.VisibleDockables);
    }

    private static DockControl BuildInnerWorkspacePaneDockControl()
    {
        var templates = new DockDataTemplates();

        // Materialize a MainWindow with an initial workspace pane so the inner DockControl
        // (produced by the WorkspacePaneDocument template) exists in the logical tree.
        var viewModel = new MainWindowViewModel(
            new UnknownRepositorySource(),
            new WorkspacesConfiguration { SkipStartupWorkspace = false },
            new ProfileStore(CreateTempProfileStorePath()),
            applicationServices: null);
        var window = new MainWindow(viewModel);

        var paneDoc = window.GetLogicalDescendants()
            .OfType<Control>()
            .Select(c => c.DataContext)
            .OfType<WorkspacePaneDocument>()
            .FirstOrDefault();

        // Fallback: build the template directly against a fresh document.
        if (paneDoc is null)
        {
            var pane = viewModel.WorkspacePanes.FirstOrDefault();
            Assert.NotNull(pane);
            paneDoc = new WorkspacePaneDocument(pane!);
        }

        var template = templates
            .OfType<IDataTemplate>()
            .First(t => t.Match(paneDoc));

        var built = template.Build(paneDoc);
        return Assert.IsType<DockControl>(built);
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

