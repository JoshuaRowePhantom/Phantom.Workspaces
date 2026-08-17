using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using global::Dock.Model.Controls;
using global::Dock.Model.Core;
using global::Dock.Model.Mvvm.Controls;
using AgentSchema;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Templates;
using Phantom.Workspaces.ViewModels;
using AgentViewModel = Phantom.Workspaces.Agent.Gui.ViewModels.AgentViewModel;
using Xunit;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Tests;

public sealed class TabHeaderViewModelTests
{
    // ── FaviconTabHeaderItemViewModel ────────────────────────────────────────

    [Fact]
    public void FaviconTabHeaderItemViewModel_FaviconUri_DefaultIsNull()
    {
        var item = new FaviconTabHeaderItemViewModel();
        Assert.Null(item.FaviconUri);
    }

    [Fact]
    public void FaviconTabHeaderItemViewModel_SetFaviconUri_RaisesPropertyChanged()
    {
        var item = new FaviconTabHeaderItemViewModel();
        var raised = false;
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(item.FaviconUri))
                raised = true;
        };

        item.FaviconUri = "https://example.com/favicon.ico";

        Assert.True(raised);
    }

    // ── AgentRunningIndicatorTabHeaderItemViewModel ──────────────────────────

    [Fact]
    public void AgentRunningIndicator_IsRunning_DefaultIsFalse()
    {
        var indicator = new AgentRunningIndicatorTabHeaderItemViewModel();
        Assert.False(indicator.IsRunning);
    }

    [Fact]
    public void AgentRunningIndicator_SetIsRunning_RaisesPropertyChanged()
    {
        var indicator = new AgentRunningIndicatorTabHeaderItemViewModel();
        var raised = false;
        indicator.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(indicator.IsRunning))
                raised = true;
        };

        indicator.IsRunning = true;

        Assert.True(raised);
    }

    // ── AgentSessionWorkspaceTabViewModel – TabStatus tracks running state ────

    [Fact]
    public void AgentSessionTab_TabStatus_IsNotNull()
    {
        var tab = new AgentSessionWorkspaceTabViewModel { Id = "t", Title = "Test" };

        Assert.NotNull(tab.TabStatus);
    }

    [Fact]
    public void AgentSessionTab_TabStatus_IsIdle_Initially()
    {
        var tab = new AgentSessionWorkspaceTabViewModel { Id = "t", Title = "Test" };

        Assert.Equal(RunningStatus.Idle, tab.TabStatus!.RunningStatus);
    }

    [Fact]
    public void AgentSessionTab_TabHeader_IsNull_ByDefault()
    {
        var tab = new AgentSessionWorkspaceTabViewModel { Id = "t", Title = "Test" };

        Assert.Null(tab.TabHeader);
    }

    [Fact]
    public void AgentSessionTab_EffectiveTabHeader_ContainsStatusIndicator()
    {
        var tab = new AgentSessionWorkspaceTabViewModel { Id = "t", Title = "Test" };
        var doc = new WorkspaceDocument(tab);

        var indicator = doc.EffectiveTabHeader.Items
            .OfType<StatusTabHeaderItemViewModel>()
            .FirstOrDefault();

        Assert.NotNull(indicator);
    }

    // ── NotificationIndicatorTabHeaderItemViewModel ──────────────────────────

    [Fact]
    public void NotificationIndicatorTabHeaderItemViewModel_HasUnread_DefaultIsFalse()
    {
        var indicator = new NotificationIndicatorTabHeaderItemViewModel();
        Assert.False(indicator.HasUnread);
    }

    [Fact]
    public void NotificationIndicatorTabHeaderItemViewModel_SetHasUnread_RaisesPropertyChanged()
    {
        var indicator = new NotificationIndicatorTabHeaderItemViewModel();
        var raised = false;
        indicator.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(indicator.HasUnread))
            {
                raised = true;
            }
        };

        indicator.HasUnread = true;

        Assert.True(raised);
    }

    // ── WorkspaceDocument – title does NOT get "! " prefix ───────────────────

    [Fact]
    public void WorkspaceDocument_Title_DoesNotPrependExclamationMark_WhenHasUnreadNotification()
    {
        var tab = new EntityWorkspaceTabViewModel { Id = "t1", Title = "My Tab" };
        var doc = new WorkspaceDocument(tab);

        doc.HasUnreadNotification = true;

        Assert.DoesNotContain("!", doc.Title);
    }

    // ── WorkspaceDocument – EffectiveTabHeader always contains the status indicator ─

    [Fact]
    public void WorkspaceDocument_EffectiveTabHeader_ContainsStatusIndicatorItem()
    {
        var tab = new EntityWorkspaceTabViewModel { Id = "t2", Title = "My Tab" };
        var doc = new WorkspaceDocument(tab);

        var indicator = doc.EffectiveTabHeader.Items
            .OfType<StatusTabHeaderItemViewModel>()
            .FirstOrDefault();

        Assert.NotNull(indicator);
    }

    [Fact]
    public void WorkspaceDocument_HasUnreadNotification_SetToTrue_SetsErrorStatusOnStatusIndicator()
    {
        var tab = new EntityWorkspaceTabViewModel { Id = "t3", Title = "My Tab" };
        var doc = new WorkspaceDocument(tab);

        doc.HasUnreadNotification = true;

        var indicator = doc.EffectiveTabHeader.Items
            .OfType<StatusTabHeaderItemViewModel>()
            .Single();
        Assert.Equal(ErrorStatus.Error, indicator.Status.ErrorStatus);
    }

    [Fact]
    public void WorkspaceDocument_HasUnreadNotification_SetToFalse_ClearsErrorStatusOnStatusIndicator()
    {
        var tab = new EntityWorkspaceTabViewModel { Id = "t4", Title = "My Tab" };
        var doc = new WorkspaceDocument(tab);

        doc.HasUnreadNotification = true;
        doc.HasUnreadNotification = false;

        var indicator = doc.EffectiveTabHeader.Items
            .OfType<StatusTabHeaderItemViewModel>()
            .Single();
        Assert.Equal(ErrorStatus.None, indicator.Status.ErrorStatus);
    }

    // ── WorkspaceDocument – icon items are preserved from TabHeader ──────────

    [Fact]
    public void WorkspaceDocument_EffectiveTabHeader_WithIconTabHeader_ContainsIconItem()
    {
        var tab = new EntityWorkspaceTabViewModel
        {
            Id = "t5",
            Title = "My Tab",
            TabHeader = TabHeaderViewModel.WithIcon("🧠", "My Tab"),
        };
        var doc = new WorkspaceDocument(tab);

        var iconItem = doc.EffectiveTabHeader.Items
            .OfType<IconTabHeaderItemViewModel>()
            .FirstOrDefault();

        Assert.NotNull(iconItem);
        Assert.Equal("🧠", iconItem!.Icon);
    }

    // ── WorkspaceDocument – EffectiveTabHeader.Title tracks tab title ────────

    [Fact]
    public void WorkspaceDocument_EffectiveTabHeader_Title_MatchesTabTitle()
    {
        var tab = new EntityWorkspaceTabViewModel { Id = "t6", Title = "Some Title" };
        var doc = new WorkspaceDocument(tab);

        Assert.Equal("Some Title", doc.EffectiveTabHeader.Title);
    }

    [Fact]
    public void WorkspaceDocument_EffectiveTabHeader_Title_UpdatesWhenTabTitleChanges()
    {
        var tab = new EntityWorkspaceTabViewModel { Id = "t7", Title = "Original" };
        var doc = new WorkspaceDocument(tab);

        tab.Title = "Updated";

        Assert.Equal("Updated", doc.EffectiveTabHeader.Title);
    }

    // ── #1324: per-item templates are centralized keyed resources, not implicit ──

    [AvaloniaFact(Timeout = 15_000)]
    public void WorkspaceDataTemplates_DoesNotDeclareImplicitIconTabHeaderItemTemplate()
    {
        // #1324: the Icon per-item template was moved out of WorkspaceDataTemplates (where the
        // scope-blocked inner DockControl could not reach it) into the centralized keyed resource
        // dictionary. It must NOT be re-declared here as an implicit top-level template.
        var templates = new WorkspaceDataTemplates();
        var viewModel = new IconTabHeaderItemViewModel { Icon = "🧠" };

        Assert.DoesNotContain(templates.OfType<IDataTemplate>(), t => t.Match(viewModel));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void IconTabHeaderItemTemplate_IsCentralizedKeyedResource()
    {
        // #1324: the Icon per-item template is now a keyed resource in TabHeaderItemTemplates.axaml
        // (merged into App.axaml), reachable identically from every DockControl scope.
        Assert.NotNull(Avalonia.Application.Current);
        Assert.True(Avalonia.Application.Current!.TryFindResource(
            "IconTabHeaderItemTemplate", null, out var resource));
        var template = Assert.IsAssignableFrom<IDataTemplate>(resource);
        Assert.True(template.Match(new IconTabHeaderItemViewModel { Icon = "🧠" }));
    }

    // ── #1196: The outer tab-header body is a single keyed resource ───────────

    [AvaloniaFact(Timeout = 15_000)]
    public void TabHeaderTemplate_IsDefinedExactlyOnceInApplicationResources()
    {
        // #1196: the outer DocumentControl.HeaderTemplate references this keyed
        // resource explicitly via ContentControl.ContentTemplate; there must be
        // exactly one definition so no implicit vm:TabHeaderViewModel lookup is
        // required from inside the Dock.Avalonia tab-strip item scope.
        Assert.NotNull(Avalonia.Application.Current);
        var found = Avalonia.Application.Current!.TryFindResource(
            "TabHeaderTemplate", null, out var resource);

        Assert.True(found);
        var template = Assert.IsAssignableFrom<IDataTemplate>(resource);
        Assert.True(template.Match(new TabHeaderViewModel { Title = "T" }));

        // The keyed template body must not be duplicated as an implicit
        // DataTemplate in either DockDataTemplates or WorkspaceDataTemplates.
        Assert.DoesNotContain(
            new DockDataTemplates().OfType<IDataTemplate>(),
            t => t.Match(new TabHeaderViewModel { Title = "T" }));
        Assert.DoesNotContain(
            new WorkspaceDataTemplates().OfType<IDataTemplate>(),
            t => t.Match(new TabHeaderViewModel { Title = "T" }));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void WebTabHeaderTemplate_IsDefinedInApplicationResources()
    {
        Assert.NotNull(Avalonia.Application.Current);
        var found = Avalonia.Application.Current!.TryFindResource(
            "WebTabHeaderTemplate", null, out var resource);

        Assert.True(found);
        var template = Assert.IsAssignableFrom<IDataTemplate>(resource);
        Assert.True(template.Match(new WebTabHeaderViewModel { Title = "T" }));
    }

    private static IDataTemplate ResolveTabHeaderTemplate()
    {
        // #1196: the single-source header body lives in App.Resources as the
        // keyed "TabHeaderTemplate" (TabHeaderItemTemplates.axaml).
        Assert.NotNull(Avalonia.Application.Current);
        Assert.True(Avalonia.Application.Current!.TryFindResource(
            "TabHeaderTemplate", null, out var resource));
        return Assert.IsAssignableFrom<IDataTemplate>(resource);
    }

    private static IDataTemplate ResolveWebTabHeaderTemplate()
    {
        Assert.NotNull(Avalonia.Application.Current);
        Assert.True(Avalonia.Application.Current!.TryFindResource(
            "WebTabHeaderTemplate", null, out var resource));
        return Assert.IsAssignableFrom<IDataTemplate>(resource);
    }

    // ── #1181: TabHeader title TextBlock exposes full title via ToolTip.Tip ──

    [AvaloniaFact(Timeout = 15_000)]
    public void TabHeaderDataTemplate_TitleTextBlock_HasToolTipBoundToTitle()
    {
        var viewModel = new TabHeaderViewModel { Title = "My Tab" };
        var titleTextBlock = InflateTabHeaderTitleTextBlock(viewModel);

        Assert.Equal("My Tab", ToolTip.GetTip(titleTextBlock));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void TabHeaderDataTemplate_LongTitle_ToolTipExposesFullUntruncatedText()
    {
        const string longTitle = "Copilot SDK sub-agent design document";
        var viewModel = new TabHeaderViewModel { Title = longTitle };
        var titleTextBlock = InflateTabHeaderTitleTextBlock(viewModel);

        Assert.Equal(longTitle, ToolTip.GetTip(titleTextBlock));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void TabHeaderDataTemplate_TitleChanged_ToolTipUpdatesLive()
    {
        var viewModel = new TabHeaderViewModel { Title = "Original" };
        var titleTextBlock = InflateTabHeaderTitleTextBlock(viewModel);

        viewModel.Title = "Renamed";

        Assert.Equal("Renamed", ToolTip.GetTip(titleTextBlock));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void TabHeaderViewModel_WebTabHeaderTemplate_UsesCharacterEllipsisTrimming()
    {
        var viewModel = new WebTabHeaderViewModel { Title = "Web Tab" };
        var titleTextBlock = InflateTabHeaderTitleTextBlock(viewModel, ResolveWebTabHeaderTemplate());

        Assert.Equal(TextTrimming.CharacterEllipsis, titleTextBlock.TextTrimming);
    }

    // #1287: long titles are capped at MaxWidth=180 (0.75 * previous 240 fixed
    // Width) under the tab strip's infinite-width measure pass, so trimming
    // still engages while short titles are free to shrink to content.
    [AvaloniaFact(Timeout = 15_000)]
    public void WebTabHeaderTemplate_LongTitleUnderInfiniteAvailableWidth_TextBlockIsCappedAtMaxWidth()
    {
        const string longTitle = "Bug: \"Microsoft.Extensions.AI.UsageContent\" leaks as literal text in assistant replies on tool-only turns";
        var viewModel = new WebTabHeaderViewModel { Title = longTitle };

        var titleTextBlock = InflateTabHeaderTitleTextBlock(
            viewModel,
            ResolveWebTabHeaderTemplate(),
            new Avalonia.Size(double.PositiveInfinity, double.PositiveInfinity));

        Assert.Equal(TextTrimming.CharacterEllipsis, titleTextBlock.TextTrimming);
        Assert.Equal(TextWrapping.NoWrap, titleTextBlock.TextWrapping);
        Assert.True(double.IsNaN(titleTextBlock.Width));
        Assert.Equal(180, titleTextBlock.MaxWidth);

        var desired = MeasureAndArrangeTextBlockUnderInfiniteWidth(titleTextBlock);
        Assert.InRange(desired.Width, 150, 180);
        Assert.InRange(titleTextBlock.Bounds.Width, 150, 180);
        AssertTextLayoutTrimmedToSingleEllipsizedLine(titleTextBlock, longTitle);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void WebTabHeaderTemplate_LongTitle_ToolTipStillExposesFullTitle()
    {
        const string longTitle = "Bug: Web tab titles are not length-limited and full page titles overflow the tab strip";
        var viewModel = new WebTabHeaderViewModel { Title = longTitle };

        var titleTextBlock = InflateTabHeaderTitleTextBlock(viewModel, ResolveWebTabHeaderTemplate());

        Assert.Equal(longTitle, ToolTip.GetTip(titleTextBlock));
    }

    // #1287: short titles size to content — bounds width strictly < MaxWidth=180.
    [AvaloniaFact(Timeout = 15_000)]
    public void WebTabHeaderTemplate_ShortTitleUnderInfiniteAvailableWidth_TextBlockShrinksBelowMaxWidth()
    {
        const string title = "Design 6";
        var viewModel = new WebTabHeaderViewModel { Title = title };

        var titleTextBlock = InflateTabHeaderTitleTextBlock(
            viewModel,
            ResolveWebTabHeaderTemplate(),
            new Avalonia.Size(double.PositiveInfinity, double.PositiveInfinity));

        Assert.Equal(title, titleTextBlock.Text);
        Assert.Equal(TextTrimming.CharacterEllipsis, titleTextBlock.TextTrimming);
        Assert.True(double.IsNaN(titleTextBlock.Width));
        Assert.Equal(180, titleTextBlock.MaxWidth);
        var desired = MeasureAndArrangeTextBlockUnderInfiniteWidth(titleTextBlock);
        Assert.True(
            desired.Width < 180,
            $"Expected short title desired width < 180 but was {desired.Width}.");
        Assert.True(
            titleTextBlock.Bounds.Width < 180 && titleTextBlock.Bounds.Width > 0,
            $"Expected short title bounds width in (0, 180) but was {titleTextBlock.Bounds.Width}.");
    }

    // #1287: long non-web titles are also capped at MaxWidth=180.
    [AvaloniaFact(Timeout = 15_000)]
    public void TabHeaderTemplate_LongTitleUnderInfiniteAvailableWidth_TextBlockIsCappedAtMaxWidth()
    {
        var viewModel = new TabHeaderViewModel
        {
            Title = "Long non-web tab title that should be bounded by the shared tab header template",
        };

        var titleTextBlock = InflateTabHeaderTitleTextBlock(
            viewModel,
            ResolveTabHeaderTemplate(),
            new Avalonia.Size(double.PositiveInfinity, double.PositiveInfinity));

        Assert.Equal(TextTrimming.CharacterEllipsis, titleTextBlock.TextTrimming);
        Assert.Equal(TextWrapping.NoWrap, titleTextBlock.TextWrapping);
        Assert.True(double.IsNaN(titleTextBlock.Width));
        Assert.Equal(180, titleTextBlock.MaxWidth);
        var desired = MeasureAndArrangeTextBlockUnderInfiniteWidth(titleTextBlock);
        Assert.InRange(desired.Width, 150, 180);
        Assert.InRange(titleTextBlock.Bounds.Width, 150, 180);
    }

    // #1287: short non-web titles size to content — bounds width strictly < MaxWidth=180.
    [AvaloniaFact(Timeout = 15_000)]
    public void TabHeaderTemplate_ShortTitleUnderInfiniteAvailableWidth_TextBlockShrinksBelowMaxWidth()
    {
        const string title = "Design 6";
        var viewModel = new TabHeaderViewModel { Title = title };

        var titleTextBlock = InflateTabHeaderTitleTextBlock(
            viewModel,
            ResolveTabHeaderTemplate(),
            new Avalonia.Size(double.PositiveInfinity, double.PositiveInfinity));

        Assert.Equal(title, titleTextBlock.Text);
        Assert.True(double.IsNaN(titleTextBlock.Width));
        Assert.Equal(180, titleTextBlock.MaxWidth);
        var desired = MeasureAndArrangeTextBlockUnderInfiniteWidth(titleTextBlock);
        Assert.True(
            desired.Width < 180,
            $"Expected short title desired width < 180 but was {desired.Width}.");
        Assert.True(
            titleTextBlock.Bounds.Width < 180 && titleTextBlock.Bounds.Width > 0,
            $"Expected short title bounds width in (0, 180) but was {titleTextBlock.Bounds.Width}.");
    }

    // #1287: directly measure/arrange a title TextBlock under infinite available
    // width so Bounds reflects the arranged size (the InflateTabHeaderTitleTextBlock
    // ContentControl host has no theme applied in these unit tests, so its
    // DesiredSize is 0 and it cannot arrange the child TextBlock itself).
    private static Avalonia.Size MeasureAndArrangeTextBlockUnderInfiniteWidth(TextBlock textBlock)
    {
        textBlock.Measure(new Avalonia.Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = textBlock.DesiredSize;
        textBlock.Arrange(new Avalonia.Rect(0, 0, desired.Width, desired.Height));
        return desired;
    }

    private static TextBlock InflateTabHeaderTitleTextBlock(TabHeaderViewModel viewModel)
        => InflateTabHeaderTitleTextBlock(viewModel, ResolveTabHeaderTemplate());

    private static TextBlock InflateTabHeaderTitleTextBlock(TabHeaderViewModel viewModel, IDataTemplate template)
        => InflateTabHeaderTitleTextBlock(viewModel, template, new Avalonia.Size(1000, 600));

    private static TextBlock InflateTabHeaderTitleTextBlock(TabHeaderViewModel viewModel, IDataTemplate template, Avalonia.Size measureSize)
    {
        var control = template.Build(viewModel);
        Assert.NotNull(control);
        control!.DataContext = viewModel;

        var host = new ContentControl { Content = control };
        host.Measure(measureSize);
        var arrangeWidth = double.IsPositiveInfinity(measureSize.Width) ? host.DesiredSize.Width : measureSize.Width;
        var arrangeHeight = double.IsPositiveInfinity(measureSize.Height) ? host.DesiredSize.Height : measureSize.Height;
        host.Arrange(new Avalonia.Rect(0, 0, arrangeWidth, arrangeHeight));

        return control.GetLogicalDescendants()
            .OfType<TextBlock>()
            .First(tb => tb.Text == viewModel.Title);
    }

    private static void AssertTextLayoutTrimmedToSingleEllipsizedLine(TextBlock titleTextBlock, string fullTitle)
    {
        var lines = titleTextBlock.TextLayout.TextLines;
        var line = Assert.Single(lines);
        Assert.True(
            line.Length > fullTitle.Length,
            $"Expected the trimmed layout line to include an ellipsis marker; line length was {line.Length}, title length was {fullTitle.Length}.");
    }

    // ── #1196: Indicator DataTemplates are keyed resources in App.Resources ────
    //
    // The previous DockDataTemplates / WorkspaceDataTemplates presence tests are
    // superseded by the Application-resources tests below
    // (AgentRunningIndicatorTabHeaderItemTemplate_IsDefinedExactlyOnceInApplicationResources,
    // TabHeaderViewModelTemplate_WithRunningIndicatorItem_MaterialisesPulsatingBrainProgressBar,
    // etc.). The templates now live in exactly one place — TabHeaderItemTemplates.axaml.

    // ── #1119: DockDataTemplates must include a template for WorkspacesPaneDock ─────

    [AvaloniaFact(Timeout = 15_000)]
    public void DockDataTemplates_HasDataTemplateFor_WorkspacesPaneDock()
    {
        var templates = new DockDataTemplates();
        var viewModel = new WorkspacesPaneDock();

        var matchingTemplate = templates.Cast<IDataTemplate>().First(t => t.Match(viewModel));

        Assert.NotNull(matchingTemplate);
    }

    // ── #1196: Application-resource-scoped indicator DataTemplates ────────────

    [AvaloniaFact(Timeout = 15_000)]
    public void AgentRunningIndicatorTabHeaderItemTemplate_IsDefinedExactlyOnceInApplicationResources()
    {
        Assert.NotNull(Avalonia.Application.Current);
        var found = Avalonia.Application.Current!.TryFindResource(
            "AgentRunningIndicatorTabHeaderItemTemplate", null, out var resource);

        Assert.True(found);
        var template = Assert.IsAssignableFrom<IDataTemplate>(resource);
        Assert.True(template.Match(new AgentRunningIndicatorTabHeaderItemViewModel()));

        AssertClassOccursInExactlyOneTemplateFile("pulsating-brain");
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void NotificationIndicatorTabHeaderItemTemplate_IsDefinedExactlyOnceInApplicationResources()
    {
        Assert.NotNull(Avalonia.Application.Current);
        var found = Avalonia.Application.Current!.TryFindResource(
            "NotificationIndicatorTabHeaderItemTemplate", null, out var resource);

        Assert.True(found);
        var template = Assert.IsAssignableFrom<IDataTemplate>(resource);
        Assert.True(template.Match(new NotificationIndicatorTabHeaderItemViewModel()));

        AssertClassOccursInExactlyOneTemplateFile("exclamation-indicator");
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void TabHeaderViewModelTemplate_WithRunningIndicatorItem_MaterialisesPulsatingBrainProgressBar()
    {
        var viewModel = new TabHeaderViewModel { Title = "T" };
        viewModel.Items.Add(new AgentRunningIndicatorTabHeaderItemViewModel { IsRunning = true });

        var control = InflateAndRenderTabHeader(viewModel);

        var progressBar = control.GetLogicalDescendants()
            .OfType<ProgressBar>()
            .FirstOrDefault(pb => pb.Classes.Contains("pulsating-brain"));

        Assert.NotNull(progressBar);
        Assert.Contains("glyph-indicator", progressBar!.Classes);
        Assert.True(progressBar.IsIndeterminate);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void TabHeaderViewModelTemplate_WithNotificationIndicatorItem_MaterialisesExclamationProgressBar()
    {
        var viewModel = new TabHeaderViewModel { Title = "T" };
        viewModel.Items.Add(new NotificationIndicatorTabHeaderItemViewModel { HasUnread = true });

        var control = InflateAndRenderTabHeader(viewModel);

        var progressBar = control.GetLogicalDescendants()
            .OfType<ProgressBar>()
            .FirstOrDefault(pb => pb.Classes.Contains("exclamation-indicator"));

        Assert.NotNull(progressBar);
        Assert.Contains("glyph-indicator", progressBar!.Classes);
        Assert.True(progressBar.IsIndeterminate);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void TabHeaderViewModelTemplate_WithNeitherIndicator_ShowsNeitherBrainNorExclamation()
    {
        var viewModel = new TabHeaderViewModel { Title = "T" };

        var control = InflateAndRenderTabHeader(viewModel);

        Assert.DoesNotContain(
            control.GetLogicalDescendants().OfType<ProgressBar>(),
            pb => pb.Classes.Contains("pulsating-brain") || pb.Classes.Contains("exclamation-indicator"));
    }

    private static Avalonia.Controls.Control InflateAndRenderTabHeader(TabHeaderViewModel viewModel)
    {
        var template = ResolveTabHeaderTemplate();
        var control = template.Build(viewModel);
        Assert.NotNull(control);
        control!.DataContext = viewModel;

        // Attach to a Window so the StaticResource lookups on
        // ItemsControl.DataTemplates can walk the StylingParent chain up to
        // Application.Current and resolve the keyed indicator DataTemplates
        // in TabHeaderItemTemplates.axaml.
        var window = new Window { Content = control };
        window.Show();
        // Force the layout pass so ItemsControl materialises and the
        // {StaticResource} lookups resolve against Application.Current.
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return control;
    }

    private static void AssertClassOccursInExactlyOneTemplateFile(string cssClassOrText)
    {
        var repoRoot = FindRepositoryRoot();
        var templatesDir = System.IO.Path.Combine(repoRoot.FullName, "Phantom.Workspaces", "Templates");
        // Match ProgressBar-classes markup only, so passing mentions in XML comments
        // do not count as a duplicate template definition.
        var needle = $"Classes=\"glyph-indicator {cssClassOrText}\"";
        var matches = System.IO.Directory
            .EnumerateFiles(templatesDir, "*.axaml", System.IO.SearchOption.AllDirectories)
            .Where(path => System.IO.File.ReadAllText(path).Contains(needle))
            .ToList();

        Assert.Single(matches);
    }

    private static System.IO.DirectoryInfo FindRepositoryRoot()
    {
        var current = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (current is not null)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(current.FullName, "Phantom.Workspaces.slnx")))
            {
                return current;
            }
            current = current.Parent;
        }
        throw new System.IO.DirectoryNotFoundException(
            "Could not locate repository root from test base directory.");
    }

    // ── AgentSessionWorkspaceTabViewModel – SetReady wires tab header indicators

    [Fact]
    public async Task AgentSessionTab_SetReady_SetsTabHeader()
    {
        var tab = new AgentSessionWorkspaceTabViewModel { Id = "t", Title = "Test" };

        await using var agentChat = await CreateMinimalEchoAgentChatAsync();
        var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(agentChat, "test", "", loggerFactory, TaskScheduler.Default);

        tab.SetReady(agentViewModel, loggerFactory);

        Assert.NotNull(tab.TabHeader);
    }

    [Fact]
    public async Task AgentSessionTab_SetReady_TabHeaderContainsAgentRunningIndicator()
    {
        var tab = new AgentSessionWorkspaceTabViewModel { Id = "t", Title = "Test" };

        await using var agentChat = await CreateMinimalEchoAgentChatAsync();
        var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(agentChat, "test", "", loggerFactory, TaskScheduler.Default);

        tab.SetReady(agentViewModel, loggerFactory);

        var indicator = tab.TabHeader!.Items.OfType<AgentRunningIndicatorTabHeaderItemViewModel>().FirstOrDefault();
        Assert.NotNull(indicator);
    }

    [Fact]
    public async Task AgentSessionTab_SetReady_TabHeaderContainsNotificationIndicator()
    {
        var tab = new AgentSessionWorkspaceTabViewModel { Id = "t", Title = "Test" };

        await using var agentChat = await CreateMinimalEchoAgentChatAsync();
        var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(agentChat, "test", "", loggerFactory, TaskScheduler.Default);

        tab.SetReady(agentViewModel, loggerFactory);

        var indicator = tab.TabHeader!.Items.OfType<NotificationIndicatorTabHeaderItemViewModel>().FirstOrDefault();
        Assert.NotNull(indicator);
    }

    [Fact]
    public async Task AgentSessionTab_SetReady_RunningIndicator_InitiallyNotRunning()
    {
        var tab = new AgentSessionWorkspaceTabViewModel { Id = "t", Title = "Test" };

        await using var agentChat = await CreateMinimalEchoAgentChatAsync();
        var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(agentChat, "test", "", loggerFactory, TaskScheduler.Default);

        tab.SetReady(agentViewModel, loggerFactory);

        var indicator = tab.TabHeader!.Items.OfType<AgentRunningIndicatorTabHeaderItemViewModel>().Single();
        Assert.False(indicator.IsRunning);
    }

    [Fact]
    public async Task AgentSessionTab_SetReady_EffectiveTabHeaderContainsRunningAndNotificationIndicators()
    {
        var tab = new AgentSessionWorkspaceTabViewModel { Id = "t", Title = "Test" };
        var doc = new WorkspaceDocument(tab);

        await using var agentChat = await CreateMinimalEchoAgentChatAsync();
        var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(agentChat, "test", "", loggerFactory, TaskScheduler.Default);

        tab.SetReady(agentViewModel, loggerFactory);

        Assert.Contains(doc.EffectiveTabHeader.Items, i => i is AgentRunningIndicatorTabHeaderItemViewModel);
        Assert.Contains(doc.EffectiveTabHeader.Items, i => i is NotificationIndicatorTabHeaderItemViewModel);
    }

    // ── WorkspaceDocument – NotificationIndicatorTabHeaderItemViewModel.HasUnread tracks HasUnreadNotification

    [Fact]
    public async Task WorkspaceDocument_HasUnreadNotification_SetToTrue_SetsHasUnreadOnNotificationIndicator()
    {
        var tab = new AgentSessionWorkspaceTabViewModel { Id = "t", Title = "Test" };
        var doc = new WorkspaceDocument(tab);

        await using var agentChat = await CreateMinimalEchoAgentChatAsync();
        var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(agentChat, "test", "", loggerFactory, TaskScheduler.Default);
        tab.SetReady(agentViewModel, loggerFactory);

        doc.HasUnreadNotification = true;

        var indicator = doc.EffectiveTabHeader.Items.OfType<NotificationIndicatorTabHeaderItemViewModel>().Single();
        Assert.True(indicator.HasUnread);
    }

    [Fact]
    public async Task WorkspaceDocument_HasUnreadNotification_SetToFalse_ClearsHasUnreadOnNotificationIndicator()
    {
        var tab = new AgentSessionWorkspaceTabViewModel { Id = "t", Title = "Test" };
        var doc = new WorkspaceDocument(tab);

        await using var agentChat = await CreateMinimalEchoAgentChatAsync();
        var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(agentChat, "test", "", loggerFactory, TaskScheduler.Default);
        tab.SetReady(agentViewModel, loggerFactory);

        doc.HasUnreadNotification = true;
        doc.HasUnreadNotification = false;

        var indicator = doc.EffectiveTabHeader.Items.OfType<NotificationIndicatorTabHeaderItemViewModel>().Single();
        Assert.False(indicator.HasUnread);
    }

    private static async Task<AgentChat> CreateMinimalEchoAgentChatAsync()
    {
        const string EchoAgentDefinitionJson =
            """
            {
              "kind": "prompt",
              "name": "test-agent",
              "model": {
                "id": "echo",
                "provider": "echo",
                "apiType": "Echo"
              },
              "tools": []
            }
            """;
        var agentDefinition = AgentDefinitionLoader.LoadAgentFromJson(EchoAgentDefinitionJson);
        return await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = agentDefinition,
        });
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task WorkspaceDocument_EffectiveTabHeader_TitleIsNonEmpty_AfterRestore()
    {
        // Regression for #1190: after a full save-close-reopen cycle, every restored
        // WorkspaceDocument's EffectiveTabHeader.Title (bound in DockDataTemplates.axaml:136
        // via <TextBlock Text="{Binding Title}"/>) must be non-empty. Directly targets the
        // symptom the user reported.
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = MainWindowIntegrationTests.GetEntityBroker(viewModel);
        var entityId = new Phantom.Workspaces.Data.EntityId("11901190-2222-4000-8000-000000000001");
        var entity = await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(entityBroker, entityId, """
            {
              "entity-id": "11901190-2222-4000-8000-000000000001",
              "entity-types": ["entity", "note"],
              "names": [["notes", "1190-header-tab"]],
              "display-name": { "default": "" },
              "content": { "mime-type": "text/markdown", "content": { "text": "h" } }
            }
            """);

        var workspaceId = new Phantom.Workspaces.Data.EntityId("11901190-2222-4000-8000-0000000000f1");
        await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(entityBroker, workspaceId, $$"""
            {
              "entity-id": "{{workspaceId}}",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Header Cycle WS" },
              "regions": []
            }
            """);
        await viewModel.OpenWorkspaceAsync(new Phantom.Workspaces.Data.GetEntityRequest { EntityId = workspaceId });
        var pane = viewModel.WorkspacePanes.FirstOrDefault(
            p => string.Equals(p.Id, workspaceId.ToString(), System.StringComparison.Ordinal));
        Assert.NotNull(pane);
        await MainWindowIntegrationTests.WaitForPanePopulatedAsync(pane!);

        var tab = new EntityWorkspaceTabViewModel
        {
            Id = "1190-header-tab",
            Title = "before",
            Entity = entity,
            DockRegion = "full",
        };
        await viewModel.OpenTabAsync(tab);

        var contentDock = MainWindowIntegrationTests.FindDocumentDockIn(pane!.ContentLayout!);
        Assert.NotNull(contentDock);
        await MainWindowIntegrationTests.WaitForWorkspaceTabAsync(contentDock!, "1190-header-tab");

        tab.Title = "After Modify";

        await viewModel.WriteBackWorkspaceTabs(pane);
        await viewModel.CloseWorkspacePaneAsync(pane);
        await viewModel.OpenWorkspaceAsync(new Phantom.Workspaces.Data.GetEntityRequest { EntityId = workspaceId });

        var restoredPane = viewModel.WorkspacePanes.FirstOrDefault(
            p => string.Equals(p.Id, workspaceId.ToString(), System.StringComparison.Ordinal));
        Assert.NotNull(restoredPane);
        await MainWindowIntegrationTests.WaitForPanePopulatedAsync(restoredPane!);

        var restoredDoc = MainWindowViewModel.EnumerateAllDocuments(restoredPane!.ContentLayout!)
            .First(d => d.Id == "1190-header-tab");
        Assert.False(string.IsNullOrEmpty(restoredDoc.EffectiveTabHeader.Title),
            $"EffectiveTabHeader.Title must be non-empty after restore (was '{restoredDoc.EffectiveTabHeader.Title}').");
        Assert.Equal("After Modify", restoredDoc.EffectiveTabHeader.Title);
    }
}
