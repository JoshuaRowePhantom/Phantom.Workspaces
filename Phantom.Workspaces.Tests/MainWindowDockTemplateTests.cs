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
using Phantom.Workspaces.Data;
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
    public void WorkspacePaneDockControl_InnerDockControl_HasRootDockTemplate()
    {
        // #1334: the inner workspace-pane DockControl has its own scoped, non-inheriting template
        // set (AutoCreateDataTemplates="False", per #1130 lookup does not walk up to ancestor
        // scopes). On restore, TryRestoreFromDockLayoutAsync wholesale-swaps this DockControl's
        // Layout to a brand-new IRootDock graph whose subtree can be a multi-region ProportionalDock
        // of WorkspaceContentDock leaves. Without a local IRootDock template the restored root (and
        // its nested leaves) fall through to the generic IDocumentDock fallback (no HeaderTemplate),
        // so BOTH regions render headerless. Guard the inner scope owns the IRootDock key.
        var innerDockControl = BuildInnerWorkspacePaneDockControl();

        var rootDock = new global::Dock.Model.Mvvm.Controls.RootDock();
        var matching = innerDockControl.DataTemplates
            .OfType<IDataTemplate>()
            .FirstOrDefault(t => t.Match(rootDock));

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

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowInnerPaneDocumentTabStrip_RestoredBaseDocumentDock_RendersAllFiveHeaderItemTypes()
    {
        // #1324 end-to-end: a restored base DocumentDock (the pre-#1307 shape) hosting a live
        // document with all five header-item families renders headerless BEFORE substitution.
        // After MigrateBaseDocumentDocksToWorkspaceContentDock the region is a WorkspaceContentDock,
        // so its tab strip renders every per-item glyph via the centralized header template.
        await using var viewModel = CreateBootedMainWindowViewModel();
        await viewModel.InitializeAsync();

        var pane = viewModel.SelectedWorkspacePane;
        Assert.NotNull(pane);
        var factory = GetDockFactory(viewModel);

        var tab = new WebViewModel("https://restored.example.com")
        {
            Id = "restored-all-five",
            Title = "Restored All Five",
        };
        tab.TabHeader!.Items.Add(new IconTabHeaderItemViewModel { Icon = "🚀" });
        tab.TabHeader!.Items.Add(new AgentRunningIndicatorTabHeaderItemViewModel { IsRunning = true });
        tab.TabHeader!.Items.Add(new NotificationIndicatorTabHeaderItemViewModel { HasUnread = true });

        var document = new WorkspaceDocument(tab);

        // A base DocumentDock exactly as a pre-#1307 persisted layout re-hydrates.
        var baseDock = new DocumentDock
        {
            Id = "restored-base-dock",
            VisibleDockables = factory.CreateList<IDockable>(document),
        };
        baseDock.ActiveDockable = document;
        document.Owner = baseDock;

        var root = factory.CreateRootDock();
        root.IsCollapsable = false;
        root.VisibleDockables = factory.CreateList<IDockable>(baseDock);
        baseDock.Owner = root;
        root.ActiveDockable = baseDock;
        root.DefaultDockable = baseDock;

        MainWindowViewModel.MigrateBaseDocumentDocksToWorkspaceContentDock(root);

        var migrated = Assert.IsType<WorkspaceContentDock>(FindDocumentDockIn(root));

        factory.InitLayout(root);
        pane!.ContentLayout = root;

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            bool IsRestoredStrip(object? dc) => ReferenceEquals(dc, migrated);

            var textBlocks = await GetTabStripHeaderControlsAsync<TextBlock>(window, IsRestoredStrip);
            Assert.Contains(textBlocks, tb => tb.Text == "🚀");
            Assert.Contains(textBlocks, tb => tb.Text == "🌐");

            var statusControls = await GetTabStripHeaderControlsAsync<Phantom.Workspaces.Controls.StatusControl>(
                window, IsRestoredStrip);
            Assert.NotEmpty(statusControls);

            var progressBars = await GetTabStripHeaderProgressBarsAsync(window, IsRestoredStrip);
            Assert.Contains(progressBars, pb => pb.Classes.Contains("pulsating-brain"));
            Assert.Contains(progressBars, pb => pb.Classes.Contains("exclamation-indicator"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowInnerPaneDocumentTabStrip_RestoredMultiRegionSplitBaseDocumentDock_BothRegionsRenderAllFiveHeaderItemTypes()
    {
        // #1330 end-to-end: a restored multi-region split (ProportionalDock [ baseDock, splitter,
        // baseDock ]) where BOTH leaves are pre-#1307 base DocumentDocks. After the restore-time
        // migration, every region is a WorkspaceContentDock, so BOTH tab strips render all five
        // per-item header families via the single centralized header template.
        await using var viewModel = CreateBootedMainWindowViewModel();
        await viewModel.InitializeAsync();

        var pane = viewModel.SelectedWorkspacePane;
        Assert.NotNull(pane);
        var factory = GetDockFactory(viewModel);

        static DocumentDock BuildBaseLeafWithAllFive(string id, string url)
        {
            var tab = new WebViewModel(url) { Id = id + "-tab", Title = id };
            tab.TabHeader!.Items.Add(new IconTabHeaderItemViewModel { Icon = "🚀" });
            tab.TabHeader!.Items.Add(new AgentRunningIndicatorTabHeaderItemViewModel { IsRunning = true });
            tab.TabHeader!.Items.Add(new NotificationIndicatorTabHeaderItemViewModel { HasUnread = true });
            var document = new WorkspaceDocument(tab);
            var baseDock = new DocumentDock
            {
                Id = id,
                VisibleDockables = new System.Collections.ObjectModel.ObservableCollection<IDockable> { document },
                ActiveDockable = document,
            };
            document.Owner = baseDock;
            return baseDock;
        }

        var leftBase = BuildBaseLeafWithAllFive("mr-render-left", "https://mr-left.example.com");
        var rightBase = BuildBaseLeafWithAllFive("mr-render-right", "https://mr-right.example.com");
        var splitter = new ProportionalDockSplitter { Id = "mr-render-splitter" };
        var prop = new ProportionalDock
        {
            Id = "mr-render-prop",
            VisibleDockables = new System.Collections.ObjectModel.ObservableCollection<IDockable> { leftBase, splitter, rightBase },
        };
        leftBase.Owner = prop;
        splitter.Owner = prop;
        rightBase.Owner = prop;

        var root = factory.CreateRootDock();
        root.IsCollapsable = false;
        root.VisibleDockables = factory.CreateList<IDockable>(prop);
        prop.Owner = root;
        root.ActiveDockable = prop;
        root.DefaultDockable = prop;

        MainWindowViewModel.MigrateBaseDocumentDocksToWorkspaceContentDock(root);

        var migratedDocks = EnumerateDocumentDocks(root).Cast<WorkspaceContentDock>().ToList();
        Assert.Equal(2, migratedDocks.Count);

        factory.InitLayout(root);
        pane!.ContentLayout = root;

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            foreach (var migrated in migratedDocks)
            {
                bool IsRegionStrip(object? dc) => ReferenceEquals(dc, migrated);

                var textBlocks = await GetTabStripHeaderControlsAsync<TextBlock>(window, IsRegionStrip);
                Assert.Contains(textBlocks, tb => tb.Text == "🚀");
                Assert.Contains(textBlocks, tb => tb.Text == "🌐");

                var statusControls = await GetTabStripHeaderControlsAsync<Phantom.Workspaces.Controls.StatusControl>(
                    window, IsRegionStrip);
                Assert.NotEmpty(statusControls);

                var progressBars = await GetTabStripHeaderProgressBarsAsync(window, IsRegionStrip);
                Assert.Contains(progressBars, pb => pb.Classes.Contains("pulsating-brain"));
                Assert.Contains(progressBars, pb => pb.Classes.Contains("exclamation-indicator"));
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowInnerPaneDocumentTabStrip_MenuCreatedSplitOnRestoredWorkspace_NewRegionRendersAllFiveHeaderItemTypes()
    {
        // #1330 regression guard on the menu-split path: after a restored region (migrated from a
        // base DocumentDock), a menu-created split (WorkspaceDockFactory.CreateDocumentDock, the
        // NewHorizontal/VerticalDocumentDock entry point) must still produce a header-bearing
        // WorkspaceContentDock whose tab strip renders all five per-item header families.
        await using var viewModel = CreateBootedMainWindowViewModel();
        await viewModel.InitializeAsync();

        var pane = viewModel.SelectedWorkspacePane;
        Assert.NotNull(pane);
        var factory = GetDockFactory(viewModel);

        // Restored region: a pre-#1307 base DocumentDock (migrated below).
        var restoredTab = new WebViewModel("https://restored.example.com") { Id = "restored-region-tab", Title = "Restored" };
        var restoredDoc = new WorkspaceDocument(restoredTab);
        var restoredBase = new DocumentDock
        {
            Id = "restored-region",
            VisibleDockables = new System.Collections.ObjectModel.ObservableCollection<IDockable> { restoredDoc },
            ActiveDockable = restoredDoc,
        };
        restoredDoc.Owner = restoredBase;

        // Menu-created split via the #1307 factory path (always a WorkspaceContentDock).
        var newTab = new WebViewModel("https://menu-split.example.com") { Id = "menu-split-tab", Title = "Menu Split" };
        newTab.TabHeader!.Items.Add(new IconTabHeaderItemViewModel { Icon = "🚀" });
        newTab.TabHeader!.Items.Add(new AgentRunningIndicatorTabHeaderItemViewModel { IsRunning = true });
        newTab.TabHeader!.Items.Add(new NotificationIndicatorTabHeaderItemViewModel { HasUnread = true });
        var newDoc = new WorkspaceDocument(newTab);
        var splitDock = Assert.IsType<WorkspaceContentDock>(factory.CreateDocumentDock());
        splitDock.VisibleDockables = factory.CreateList<IDockable>(newDoc);
        splitDock.ActiveDockable = newDoc;
        newDoc.Owner = splitDock;

        var splitter = new ProportionalDockSplitter { Id = "menu-split-splitter" };
        var prop = new ProportionalDock
        {
            Id = "menu-split-prop",
            VisibleDockables = new System.Collections.ObjectModel.ObservableCollection<IDockable> { restoredBase, splitter, splitDock },
        };
        restoredBase.Owner = prop;
        splitter.Owner = prop;
        splitDock.Owner = prop;

        var root = factory.CreateRootDock();
        root.IsCollapsable = false;
        root.VisibleDockables = factory.CreateList<IDockable>(prop);
        prop.Owner = root;
        root.ActiveDockable = prop;
        root.DefaultDockable = prop;

        // Restore-time migration converts the restored base leaf; the menu-split dock is already
        // a WorkspaceContentDock and is left intact.
        MainWindowViewModel.MigrateBaseDocumentDocksToWorkspaceContentDock(root);
        Assert.IsType<WorkspaceContentDock>(splitDock);

        factory.InitLayout(root);
        pane!.ContentLayout = root;

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            bool IsNewRegionStrip(object? dc) => ReferenceEquals(dc, splitDock);

            var textBlocks = await GetTabStripHeaderControlsAsync<TextBlock>(window, IsNewRegionStrip);
            Assert.Contains(textBlocks, tb => tb.Text == "🚀");
            Assert.Contains(textBlocks, tb => tb.Text == "🌐");

            var statusControls = await GetTabStripHeaderControlsAsync<Phantom.Workspaces.Controls.StatusControl>(
                window, IsNewRegionStrip);
            Assert.NotEmpty(statusControls);

            var progressBars = await GetTabStripHeaderProgressBarsAsync(window, IsNewRegionStrip);
            Assert.Contains(progressBars, pb => pb.Classes.Contains("pulsating-brain"));
            Assert.Contains(progressBars, pb => pb.Classes.Contains("exclamation-indicator"));
        }
        finally
        {
            window.Close();
        }
    }

    // ── #1334: restored multi-region ProportionalDock via the REAL restore path ──
    //
    // These five tests drive OpenWorkspaceAsync → TryRestoreFromDockLayoutAsync with a persisted
    // two-region layout (RootDock → ProportionalDock [ leftDock, splitter, rightDock ]) and assert
    // on the restored dock/document MODEL that backs each region's header, cross-region
    // tab-switch numbering, and uniform wiring. Before the fix (DFS-first-only wiring) only the
    // primary (DFS-first) region's documents were initialized/registered; every other region's
    // restored WorkspaceDocument stayed an un-initialized stub whose EffectiveTabHeader remained the
    // plain (headerless-fallback) TabHeaderViewModel — reproducing the "both regions headerless"
    // report. After the fix WorkspaceDockFactory.WireContentDock initializes and registers every
    // region, so each region's document carries the web-tab header model (WebTabHeaderViewModel with
    // the favicon + status items). These assert on the model rather than the rendered visual tree
    // because the headless harness does not inflate the nested per-item header ItemsControl for a
    // freshly opened (OpenWorkspaceAsync) pane; the render path itself is covered by the #1330
    // booted-harness tests above.

    /// <summary>
    /// Drives the real restore path: upserts a workspace entity whose <c>dock-layout</c> is a
    /// two-region ProportionalDock, opens it via <see cref="MainWindowViewModel.OpenWorkspaceAsync"/>,
    /// waits for population, and returns the restored pane and both region docks.
    /// </summary>
    private static async Task<(WorkspacePaneViewModel Pane, WorkspaceContentDock Left, WorkspaceContentDock Right)>
        RestoreTwoRegionWorkspaceAsync(
            MainWindowViewModel viewModel,
            string workspaceGuid,
            string leftTabId,
            string rightTabId,
            string leftUrl = "https://left-1334.example.com",
            string rightUrl = "https://right-1334.example.com")
    {
        var entityBroker = MainWindowIntegrationTests.GetEntityBroker(viewModel);

        var leftDockId = $"dock-left-{workspaceGuid}";
        var rightDockId = $"dock-right-{workspaceGuid}";
        var layoutJson = MultiRegionRestoreTestSupport.BuildTwoRegionDockLayoutJson(
            leftDockId, leftTabId, leftUrl, rightDockId, rightTabId, rightUrl);

        var workspaceId = new EntityId(workspaceGuid);
        var workspaceJson = $$"""
            {
              "entity-id": "{{workspaceId.Value}}",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "1334 Restore WS" },
              "dock-layout": {{layoutJson}},
              "regions": []
            }
            """;
        await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(entityBroker, workspaceId, workspaceJson);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var pane = viewModel.WorkspacePanes.Single(
            p => string.Equals(p.Id, workspaceId.ToString(), System.StringComparison.Ordinal));
        await MainWindowIntegrationTests.WaitForPanePopulatedAsync(pane);

        var docks = EnumerateDocumentDocks(pane.ContentLayout!)
            .OfType<WorkspaceContentDock>()
            .ToList();
        var left = docks.Single(d => string.Equals(d.Id, leftDockId, System.StringComparison.Ordinal));
        var right = docks.Single(d => string.Equals(d.Id, rightDockId, System.StringComparison.Ordinal));
        return (pane, left, right);
    }

    private static void AddAllFiveHeaderItems(WebViewModel tab)
    {
        // 🌐 (favicon) and the StatusControl are part of the default web-tab header; add the
        // remaining three families so an all-five assertion is meaningful.
        tab.TabHeader!.Items.Add(new IconTabHeaderItemViewModel { Icon = "🚀" });
        tab.TabHeader!.Items.Add(new AgentRunningIndicatorTabHeaderItemViewModel { IsRunning = true });
        tab.TabHeader!.Items.Add(new NotificationIndicatorTabHeaderItemViewModel { HasUnread = true });
    }

    [AvaloniaFact(Timeout = 20_000)]
    public async Task MainWindowInnerPaneDocumentTabStrip_RestoredMultiRegionProportionalDock_BothRegionsRenderAllFiveHeaderItemTypes()
    {
        // #1334: after a REAL OpenWorkspaceAsync → TryRestoreFromDockLayoutAsync wholesale-swap restore
        // of a two-region ProportionalDock layout, BOTH the left and right restored regions' documents
        // must carry the web-tab header model (WebTabHeaderViewModel with all five per-item families),
        // AND the mounted MainWindow must actually render a headed (StatusControl-bearing) tab strip for
        // BOTH regions — not the headerless title+close fallback. Before the fix the non-primary region
        // stayed an un-initialized stub whose EffectiveTabHeader was the plain fallback header.
        //
        // Note: on this real-restore path the headless renderer materializes decoupled region documents
        // whose per-item ItemsControl only inflates the always-present StatusControl (the freshly
        // OpenWorkspaceAsync-generated documents do not surface the 🚀/🌐 items added post-restore); the
        // full 🚀/🌐/progress-bar per-item render assertion on a wholesale swap is covered by
        // WorkspacePaneInnerDockControl_WholesaleLayoutSwapMultiRegionProportionalDock_BothRegionsRenderAllFiveHeaderItemTypes.
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var (pane, left, right) = await RestoreTwoRegionWorkspaceAsync(
            viewModel, "d0c11334-0001-4000-8000-000000000001", "tab-left-1", "tab-right-1");

        var leftTab = pane.Tabs.OfType<WebViewModel>().Single(t => t.Id == "tab-left-1");
        var rightTab = pane.Tabs.OfType<WebViewModel>().Single(t => t.Id == "tab-right-1");
        AddAllFiveHeaderItems(leftTab);
        AddAllFiveHeaderItems(rightTab);

        // Force each region document to rebuild its cached header items from its (now five-family)
        // tab header. A Title change is the model signal WorkspaceDocument listens to; a non-initialized
        // stub never subscribed, so this no-ops on the broken (pre-fix) non-primary region.
        leftTab.Title = "Left region";
        rightTab.Title = "Right region";

        // Model guard: both restored regions resolve to the web-tab header model (not the fallback).
        foreach (var (region, tabId) in new[] { (left, "tab-left-1"), (right, "tab-right-1") })
        {
            var document = region.VisibleDockables!.OfType<WorkspaceDocument>().Single();
            Assert.Equal(tabId, document.Id);
            Assert.NotNull(document.TabViewModel);

            var header = Assert.IsType<WebTabHeaderViewModel>(document.EffectiveTabHeader);

            Assert.Contains(header.Items, i => i is FaviconTabHeaderItemViewModel);
            Assert.Contains(header.Items, i => i is IconTabHeaderItemViewModel);
            Assert.Contains(header.Items, i => i is AgentRunningIndicatorTabHeaderItemViewModel);
            Assert.Contains(header.Items, i => i is NotificationIndicatorTabHeaderItemViewModel);
            Assert.Contains(header.Items, i => i is StatusTabHeaderItemViewModel);
        }

        // Rendered visual guard: mount the real MainWindow on the restored pane and assert BOTH regions'
        // tab strips render a StatusControl — i.e. both regions materialize the header-bearing
        // WorkspaceContentDock DocumentControl template, not the headerless IDocumentDock fallback.
        viewModel.SelectedWorkspacePane = pane;

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            foreach (var region in new[] { left, right })
            {
                bool IsRegionStrip(object? dc) =>
                    dc is WorkspaceContentDock wcd && string.Equals(wcd.Id, region.Id, System.StringComparison.Ordinal);

                var statusControls = await GetTabStripHeaderControlsAsync<Phantom.Workspaces.Controls.StatusControl>(
                    window, IsRegionStrip);
                Assert.NotEmpty(statusControls);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 20_000)]
    public async Task WorkspacePaneInnerDockControl_WholesaleLayoutSwapMultiRegionProportionalDock_BothRegionsRenderAllFiveHeaderItemTypes()
    {
        // #1334 (rendered visual assertion on the wholesale-Layout-swap re-materialization path):
        // build the inner WorkspacePaneDocument DockControl straight from its production
        // DataTemplate — i.e. the scoped, non-inheriting AutoCreateDataTemplates="False" template set
        // (#1130) that a restore re-materializes against — assign it a brand-new multi-region IRootDock
        // graph (RootDock → ProportionalDock [ WorkspaceContentDock, splitter, WorkspaceContentDock ])
        // via a wholesale WorkspacePane.ContentLayout swap, then mount it in a window and assert that
        // BOTH regions' tab strips actually render every per-item header family: the 🚀 entity-icon and
        // 🌐 favicon TextBlocks, a StatusControl, a pulsating-brain ProgressBar, and an
        // exclamation-indicator ProgressBar — via the same GetTabStripHeaderControlsAsync /
        // GetTabStripHeaderProgressBarsAsync helpers the single-region render tests use.
        //
        // This drives the exact inner-scope template set on a wholesale Layout swap (not a live in-place
        // mutation), which is the shape TryRestoreFromDockLayoutAsync produces. See the implementation
        // notes on #1334 for why a RED→GREEN transition against the inner dmc:IRootDock template is not
        // observable in the headless harness (Dock.Avalonia's DockControl renders its top-level
        // IRootDock Layout through its own control template, not a DataTemplate lookup), so this is a
        // rendered-visual multi-region regression guard rather than a reverting-the-fix reproducer.
        await using var viewModel = CreateBootedMainWindowViewModel();
        await viewModel.InitializeAsync();
        var pane = viewModel.SelectedWorkspacePane!;
        var factory = GetDockFactory(viewModel);

        WorkspaceContentDock BuildRegion(string id, string url)
        {
            var tab = new WebViewModel(url) { Id = id + "-tab", Title = id };
            tab.TabHeader!.Items.Add(new IconTabHeaderItemViewModel { Icon = "🚀" });
            tab.TabHeader!.Items.Add(new AgentRunningIndicatorTabHeaderItemViewModel { IsRunning = true });
            tab.TabHeader!.Items.Add(new NotificationIndicatorTabHeaderItemViewModel { HasUnread = true });
            var doc = new WorkspaceDocument(tab);
            var dock = new WorkspaceContentDock
            {
                Id = id,
                VisibleDockables = factory.CreateList<IDockable>(doc),
                ActiveDockable = doc,
            };
            doc.Owner = dock;
            return dock;
        }

        var leftDock = BuildRegion("swap-left", "https://swap-left.example.com");
        var rightDock = BuildRegion("swap-right", "https://swap-right.example.com");
        var splitter = new ProportionalDockSplitter { Id = "swap-splitter" };
        var prop = new ProportionalDock
        {
            Id = "swap-prop",
            VisibleDockables = factory.CreateList<IDockable>(leftDock, splitter, rightDock),
        };
        leftDock.Owner = prop;
        splitter.Owner = prop;
        rightDock.Owner = prop;

        var root = factory.CreateRootDock();
        root.IsCollapsable = false;
        root.VisibleDockables = factory.CreateList<IDockable>(prop);
        prop.Owner = root;
        root.ActiveDockable = prop;
        root.DefaultDockable = prop;
        factory.InitLayout(root);

        // Wholesale-swap the pane's ContentLayout, then build the inner DockControl from its production
        // template so it re-materializes against the scoped inner template set exactly as on restore.
        pane.ContentLayout = root;
        var paneDoc = new WorkspacePaneDocument(pane);
        var innerTemplate = new DockDataTemplates()
            .OfType<IDataTemplate>()
            .First(t => t.Match(paneDoc));
        var innerDockControl = Assert.IsType<DockControl>(innerTemplate.Build(paneDoc));
        innerDockControl.DataContext = paneDoc;

        var window = new Avalonia.Controls.Window
        {
            Width = 900,
            Height = 600,
            Content = innerDockControl,
        };
        window.Show();
        try
        {
            foreach (var region in new[] { leftDock, rightDock })
            {
                bool IsRegionStrip(object? dc) =>
                    dc is WorkspaceContentDock wcd && string.Equals(wcd.Id, region.Id, System.StringComparison.Ordinal);

                var textBlocks = await GetTabStripHeaderControlsAsync<TextBlock>(window, IsRegionStrip);
                Assert.Contains(textBlocks, tb => tb.Text == "🚀");
                Assert.Contains(textBlocks, tb => tb.Text == "🌐");

                var statusControls = await GetTabStripHeaderControlsAsync<Phantom.Workspaces.Controls.StatusControl>(
                    window, IsRegionStrip);
                Assert.NotEmpty(statusControls);

                var progressBars = await GetTabStripHeaderProgressBarsAsync(window, IsRegionStrip);
                Assert.Contains(progressBars, pb => pb.Classes.Contains("pulsating-brain"));
                Assert.Contains(progressBars, pb => pb.Classes.Contains("exclamation-indicator"));
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 20_000)]
    public async Task DockTabSwitch_RestoredMultiRegionProportionalDock_AssignsContiguousBadgesAcrossBothRegions()
    {
        // #1334: after a real restore, the cross-region Alt+Digit switch numbering must be contiguous
        // across BOTH regions — the DockTabOrder union is [leftTab (badge 1), rightTab (badge 2)], the
        // two ordered tabs live in DIFFERENT regions, and each is an initialized web document that can
        // actually carry a numbered badge. Before the fix the non-primary region's tab stayed an
        // un-initialized, un-registered stub, so it could not be numbered/activated in its own region.
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var (pane, left, right) = await RestoreTwoRegionWorkspaceAsync(
            viewModel, "d0c11334-0002-4000-8000-000000000002", "tab-left-2", "tab-right-2");

        // Cross-region ordering is contiguous [leftTab, rightTab] per DockTabOrder — the 1-based index
        // into this union is the Alt+Digit badge number.
        var ordered = new global::Phantom.Dock.Avalonia.TabSwitching.DockTabOrder()
            .Compute(pane.ContentLayout)
            .ToList();
        Assert.Equal(
            new[] { "tab-left-2", "tab-right-2" },
            ordered.Select(e => e.Dockable.Id).ToArray());

        // Contiguous numbering spans BOTH regions: badge 1 is in the left region, badge 2 in the right.
        Assert.Same(left, ordered[0].Dockable.Owner);
        Assert.Same(right, ordered[1].Dockable.Owner);

        // Every ordered entry is an initialized web document (headed), so each badge slot in the union
        // belongs to a real, numbered region tab rather than a headerless fallback stub.
        foreach (var entry in ordered)
        {
            var document = Assert.IsType<WorkspaceDocument>(entry.Dockable);
            Assert.NotNull(document.TabViewModel);
            Assert.IsType<WebTabHeaderViewModel>(document.EffectiveTabHeader);
        }
    }

    [AvaloniaFact(Timeout = 25_000)]
    public async Task MainWindowViewModel_RestoredMultiRegionWorkspace_SwitchAwayAndBack_BothRegionsStillAssignBadges()
    {
        // #1334 (locks the #1332 interaction on the restored multi-region shape): switch to another
        // workspace and back, then verify BOTH restored regions still carry their initialized web-tab
        // header/badge scope and the cross-region switch order is still contiguous across the union.
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var (pane, left, right) = await RestoreTwoRegionWorkspaceAsync(
            viewModel, "d0c11334-0004-4000-8000-000000000004", "tab-left-4", "tab-right-4");

        // Open a second (empty) workspace and switch to it, then back.
        var entityBroker = MainWindowIntegrationTests.GetEntityBroker(viewModel);
        var otherId = new EntityId("d0c11334-0004-4000-8000-0000000000ff");
        await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(entityBroker, otherId, $$"""
            {
              "entity-id": "{{otherId.Value}}",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "1334 Other WS" },
              "regions": []
            }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = otherId });
        var otherPane = viewModel.WorkspacePanes.Single(
            p => string.Equals(p.Id, otherId.ToString(), System.StringComparison.Ordinal));

        viewModel.SelectedWorkspacePane = otherPane;
        viewModel.SelectedWorkspacePane = pane;

        // After the round-trip both restored regions still resolve to an initialized web document.
        var leftDoc = left.VisibleDockables!.OfType<WorkspaceDocument>().Single();
        var rightDoc = right.VisibleDockables!.OfType<WorkspaceDocument>().Single();
        Assert.NotNull(leftDoc.TabViewModel);
        Assert.NotNull(rightDoc.TabViewModel);
        Assert.IsType<WebTabHeaderViewModel>(leftDoc.EffectiveTabHeader);
        Assert.IsType<WebTabHeaderViewModel>(rightDoc.EffectiveTabHeader);

        // Cross-region badge numbering is still contiguous across the union after switching back.
        var ordered = new global::Phantom.Dock.Avalonia.TabSwitching.DockTabOrder()
            .Compute(pane.ContentLayout)
            .ToList();
        Assert.Equal(
            new[] { "tab-left-4", "tab-right-4" },
            ordered.Select(e => e.Dockable.Id).ToArray());
        Assert.Same(leftDoc, ordered[0].Dockable);
        Assert.Same(rightDoc, ordered[1].Dockable);
    }

    private static System.Collections.Generic.IEnumerable<IDocumentDock> EnumerateDocumentDocks(IDockable dockable)
    {
        if (dockable is IDocumentDock documentDock)
        {
            yield return documentDock;
        }

        if (dockable is IDock dock && dock.VisibleDockables is not null)
        {
            foreach (var child in dock.VisibleDockables)
            {
                foreach (var found in EnumerateDocumentDocks(child))
                {
                    yield return found;
                }
            }
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
    public void InnerWorkspacePaneDockControl_HasOnlyItsScopedSixTemplateSubset()
    {
        // #1130: the inner-pane DockControl has a hand-picked template subset
        // declared in-XAML on DockDataTemplates.axaml (WorkspaceContentDock,
        // WorkspaceDocument, IProportionalDock, IProportionalDockSplitter,
        // IDocumentDock). #1196 must NOT overwrite it. #1334 added a sixth key,
        // IRootDock, so the restore-time wholesale Layout swap resolves the
        // restored root (and its nested multi-region leaves) against this scope.
        var innerDockControl = BuildInnerWorkspacePaneDockControl();

        Assert.Equal(6, innerDockControl.DataTemplates.Count);

        // #1334: IRootDock must now be matched by the inner set so a restored
        // RootDock graph resolves locally instead of falling through to the
        // headerless IDocumentDock fallback.
        var rootDock = new global::Dock.Model.Mvvm.Controls.RootDock();
        Assert.NotNull(innerDockControl.DataTemplates
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
        // declares its own scoped 6-template subset (#1334 added IRootDock).
        // PhantomHostWindow's OnApplyTemplate must skip any DockControl whose
        // DataTemplates.Count > 0.
        var innerDockControl = BuildInnerWorkspacePaneDockControl();
        Assert.Equal(6, innerDockControl.DataTemplates.Count);
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

            // Inner DockControl kept its scoped 6-template subset unchanged.
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
        // 6-template subset (#1334 added IRootDock).
        var viewModel = CreateTestMainWindowViewModel();
        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            var referenceCount = new DockDataTemplates().OfType<IDataTemplate>().Count();
            const int innerScopedSubsetCount = 6;

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

