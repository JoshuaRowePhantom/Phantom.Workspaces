using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
        // #1196: The outer tab-header body is a single keyed resource
        // (TabHeaderTemplate) referenced explicitly via
        // ContentControl.ContentTemplate from each DocumentControl.HeaderTemplate,
        // and the two indicator DataTemplates live in Application.Resources
        // (TabHeaderItemTemplates.axaml), referenced via {StaticResource} from
        // inside the TabHeaderTemplate. Verify all three wiring points.
        var topLevelDockControl = GetTopLevelDockControl();

        // The implicit vm:TabHeaderViewModel DataTemplate must NOT live on the
        // DockControl any more — the tab-strip scope could not reach it, which
        // was the root cause of the regression.
        var tabHeader = new TabHeaderViewModel { Title = "T" };
        Assert.Null(topLevelDockControl.DataTemplates
            .OfType<IDataTemplate>()
            .FirstOrDefault(t => t.Match(tabHeader)));

        Assert.NotNull(Avalonia.Application.Current);
        Assert.True(Avalonia.Application.Current!.TryFindResource(
            "TabHeaderTemplate", null, out _));
        Assert.True(Avalonia.Application.Current!.TryFindResource(
            "AgentRunningIndicatorTabHeaderItemTemplate", null, out _));
        Assert.True(Avalonia.Application.Current!.TryFindResource(
            "NotificationIndicatorTabHeaderItemTemplate", null, out _));
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
    public void WorkspacePaneDocumentTemplate_DockControl_DeclaresInstallOnTopLevelTrue()
    {
        // #1329: the inner WorkspacePaneDocument DockControl must opt into top-level key
        // sourcing (InstallOnTopLevel=True) exactly like the outer TopLevelDockControl, so the
        // Alt+Digit chord fires regardless of where focus currently lives — symmetric with the
        // outer Alt+Shift+Digit chord. Guards the XAML gap from silently returning.
        var innerDockControl = BuildInnerWorkspacePaneDockControl();

        Assert.True(
            Phantom.Dock.Avalonia.TabSwitching.DockTabSwitch.GetInstallOnTopLevel(innerDockControl));
        Assert.True(
            Phantom.Dock.Avalonia.TabSwitching.DockTabSwitch.GetEnabled(innerDockControl));
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

    [AvaloniaFact(Timeout = 15_000)]
    public void MainWindowDockControl_TopLevelDockControl_HasProportionalDockSplitterTemplate()
    {
        // #1130 (reopened): TopLevelDockControl.DataTemplates must contain a template whose
        // Match(new ProportionalDockSplitter()) is true, otherwise a runtime split renders
        // the raw type name Dock.Model.Mvvm.Controls.ProportionalDockSplitter.
        var topLevelDockControl = GetTopLevelDockControl();

        var splitter = new ProportionalDockSplitter();
        var matching = topLevelDockControl.DataTemplates
            .OfType<IDataTemplate>()
            .FirstOrDefault(t => t.Match(splitter));

        Assert.NotNull(matching);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void MainWindowDockControl_TopLevelDockControl_HasDocumentDockTemplate()
    {
        // #1130 (reopened): TopLevelDockControl.DataTemplates must contain a template whose
        // Match(new DocumentDock()) is true. The typed subclasses WorkspaceContentDock /
        // WorkspacesPaneDock do not cover the plain base DocumentDock produced by a
        // runtime split via CreateDocumentDock().
        var topLevelDockControl = GetTopLevelDockControl();

        var doc = new DocumentDock();
        var matching = topLevelDockControl.DataTemplates
            .OfType<IDataTemplate>()
            .FirstOrDefault(t => t.Match(doc));

        Assert.NotNull(matching);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void WorkspacePaneDockControl_InnerDockControl_HasProportionalDockSplitterTemplate()
    {
        // #1130 (reopened): inner workspace-pane DockControl has its own scoped,
        // non-inheriting template set and must also carry a splitter template.
        var innerDockControl = BuildInnerWorkspacePaneDockControl();

        var splitter = new ProportionalDockSplitter();
        var matching = innerDockControl.DataTemplates
            .OfType<IDataTemplate>()
            .FirstOrDefault(t => t.Match(splitter));

        Assert.NotNull(matching);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void WorkspacePaneDockControl_InnerDockControl_HasDocumentDockTemplate()
    {
        // #1130 (reopened): inner workspace-pane DockControl must also resolve the
        // base DocumentDock.
        var innerDockControl = BuildInnerWorkspacePaneDockControl();

        var doc = new DocumentDock();
        var matching = innerDockControl.DataTemplates
            .OfType<IDataTemplate>()
            .FirstOrDefault(t => t.Match(doc));

        Assert.NotNull(matching);
    }

    // ── Regression tests for #1307 ────────────────────────────────────────────
    // Split-created document docks (via NewHorizontalDocumentDock / NewVerticalDocumentDock)
    // must be WorkspaceContentDock so tabs they host match the rich header template
    // (favicon + MaxWidth=180 + CharacterEllipsis) instead of the bare IDocumentDock fallback.

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspaceDockFactory_CreateDocumentDock_ReturnsWorkspaceContentDock()
    {
        await using var viewModel = CreateBootedMainWindowViewModel();
        await viewModel.InitializeAsync();
        var factory = GetDockFactory(viewModel);

        var created = factory.CreateDocumentDock();

        Assert.IsType<WorkspaceContentDock>(created);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspaceDockFactory_CreateDocumentDock_AssignsFreshUniqueId()
    {
        await using var viewModel = CreateBootedMainWindowViewModel();
        await viewModel.InitializeAsync();
        var factory = GetDockFactory(viewModel);

        var a = factory.CreateDocumentDock();
        var b = factory.CreateDocumentDock();

        Assert.False(string.IsNullOrEmpty(a.Id));
        Assert.False(string.IsNullOrEmpty(b.Id));
        Assert.NotEqual(a.Id, b.Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspaceDockFactory_CreateDocumentDock_MatchesRichHeaderTemplate_InInnerPaneDockControl()
    {
        // #1307: the split-created dock must be picked up by the rich WorkspaceContentDock
        // template ahead of the generic IDocumentDock fallback in the inner workspace-pane
        // DockControl.DataTemplates scope.
        await using var viewModel = CreateBootedMainWindowViewModel();
        await viewModel.InitializeAsync();
        var factory = GetDockFactory(viewModel);

        var splitCreated = factory.CreateDocumentDock();
        var innerDockControl = BuildInnerWorkspacePaneDockControl();

        var matching = innerDockControl.DataTemplates
            .OfType<IDataTemplate>()
            .FirstOrDefault(t => t.Match(splitCreated));

        Assert.NotNull(matching);
        // Ensure it is the WorkspaceContentDock-specific template (matches WorkspaceContentDock
        // but does NOT match a plain DocumentDock), not the generic IDocumentDock fallback.
        Assert.True(matching!.Match(new WorkspaceContentDock()));
        Assert.False(matching!.Match(new DocumentDock()));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void DockDataTemplates_ProportionalDockSplitter_ResolvesProportionalStackPanelSplitter()
    {
        // #1130 (reopened): the matched template builds the ProportionalStackPanelSplitter
        // primitive that participates correctly in ProportionalDockControl's layout.
        var topLevelDockControl = GetTopLevelDockControl();

        var splitter = new ProportionalDockSplitter();
        var matching = topLevelDockControl.DataTemplates
            .OfType<IDataTemplate>()
            .First(t => t.Match(splitter));

        var built = matching.Build(splitter);
        Assert.IsType<global::Dock.Controls.ProportionalStackPanel.ProportionalStackPanelSplitter>(built);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void DockDataTemplates_DocumentDock_ResolvesDocumentDockControl()
    {
        // #1130 (reopened): the matched template builds a real DocumentDockControl
        // (not raw text) for a plain DocumentDock instance.
        var topLevelDockControl = GetTopLevelDockControl();

        var doc = new DocumentDock();
        var matching = topLevelDockControl.DataTemplates
            .OfType<IDataTemplate>()
            .First(t => t.Match(doc));

        var built = matching.Build(doc);
        Assert.IsType<DocumentDockControl>(built);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void SplitDock_WhenCreated_SplitterDoesNotRenderRawTypeName()
    {
        // #1130 (reopened): factory.CreateProportionalDockSplitter() must not render
        // its raw type name Dock.Model.Mvvm.Controls.ProportionalDockSplitter.
        var factory = new global::Dock.Model.Mvvm.Factory();
        var splitter = factory.CreateProportionalDockSplitter();

        var topLevelDockControl = GetTopLevelDockControl();
        var matching = topLevelDockControl.DataTemplates
            .OfType<IDataTemplate>()
            .FirstOrDefault(t => t.Match(splitter));

        Assert.NotNull(matching);
        var built = matching!.Build(splitter);
        Assert.IsNotType<TextBlock>(built);
        Assert.NotEqual(typeof(ProportionalDockSplitter).FullName, built?.GetType().FullName);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void SplitDock_WhenCreated_DocumentDockDoesNotRenderRawTypeName()
    {
        // #1130 (reopened): factory.CreateDocumentDock() (plain
        // Dock.Model.Mvvm.Controls.DocumentDock) must not render its raw type name.
        var factory = new global::Dock.Model.Mvvm.Factory();
        var doc = factory.CreateDocumentDock();

        var topLevelDockControl = GetTopLevelDockControl();
        var matching = topLevelDockControl.DataTemplates
            .OfType<IDataTemplate>()
            .FirstOrDefault(t => t.Match(doc));

        Assert.NotNull(matching);
        var built = matching!.Build(doc);
        Assert.IsNotType<TextBlock>(built);
        Assert.NotEqual(typeof(DocumentDock).FullName, built?.GetType().FullName);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void NewHorizontalDock_WhenSplitPopulated_AllChildrenRenderAsControls()
    {
        // #1130 (reopened): given a ProportionalDock populated with
        // [DocumentDock, ProportionalDockSplitter, DocumentDock] (matching the shape
        // Dock produces for a "New Horizontal Dock" split), the top-level DataTemplates
        // must resolve a real Avalonia control for every child — no child renders as
        // TextBlock, and none has a type name equal to any Dock.Model.Mvvm.Controls.* primitive.
        var factory = new global::Dock.Model.Mvvm.Factory();
        var proportional = factory.CreateProportionalDock();
        var leftDoc = factory.CreateDocumentDock();
        var splitter = factory.CreateProportionalDockSplitter();
        var rightDoc = factory.CreateDocumentDock();
        proportional.VisibleDockables = factory.CreateList<IDockable>(leftDoc, splitter, rightDoc);

        var topLevelDockControl = GetTopLevelDockControl();
        var dataTemplates = topLevelDockControl.DataTemplates.OfType<IDataTemplate>().ToList();

        foreach (var child in proportional.VisibleDockables!)
        {
            var matching = dataTemplates.FirstOrDefault(t => t.Match(child));
            Assert.NotNull(matching);
            var built = matching!.Build(child);
            Assert.IsNotType<TextBlock>(built);
            var builtTypeName = built?.GetType().FullName ?? string.Empty;
            Assert.False(
                builtTypeName.StartsWith("Dock.Model.Mvvm.Controls.", System.StringComparison.Ordinal),
                $"Child {child.GetType().FullName} rendered as raw model type {builtTypeName}");
        }
    }

    // ── Regression tests for #1170 ────────────────────────────────────────────
    // Empty split-dock auto-collapse: Ctrl+W / CloseActiveTabCommand must delegate
    // to Factory.CloseDockable so the library's CollapseDock chain runs, and MRU
    // navigation + single-dispose semantics match the close-button / middle-click
    // paths.

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CloseActiveTabCommand_WhenInvoked_RoutesThroughFactoryCloseDockable()
    {
        // #1170: Ctrl+W must go through Factory.CloseDockable(activeDoc) — observable
        // via factory.DockableClosed, which is NOT raised by a raw pane.Tabs.Remove(tab).
        await using var viewModel = CreateBootedMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://route.example.com") { Id = "route-a", Title = "Route A" };
        await viewModel.OpenTabAsync(tab);

        var pane = viewModel.SelectedWorkspacePane;
        Assert.NotNull(pane);
        var factory = GetDockFactory(viewModel);
        var documentDock = FindDocumentDockIn(pane!.ContentLayout!);
        Assert.NotNull(documentDock);

        IDockable? closedDockable = null;
        factory.DockableClosed += (_, e) => closedDockable = e.Dockable;

        Assert.Equal("route-a", documentDock!.ActiveDockable?.Id);
        viewModel.CloseActiveTabCommand.Execute(null);

        Assert.NotNull(closedDockable);
        Assert.IsAssignableFrom<WorkspaceDocument>(closedDockable!);
        Assert.Equal("route-a", closedDockable!.Id);
        Assert.DoesNotContain(pane.Tabs, t => t.Id == "route-a");
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CloseActiveTabCommand_WhenLastTabInSplitDockClosed_RemovesEmptyDockAndSplitter()
    {
        // #1170: after closing the last tab of a nested split DocumentDock, the empty
        // DocumentDock AND its adjacent ProportionalDockSplitter must be removed from
        // the parent ProportionalDock's VisibleDockables.
        await using var viewModel = CreateBootedMainWindowViewModel();
        await viewModel.InitializeAsync();

        var pane = viewModel.SelectedWorkspacePane;
        Assert.NotNull(pane);
        var factory = GetDockFactory(viewModel);

        var (root, prop, splitDoc, splitter, mainDocA, mainDocB) = BuildSplitLayout(factory);
        var tab = new WebViewModel("about:blank") { Id = "split-last-a", Title = "Split A" };
        var doc = new WorkspaceDocument(tab) { Owner = splitDoc };
        splitDoc.VisibleDockables = factory.CreateList<IDockable>(doc);
        splitDoc.ActiveDockable = doc;
        pane!.ContentLayout = root;

        viewModel.CloseActiveTabCommand.Execute(null);

        Assert.NotNull(prop.VisibleDockables);
        Assert.DoesNotContain(splitDoc, prop.VisibleDockables!);
        Assert.DoesNotContain(splitter, prop.VisibleDockables!);
        // The other split children are untouched.
        Assert.Contains(mainDocA, prop.VisibleDockables!);
        Assert.Contains(mainDocB, prop.VisibleDockables!);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CloseActiveTabCommand_WhenNonLastTabInSplitDockClosed_KeepsDockRegionAndSplitter()
    {
        // #1170: closing one of several tabs in a split region must NOT collapse the
        // region — the DocumentDock and its adjacent splitter stay in place and the
        // sibling tab remains.
        await using var viewModel = CreateBootedMainWindowViewModel();
        await viewModel.InitializeAsync();

        var pane = viewModel.SelectedWorkspacePane;
        Assert.NotNull(pane);
        var factory = GetDockFactory(viewModel);

        var (root, prop, splitDoc, splitter, mainDocA, _) = BuildSplitLayout(factory);
        var tabActive = new WebViewModel("about:blank") { Id = "split-multi-a", Title = "Split A" };
        var tabOther = new WebViewModel("about:blank") { Id = "split-multi-b", Title = "Split B" };
        var docActive = new WorkspaceDocument(tabActive) { Owner = splitDoc };
        var docOther = new WorkspaceDocument(tabOther) { Owner = splitDoc };
        splitDoc.VisibleDockables = factory.CreateList<IDockable>(docActive, docOther);
        splitDoc.ActiveDockable = docActive;
        pane!.ContentLayout = root;

        viewModel.CloseActiveTabCommand.Execute(null);

        Assert.NotNull(prop.VisibleDockables);
        Assert.Contains(splitDoc, prop.VisibleDockables!);
        Assert.Contains(splitter, prop.VisibleDockables!);
        Assert.Contains(mainDocA, prop.VisibleDockables!);
        Assert.NotNull(splitDoc.VisibleDockables);
        Assert.DoesNotContain(docActive, splitDoc.VisibleDockables!);
        Assert.Contains(docOther, splitDoc.VisibleDockables!);
    }

    // ── Regression tests for #1310 ────────────────────────────────────────────
    // Ctrl+W / CloseActiveTabCommand must close the tab of the currently focused
    // DocumentDock, not the depth-first "first" DocumentDock in the layout.

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CloseActiveTabCommand_WhenFocusedInRightSplitRegion_ClosesOnlyRightRegionActiveTab_AndLeavesLeftRegionActiveTabOpen()
    {
        await using var viewModel = CreateBootedMainWindowViewModel();
        await viewModel.InitializeAsync();

        var pane = viewModel.SelectedWorkspacePane;
        Assert.NotNull(pane);
        var factory = GetDockFactory(viewModel);

        var (root, _, leftDock, _, _, rightDock) = BuildSplitLayout(factory);
        var leftTab = new WebViewModel("about:blank") { Id = "focus-left", Title = "Left" };
        var rightTab = new WebViewModel("about:blank") { Id = "focus-right", Title = "Right" };
        var leftDoc = new WorkspaceDocument(leftTab) { Owner = leftDock };
        var rightDoc = new WorkspaceDocument(rightTab) { Owner = rightDock };
        leftDock.VisibleDockables = factory.CreateList<IDockable>(leftDoc);
        leftDock.ActiveDockable = leftDoc;
        rightDock.VisibleDockables = factory.CreateList<IDockable>(rightDoc);
        rightDock.ActiveDockable = rightDoc;
        pane!.ContentLayout = root;

        // Focus the RIGHT region; without #1310's fix, FindDocumentDock's depth-first
        // walk would return the LEFT region (index 0 in the ProportionalDock).
        factory.SetFocusedDockable(rightDock, rightDoc);

        viewModel.CloseActiveTabCommand.Execute(null);

        Assert.Contains(leftDoc, leftDock.VisibleDockables!);
        Assert.NotNull(rightDock.VisibleDockables);
        Assert.DoesNotContain(rightDoc, rightDock.VisibleDockables!);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CloseActiveTabCommand_WhenFocusedInLeftSplitRegion_ClosesOnlyLeftRegionActiveTab_AndLeavesRightRegionActiveTabOpen()
    {
        await using var viewModel = CreateBootedMainWindowViewModel();
        await viewModel.InitializeAsync();

        var pane = viewModel.SelectedWorkspacePane;
        Assert.NotNull(pane);
        var factory = GetDockFactory(viewModel);

        var (root, _, leftDock, _, _, rightDock) = BuildSplitLayout(factory);
        var leftTab = new WebViewModel("about:blank") { Id = "focus-left-2", Title = "Left" };
        var rightTab = new WebViewModel("about:blank") { Id = "focus-right-2", Title = "Right" };
        var leftDoc = new WorkspaceDocument(leftTab) { Owner = leftDock };
        var rightDoc = new WorkspaceDocument(rightTab) { Owner = rightDock };
        leftDock.VisibleDockables = factory.CreateList<IDockable>(leftDoc);
        leftDock.ActiveDockable = leftDoc;
        rightDock.VisibleDockables = factory.CreateList<IDockable>(rightDoc);
        rightDock.ActiveDockable = rightDoc;
        pane!.ContentLayout = root;

        factory.SetFocusedDockable(leftDock, leftDoc);

        viewModel.CloseActiveTabCommand.Execute(null);

        Assert.NotNull(leftDock.VisibleDockables);
        Assert.DoesNotContain(leftDoc, leftDock.VisibleDockables!);
        Assert.Contains(rightDoc, rightDock.VisibleDockables!);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task RootAndWorkspacesPaneDock_WhenLastChildClosed_AreNotRemoved()
    {
        // #1170: the top-level RootDock and WorkspacesPaneDock have IsCollapsable=false,
        // so FactoryBase.CollapseDock refuses to remove them even when their child list
        // is empty. This guards the primary layout from ever disappearing.
        await using var viewModel = CreateBootedMainWindowViewModel();
        await viewModel.InitializeAsync();
        Assert.NotNull(viewModel.Layout);

        var root = viewModel.Layout!;
        Assert.False(root.IsCollapsable);

        var workspacesDock = root.VisibleDockables!.OfType<WorkspacesPaneDock>().First();
        Assert.False(workspacesDock.IsCollapsable);

        var factory = GetDockFactory(viewModel);

        // Snapshot children, empty both docks, invoke CollapseDock, verify no removal.
        var rootChildren = root.VisibleDockables!.ToList();
        var workspacesChildren = workspacesDock.VisibleDockables!.ToList();
        workspacesDock.VisibleDockables!.Clear();
        factory.CollapseDock(workspacesDock);
        Assert.Contains(workspacesDock, root.VisibleDockables!);

        root.VisibleDockables!.Clear();
        factory.CollapseDock(root);
        // A root is only actually collapsed if its Owner has it in a list AND it is
        // collapsable; neither holds. Assert it still exists as an object with no owner
        // change and that IsCollapsable is still false.
        Assert.False(root.IsCollapsable);

        // Restore for viewModel disposal.
        foreach (var c in workspacesChildren) workspacesDock.VisibleDockables!.Add(c);
        foreach (var c in rootChildren) root.VisibleDockables!.Add(c);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CloseActiveTabCommand_WhenActiveTabClosed_DisposesTabExactlyOnceViaOnDockableTabClosed()
    {
        // #1170: after routing through Factory.CloseDockable, disposal must run exactly
        // once. The Ctrl+W code path used to call DisposeWorkspaceTabAsync itself AND
        // OnDockableTabClosed also runs it — that duplicate is gone with the fix.
        await using var viewModel = CreateBootedMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://dispose.example.com") { Id = "dispose-a", Title = "Dispose A" };
        await viewModel.OpenTabAsync(tab);

        var pane = viewModel.SelectedWorkspacePane;
        Assert.NotNull(pane);

        var removeCount = 0;
        ((System.Collections.Specialized.INotifyCollectionChanged)pane!.Tabs).CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove
                && e.OldItems?.Contains(tab) == true)
            {
                removeCount++;
            }
        };

        viewModel.CloseActiveTabCommand.Execute(null);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.Equal(1, removeCount);
        Assert.DoesNotContain(pane.Tabs, t => ReferenceEquals(t, tab));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CloseActiveTabCommand_WhenActiveTabClosed_ActivatesMostRecentlyUsedTab()
    {
        // #1170: after Ctrl+W closes the active tab, MRU navigation (via
        // navigationHistoryService.GoBackSkipping -> ActivateTabById) must activate the
        // previously-open tab — matching the close-button / middle-click paths.
        await using var viewModel = CreateBootedMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://mru.example.com/a") { Id = "mru-1170-a", Title = "A" };
        var tabB = new WebViewModel("https://mru.example.com/b") { Id = "mru-1170-b", Title = "B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);

        var documentDock = FindDocumentDockIn(viewModel.SelectedWorkspacePane!.ContentLayout!);
        Assert.NotNull(documentDock);
        Assert.Equal("mru-1170-b", documentDock!.ActiveDockable?.Id);

        viewModel.CloseActiveTabCommand.Execute(null);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.Equal("mru-1170-a", documentDock.ActiveDockable?.Id);
    }

    // ── #1196: Complete-template-set / instance-sharing / floating-host tests ─

    [AvaloniaFact(Timeout = 15_000)]
    public void MainWindowDockControl_HasCompleteDockDataTemplateSet()
    {
        // MainWindow.TopLevelDockControl.DataTemplates must contain a template
        // that Matches every DataType produced by new DockDataTemplates().
        var topLevelDockControl = GetTopLevelDockControl();
        var reference = new DockDataTemplates().OfType<IDataTemplate>().ToList();

        foreach (var referenceTemplate in reference)
        {
            var matchesReference = topLevelDockControl.DataTemplates
                .OfType<IDataTemplate>()
                .Any(t => object.ReferenceEquals(t, referenceTemplate)
                          || t.GetType() == referenceTemplate.GetType());
            Assert.True(matchesReference,
                $"TopLevelDockControl.DataTemplates missing template of type {referenceTemplate.GetType()}");
        }

        Assert.Equal(reference.Count, topLevelDockControl.DataTemplates.Count);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowDockControl_WorkspacePaneTabStrip_RendersPulsatingBrainOnOuterTabHeader()
    {
        // Regression for #1196 (reopened): the outer workspace-level tab header
        // must render through the REAL Dock.Avalonia pipeline
        // (DocumentTabStrip → DocumentTabStripItem.PART_HeaderPresenter →
        // DocumentControl.HeaderTemplate). WorkspacePaneDocument always adds an
        // AgentRunningIndicator item, so its pulsating-brain ProgressBar must
        // materialise under the header presenter. On HEAD (before the explicit
        // ContentTemplate fix) the header collapsed to TabHeaderViewModel.ToString()
        // and no ProgressBar appeared.
        await using var viewModel = CreateBootedMainWindowViewModel();
        await viewModel.InitializeAsync();

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            var progressBars = await GetOuterPaneHeaderProgressBarsAsync(window);

            Assert.Contains(progressBars, pb => pb.Classes.Contains("pulsating-brain"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowDockControl_WorkspacePaneTabStrip_RendersExclamationOnOuterTabHeader()
    {
        // Regression for #1196 (reopened): as above, the NotificationIndicator
        // item that WorkspacePaneDocument always adds must materialise its
        // exclamation-indicator ProgressBar via the real tab-strip render path.
        await using var viewModel = CreateBootedMainWindowViewModel();
        await viewModel.InitializeAsync();

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            var progressBars = await GetOuterPaneHeaderProgressBarsAsync(window);

            Assert.Contains(progressBars, pb => pb.Classes.Contains("exclamation-indicator"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowInnerPaneDocumentTabStrip_ContentTabWithRunningIndicator_RendersPulsatingBrainInsidePartHeaderPresenter()
    {
        // Regression for #1196 (reopened): the inner-pane content-level tab header
        // (WorkspaceContentDock.DocumentControl.HeaderTemplate in
        // DockDataTemplates.axaml) must inflate its indicator items through the REAL
        // Dock.Avalonia render path (DocumentTabStrip →
        // DocumentTabStripItem.PART_HeaderPresenter). A content tab whose
        // EffectiveTabHeader carries an AgentRunningIndicator (IsRunning=true) must
        // materialise its pulsating-brain ProgressBar. The explicit
        // ContentTemplate="{StaticResource TabHeaderTemplate}" fix repaired this
        // content-level path; before it the header collapsed to
        // TabHeaderViewModel.ToString() and no ProgressBar appeared. This locks in
        // the inner-pane HeaderTemplate replacement, complementing the outer
        // WorkspacesPaneDock coverage above.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://running.example.com")
        {
            Id = "inner-running-indicator",
            Title = "Running Tab",
        };
        // Attach a running-agent indicator so the content tab's EffectiveTabHeader
        // exposes a pulsating-brain item to the inner-pane HeaderTemplate.
        tab.TabHeader!.Items.Add(new AgentRunningIndicatorTabHeaderItemViewModel { IsRunning = true });
        await viewModel.OpenTabAsync(tab);

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            var progressBars = await GetTabStripHeaderProgressBarsAsync(
                window, dc => dc is WorkspaceContentDock);

            Assert.Contains(progressBars, pb => pb.Classes.Contains("pulsating-brain"));
        }
        finally
        {
            window.Close();
        }
    }

    // ---- #1324: centralized tab-header per-item template provisioning ----

    private static TabHeaderItemViewModel[] AllTabHeaderItemInstances() =>
    [
        new AgentRunningIndicatorTabHeaderItemViewModel(),
        new NotificationIndicatorTabHeaderItemViewModel(),
        new IconTabHeaderItemViewModel { Icon = "🚀" },
        new FaviconTabHeaderItemViewModel(),
        new StatusTabHeaderItemViewModel(),
    ];

    private static System.Collections.Generic.IReadOnlyList<System.Type> AllTabHeaderItemSubtypes() =>
        typeof(TabHeaderItemViewModel).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(TabHeaderItemViewModel)) && !t.IsAbstract)
            .ToList();

    private static ItemsControl BuildTabHeaderItemsControl(string resourceKey)
    {
        Assert.NotNull(Avalonia.Application.Current);
        Assert.True(
            Avalonia.Application.Current!.TryFindResource(resourceKey, null, out var resource),
            $"Expected keyed resource '{resourceKey}' to exist.");
        var template = Assert.IsAssignableFrom<IDataTemplate>(resource);

        var built = template.Build(new WebTabHeaderViewModel { Title = "t" });
        Assert.NotNull(built);

        var itemsControl = built!.GetLogicalDescendants().OfType<ItemsControl>().FirstOrDefault();
        Assert.NotNull(itemsControl);
        return itemsControl!;
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void TabHeaderItemTemplates_HasKeyedResource_ForEveryTabHeaderItemViewModelSubtype()
    {
        // #1324: the centralized dictionary must define exactly one keyed DataTemplate per
        // TabHeaderItemViewModel subtype, so the set is complete and a future subtype forces an edit.
        Assert.NotNull(Avalonia.Application.Current);
        foreach (var subtype in AllTabHeaderItemSubtypes())
        {
            var key = subtype.Name.Replace("ViewModel", "Template");
            Assert.True(
                Avalonia.Application.Current!.TryFindResource(key, null, out var resource),
                $"Missing centralized keyed template '{key}' for {subtype.Name}.");
            Assert.IsAssignableFrom<IDataTemplate>(resource);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void AllTabHeaderItemInstances_CoverEveryTabHeaderItemViewModelSubtype()
    {
        // Guard so the "resolves every subtype" tests below stay complete if a new subtype is added.
        Assert.Equal(AllTabHeaderItemSubtypes().Count, AllTabHeaderItemInstances().Length);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void TabHeaderTemplate_ItemsControlDataTemplates_ResolvesEveryTabHeaderItemViewModelSubtype()
    {
        var itemsControl = BuildTabHeaderItemsControl("TabHeaderTemplate");

        foreach (var instance in AllTabHeaderItemInstances())
        {
            Assert.Contains(
                itemsControl.DataTemplates.OfType<IDataTemplate>(),
                t => t.Match(instance));
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void WebTabHeaderTemplate_ItemsControlDataTemplates_ResolvesEveryTabHeaderItemViewModelSubtype()
    {
        var itemsControl = BuildTabHeaderItemsControl("WebTabHeaderTemplate");

        foreach (var instance in AllTabHeaderItemInstances())
        {
            Assert.Contains(
                itemsControl.DataTemplates.OfType<IDataTemplate>(),
                t => t.Match(instance));
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void TabHeaderTemplateAndWebTabHeaderTemplate_ShareTheSameKeyedItemTemplates()
    {
        // Both header bodies must reference an identical per-item template set (no plain/web divergence).
        var plain = BuildTabHeaderItemsControl("TabHeaderTemplate").DataTemplates.OfType<IDataTemplate>().ToList();
        var web = BuildTabHeaderItemsControl("WebTabHeaderTemplate").DataTemplates.OfType<IDataTemplate>().ToList();

        Assert.Equal(AllTabHeaderItemInstances().Length, plain.Count);
        Assert.Equal(plain.Count, web.Count);

        foreach (var instance in AllTabHeaderItemInstances())
        {
            Assert.Equal(
                plain.Any(t => t.Match(instance)),
                web.Any(t => t.Match(instance)));
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void WorkspaceDataTemplates_DoNotDeclareImplicitTabHeaderItemTemplates()
    {
        // #1324: the Icon/Favicon/Status per-item templates must NOT be re-declared as implicit
        // top-level templates in WorkspaceDataTemplates (that per-scope duplication was the bug).
        var dictionary = new WorkspaceDataTemplates();
        foreach (var instance in AllTabHeaderItemInstances())
        {
            Assert.DoesNotContain(
                dictionary.OfType<IDataTemplate>(),
                t => t.Match(instance));
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowInnerPaneDocumentTabStrip_AgentSessionTab_RendersRunningAndNotificationIndicators()
    {
        // #1324: both indicator items must materialise on an inner content tab (the outer strip
        // already works; this extends the running-only coverage to include the notification glyph).
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://indicators.example.com")
        {
            Id = "inner-both-indicators",
            Title = "Indicators Tab",
        };
        tab.TabHeader!.Items.Add(new AgentRunningIndicatorTabHeaderItemViewModel { IsRunning = true });
        tab.TabHeader!.Items.Add(new NotificationIndicatorTabHeaderItemViewModel { HasUnread = true });
        await viewModel.OpenTabAsync(tab);

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            var progressBars = await GetTabStripHeaderProgressBarsAsync(
                window, dc => dc is WorkspaceContentDock);

            Assert.Contains(progressBars, pb => pb.Classes.Contains("pulsating-brain"));
            Assert.Contains(progressBars, pb => pb.Classes.Contains("exclamation-indicator"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowInnerPaneDocumentTabStrip_TabWithIconHeader_RendersIconGlyphInsidePartHeaderPresenter()
    {
        // #1324: IconTabHeaderItemViewModel must render on inner tabs (previously unreachable in the
        // scope-blocked inner DockControl).
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://icon.example.com")
        {
            Id = "inner-icon",
            Title = "Icon Tab",
        };
        tab.TabHeader!.Items.Add(new IconTabHeaderItemViewModel { Icon = "🚀" });
        await viewModel.OpenTabAsync(tab);

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            var textBlocks = await GetTabStripHeaderControlsAsync<TextBlock>(
                window, dc => dc is WorkspaceContentDock);

            Assert.Contains(textBlocks, tb => tb.Text == "🚀");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowInnerPaneDocumentTabStrip_WebTab_RendersFaviconGlyphInsidePartHeaderPresenter()
    {
        // #1324: a WebViewModel inner tab carries a FaviconTabHeaderItemViewModel and must render the
        // globe glyph via WebTabHeaderTemplate (previously the inner scope forced TabHeaderTemplate and
        // never reached the favicon template at all).
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://favicon.example.com")
        {
            Id = "inner-favicon",
            Title = "Favicon Tab",
        };
        await viewModel.OpenTabAsync(tab);

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            var textBlocks = await GetTabStripHeaderControlsAsync<TextBlock>(
                window, dc => dc is WorkspaceContentDock);

            Assert.Contains(textBlocks, tb => tb.Text == "🌐");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowInnerPaneDocumentTabStrip_ContentTab_RendersStatusControlInsidePartHeaderPresenter()
    {
        // #1324: WorkspaceDocument.RebuildTabHeaderItems appends a StatusTabHeaderItemViewModel to every
        // inner tab; its StatusControl must materialise inside the inner PART_HeaderPresenter.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://status.example.com")
        {
            Id = "inner-status",
            Title = "Status Tab",
        };
        await viewModel.OpenTabAsync(tab);

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            var statusControls = await GetTabStripHeaderControlsAsync<Phantom.Workspaces.Controls.StatusControl>(
                window, dc => dc is WorkspaceContentDock);

            Assert.NotEmpty(statusControls);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowSplitDocumentTabStrip_AfterHorizontalSplit_RendersAllFiveHeaderItemTypes()
    {
        // #1324 + #1307: a horizontal split routes the new region through
        // WorkspaceDockFactory.CreateDocumentDock (which returns a WorkspaceContentDock).
        // The centralized per-item template provisioning must survive that runtime split:
        // the split-created dock's tab strip must render ALL FIVE per-item header
        // templates (icon, favicon, status, running indicator, notification indicator),
        // not just the default (non-split) content dock. This is the exact path the
        // issue's "Relationship to #1307" section calls out.
        await using var viewModel = CreateBootedMainWindowViewModel();
        await viewModel.InitializeAsync();

        var pane = viewModel.SelectedWorkspacePane;
        Assert.NotNull(pane);
        var factory = GetDockFactory(viewModel);

        // A single tab carrying every header-item family: favicon (auto on WebViewModel),
        // icon + running + notification (added here), and status (appended by
        // WorkspaceDocument.RebuildTabHeaderItems).
        var tab = new WebViewModel("https://split.example.com")
        {
            Id = "split-all-five",
            Title = "Split All Five",
        };
        tab.TabHeader!.Items.Add(new IconTabHeaderItemViewModel { Icon = "🚀" });
        tab.TabHeader!.Items.Add(new AgentRunningIndicatorTabHeaderItemViewModel { IsRunning = true });
        tab.TabHeader!.Items.Add(new NotificationIndicatorTabHeaderItemViewModel { HasUnread = true });

        // The split-created dock must come from the #1307 factory path.
        var splitDock = Assert.IsType<WorkspaceContentDock>(factory.CreateDocumentDock());
        var document = new WorkspaceDocument(tab) { Owner = splitDock };
        splitDock.IsCollapsable = true;
        splitDock.VisibleDockables = factory.CreateList<IDockable>(document);
        splitDock.ActiveDockable = document;

        // Assemble the post-horizontal-split layout shape:
        //   Root -> ProportionalDock [ existingDock, splitter, splitCreatedDock ]
        var root = factory.CreateRootDock();
        root.IsCollapsable = false;
        var prop = factory.CreateProportionalDock();
        var existingDock = factory.CreateDocumentDock();
        existingDock.IsCollapsable = true;
        var splitter = factory.CreateProportionalDockSplitter();

        prop.VisibleDockables = factory.CreateList<IDockable>(existingDock, splitter, splitDock);
        existingDock.Owner = prop;
        splitter.Owner = prop;
        splitDock.Owner = prop;
        prop.ActiveDockable = splitDock;

        root.VisibleDockables = factory.CreateList<IDockable>(prop);
        prop.Owner = root;
        root.ActiveDockable = prop;

        factory.InitLayout(root);
        pane!.ContentLayout = root;

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            // Anchor every assertion to the split-created dock's own tab strip so we
            // prove the split region — not the default content dock — renders them all.
            bool IsSplitStrip(object? dc) => ReferenceEquals(dc, splitDock);

            var textBlocks = await GetTabStripHeaderControlsAsync<TextBlock>(window, IsSplitStrip);
            Assert.Contains(textBlocks, tb => tb.Text == "🚀");
            Assert.Contains(textBlocks, tb => tb.Text == "🌐");

            var statusControls = await GetTabStripHeaderControlsAsync<Phantom.Workspaces.Controls.StatusControl>(
                window, IsSplitStrip);
            Assert.NotEmpty(statusControls);

            var progressBars = await GetTabStripHeaderProgressBarsAsync(window, IsSplitStrip);
            Assert.Contains(progressBars, pb => pb.Classes.Contains("pulsating-brain"));
            Assert.Contains(progressBars, pb => pb.Classes.Contains("exclamation-indicator"));
        }
        finally
        {
            window.Close();
        }
    }

    private static Task<System.Collections.Generic.IReadOnlyList<ProgressBar>>
        GetOuterPaneHeaderProgressBarsAsync(Avalonia.Controls.Window window)
    {
        // Drive the real Dock.Avalonia render path for the outer workspace-level
        // DocumentTabStrip (DataContext = WorkspacesPaneDock). This is the exact
        // scope that the implicit vm:TabHeaderViewModel lookup failed to resolve
        // before the #1196 fix.
        return GetTabStripHeaderProgressBarsAsync(window, dc => dc is WorkspacesPaneDock);
    }

    private static Task<System.Collections.Generic.IReadOnlyList<ProgressBar>>
        GetTabStripHeaderProgressBarsAsync(
            Avalonia.Controls.Window window,
            Func<object?, bool> dataContextPredicate)
    {
        // Event-driven synchronization (no Task.Delay / polling loop): locate the
        // DocumentTabStrip whose DataContext matches the predicate, walk each
        // DocumentTabStripItem's PART_HeaderPresenter, and resolve as soon as the
        // DocumentControl.HeaderTemplate has inflated indicator ProgressBars.
        // Progress is anchored to the window's LayoutUpdated event, which fires on
        // every layout pass performed during Dock.Avalonia tab-strip realization,
        // matching the WaitForLayoutAsync/WaitForDocumentTabStripAsync helpers used
        // elsewhere in this test suite.
        var tcs = new TaskCompletionSource<System.Collections.Generic.IReadOnlyList<ProgressBar>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        System.Collections.Generic.IReadOnlyList<ProgressBar>? TryCollect()
        {
            var tabStrip = window.GetVisualDescendants()
                .OfType<DocumentTabStrip>()
                .FirstOrDefault(ts => dataContextPredicate(ts.DataContext));
            if (tabStrip is null)
                return null;

            var progressBars = tabStrip.GetVisualDescendants()
                .OfType<DocumentTabStripItem>()
                .SelectMany(item => item.GetVisualDescendants()
                    .OfType<Avalonia.Controls.Presenters.ContentPresenter>()
                    .Where(cp => cp.Name == "PART_HeaderPresenter"))
                .SelectMany(headerPresenter => headerPresenter.GetVisualDescendants()
                    .OfType<ProgressBar>())
                .ToList();

            return progressBars.Count > 0 ? progressBars : null;
        }

        EventHandler? handler = null;
        handler = (_, _) =>
        {
            var bars = TryCollect();
            if (bars is null)
                return;
            window.LayoutUpdated -= handler;
            tcs.TrySetResult(bars);
        };
        window.LayoutUpdated += handler;

        // TOCTOU: re-check after subscribing in case the bars already materialised
        // between construction and the subscribe.
        var initial = TryCollect();
        if (initial is not null)
        {
            window.LayoutUpdated -= handler;
            tcs.TrySetResult(initial);
        }

        return tcs.Task;
    }

    private static Task<System.Collections.Generic.IReadOnlyList<T>>
        GetTabStripHeaderControlsAsync<T>(
            Avalonia.Controls.Window window,
            Func<object?, bool> dataContextPredicate)
        where T : Avalonia.Controls.Control
    {
        // Generic sibling of GetTabStripHeaderProgressBarsAsync: resolves as soon as at least one
        // control of type T has materialised inside a matching DocumentTabStripItem's
        // PART_HeaderPresenter. Event-driven (anchored to LayoutUpdated), no Task.Delay / polling.
        var tcs = new TaskCompletionSource<System.Collections.Generic.IReadOnlyList<T>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        System.Collections.Generic.IReadOnlyList<T>? TryCollect()
        {
            var tabStrip = window.GetVisualDescendants()
                .OfType<DocumentTabStrip>()
                .FirstOrDefault(ts => dataContextPredicate(ts.DataContext));
            if (tabStrip is null)
                return null;

            var controls = tabStrip.GetVisualDescendants()
                .OfType<DocumentTabStripItem>()
                .SelectMany(item => item.GetVisualDescendants()
                    .OfType<Avalonia.Controls.Presenters.ContentPresenter>()
                    .Where(cp => cp.Name == "PART_HeaderPresenter"))
                .SelectMany(headerPresenter => headerPresenter.GetVisualDescendants()
                    .OfType<T>())
                .ToList();

            return controls.Count > 0 ? controls : null;
        }

        EventHandler? handler = null;
        handler = (_, _) =>
        {
            var controls = TryCollect();
            if (controls is null)
                return;
            window.LayoutUpdated -= handler;
            tcs.TrySetResult(controls);
        };
        window.LayoutUpdated += handler;

        var initial = TryCollect();
        if (initial is not null)
        {
            window.LayoutUpdated -= handler;
            tcs.TrySetResult(initial);
        }

        return tcs.Task;
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void InnerWorkspacePaneDockControl_HasOnlyItsScopedFiveTemplateSubset()
    {
        // #1130: the inner-pane DockControl has a hand-picked 5-template subset
        // declared in-XAML on DockDataTemplates.axaml (WorkspaceContentDock,
        // WorkspaceDocument, IProportionalDock, IProportionalDockSplitter,
        // IDocumentDock). #1196 must NOT overwrite it.
        var innerDockControl = BuildInnerWorkspacePaneDockControl();

        Assert.Equal(5, innerDockControl.DataTemplates.Count);

        // Guard: IRootDock must NOT be matched by the inner set. (WorkspacesPaneDock
        // inherits DocumentDock and would be matched by the IDocumentDock template,
        // but there is no scenario in which a WorkspacesPaneDock is placed inside
        // the inner-pane DockControl.)
        var rootDock = new global::Dock.Model.Mvvm.Controls.RootDock();
        Assert.Null(innerDockControl.DataTemplates
            .OfType<IDataTemplate>().FirstOrDefault(t => t.Match(rootDock)));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void MainWindow_AddDockDataTemplates_SharesInstancesBetweenWindowAndTopLevelDockControl()
    {
        // Every template registered on MainWindow.DataTemplates must be the
        // SAME IDataTemplate instance as the corresponding entry on
        // TopLevelDockControl.DataTemplates — a single new DockDataTemplates()
        // shared across both scopes.
        var viewModel = CreateTestMainWindowViewModel();
        var window = new MainWindow(viewModel);

        var topLevelDockControl = window.GetLogicalDescendants()
            .OfType<DockControl>()
            .First(dc => dc.Name == "TopLevelDockControl");

        var windowTemplates = window.DataTemplates.ToList();
        var dockTemplates = topLevelDockControl.DataTemplates.ToList();
        Assert.Equal(windowTemplates.Count, dockTemplates.Count);
        for (var i = 0; i < windowTemplates.Count; i++)
        {
            Assert.Same(windowTemplates[i], dockTemplates[i]);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void PhantomHostWindow_AfterOnApplyTemplate_OuterDockControlHasCompleteDockDataTemplateSet()
    {
        // Attach an empty descendant DockControl and force template application
        // so PhantomHostWindow.OnApplyTemplate copies the source's templates
        // into the descendant DockControl's DataTemplates collection.
        var referenceTemplates = new DockDataTemplates().OfType<IDataTemplate>().ToList();

        var source = new DockControl { AutoCreateDataTemplates = false };
        foreach (var template in referenceTemplates)
        {
            source.DataTemplates.Add(template);
        }

        var innerDockControl = new DockControl { AutoCreateDataTemplates = false };
        Assert.Empty(innerDockControl.DataTemplates);

        var host = new Phantom.Workspaces.Controls.PhantomHostWindow(source)
        {
            Content = innerDockControl,
        };
        host.Show();
        try
        {
            host.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(referenceTemplates.Count, innerDockControl.DataTemplates.Count);
            foreach (var referenceTemplate in referenceTemplates)
            {
                Assert.Contains(innerDockControl.DataTemplates,
                    t => object.ReferenceEquals(t, referenceTemplate));
            }
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void PhantomHostWindow_AfterOnApplyTemplate_InnerPaneDockControlIsNotOverwritten()
    {
        // Inner-pane DockControl (built by the WorkspacePaneDocument template)
        // declares its own scoped 5-template subset. PhantomHostWindow's
        // OnApplyTemplate must skip any DockControl whose DataTemplates.Count > 0.
        var innerDockControl = BuildInnerWorkspacePaneDockControl();
        Assert.Equal(5, innerDockControl.DataTemplates.Count);
        var originalTemplates = innerDockControl.DataTemplates.ToList();

        var referenceTemplates = new DockDataTemplates().OfType<IDataTemplate>().ToList();
        var source = new DockControl { AutoCreateDataTemplates = false };
        foreach (var t in referenceTemplates) source.DataTemplates.Add(t);

        var host = new Phantom.Workspaces.Controls.PhantomHostWindow(source)
        {
            Content = innerDockControl,
        };
        host.Show();
        try
        {
            host.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            // Inner DockControl kept its scoped 5-template subset unchanged.
            Assert.Equal(originalTemplates.Count, innerDockControl.DataTemplates.Count);
            for (var i = 0; i < originalTemplates.Count; i++)
            {
                Assert.Same(originalTemplates[i], innerDockControl.DataTemplates[i]);
            }
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void FloatingHostWindow_UsesSameTemplateInstancesAsSourceDockControl()
    {
        // A directly-constructed PhantomHostWindow(sourceDockControl) shares
        // every IDataTemplate INSTANCE with the source, not a fresh copy.
        var referenceTemplates = new DockDataTemplates().OfType<IDataTemplate>().ToList();
        var source = new DockControl { AutoCreateDataTemplates = false };
        foreach (var t in referenceTemplates) source.DataTemplates.Add(t);

        var host = new Phantom.Workspaces.Controls.PhantomHostWindow(source);

        Assert.Equal(source.DataTemplates.Count, host.DataTemplates.Count);
        for (var i = 0; i < source.DataTemplates.Count; i++)
        {
            Assert.Same(source.DataTemplates[i], host.DataTemplates[i]);
        }
    }

    // ── #1196 Cross-host invariants: "no host silently lacks its templates" ──

    [AvaloniaFact(Timeout = 15_000)]
    public void EveryOpenDockControl_HasExactlyTheTemplatesItsHostRoleRequires()
    {
        // Enumerate every DockControl reachable from every open Window and
        // classify by role. Outer DockControls must match the full DockDataTemplates
        // universe; inner-pane DockControls must have exactly the scoped
        // 5-template subset.
        var viewModel = CreateTestMainWindowViewModel();
        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            var referenceCount = new DockDataTemplates().OfType<IDataTemplate>().Count();
            const int innerScopedSubsetCount = 5;

            Assert.NotNull(Avalonia.Application.Current);
            var openWindows = GetOpenWindows();
            // In headless tests without IClassicDesktopStyleApplicationLifetime,
            // fall back to inspecting the single window we opened directly.
            var windowsToInspect = openWindows.Count > 0
                ? openWindows
                : new[] { (Avalonia.Controls.Window)window };
            var allDockControls = windowsToInspect
                .SelectMany(w => w.GetVisualDescendants().OfType<DockControl>())
                .ToList();

            Assert.NotEmpty(allDockControls);
            foreach (var dc in allDockControls)
            {
                Assert.True(
                    dc.DataTemplates.Count == referenceCount
                    || dc.DataTemplates.Count == innerScopedSubsetCount,
                    $"DockControl carries {dc.DataTemplates.Count} templates; expected " +
                    $"either {referenceCount} (outer) or {innerScopedSubsetCount} (inner).");
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EveryOuterFloatingDockControl_SharesTemplateInstancesWithSourceDockControl()
    {
        // Every open PhantomHostWindow's DataTemplates entries must be reference-
        // equal to entries in MainWindow.TopLevelDockControl.DataTemplates.
        var viewModel = CreateTestMainWindowViewModel();
        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            var topLevelDockControl = window.GetLogicalDescendants()
                .OfType<DockControl>()
                .First(dc => dc.Name == "TopLevelDockControl");
            var referenceTemplates = topLevelDockControl.DataTemplates.ToList();

            Assert.NotNull(Avalonia.Application.Current);
            var openWindows = GetOpenWindows();
            var floatingHosts = openWindows
                .OfType<Phantom.Workspaces.Controls.PhantomHostWindow>()
                .ToList();

            foreach (var host in floatingHosts)
            {
                foreach (var template in host.DataTemplates)
                {
                    Assert.Contains(referenceTemplates, t => object.ReferenceEquals(t, template));
                }
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EveryOpenDockControl_ResolvesSharedIndicatorResourcesViaStaticResource()
    {
        // The Application-level MergedDictionaries wiring must expose the two
        // keyed indicator DataTemplates to every open Window.
        var viewModel = CreateTestMainWindowViewModel();
        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            Assert.NotNull(Avalonia.Application.Current);
            var openWindows = GetOpenWindows();
            var windowsToInspect = openWindows.Count > 0
                ? openWindows
                : new[] { (Avalonia.Controls.Window)window };
            foreach (var w in windowsToInspect)
            {
                Assert.True(w.TryFindResource(
                    "AgentRunningIndicatorTabHeaderItemTemplate", null, out _));
                Assert.True(w.TryFindResource(
                    "NotificationIndicatorTabHeaderItemTemplate", null, out _));
            }
        }
        finally
        {
            window.Close();
        }
    }

    // ── #1235: rich per-content-type tab tooltips (TabTooltipView) ──────────

    [AvaloniaFact(Timeout = 15_000)]
    public void TabTooltipView_ResolvesBrowserTemplate_ForWebViewModel()
    {
        Assert.Equal(
            typeof(WebViewModel),
            ResolveTabTooltipTemplateDataType(new WebViewModel("https://example.com") { Id = "b", Title = "b" }));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void TabTooltipView_ResolvesShellTemplate_ForShellTabViewModel()
    {
        Assert.Equal(
            typeof(ShellTabViewModel),
            ResolveTabTooltipTemplateDataType(CreateShellTab()));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void TabTooltipView_ResolvesEntityTemplate_ForEntityWorkspaceTabViewModel()
    {
        Assert.Equal(
            typeof(EntityWorkspaceTabViewModel),
            ResolveTabTooltipTemplateDataType(new EntityWorkspaceTabViewModel { Id = "e", Title = "Entity" }));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void TabTooltipView_ResolvesAgentTemplate_ForAgentSessionWorkspaceTabViewModel()
    {
        Assert.Equal(
            typeof(AgentSessionWorkspaceTabViewModel),
            ResolveTabTooltipTemplateDataType(new AgentSessionWorkspaceTabViewModel { Id = "a", Title = "Agent" }));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void TabTooltipView_ResolvesFallbackTemplate_ForUnknownTabKind()
    {
        // A tab kind with no dedicated template resolves the WorkspaceTabViewModel fallback.
        Assert.Equal(
            typeof(WorkspaceTabViewModel),
            ResolveTabTooltipTemplateDataType(new PlainTabViewModel { Id = "p", Title = "Plain" }));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void TabTooltipView_ForBrowserTab_RendersFullUrlWithoutHeavyContentView()
    {
        var vm = new WebViewModel("https://example.com/very/long/path?query=1&more=2")
        {
            Id = "b",
            Title = "Example Page",
        };
        var view = new TabTooltipView { DataContext = vm };
        var window = new Window { Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            var texts = view.GetVisualDescendants()
                .OfType<TextBlock>()
                .Select(t => t.Text)
                .ToList();

            Assert.Contains("Example Page", texts);
            Assert.Contains(vm.AddressBarUrl, texts);
            // The "Browser tab" label proves the tooltip template — not the ambient
            // WorkspaceDataTemplates browser content view — rendered this content.
            Assert.Contains("Browser tab", texts);
        }
        finally
        {
            window.Close();
        }
    }

    private static System.Type? ResolveTabTooltipTemplateDataType(object viewModel)
    {
        var view = new TabTooltipView();
        var template = view.DataTemplates
            .OfType<Avalonia.Markup.Xaml.Templates.DataTemplate>()
            .FirstOrDefault(t => t.Match(viewModel));
        return template?.DataType;
    }

    private static ShellTabViewModel CreateShellTab() =>
        new(
            new NoopTerminalSession(),
            new Phantom.Workspaces.ViewModels.ShellEntityOpenSpec { Mode = "pty", Command = "pwsh" },
            sessionFactory: null,
            sourceEntityId: null,
            concurrencyTag: null,
            sourceEntityData: null,
            entityWriter: null,
            dialogOpener: null)
        {
            Id = "s",
            Title = "Shell",
        };

    private sealed class PlainTabViewModel : WorkspaceTabViewModel
    {
    }

    private sealed class NoopTerminalSession : Phantom.Workspaces.Llm.Shell.ITerminalSession
    {
        private readonly System.IO.MemoryStream stream = new();

        public System.IO.Stream Stream => this.stream;

        public System.Threading.Tasks.ValueTask ResizeAsync(int columns, int rows, System.Threading.CancellationToken cancellationToken)
            => System.Threading.Tasks.ValueTask.CompletedTask;

        public System.Threading.Tasks.ValueTask SignalAsync(string signal, System.Threading.CancellationToken cancellationToken)
            => System.Threading.Tasks.ValueTask.CompletedTask;

        public System.Threading.Tasks.Task<int> WaitForExitAsync() => System.Threading.Tasks.Task.FromResult(0);

        public System.Threading.Tasks.ValueTask DisposeAsync()
        {
            this.stream.Dispose();
            return System.Threading.Tasks.ValueTask.CompletedTask;
        }
    }

    private static WorkspacePaneDocument? FindFirstWorkspacePaneDocument(Avalonia.Controls.Window window)
    {
        return window.GetLogicalDescendants()
            .OfType<Control>()
            .Select(c => c.DataContext)
            .OfType<WorkspacePaneDocument>()
            .FirstOrDefault();
    }

    private static System.Collections.Generic.IReadOnlyList<Avalonia.Controls.Window> GetOpenWindows()
    {
        // Headless tests do not set up an IClassicDesktopStyleApplicationLifetime,
        // so Application.Windows is not available. Fall back to enumerating open
        // top-levels via HeadlessApp-friendly APIs.
        var app = Avalonia.Application.Current;
        if (app?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.Windows.ToList();
        }
        return System.Array.Empty<Avalonia.Controls.Window>();
    }

    private static (
        IRootDock root,
        IProportionalDock prop,
        IDocumentDock splitDoc,
        IProportionalDockSplitter splitter,
        IDocumentDock mainDocA,
        IDocumentDock mainDocB)
        BuildSplitLayout(WorkspaceDockFactory factory)
    {
        // Layout: RootDock(IsCollapsable=false) ->
        //   ProportionalDock [splitDoc(IsCollapsable=true), splitter, mainDocA, splitter2, mainDocB]
        // Three non-splitter children guarantee CollapseDock does NOT trigger the
        // "single non-splitter left" cleanup after we remove splitDoc + splitter.
        var root = factory.CreateRootDock();
        root.IsCollapsable = false;

        var prop = factory.CreateProportionalDock();
        var splitDoc = factory.CreateDocumentDock();
        splitDoc.IsCollapsable = true;
        var splitter = factory.CreateProportionalDockSplitter();
        var mainDocA = factory.CreateDocumentDock();
        mainDocA.IsCollapsable = true;
        var splitter2 = factory.CreateProportionalDockSplitter();
        var mainDocB = factory.CreateDocumentDock();
        mainDocB.IsCollapsable = true;

        prop.VisibleDockables = factory.CreateList<IDockable>(
            splitDoc, splitter, mainDocA, splitter2, mainDocB);
        splitDoc.Owner = prop;
        splitter.Owner = prop;
        mainDocA.Owner = prop;
        splitter2.Owner = prop;
        mainDocB.Owner = prop;

        root.VisibleDockables = factory.CreateList<IDockable>(prop);
        prop.Owner = root;
        root.ActiveDockable = prop;

        return (root, prop, splitDoc, splitter, mainDocA, mainDocB);
    }

    private static WorkspaceDockFactory GetDockFactory(MainWindowViewModel viewModel)
    {
        var field = typeof(MainWindowViewModel).GetField(
            "dockFactory",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<WorkspaceDockFactory>(field!.GetValue(viewModel));
    }

    private static IDocumentDock? FindDocumentDockIn(IDockable dockable)
    {
        if (dockable is IDocumentDock documentDock)
        {
            return documentDock;
        }

        if (dockable is IDock dock && dock.VisibleDockables is not null)
        {
            foreach (var child in dock.VisibleDockables)
            {
                var result = FindDocumentDockIn(child);
                if (result is not null)
                {
                    return result;
                }
            }
        }

        return null;
    }

    private static MainWindowViewModel CreateBootedMainWindowViewModel()
    {
        return new MainWindowViewModel(
            new UnknownRepositorySource(),
            new WorkspacesConfiguration { SkipStartupWorkspace = false },
            new ProfileStore(CreateTempProfileStorePath()),
            applicationServices: null);
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

