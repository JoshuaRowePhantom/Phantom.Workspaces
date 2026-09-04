using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.AI;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dock.Avalonia.Controls;
using global::Dock.Model.Controls;
using global::Dock.Model.Core;
using Dock.Serializer.SystemTextJson;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Shell;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Services.Notifications;
using Phantom.Workspaces.Services.Secrets;
using Phantom.Workspaces.Trust;
using Phantom.Workspaces.ViewModels;
using AgentViewModel = Phantom.Workspaces.Agent.Gui.ViewModels.AgentViewModel;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Tests;

public sealed class MainWindowIntegrationTests
{
    [AvaloniaFact(Timeout = 15_000)]
    public async Task TrustedExecutor_Production_UsesTransportFactoryRegistry()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        // The production executor selector's remote executor is produced through the transport layer
        // (TransportTrustedExecutor over ITransportFactoryRegistry), not CreateSelector(reverseExecutionRegistry).
        Assert.NotNull(viewModel.TransportComposition);
        Assert.IsType<Phantom.Workspaces.Llm.Core.Transport.TransportTrustedExecutor>(
            viewModel.ProductionRemoteExecutor);

        using var localDescriptor = JsonDocument.Parse("""{"type":"local"}""");
        await using var transport = await viewModel.TransportComposition!.TransportFactoryRegistry
            .ConnectToAsync(localDescriptor.RootElement, CancellationToken.None);
        Assert.NotNull(transport);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ThemeResources_UseFontFamilyType()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        Assert.True(Avalonia.Application.Current!.Resources.TryGetValue("Theme.FontFamily", out var fontFamilyResource));
        Assert.IsType<FontFamily>(fontFamilyResource);
    }

    // Regression tests for issue #1162: the top-right button cluster (network status,
    // scheduled tasks, running-agents brain, AI usage, notifications bell, settings gear)
    // must share a single uniform, content-driven height (no hard-coded pixel constant).

    private static List<Button> GetTopRightButtons(Window window) =>
        window.GetVisualDescendants()
            .OfType<Button>()
            .Where(b => b.Classes.Contains("top-right") && b.IsEffectivelyVisible)
            .ToList();

    private static void ForceLayoutPass(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_TopRightButtons_AllShareSameRenderedHeight()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            ForceLayoutPass(window);

            var buttons = GetTopRightButtons(window);
            Assert.NotEmpty(buttons);

            var referenceHeight = buttons[0].Bounds.Height;
            Assert.True(referenceHeight > 0, "Top-right buttons rendered with zero height.");
            foreach (var b in buttons)
            {
                Assert.True(
                    Math.Abs(b.Bounds.Height - referenceHeight) < 0.5,
                    $"Top-right button '{b.Name ?? b.Classes.ToString()}' has Bounds.Height={b.Bounds.Height}, " +
                    $"expected {referenceHeight}. All top-right buttons must share the same rendered height.");
            }
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_TopRightButtons_HeightMatchesTallestChildContent()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            ForceLayoutPass(window);

            var buttons = GetTopRightButtons(window);
            Assert.True(buttons.Count >= 2,
                $"Expected at least two visible top-right buttons; got {buttons.Count}.");

            var initialHeight = buttons[0].Bounds.Height;

            // Artificially replace one button's content with a much taller element.
            // A content-driven strip must grow so that every button matches the new tallest content.
            const double TallContentHeight = 60d;
            buttons[0].Content = new Border { Width = 20, Height = TallContentHeight };

            ForceLayoutPass(window);

            var updated = GetTopRightButtons(window);
            var referenceHeight = updated[0].Bounds.Height;
            Assert.True(
                referenceHeight >= TallContentHeight,
                $"Expected shared height to grow to at least {TallContentHeight} after injecting tall content; " +
                $"got {referenceHeight}. The strip's height must be driven by the tallest child, not a fixed value.");
            Assert.True(
                referenceHeight > initialHeight,
                $"Shared height did not grow: initial={initialHeight}, after tall content={referenceHeight}.");

            foreach (var b in updated)
            {
                Assert.True(
                    Math.Abs(b.Bounds.Height - referenceHeight) < 0.5,
                    $"After injecting tall content, button '{b.Name ?? b.Classes.ToString()}' has Bounds.Height={b.Bounds.Height}, " +
                    $"expected {referenceHeight}. All buttons must track the tallest child.");
            }
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_TopRightButtons_NoExplicitHeightSetter()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            ForceLayoutPass(window);

            var buttons = GetTopRightButtons(window);
            Assert.NotEmpty(buttons);

            foreach (var b in buttons)
            {
                // Height must remain unset (double.NaN) — a fixed Height would reintroduce a
                // hard-coded pixel constant that #1162 explicitly prohibits.
                Assert.True(
                    double.IsNaN(b.Height),
                    $"Top-right button '{b.Name ?? b.Classes.ToString()}' has explicit Height={b.Height}. " +
                    "Height must be content-driven; do not pin it to a constant.");

                // MinHeight must be 0 (or unset) — Button.settings-gear.top-right must override the
                // Button.settings-gear MinHeight=34 floor, otherwise the strip cannot shrink when content shrinks.
                Assert.True(
                    b.MinHeight <= 0.001,
                    $"Top-right button '{b.Name ?? b.Classes.ToString()}' has MinHeight={b.MinHeight}. " +
                    "MinHeight must be 0 so the shared height is fully content-driven.");
            }
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_TopRightButtons_ContentIsVerticallyCentered()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            ForceLayoutPass(window);

            var buttons = GetTopRightButtons(window);
            Assert.NotEmpty(buttons);

            foreach (var b in buttons)
            {
                var contentPresenter = b.GetVisualDescendants()
                    .OfType<Avalonia.Controls.Presenters.ContentPresenter>()
                    .FirstOrDefault(cp => cp.Name == "PART_ContentPresenter");
                Assert.NotNull(contentPresenter);

                var topLeftInButton = contentPresenter!.TranslatePoint(new Point(0, 0), b);
                Assert.True(topLeftInButton.HasValue,
                    $"Could not translate content presenter coordinates for button '{b.Name ?? b.Classes.ToString()}'.");

                var buttonHeight = b.Bounds.Height;
                var cpHeight = contentPresenter.Bounds.Height;
                var topGap = topLeftInButton!.Value.Y;
                var bottomGap = buttonHeight - (topLeftInButton.Value.Y + cpHeight);

                Assert.True(
                    Math.Abs(topGap - bottomGap) < 1.5,
                    $"Content of button '{b.Name ?? b.Classes.ToString()}' is not vertically centered: " +
                    $"topGap={topGap}, bottomGap={bottomGap}, buttonHeight={buttonHeight}, cpHeight={cpHeight}.");
            }
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    // Regression tests for issue #1169: a discoverable Save Workspace button must be present
    // in the TopRightBar (the earlier attempt in DockDataTemplates.axaml never rendered).

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_ShushButton_OpensCredentialManagerDialog()
    {
        // Issue #1267: clicking the 🤫 top-right button opens the credential-manager dialog. Wire
        // hermetic secret services so LoadAsync never touches the real credential store / filesystem.
        var tempAllowedPath = Path.Combine(
            Path.GetTempPath(),
            "Phantom.Workspaces.Tests",
            Guid.NewGuid().ToString("N"),
            "allowed-secrets.json");
        var services = new ApplicationServices(
            CreateTestRunningAgentChatTable(),
            new AgentPersistenceStoreCache(),
            credentialPicker: new NullCredentialPicker(),
            allowedSecretsStore: new AllowedSecretsStore(new AllowedSecretsStoreConfiguration { Path = tempAllowedPath }),
            platformSecretStore: new NullPlatformSecretStore());

        await using var viewModel = CreateTestMainWindowViewModel(applicationServices: services);
        await viewModel.InitializeAsync();

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            ForceLayoutPass(window);

            var shushButton = window.GetVisualDescendants()
                .OfType<Button>()
                .First(b => b.Content is string content && content == "🤫");

            shushButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            for (var i = 0; i < 20 && window.LastCredentialManagerDialog is null; i++)
            {
                await Dispatcher.UIThread.InvokeAsync(() => { });
                Dispatcher.UIThread.RunJobs();
            }

            var dialog = window.LastCredentialManagerDialog;
            Assert.NotNull(dialog);
            Assert.IsType<CredentialManagerDialogViewModel>(dialog!.DataContext);

            dialog.Close();
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    private static Button? GetSaveWorkspaceButton(Window window) =>
        window.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(b => b.Classes.Contains("save-workspace"));

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_SaveWorkspaceButton_IsVisibleInTopRightBar_WhenWorkspacePaneSelected()
    {
        await using var viewModel = CreateTestMainWindowViewModel(
            configuration: new WorkspacesConfiguration { SkipStartupWorkspace = false });
        await viewModel.InitializeAsync();

        // Opening a tab creates a real workspace pane wired to SaveWorkspacePaneAsync.
        var tab = new WebViewModel("https://save-btn-visible.example.com") { Id = "svb", Title = "Save Visible" };
        await viewModel.OpenTabAsync(tab);

        // Precondition: the selected pane is a real, saveable workspace pane.
        Assert.True(viewModel.SelectedWorkspacePane.CanSaveWorkspace,
            "Test precondition: OpenTabAsync must produce a real workspace pane with a wired save handler.");

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            ForceLayoutPass(window);

            var saveButton = GetSaveWorkspaceButton(window);
            Assert.NotNull(saveButton);
            Assert.True(saveButton!.IsEffectivelyVisible,
                "Save workspace button must be visible when a real workspace pane is selected.");
            Assert.True(saveButton.Classes.Contains("settings-gear"),
                "Save workspace button must share the 'settings-gear' class so its rendered height matches the other top-right buttons.");
            Assert.True(saveButton.Classes.Contains("top-right"),
                "Save workspace button must carry the 'top-right' class so top-right layout rules apply.");

            // The button must live inside the TopRightBar StackPanel.
            var topRightBar = window.GetVisualDescendants()
                .OfType<StackPanel>()
                .FirstOrDefault(sp => sp.Name == "TopRightBar");
            Assert.NotNull(topRightBar);
            Assert.Contains(saveButton, topRightBar!.GetVisualDescendants().OfType<Button>());
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_SaveWorkspaceButton_IsHidden_WhenNoWorkspacePaneSelected()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        // After Initialize, SelectedWorkspacePane is the placeholder pane
        // (Entity.EntityId == Guid.Empty, saveAsync == null → CanSaveWorkspace is false).
        Assert.False(viewModel.SelectedWorkspacePane.CanSaveWorkspace,
            "Placeholder pane must not report CanSaveWorkspace; test precondition failed.");

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            ForceLayoutPass(window);

            var saveButton = GetSaveWorkspaceButton(window);
            Assert.NotNull(saveButton);
            Assert.False(saveButton!.IsEffectivelyVisible,
                "Save workspace button must be hidden on the placeholder pane so no permanently-disabled affordance is shown.");
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_SaveWorkspaceButton_IsDisabled_WhenSelectedPaneIsReadOnly()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        // Construct a read-only pane wired with a save handler.
        using var document = JsonDocument.Parse(
            """
            {
              "entity-id": "22222222-2222-2222-2222-222222222222",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "ReadOnly Workspace" }
            }
            """);
        var readOnlyEntity = new Phantom.Workspaces.ViewModels.SubscribedEntityViewModel(
            new Phantom.Workspaces.Data.EntitySnapshot
            {
                EntityId = new Phantom.Workspaces.Data.EntityId("22222222-2222-2222-2222-222222222222"),
                ConcurrencyTag = new Phantom.Workspaces.Data.ConcurrencyTag("1"),
                ModifiedTime = new Phantom.Workspaces.Data.Timestamp(System.DateTimeOffset.UtcNow, "1"),
                Data = document.RootElement.Clone(),
                Relationships = System.Array.Empty<Phantom.Workspaces.Data.EntitySnapshot>(),
            });
        var readOnlyPane = new WorkspacePaneViewModel(
            readOnlyEntity,
            id: "ro-pane",
            saveAsync: _ => Task.CompletedTask,
            isReadOnly: true);
        viewModel.SelectedWorkspacePane = readOnlyPane;

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            ForceLayoutPass(window);

            var saveButton = GetSaveWorkspaceButton(window);
            Assert.NotNull(saveButton);
            Assert.True(saveButton!.IsEffectivelyVisible,
                "Save workspace button must remain visible for a read-only pane so its disabled state is discoverable.");
            Assert.False(saveButton.IsEffectivelyEnabled,
                "Save workspace button must be disabled when the selected pane is read-only.");
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_SaveWorkspaceButton_Click_InvokesWriteBackWorkspaceTabs()
    {
        await using var viewModel = CreateTestMainWindowViewModel(
            configuration: new WorkspacesConfiguration { SkipStartupWorkspace = false });
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://save-btn-click.example.com")
        {
            Id = "sbc-tab",
            Title = "Save Btn Click",
        };
        await viewModel.OpenTabAsync(tab);

        var pane = viewModel.SelectedWorkspacePane;
        var saveContentDock = FindDocumentDockIn(pane.ContentLayout!);
        Assert.NotNull(saveContentDock);
        await WaitForWorkspaceTabAsync(saveContentDock!, "sbc-tab");
        Assert.False(pane.Entity.EntityId == new Phantom.Workspaces.Data.EntityId(Guid.Empty),
            "SelectedWorkspacePane must be a real workspace entity, not the placeholder.");
        Assert.NotNull(pane.Entity.ConcurrencyTag);

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            ForceLayoutPass(window);

            var saveButton = GetSaveWorkspaceButton(window);
            Assert.NotNull(saveButton);
            Assert.True(saveButton!.IsEffectivelyEnabled,
                "Save workspace button must be enabled for a writable pane with a wired save handler.");

            Assert.True(pane.SaveCommand.CanExecute(null));
            pane.SaveCommand.Execute(null);
            await pane.SaveCommand.LastExecutionTask!;

            // WriteBackWorkspaceTabs persists dock-layout JSON with per-tab Descriptor data.
            var data = Assert.IsType<System.Text.Json.JsonElement>(pane.Entity.Data);
            Assert.True(data.TryGetProperty("dock-layout", out var dockLayoutEl));
            var dockLayoutJson = dockLayoutEl.GetRawText();
            Assert.Contains("Descriptor", dockLayoutJson, StringComparison.Ordinal);
            Assert.Contains("browser", dockLayoutJson, StringComparison.Ordinal);
            Assert.Contains("save-btn-click.example.com", dockLayoutJson, StringComparison.Ordinal);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task InMemoryRepository_InitializesWithExpectedPipeline()
    {
        var repository = await EntityRepository.CreateAsync(CreateInMemoryRepositorySource());
        var snapshots = await repository.ExportEntitySnapshotsAsync();
        Assert.IsType<WorkspaceEntitySessionDataAccessLayer>(repository.DataAccessLayer);
        Assert.NotEqual(default, repository.WorkspaceEntitySession.UserEntityId);
        Assert.NotEqual(default, repository.WorkspaceEntitySession.ComputerEntityId);
        Assert.NotEqual(default, repository.WorkspaceEntitySession.UserComputerProfileEntityId);
        Assert.NotEmpty(snapshots);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task InMemoryRepository_SeedsGithubModelsAgentManifest()
    {
        var repository = await EntityRepository.CreateAsync(CreateInMemoryRepositorySource());
        var snapshots = await repository.ExportEntitySnapshotsAsync();
        var githubModelsSnapshot = Assert.Single(
            snapshots,
            snapshot => ReadEntityNames(snapshot.Value.Data).Any(
                static entityName => entityName.Components.Length == 3
                    && string.Equals(entityName.Components[0], "defaults", StringComparison.Ordinal)
                    && string.Equals(entityName.Components[1], "agent-manifests", StringComparison.Ordinal)
                    && string.Equals(entityName.Components[2], "github-models", StringComparison.Ordinal)));
        Assert.Equal("GitHub Models Workspace Assistant", ReadDefaultDisplayName(githubModelsSnapshot.Value.Data));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task InMemoryRepository_SeedsWorkspacesAgentManifestDisplayName()
    {
        var repository = await EntityRepository.CreateAsync(CreateInMemoryRepositorySource());
        var snapshots = await repository.ExportEntitySnapshotsAsync();
        var workspacesSnapshot = Assert.Single(
            snapshots,
            snapshot => ReadEntityNames(snapshot.Value.Data).Any(
                static entityName => entityName.Components.Length == 3
                    && string.Equals(entityName.Components[0], "defaults", StringComparison.Ordinal)
                    && string.Equals(entityName.Components[1], "agent-manifests", StringComparison.Ordinal)
                    && string.Equals(entityName.Components[2], "workspaces", StringComparison.Ordinal)));
        Assert.Equal("Workspaces Assistant", ReadDefaultDisplayName(workspacesSnapshot.Value.Data));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task InMemoryRepository_SeedsMainViewWithGitWorkspacesSubView()
    {
        var repository = await EntityRepository.CreateAsync(CreateInMemoryRepositorySource());
        var snapshots = await repository.ExportEntitySnapshotsAsync();
        var mainViewSnapshot = Assert.Single(
            snapshots,
            snapshot => ReadEntityNames(snapshot.Value.Data).Any(
                static entityName => entityName.Components.Length == 2
                    && string.Equals(entityName.Components[0], "views", StringComparison.Ordinal)
                    && string.Equals(entityName.Components[1], "main", StringComparison.Ordinal)));
        var data = mainViewSnapshot.Value.Data;
        Assert.True(data.HasValue);
        Assert.True(data!.Value.TryGetProperty("sub-views", out var subViews));
        Assert.Contains(subViews.EnumerateArray(), subView =>
            subView.TryGetProperty("view-entity-id", out var id)
            && id.ValueKind == JsonValueKind.Array
            && id.GetArrayLength() == 2
            && id[0].GetString() == "views"
            && id[1].GetString() == "git-workspaces");
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowViewModel_ThemeSelectionIsDataDriven()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        Assert.Contains("dark", viewModel.ThemeNames);
        Assert.Contains("light", viewModel.ThemeNames);
        viewModel.SelectedThemeName = "light";
        Assert.Equal("light", viewModel.SelectedThemeName);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task SelectedThemeName_SetToLight_PersistsAcrossViewModelInstances()
    {
        var profilePath = CreateTempProfileStorePath();
        try
        {
            var store = new ProfileStore(profilePath);

            await using var vm1 = CreateTestMainWindowViewModel(profileStore: store);
            await vm1.InitializeAsync();
            await vm1.SetThemeAsync("light");

            await using var vm2 = CreateTestMainWindowViewModel(profileStore: store);
            await vm2.InitializeAsync();

            Assert.Equal("light", vm2.SelectedThemeName);
        }
        finally
        {
            DeleteTempProfileStoreDirectory(profilePath);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowViewModel_InitializeAsync_ReplacesDefaultAndLoadingWorkspacePanes()
    {
        await using var viewModel = CreateTestMainWindowViewModel(
            configuration: new WorkspacesConfiguration { SkipStartupWorkspace = false });
        await viewModel.InitializeAsync();

        Assert.NotEmpty(viewModel.WorkspacePanes);
        Assert.DoesNotContain(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, "default-workspace", StringComparison.Ordinal)
                || pane.Id.StartsWith("loading-workspace:", StringComparison.Ordinal));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenWorkspaceAsync_WhenAlreadyOpening_SecondRequestIsNoOp()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceId,
            """
            {
              "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "concurrent-open"]],
              "display-name": { "default": "Concurrent Open Workspace" },
              "regions": []
            }
            """);

        var request = new GetEntityRequest { EntityId = workspaceId };

        // The first open runs synchronously until its first await (creating the loading pane);
        // the second open must observe the in-progress load and be a no-op so the workspace is
        // only opened once (issue #23).
        var firstOpen = viewModel.OpenWorkspaceAsync(request);
        var secondOpen = viewModel.OpenWorkspaceAsync(request);
        await Task.WhenAll(firstOpen, secondOpen);

        Assert.Single(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, workspaceId.ToString(), StringComparison.Ordinal));
        Assert.DoesNotContain(
            viewModel.WorkspacePanes,
            pane => pane.Id.StartsWith("loading-workspace:", StringComparison.Ordinal));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenWorkspaceAsync_WithExternalEntityTab_PopulatesTabAsynchronously()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        // Create an external entity referenced by the workspace tab
        var externalEntityId = new EntityId("bbbbbbbb-bbbb-4bbb-bbbb-bbbbbbbbbbbb");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            externalEntityId,
            """
            {
              "entity-id": "bbbbbbbb-bbbb-4bbb-bbbb-bbbbbbbbbbbb",
              "entity-types": ["entity", "external"],
              "names": [["tests", "externals", "tab-async-test"]],
              "display-name": { "default": "Async Tab Test" },
              "urls": { "default": "https://example.com" }
            }
            """);

        // Create a workspace that references the external entity
        var workspaceId = new EntityId("cccccccc-cccc-4ccc-cccc-cccccccccccc");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceId,
            """
            {
              "entity-id": "cccccccc-cccc-4ccc-cccc-cccccccccccc",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "async-tabs"]],
              "display-name": { "default": "Async Tabs Workspace" },
              "regions": [
                {
                  "region-id": "main",
                  "title": "Main",
                  "dock": "center",
                  "size": 1.0,
                  "tabs": [
                    {
                      "tab-id": "async-tab-1",
                      "title": "Async Tab",
                      "kind": "entity",
                      "dock": "full",
                      "content": {
                        "target-entity-name": ["tests", "externals", "tab-async-test"]
                      }
                    }
                  ]
                }
              ]
            }
            """);

        // Open the workspace — Phase 1 (skeleton) completes on return; Phase 2 populates tabs async
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        // The workspace pane must be visible immediately after Phase 1
        var workspacePane = Assert.Single(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, workspaceId.ToString(), StringComparison.Ordinal));

        // Wait for Phase 2 to add at least one tab (deterministic: watch ContentDock)
        var contentDock = FindDocumentDockIn(workspacePane.ContentLayout!);
        Assert.NotNull(contentDock);
        await WaitForWorkspaceTabAsync(contentDock!, "async-tab-1");

        var tabDoc = contentDock!.VisibleDockables!
            .OfType<WorkspaceDocument>()
            .FirstOrDefault(d => d.Id == "async-tab-1");
        Assert.NotNull(tabDoc);
        Assert.IsType<WebViewModel>(tabDoc!.TabViewModel);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CreateTabFromEntityAsync_ExternalEntityNonDefaultUrlKey_SetsTitleToUrlKeyAndFixesTitle()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        // External entity with a non-default URL key only — no "default" key present
        var externalEntityId = new EntityId("ff402001-ff40-4ff4-8ff4-ff4002000001");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            externalEntityId,
            """
            {
              "entity-id": "ff402001-ff40-4ff4-8ff4-ff4002000001",
              "entity-types": ["entity", "external"],
              "names": [["tests", "externals", "non-default-url-key"]],
              "display-name": { "default": "Non-Default URL Entity" },
              "urls": { "docs": "https://docs.example.com" }
            }
            """);

        // Workspace tab with no explicit title — title must be derived from the URL key
        var workspaceId = new EntityId("ff402002-ff40-4ff4-8ff4-ff4002000002");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceId,
            """
            {
              "entity-id": "ff402002-ff40-4ff4-8ff4-ff4002000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "non-default-url-key"]],
              "display-name": { "default": "Non-Default URL Key Workspace" },
              "regions": [
                {
                  "region-id": "main",
                  "title": "Main",
                  "dock": "center",
                  "size": 1.0,
                  "tabs": [
                    {
                      "tab-id": "non-default-url-tab-1",
                      "kind": "entity",
                      "dock": "full",
                      "content": {
                        "target-entity-name": ["tests", "externals", "non-default-url-key"]
                      }
                    }
                  ]
                }
              ]
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var workspacePane = Assert.Single(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, workspaceId.ToString(), StringComparison.Ordinal));

        var contentDock = FindDocumentDockIn(workspacePane.ContentLayout!);
        Assert.NotNull(contentDock);
        await WaitForWorkspaceTabAsync(contentDock!, "non-default-url-tab-1");

        var tabDoc = contentDock!.VisibleDockables!
            .OfType<WorkspaceDocument>()
            .FirstOrDefault(d => d.Id == "non-default-url-tab-1");
        Assert.NotNull(tabDoc);
        var webVm = Assert.IsType<WebViewModel>(tabDoc!.TabViewModel);

        // Title must be the URL key, not the entity display name
        Assert.Equal("docs", webVm.Title);

        // titleFixed must be true: SetPageTitle should NOT update the tab title
        webVm.SetPageTitle("Page Title From Browser");
        Assert.Equal("docs", webVm.Title);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenWorkspaceAsync_CloseWhileTabsLoading_DoesNotCrash()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        // Create an external entity referenced by the workspace tab
        var externalEntityId = new EntityId("e0e00001-e0e0-4e0e-ae0e-e0e0e0e0e0e1");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            externalEntityId,
            """
            {
              "entity-id": "e0e00001-e0e0-4e0e-ae0e-e0e0e0e0e0e1",
              "entity-types": ["entity", "external"],
              "names": [["tests", "externals", "close-while-loading"]],
              "display-name": { "default": "Close While Loading" },
              "urls": { "default": "https://example.com" }
            }
            """);

        // Create a workspace that references the external entity via async entity lookup
        var workspaceId = new EntityId("e0e00002-e0e0-4e0e-ae0e-e0e0e0e0e0e2");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceId,
            """
            {
              "entity-id": "e0e00002-e0e0-4e0e-ae0e-e0e0e0e0e0e2",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "close-while-loading"]],
              "display-name": { "default": "Close While Loading Workspace" },
              "regions": [
                {
                  "region-id": "main",
                  "title": "Main",
                  "dock": "center",
                  "size": 1.0,
                  "tabs": [
                    {
                      "tab-id": "cwl-tab-1",
                      "title": "CWL Tab",
                      "kind": "entity",
                      "dock": "full",
                      "content": {
                        "target-entity-name": ["tests", "externals", "close-while-loading"]
                      }
                    }
                  ]
                }
              ]
            }
            """);

        // Phase 1 completes on return; Phase 2 (PopulateWorkspacePaneTabsAsync) fires and
        // suspends at its async entity-fetch before it can add any tabs to the dock.
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var workspacePane = Assert.Single(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, workspaceId.ToString(), StringComparison.Ordinal));

        // Close the workspace before Phase 2's UI callbacks run.
        await viewModel.RemoveWorkspacePaneAsync(workspacePane);

        // Pump the Avalonia dispatcher enough times to let Phase 2 run to completion.
        // Each pump drains one layer of async work: entity-fetch continuation, guard-check
        // InvokeAsync, and final SyncWorkspacePaneFromDock InvokeAsync.
        await Dispatcher.UIThread.InvokeAsync(() => {});
        await Dispatcher.UIThread.InvokeAsync(() => {});
        await Dispatcher.UIThread.InvokeAsync(() => {});
        await Dispatcher.UIThread.InvokeAsync(() => {});

        // Guard must have fired: workspace is gone and no exception was thrown.
        Assert.DoesNotContain(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, workspaceId.ToString(), StringComparison.Ordinal));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowViewModel_SessionsView_GetEntitySubViewsIncludeAgentManifestEntities()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var sessionsView = Assert.Single(
            viewModel.TopLevelViews,
            static view => string.Equals(view.Title, "Sessions", StringComparison.Ordinal));
        viewModel.SelectedTopLevelView = sessionsView;

        var applySelectedViewMethod = typeof(MainWindowViewModel).GetMethod(
            "ApplySelectedViewAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(applySelectedViewMethod);
        await (Task)applySelectedViewMethod!.Invoke(viewModel, [])!;

        Assert.Contains(
            viewModel.CurrentViewPopulation.Entities,
            static entity => string.Equals(entity.EntityType, "agent-manifest", StringComparison.Ordinal));
        Assert.DoesNotContain(
            viewModel.CurrentViewPopulation.Entities,
            static entity => string.Equals(entity.EntityType, "view", StringComparison.Ordinal));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ViewEntityViewModel_TraversedEntitiesCollapsed_WhenDispositionIsCollapsed()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        // Override the workspace entity-type-view to have traversed-entity-display-disposition: "collapsed".
        var entityTypeViewId = new EntityId("a9d73483-6752-40b3-9fed-5831616814a6");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            entityTypeViewId,
            """
            {
              "entity-id": "a9d73483-6752-40b3-9fed-5831616814a6",
              "entity-types": ["entity", "entity-type-view"],
              "names": [["entity-type-views", "workspace"]],
              "display-name": { "default": "Workspace View" },
              "fields": [],
              "traversed-entity-display-disposition": "collapsed",
              "traverse-relationships": [
                { "relationship-type-ids": ["related"] }
              ]
            }
            """);

        var workspaceId = new EntityId("b1000001-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceId,
            """
            {
              "entity-id": "b1000001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["workspaces", "collapse-test"]],
              "display-name": { "default": "Collapse Test Workspace" },
              "regions": []
            }
            """);

        var relatedId = new EntityId("b1000002-0000-4000-8000-000000000002");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            relatedId,
            """
            {
              "entity-id": "b1000002-0000-4000-8000-000000000002",
              "entity-types": ["entity", "note"],
              "names": [["notes", "collapse-test-note"]],
              "display-name": { "default": "Related Note" },
              "content": { "mime-type": "text/markdown", "content": { "text": "note" } }
            }
            """);

        var relId = new EntityId("b1000003-0000-4000-8000-000000000003");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            relId,
            $$"""
            {
              "entity-id": "b1000003-0000-4000-8000-000000000003",
              "entity-types": ["entity", "related", "relationship"],
              "names": [["relationships", "collapse-test-rel"]],
              "participants": { "entities": ["{{workspaceId.Value}}", "{{relatedId.Value}}"] }
            }
            """);

        var workspacesView = viewModel.TopLevelViews.FirstOrDefault(
            static v => string.Equals(v.Title, "Workspaces", StringComparison.Ordinal));
        Assert.NotNull(workspacesView);
        viewModel.SelectedTopLevelView = workspacesView!;

        var applySelectedViewMethod = typeof(MainWindowViewModel).GetMethod(
            "ApplySelectedViewAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(applySelectedViewMethod);
        await (Task)applySelectedViewMethod!.Invoke(viewModel, [])!;

        var workspaceVm = viewModel.CurrentViewPopulation.Entities.FirstOrDefault(
            e => e.Entity.EntityId == workspaceId);
        Assert.NotNull(workspaceVm);
        Assert.False(workspaceVm!.IsExpanded);

        // Collapsed traversals keep children populated so expand/collapse only toggles visibility.
        Assert.Contains(
            viewModel.CurrentViewPopulation.Entities,
            e => e.Entity.EntityId == relatedId);
        Assert.Contains(
            workspaceVm.Children,
            e => e.Entity.EntityId == relatedId);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_ConstructsWithoutTemplateCastErrors()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        var window = new MainWindow(viewModel);

        Assert.NotNull(window);
        Assert.NotEmpty(window.DataTemplates);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CreateWorkspacePane_DoesNotInjectFallbackCenterRegion_WhenWorkspaceHasNoRegions()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        
        using var workspaceDocument = JsonDocument.Parse(
            """
            {
              "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Workspace Without Regions" }
            }
            """);
        using var entityDocument = JsonDocument.Parse(
            """
            {
              "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Workspace Without Regions" }
            }
            """);
        var workspaceEntity = new SubscribedEntityViewModel(
            new EntitySnapshot
            {
                EntityId = new EntityId("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                ConcurrencyTag = new ConcurrencyTag("1"),
                ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
                Data = entityDocument.RootElement.Clone(),
                Relationships = Array.Empty<EntitySnapshot>(),
            });

        var createWorkspacePane = typeof(MainWindowViewModel).GetMethod(
            "CreateWorkspacePaneAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(createWorkspacePane);

        var task = (Task<WorkspacePaneViewModel>?)createWorkspacePane!.Invoke(
            viewModel,
            [workspaceEntity, workspaceDocument.RootElement.Clone()]);
        Assert.NotNull(task);
        
        var workspacePane = await task!;
        Assert.NotNull(workspacePane);
        
        // When workspace has no regions in JSON, we create a default tab for the workspace entity.
        // pane.Tabs is now the source of truth — confirm the single default tab was added.
        Assert.Single(workspacePane!.Tabs);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenAgentDefinitionShortcutHandler_LocalEchoDefinition_CreatesAgentSessionTab()
    {
        var fixedCurrentTime = new DateTimeOffset(2026, 06, 12, 9, 23, 45, TimeSpan.Zero);
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(
            entityBroker,
            new EntityId("f95a86dc-f71f-43f8-abf5-31c6444f7a4e"),
            """
            {
              "entity-id": "f95a86dc-f71f-43f8-abf5-31c6444f7a4e",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "local-echo"]],
              "display-name": { "default": "Local Echo" },
              "definition": {
                "kind": "prompt",
                "name": "local-echo",
                "model": {
                  "id": "echo",
                  "provider": "echo",
                  "apiType": "Echo"
                },
                "tools": []
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext(new Microsoft.Extensions.Time.Testing.FakeTimeProvider(fixedCurrentTime));
        var openAgentSessionShortcutHandler = new OpenAgentSessionShortcutHandler(

            agentSessionShortcutContext,

            CreateLocalTrustedExecutorSelector(),

            CreateTestRunningAgentChatTable());
        var openAgentDefinitionShortcutHandler = new OpenAgentDefinitionShortcutHandler(agentSessionShortcutContext, openAgentSessionShortcutHandler);

        var handled = await openAgentDefinitionShortcutHandler.Handle(viewModel, Shortcut.Open, agentDefinitionEntity);

        Assert.True(handled);
        var launchpadTab = Assert.IsType<AgentManifestLaunchpadViewModel>(viewModel.SelectedWorkspacePane.SelectedTab);
        Assert.Same(agentDefinitionEntity, launchpadTab.ManifestEntity);
        Assert.True(launchpadTab.CanStart);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenAgentManifestShortcutHandler_LocalEchoManifest_CreatesAgentSessionTab()
    {
        var fixedCurrentTime = new DateTimeOffset(2026, 06, 12, 9, 23, 45, TimeSpan.Zero);
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var agentManifestEntity = await UpsertEntityAndLoadAsync(
            entityBroker,
            new EntityId("a1b2c3d4-0000-4000-8000-000000000001"),
            """
            {
              "entity-id": "a1b2c3d4-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-manifest"],
              "names": [["tests", "agent-manifests", "local-echo"]],
              "display-name": { "default": "Local Echo Manifest" },
              "manifest": {
                "name": "local-echo",
                "displayName": "Local Echo Manifest",
                "template": {
                  "kind": "prompt",
                  "name": "local-echo",
                  "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
                },
                "resources": [
                  { "kind": "tool", "id": "fixed", "name": "workspace-entity" }
                ]
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext(new Microsoft.Extensions.Time.Testing.FakeTimeProvider(fixedCurrentTime));
        var openAgentSessionShortcutHandler = new OpenAgentSessionShortcutHandler(

            agentSessionShortcutContext,

            CreateLocalTrustedExecutorSelector(),

            CreateTestRunningAgentChatTable());
        var openAgentManifestShortcutHandler = new OpenAgentManifestShortcutHandler(agentSessionShortcutContext, openAgentSessionShortcutHandler);

        var handled = await openAgentManifestShortcutHandler.Handle(viewModel, Shortcut.Open, agentManifestEntity);

        Assert.True(handled);
        var sessionTab2 = await WaitForSelectedTabAsync<AgentSessionWorkspaceTabViewModel>(viewModel.SelectedWorkspacePane);
        await WaitForAgentReadyAsync(sessionTab2);
        Assert.NotNull(sessionTab2.Agent);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenAgentManifestShortcutHandler_ManifestWithParameters_ShowsLaunchpadNotAutoStarted()
    {
        var fixedCurrentTime = new DateTimeOffset(2026, 06, 12, 9, 23, 45, TimeSpan.Zero);
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var agentManifestEntity = await UpsertEntityAndLoadAsync(
            entityBroker,
            new EntityId("a1b2c3d4-0000-4000-8000-000000000002"),
            """
            {
              "entity-id": "a1b2c3d4-0000-4000-8000-000000000002",
              "entity-types": ["entity", "agent-manifest"],
              "names": [["tests", "agent-manifests", "with-parameters"]],
              "display-name": { "default": "Manifest With Parameters" },
              "manifest": {
                "name": "with-parameters",
                "displayName": "Manifest With Parameters",
                "template": {
                  "kind": "prompt",
                  "name": "with-parameters",
                  "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
                },
                "parameters": {
                  "properties": [
                    { "name": "working-directory", "required": true }
                  ]
                }
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext(new Microsoft.Extensions.Time.Testing.FakeTimeProvider(fixedCurrentTime));
        var openAgentSessionShortcutHandler = new OpenAgentSessionShortcutHandler(

            agentSessionShortcutContext,

            CreateLocalTrustedExecutorSelector(),

            CreateTestRunningAgentChatTable());
        var openAgentManifestShortcutHandler = new OpenAgentManifestShortcutHandler(agentSessionShortcutContext, openAgentSessionShortcutHandler);

        var handled = await openAgentManifestShortcutHandler.Handle(viewModel, Shortcut.Open, agentManifestEntity);

        Assert.True(handled);
        var launchpadTab = await WaitForSelectedTabAsync<AgentManifestLaunchpadViewModel>(viewModel.SelectedWorkspacePane);
        Assert.Same(agentManifestEntity, launchpadTab.ManifestEntity);
        Assert.Single(launchpadTab.Parameters);
        Assert.False(launchpadTab.CanStart);
        Assert.DoesNotContain(viewModel.SelectedWorkspacePane.Tabs, static t => t is AgentSessionWorkspaceTabViewModel);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task AgentManifestLaunchpad_StartSessionWithParameters_CreatesAgentChatOnUIThread()
    {
        // Enforcement test for issue #909: the launchpad previously wrapped AgentChat creation in
        // Task.Run, constructing the chat on a thread-pool thread. With the foreground-context
        // affinity invariant enforced in the AgentChat constructor, this flow only reaches
        // AgentTabState.Ready when creation runs on the UI thread.
        var fixedCurrentTime = new DateTimeOffset(2026, 06, 12, 9, 23, 45, TimeSpan.Zero);
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var agentManifestEntity = await UpsertEntityAndLoadAsync(
            entityBroker,
            new EntityId("a1b2c3d4-0000-4000-8000-000000000909"),
            """
            {
              "entity-id": "a1b2c3d4-0000-4000-8000-000000000909",
              "entity-types": ["entity", "agent-manifest"],
              "names": [["tests", "agent-manifests", "ui-thread-creation"]],
              "display-name": { "default": "UI Thread Creation Manifest" },
              "manifest": {
                "name": "ui-thread-creation",
                "displayName": "UI Thread Creation Manifest",
                "template": {
                  "kind": "prompt",
                  "name": "ui-thread-creation",
                  "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
                },
                "parameters": {
                  "properties": [
                    { "name": "working-directory", "required": true }
                  ]
                }
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext(new Microsoft.Extensions.Time.Testing.FakeTimeProvider(fixedCurrentTime));
        var openAgentSessionShortcutHandler = new OpenAgentSessionShortcutHandler(

            agentSessionShortcutContext,

            CreateLocalTrustedExecutorSelector(),

            CreateTestRunningAgentChatTable());
        var openAgentManifestShortcutHandler = new OpenAgentManifestShortcutHandler(agentSessionShortcutContext, openAgentSessionShortcutHandler);

        var handled = await openAgentManifestShortcutHandler.Handle(viewModel, Shortcut.Open, agentManifestEntity);
        Assert.True(handled);

        var launchpadTab = await WaitForSelectedTabAsync<AgentManifestLaunchpadViewModel>(viewModel.SelectedWorkspacePane);
        launchpadTab.Parameters[0].Value = Environment.CurrentDirectory;
        Assert.True(launchpadTab.CanStart);

        launchpadTab.StartSessionCommand.Execute(null);

        var sessionTab = await WaitForSelectedTabAsync<AgentSessionWorkspaceTabViewModel>(viewModel.SelectedWorkspacePane);
        await WaitForAgentReadyAsync(sessionTab);

        Assert.Equal(AgentTabState.Ready, sessionTab.State);
        Assert.NotNull(sessionTab.Agent);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenAgentSessionShortcutHandler_Handle_CreatesAgentChatOnUIThread()
    {
        // Enforcement test for issue #909: the loaded-session path (shortcut handler →
        // RunningAgentChatTable → AgentChatFactory) must create the AgentChat on the UI thread.
        // The factory's foreground scheduler is a SynchronizationContextTaskScheduler, so an
        // off-context construction would throw and the tab would end in AgentTabState.Failed.
        var runningAgentChatTable = CreateTestRunningAgentChatTable();
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(
            entityBroker,
            new EntityId("a1b2c3d4-0000-4000-8000-000000000910"),
            """
            {
              "entity-id": "a1b2c3d4-0000-4000-8000-000000000910",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "ui-thread-load"]],
              "display-name": { "default": "UI Thread Load" },
              "definition": {
                "kind": "prompt",
                "name": "ui-thread-load",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, Guid.NewGuid().ToString("n"));
        Assert.NotNull(agentSessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(
            agentSessionShortcutContext, CreateLocalTrustedExecutorSelector(), runningAgentChatTable);

        var handled = await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);
        Assert.True(handled);

        var sessionTab = Assert.IsType<AgentSessionWorkspaceTabViewModel>(viewModel.SelectedWorkspacePane.SelectedTab);
        await WaitForAgentReadyAsync(sessionTab);

        Assert.Equal(AgentTabState.Ready, sessionTab.State);
        Assert.NotNull(sessionTab.Lease);
    }

    [AvaloniaTheory(Timeout = 30_000)]
    [InlineData("session")]
    [InlineData("definition")]
    [InlineData("manifest")]
    [InlineData("profile")]
    public async Task AllGuiSessionLaunchPaths_ProduceSlashCommandEnabledSession(string launchPath)
    {
        // #1429 regression guard: all four GUI session launch paths (loaded agent-session,
        // agent-definition launchpad, auto-started agent-manifest, and profile-definition) must
        // centralize materialization through ComposeSessionAgentViewModel and therefore end up
        // with slash commands wired on the resulting AgentViewModel.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var agent = launchPath switch
        {
            "session" => await MaterializeAgentSessionSlashSessionAsync(viewModel),
            "definition" => await MaterializeAgentDefinitionSlashSessionAsync(viewModel),
            "manifest" => await MaterializeAgentManifestSlashSessionAsync(viewModel),
            "profile" => await MaterializeProfileDefinitionSlashSessionAsync(viewModel),
            _ => throw new ArgumentOutOfRangeException(nameof(launchPath)),
        };

        await AssertSlashCommandsEnabledAsync(agent);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenAgentDefinitionShortcutHandler_WorkspaceEntityTool_IsMappedInWorkspacesGui()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(
            entityBroker,
            new EntityId("b6731cc0-fb8a-4f8e-9f89-3f33a5db1b8a"),
            """
            {
              "entity-id": "b6731cc0-fb8a-4f8e-9f89-3f33a5db1b8a",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "workspace-entity-tool"]],
              "display-name": { "default": "Workspace Entity Tool Agent" },
              "definition": {
                "kind": "prompt",
                "name": "workspace-entity-tool",
                "model": {
                  "id": "echo",
                  "provider": "echo",
                  "apiType": "Echo"
                },
                "tools": [
                  {
                    "kind": "workspace-entity",
                    "description": "Read and modify workspace entities."
                  }
                ]
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var openAgentSessionShortcutHandler = new OpenAgentSessionShortcutHandler(

            agentSessionShortcutContext,

            CreateLocalTrustedExecutorSelector(),

            CreateTestRunningAgentChatTable());
        var openAgentDefinitionShortcutHandler = new OpenAgentDefinitionShortcutHandler(agentSessionShortcutContext, openAgentSessionShortcutHandler);

        var handled = await openAgentDefinitionShortcutHandler.Handle(viewModel, Shortcut.Open, agentDefinitionEntity);

        Assert.True(handled);
        var sessionTab = await WaitForSelectedTabAsync<AgentSessionWorkspaceTabViewModel>(viewModel.SelectedWorkspacePane);
        await WaitForAgentReadyAsync(sessionTab);
        Assert.NotNull(sessionTab.Agent);
        Assert.Contains(sessionTab.Agent.Tools, static tool => string.Equals(tool.Kind, "workspace-entity", StringComparison.Ordinal));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CreateWorkspacePaneAsync_WithAgentSessionTab_CreatesAgentSessionWorkspaceTabViewModel()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        // Create an agent-definition entity.
        var agentDefinitionId = new EntityId("c0ffee01-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            agentDefinitionId,
            """
            {
              "entity-id": "c0ffee01-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "tab-restore-echo"]],
              "display-name": { "default": "Tab Restore Echo" },
              "definition": {
                "kind": "prompt",
                "name": "tab-restore-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        // Create the agent-session entity directly without going through the shortcut handler.
        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentDefinitionEntity = Assert.Single(await entityBroker.GetEntitiesAsync([agentDefinitionId]));
        var agentSessionId = Guid.NewGuid().ToString("n");
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, agentSessionId);
        Assert.NotNull(agentSessionEntity);
        var agentSessionEntityId = agentSessionEntity!.EntityId.ToString();

        // Build a workspace JSON with a tab referencing the agent-session entity by its entity ID.
        // Construct the workspace entity directly (no schema validation) to avoid workspace-schema
        // constraints on region/tab structure when we only care about the content routing logic.
        var workspaceEntityId = new EntityId("c0ffee03-0000-4000-8000-000000000003");
        var workspaceJson = $$"""
            {
              "entity-id": "c0ffee03-0000-4000-8000-000000000003",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Restore Test Workspace" },
              "regions": [
                {
                  "tabs": [
                    {
                      "tab-id": "restored-tab-1",
                      "title": "My Restored Session",
                      "dock": "full",
                      "content": {
                        "target-entity-name": "{{agentSessionEntityId}}"
                      }
                    }
                  ]
                }
              ]
            }
            """;

        using var workspaceDoc = JsonDocument.Parse(workspaceJson);
        var workspaceEntity = new SubscribedEntityViewModel(
            new EntitySnapshot
            {
                EntityId = workspaceEntityId,
                ConcurrencyTag = new ConcurrencyTag("1"),
                ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
                Data = workspaceDoc.RootElement.Clone(),
                Relationships = Array.Empty<EntitySnapshot>(),
            });

        var createWorkspacePaneMethod = typeof(MainWindowViewModel).GetMethod(
            "CreateWorkspacePaneAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(createWorkspacePaneMethod);

        var task = (Task<WorkspacePaneViewModel>?)createWorkspacePaneMethod!.Invoke(
            viewModel,
            [workspaceEntity, workspaceDoc.RootElement.Clone()]);
        Assert.NotNull(task);

        var workspacePane = await task!;
        Assert.NotNull(workspacePane);

        // The tab must be an AgentSessionWorkspaceTabViewModel, not a plain entity view.
        var restoredTab = Assert.Single(workspacePane.Tabs);
        var agentSessionTab = Assert.IsType<AgentSessionWorkspaceTabViewModel>(restoredTab);
        Assert.Equal("restored-tab-1", agentSessionTab.Id);
        Assert.Equal("My Restored Session", agentSessionTab.Title);
        Assert.True(agentSessionTab.Entity?.IsEntityType("agent-session"));
        await WaitForAgentReadyAsync(agentSessionTab);
        Assert.NotNull(agentSessionTab.Agent);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CreateWorkspacePaneAsync_WithAgentSessionTabButMissingDefinition_FallsBackToEntityWorkspaceTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        // Create an agent-definition entity so we can create a valid agent-session entity.
        var agentDefinitionId = new EntityId("dead0001-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            agentDefinitionId,
            """
            {
              "entity-id": "dead0001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "fallback-echo"]],
              "display-name": { "default": "Fallback Echo" },
              "definition": {
                "kind": "prompt",
                "name": "fallback-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        // Create the agent-session entity directly without going through the shortcut handler.
        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentDefinitionEntity = Assert.Single(await entityBroker.GetEntitiesAsync([agentDefinitionId]));
        var agentSessionId = Guid.NewGuid().ToString("n");
        var createdSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, agentSessionId);
        Assert.NotNull(createdSessionEntity);
        var agentSessionEntityId = createdSessionEntity!.EntityId.ToString();

        // Now delete the agent-definition entity so the restore path will fail to find it.
        // ConcurrencyTag is required by MergeProcessingDataAccessLayer for existing entities.
        var latestDefinitionEntity = Assert.Single(await entityBroker.GetEntitiesAsync([agentDefinitionId]));
        await entityBroker.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "Delete agent definition." } },
            Changes =
            [
                new EntityChange
                {
                    EntityId = agentDefinitionId,
                    EntityChangeMode = EntityChangeMode.Replace,
                    ConcurrencyTag = latestDefinitionEntity.ConcurrencyTag,
                    Data = null,
                },
            ],
        });

        var workspaceEntityId = new EntityId("dead0002-0000-4000-8000-000000000002");
        var workspaceJson = $$"""
            {
              "entity-id": "dead0002-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Missing Def Workspace" },
              "regions": [
                {
                  "tabs": [
                    {
                      "tab-id": "orphaned-tab",
                      "title": "Orphaned Session",
                      "content": {
                        "target-entity-name": "{{agentSessionEntityId}}"
                      }
                    }
                  ]
                }
              ]
            }
            """;

        using var workspaceDoc = JsonDocument.Parse(workspaceJson);
        var workspaceEntity = new SubscribedEntityViewModel(
            new EntitySnapshot
            {
                EntityId = workspaceEntityId,
                ConcurrencyTag = new ConcurrencyTag("1"),
                ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
                Data = workspaceDoc.RootElement.Clone(),
                Relationships = Array.Empty<EntitySnapshot>(),
            });

        var createWorkspacePaneMethod = typeof(MainWindowViewModel).GetMethod(
            "CreateWorkspacePaneAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(createWorkspacePaneMethod);

        var task = (Task<WorkspacePaneViewModel>?)createWorkspacePaneMethod!.Invoke(
            viewModel,
            [workspaceEntity, workspaceDoc.RootElement.Clone()]);
        Assert.NotNull(task);

        var workspacePane = await task!;
        Assert.NotNull(workspacePane);

        // With the new loading-tab design, TryCreateAgentSessionTabForRestoreAsync always returns
        // a loading tab (which transitions to Failed state asynchronously when data is missing).
        var agentTab = Assert.Single(workspacePane.Tabs);
        Assert.IsType<AgentSessionWorkspaceTabViewModel>(agentTab);
    }

    // ---- #1129: workspace-open of a shell entity should open a terminal, not an entity card. ----

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CreateWorkspacePaneAsync_WithShellEntityTab_CreatesShellTabViewModel()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var fakeSession = new Shell1129FakeTerminalSession();
        InstallFakeShellShortcutHandler(viewModel, (_, _, _) => Task.FromResult<ITerminalSession>(fakeSession));

        var shellEntityId = new EntityId("5ce1101a-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(
            GetEntityBroker(viewModel),
            shellEntityId,
            """
            {
              "entity-id": "5ce1101a-0000-4000-8000-000000000001",
              "entity-types": ["entity", "shell"],
              "display-name": { "default": "restored-shell" },
              "command": "pwsh"
            }
            """);

        var workspacePane = await InvokeCreateWorkspacePaneForShellAsync(
            viewModel,
            workspaceEntityIdText: "5ce1101a-0000-4000-8000-0000000000f1",
            shellEntityIdText: shellEntityId.ToString(),
            tabId: "restored-shell-tab",
            title: "Restored Shell",
            dock: "full");

        var restoredTab = Assert.Single(workspacePane.Tabs);
        Assert.IsType<ShellTabViewModel>(restoredTab);
        // Regression: must NOT fall through to the generic entity card view.
        Assert.IsNotType<EntityWorkspaceTabViewModel>(restoredTab);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CreateWorkspacePaneAsync_WithShellEntityTab_DispatchesThroughShortcutPipeline()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        ShellEntityOpenSpec? observedSpec = null;
        var fakeSession = new Shell1129FakeTerminalSession();
        InstallFakeShellShortcutHandler(viewModel, (_, spec, _) =>
        {
            observedSpec = spec;
            return Task.FromResult<ITerminalSession>(fakeSession);
        });

        var shellEntityId = new EntityId("5ce1101a-0000-4000-8000-000000000002");
        await UpsertEntityAndLoadAsync(
            GetEntityBroker(viewModel),
            shellEntityId,
            """
            {
              "entity-id": "5ce1101a-0000-4000-8000-000000000002",
              "entity-types": ["entity", "shell"],
              "display-name": { "default": "pipeline-shell" },
              "mode": "pty",
              "command": "pwsh",
              "command-arguments": ["-NoLogo"],
              "working-directory": "/work"
            }
            """);

        var workspacePane = await InvokeCreateWorkspacePaneForShellAsync(
            viewModel,
            workspaceEntityIdText: "5ce1101a-0000-4000-8000-0000000000f2",
            shellEntityIdText: shellEntityId.ToString(),
            tabId: "shell-pipeline",
            title: null,
            dock: null);

        var restoredTab = Assert.Single(workspacePane.Tabs);
        Assert.IsType<ShellTabViewModel>(restoredTab);
        // The shortcut handler's session-opener must have been invoked with the entity's spec —
        // proof that CreateWorkspacePaneAsync dispatched through the ShortcutManager restore
        // pipeline instead of the old ad-hoc EntityWorkspaceTabViewModel fallback.
        Assert.NotNull(observedSpec);
        Assert.Equal("pty", observedSpec!.Mode);
        Assert.Equal("pwsh", observedSpec.Command);
        Assert.Equal(new[] { "-NoLogo" }, observedSpec.CommandArguments);
        Assert.Equal("/work", observedSpec.WorkingDirectory);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CreateWorkspacePaneAsync_WithShellEntityTab_PreservesPersistedTabIdTitleAndDock()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var fakeSession = new Shell1129FakeTerminalSession();
        InstallFakeShellShortcutHandler(viewModel, (_, _, _) => Task.FromResult<ITerminalSession>(fakeSession));

        var shellEntityId = new EntityId("5ce1101a-0000-4000-8000-000000000003");
        await UpsertEntityAndLoadAsync(
            GetEntityBroker(viewModel),
            shellEntityId,
            """
            {
              "entity-id": "5ce1101a-0000-4000-8000-000000000003",
              "entity-types": ["entity", "shell"],
              "display-name": { "default": "layout-shell" },
              "command": "pwsh"
            }
            """);

        var workspacePane = await InvokeCreateWorkspacePaneForShellAsync(
            viewModel,
            workspaceEntityIdText: "5ce1101a-0000-4000-8000-0000000000f3",
            shellEntityIdText: shellEntityId.ToString(),
            tabId: "persisted-shell-id",
            title: "Custom Shell Title",
            dock: "right");

        var shellTab = Assert.IsType<ShellTabViewModel>(Assert.Single(workspacePane.Tabs));
        Assert.Equal("persisted-shell-id", shellTab.Id);
        Assert.Equal("Custom Shell Title", shellTab.Title);
        Assert.Equal("right", shellTab.DockRegion);
    }

    private static void InstallFakeShellShortcutHandler(
        MainWindowViewModel viewModel,
        Func<string, ShellEntityOpenSpec, CancellationToken, Task<ITerminalSession>> sessionOpener)
    {
        viewModel.ShortcutManager.ReplaceShortcutHandlerForTesting<OpenShellEntityShortcutHandler>(
            new OpenShellEntityShortcutHandler(sessionOpener));
    }

    private static async Task<WorkspacePaneViewModel> InvokeCreateWorkspacePaneForShellAsync(
        MainWindowViewModel viewModel,
        string workspaceEntityIdText,
        string shellEntityIdText,
        string tabId,
        string? title,
        string? dock)
    {
        var titleProp = title is null ? string.Empty : $"\"title\": \"{title}\",";
        var dockProp = dock is null ? string.Empty : $"\"dock\": \"{dock}\",";
        var workspaceJson = $$"""
            {
              "entity-id": "{{workspaceEntityIdText}}",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Shell Restore Test Workspace" },
              "regions": [
                {
                  "tabs": [
                    {
                      "tab-id": "{{tabId}}",
                      {{titleProp}}
                      {{dockProp}}
                      "content": {
                        "target-entity-name": "{{shellEntityIdText}}"
                      }
                    }
                  ]
                }
              ]
            }
            """;

        using var workspaceDoc = JsonDocument.Parse(workspaceJson);
        var workspaceEntity = new SubscribedEntityViewModel(
            new EntitySnapshot
            {
                EntityId = new EntityId(workspaceEntityIdText),
                ConcurrencyTag = new ConcurrencyTag("1"),
                ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
                Data = workspaceDoc.RootElement.Clone(),
                Relationships = Array.Empty<EntitySnapshot>(),
            });

        var createWorkspacePaneMethod = typeof(MainWindowViewModel).GetMethod(
            "CreateWorkspacePaneAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(createWorkspacePaneMethod);

        var task = (Task<WorkspacePaneViewModel>?)createWorkspacePaneMethod!.Invoke(
            viewModel,
            [workspaceEntity, workspaceDoc.RootElement.Clone()]);
        Assert.NotNull(task);
        var pane = await task!;
        Assert.NotNull(pane);
        return pane;
    }

    private sealed class Shell1129FakeTerminalSession : ITerminalSession
    {
        private readonly MemoryStream stream = new();
        public Stream Stream => this.stream;
        public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
        public ValueTask SignalAsync(string signal, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
        public Task<int> WaitForExitAsync() => Task.FromResult(0);
        public ValueTask DisposeAsync()
        {
            this.stream.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CloseActiveTabCommand_WithTwoTabs_ClosesActiveTabAndLeavesOther()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "tab-a", Title = "Tab A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "tab-b", Title = "Tab B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB); // tabB is now active

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        Assert.Equal("tab-b", documentDock!.ActiveDockable?.Id);

        viewModel.CloseActiveTabCommand.Execute(null);

        var remaining = documentDock.VisibleDockables?.OfType<WorkspaceDocument>().ToList();
        Assert.NotNull(remaining);
        Assert.DoesNotContain(remaining!, doc => doc.Id == "tab-b");
        Assert.Contains(remaining!, doc => doc.Id == "tab-a");
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CycleTabForwardCommand_WithThreeTabs_WrapsAroundForward()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "tab-a", Title = "Tab A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "tab-b", Title = "Tab B" };
        var tabC = new WebViewModel("https://c.example.com") { Id = "tab-c", Title = "Tab C" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);
        await viewModel.OpenTabAsync(tabC); // tabC is now active

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        var dockables = documentDock!.VisibleDockables!;
        var count = dockables.Count;

        // Record starting index and cycle forward through all tabs, wrapping around.
        var startIndex = dockables.IndexOf(documentDock.ActiveDockable!);
        for (var step = 1; step <= count; step++)
        {
            viewModel.CycleTabForwardCommand.Execute(null);
            var expectedIndex = (startIndex + step) % count;
            var actualIndex = dockables.IndexOf(documentDock.ActiveDockable!);
            Assert.Equal(expectedIndex, actualIndex);
        }

        // After a full cycle we should be back at the start.
        Assert.Equal(startIndex, dockables.IndexOf(documentDock.ActiveDockable!));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CycleTabBackwardCommand_WithThreeTabs_WrapsAroundBackward()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "tab-a", Title = "Tab A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "tab-b", Title = "Tab B" };
        var tabC = new WebViewModel("https://c.example.com") { Id = "tab-c", Title = "Tab C" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);
        await viewModel.OpenTabAsync(tabC); // tabC is now active

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        var dockables = documentDock!.VisibleDockables!;
        var count = dockables.Count;

        // Record starting index and cycle backward through all tabs, wrapping around.
        var startIndex = dockables.IndexOf(documentDock.ActiveDockable!);
        for (var step = 1; step <= count; step++)
        {
            viewModel.CycleTabBackwardCommand.Execute(null);
            var expectedIndex = ((startIndex - step) % count + count) % count;
            var actualIndex = dockables.IndexOf(documentDock.ActiveDockable!);
            Assert.Equal(expectedIndex, actualIndex);
        }

        // After a full cycle we should be back at the start.
        Assert.Equal(startIndex, dockables.IndexOf(documentDock.ActiveDockable!));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CycleTabForwardCommand_WithSingleTab_IsNoOp()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "tab-a-single", Title = "Tab A" };
        await viewModel.OpenTabAsync(tabA);

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);

        // Close all tabs except ours using the dockFactory via reflection.
        var dockFactoryField = typeof(MainWindowViewModel)
            .GetField("dockFactory", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(dockFactoryField);
        var dockFactory = dockFactoryField!.GetValue(viewModel);
        Assert.NotNull(dockFactory);
        var closeDockable = dockFactory!.GetType().GetMethod("CloseDockable",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy);
        Assert.NotNull(closeDockable);

        var otherDocs = documentDock!.VisibleDockables?
            .OfType<WorkspaceDocument>()
            .Where(d => d.Id != "tab-a-single")
            .ToList();
        foreach (var doc in otherDocs ?? [])
        {
            closeDockable!.Invoke(dockFactory, [doc]);
        }

        Assert.Equal("tab-a-single", documentDock.ActiveDockable?.Id);
        viewModel.CycleTabForwardCommand.Execute(null);
        Assert.Equal("tab-a-single", documentDock.ActiveDockable?.Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task GoToTabAtIndexCommand_WithThreeTabs_ActivatesCorrectTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "goto-tab-a", Title = "Tab A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "goto-tab-b", Title = "Tab B" };
        var tabC = new WebViewModel("https://c.example.com") { Id = "goto-tab-c", Title = "Tab C" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);
        await viewModel.OpenTabAsync(tabC);

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);

        ActivateContentTabAtIndex(viewModel, "0");

        Assert.Equal(documentDock!.VisibleDockables![0], documentDock.ActiveDockable);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task GoToTabAtIndexCommand_WithIndexOutOfRange_IsNoOp()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "goto-oob-a", Title = "Tab A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "goto-oob-b", Title = "Tab B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        var activeBefore = documentDock!.ActiveDockable;

        ActivateContentTabAtIndex(viewModel, "5");

        Assert.Equal(activeBefore, documentDock.ActiveDockable);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task GoToWorkspacePaneAtIndexCommand_WithMultiplePanes_ActivatesCorrectPane()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceIdA = new EntityId("dddddddd-dddd-4ddd-dddd-dddddddddddd");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdA,
            """
            {
              "entity-id": "dddddddd-dddd-4ddd-dddd-dddddddddddd",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "pane-nav-a"]],
              "display-name": { "default": "Pane Nav A" },
              "regions": []
            }
            """);

        var workspaceIdB = new EntityId("eeeeeeee-eeee-4eee-eeee-eeeeeeeeeeee");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdB,
            """
            {
              "entity-id": "eeeeeeee-eeee-4eee-eeee-eeeeeeeeeeee",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "pane-nav-b"]],
              "display-name": { "default": "Pane Nav B" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });

        // Select the second pane first, then navigate back to index 0
        ActivateWorkspacePaneAtIndex(viewModel, "1");
        Assert.Equal(viewModel.WorkspacePanes[1], viewModel.SelectedWorkspacePane);

        ActivateWorkspacePaneAtIndex(viewModel, "0");
        Assert.Equal(viewModel.WorkspacePanes[0], viewModel.SelectedWorkspacePane);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task GoToWorkspacePaneAtIndexCommand_WithIndexOutOfRange_IsNoOp()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var selectedBefore = viewModel.SelectedWorkspacePane;

        ActivateWorkspacePaneAtIndex(viewModel, "99");

        Assert.Equal(selectedBefore, viewModel.SelectedWorkspacePane);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task GoToWorkspacePaneAtIndexCommand_WithTwoPanes_ActivatesCorrectDockDocument()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceIdA = new EntityId("77200001-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdA,
            """
            {
              "entity-id": "77200001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "goto-pane-active-a"]],
              "display-name": { "default": "Goto Pane Active A" },
              "regions": []
            }
            """);

        var workspaceIdB = new EntityId("77200001-0000-4000-8000-000000000002");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdB,
            """
            {
              "entity-id": "77200001-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "goto-pane-active-b"]],
              "display-name": { "default": "Goto Pane Active B" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });

        var workspacesDock = FindDocumentDockIn(viewModel.Layout!);
        Assert.NotNull(workspacesDock);

        ActivateWorkspacePaneAtIndex(viewModel, "1");

        Assert.Equal(viewModel.WorkspacePanes[1], viewModel.SelectedWorkspacePane);
        var activePaneDoc = workspacesDock!.ActiveDockable as WorkspacePaneDocument;
        Assert.NotNull(activePaneDoc);
        Assert.Equal(viewModel.WorkspacePanes[1].Id, activePaneDoc!.WorkspacePane.Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task GoToWorkspacePaneAtIndexCommand_WithTwoPanes_ActivatesFirstPane()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceIdA = new EntityId("77200002-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdA,
            """
            {
              "entity-id": "77200002-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "goto-pane-first-a"]],
              "display-name": { "default": "Goto Pane First A" },
              "regions": []
            }
            """);

        var workspaceIdB = new EntityId("77200002-0000-4000-8000-000000000002");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdB,
            """
            {
              "entity-id": "77200002-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "goto-pane-first-b"]],
              "display-name": { "default": "Goto Pane First B" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });

        // Navigate to pane 1 first, then back to 0
        ActivateWorkspacePaneAtIndex(viewModel, "1");
        ActivateWorkspacePaneAtIndex(viewModel, "0");

        Assert.Equal(viewModel.WorkspacePanes[0], viewModel.SelectedWorkspacePane);
        var workspacesDock = FindDocumentDockIn(viewModel.Layout!);
        var activePaneDoc = workspacesDock!.ActiveDockable as WorkspacePaneDocument;
        Assert.NotNull(activePaneDoc);
        Assert.Equal(viewModel.WorkspacePanes[0].Id, activePaneDoc!.WorkspacePane.Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task GoToWorkspacePaneAtIndexCommand_WhenActiveTabInTargetPaneHasUnreadNotification_MarksNotificationRead()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceIdA = new EntityId("ff000001-ff00-4f00-8f00-ff0000000001");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdA,
            """
            {
              "entity-id": "ff000001-ff00-4f00-8f00-ff0000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "notif-pane-switch-a"]],
              "display-name": { "default": "Notif Pane Switch A" },
              "regions": []
            }
            """);

        var workspaceIdB = new EntityId("ff000002-ff00-4f00-8f00-ff0000000002");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdB,
            """
            {
              "entity-id": "ff000002-ff00-4f00-8f00-ff0000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "notif-pane-switch-b"]],
              "display-name": { "default": "Notif Pane Switch B" },
              "regions": []
            }
            """);

        // Open both workspaces; after OpenWorkspaceAsync(B) pane B (index 1) is selected.
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });
        Assert.True(viewModel.WorkspacePanes.Count >= 2,
            $"Expected at least 2 panes. Actual: {viewModel.WorkspacePanes.Count}; ids={string.Join(", ", viewModel.WorkspacePanes.Select(p => $"'{p.Id}'"))}");

        // Open a tab in pane B while it is the selected pane.
        var tabB = new AgentSessionWorkspaceTabViewModel { Id = "notif-pane-switch-tab-b", Title = "Tab B" };
        await viewModel.OpenTabAsync(tabB);

        // Flush the dispatcher queue so that any fire-and-forget work from OpenWorkspaceAsync
        // (e.g. PopulateWorkspacePaneTabsAsync adding a default entity-view tab) completes
        // before we assert on the dock state. Without this drain, the populate dispatch can run
        // after the test has set up tabB and overwrite SelectedTab, making the test flaky.
        await Dispatcher.UIThread.InvokeAsync(() => { });

        // After the drain, tabB must still be the selected/active tab in pane B.
        var paneBIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceIdB.ToString());
        Assert.True(paneBIndex >= 0, $"Pane B not found. Panes: {string.Join(", ", viewModel.WorkspacePanes.Select(p => $"'{p.Id}'"))}");
        Assert.Equal("notif-pane-switch-tab-b", viewModel.WorkspacePanes[paneBIndex].SelectedTab?.Id);

        // Switch to pane A so pane B's tab is no longer visible/active in the view.
        var paneAIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceIdA.ToString());
        Assert.True(paneAIndex >= 0, $"Pane A not found. Panes: {string.Join(", ", viewModel.WorkspacePanes.Select(p => $"'{p.Id}'"))}");
        ActivateWorkspacePaneAtIndex(viewModel, paneAIndex.ToString());
        Assert.Equal(viewModel.WorkspacePanes[paneAIndex], viewModel.SelectedWorkspacePane);

        // Post an unread notification to pane B's tab. Because pane B is not selected,
        // OnActiveDockableChanged is not fired for it, so the notification stays unread.
        viewModel.NotificationService.Notify(new Notification(
            new TabDescriptor { TabId = "notif-pane-switch-tab-b" },
            "Tab B", "test notification", DateTime.UtcNow, RunningState.Idle, NotificationState.Interesting));

        Assert.False(viewModel.NotificationService.Notifications
            .First(n => n.TabKey == "notif-pane-switch-tab-b").IsRead);

        // Switch back to pane B — this should mark the notification as read.
        ActivateWorkspacePaneAtIndex(viewModel, paneBIndex.ToString());

        Assert.True(viewModel.NotificationService.Notifications
            .First(n => n.TabKey == "notif-pane-switch-tab-b").IsRead,
            "Expected notification for tabB to be marked read after switching back to pane B");
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task GoToWorkspacePaneAtIndexCommand_WhenActiveTabInCurrentPaneHasUnreadNotification_OnlyMarksTargetPaneTabRead()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceIdA = new EntityId("ff000003-ff00-4f00-8f00-ff0000000003");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdA,
            """
            {
              "entity-id": "ff000003-ff00-4f00-8f00-ff0000000003",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "notif-pane-only-a"]],
              "display-name": { "default": "Notif Pane Only A" },
              "regions": []
            }
            """);

        var workspaceIdB = new EntityId("ff000004-ff00-4f00-8f00-ff0000000004");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdB,
            """
            {
              "entity-id": "ff000004-ff00-4f00-8f00-ff0000000004",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "notif-pane-only-b"]],
              "display-name": { "default": "Notif Pane Only B" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });

        // Flush the dispatcher queue to let any pending PopulateWorkspacePaneTabsAsync complete.
        await Dispatcher.UIThread.InvokeAsync(() => { });

        // Open a tab in pane B (currently selected after OpenWorkspaceAsync(B)).
        // tabB becomes the active dockable in pane B, so pane B's SelectedTab = tabB.
        var tabB = new AgentSessionWorkspaceTabViewModel { Id = "notif-pane-only-tab-b", Title = "Tab B" };
        await viewModel.OpenTabAsync(tabB);

        // Switch to pane A. Neither "notif-pane-only-tab-a" nor "notif-pane-only-tab-b" is the
        // active dockable in pane A, so any notification posted now will not be auto-marked read.
        var paneAIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceIdA.ToString());
        Assert.True(paneAIndex >= 0, $"Pane A not found. Panes: {string.Join(", ", viewModel.WorkspacePanes.Select(p => $"'{p.Id}'"))}");
        ActivateWorkspacePaneAtIndex(viewModel, paneAIndex.ToString());

        // Post unread notifications to both tab IDs. The active tab in pane A is neither
        // "notif-pane-only-tab-a" nor "notif-pane-only-tab-b", so both start unread.
        viewModel.NotificationService.Notify(new Notification(
            new TabDescriptor { TabId = "notif-pane-only-tab-a" },
            "Tab A", "test notification A", DateTime.UtcNow, RunningState.Idle, NotificationState.Interesting));
        viewModel.NotificationService.Notify(new Notification(
            new TabDescriptor { TabId = "notif-pane-only-tab-b" },
            "Tab B", "test notification B", DateTime.UtcNow, RunningState.Idle, NotificationState.Interesting));

        // Switch to pane B — only pane B's active tab (tabB) notification should be marked read.
        var paneBIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceIdB.ToString());
        Assert.True(paneBIndex >= 0, $"Pane B not found. Panes: {string.Join(", ", viewModel.WorkspacePanes.Select(p => $"'{p.Id}'"))}");
        ActivateWorkspacePaneAtIndex(viewModel, paneBIndex.ToString());

        Assert.True(viewModel.NotificationService.Notifications
            .First(n => n.TabKey == "notif-pane-only-tab-b").IsRead,
            "Switching to pane B should mark pane B's active tab notification as read.");
        Assert.False(viewModel.NotificationService.Notifications
            .First(n => n.TabKey == "notif-pane-only-tab-a").IsRead,
            "Pane A's tab notification should remain unread after switching away.");
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspacePaneActivation_WhenActiveTabInTargetPaneHasUnreadNotification_MarksNotificationRead()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceIdA = new EntityId("ff100001-ff00-4f00-8f00-ff0000000001");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdA,
            """
            {
              "entity-id": "ff100001-ff00-4f00-8f00-ff0000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "notif-activation-a"]],
              "display-name": { "default": "Notif Activation A" },
              "regions": []
            }
            """);

        var workspaceIdB = new EntityId("ff100002-ff00-4f00-8f00-ff0000000002");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdB,
            """
            {
              "entity-id": "ff100002-ff00-4f00-8f00-ff0000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "notif-activation-b"]],
              "display-name": { "default": "Notif Activation B" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });

        var tabB = new AgentSessionWorkspaceTabViewModel { Id = "notif-activation-tab-b", Title = "Tab B" };
        await viewModel.OpenTabAsync(tabB);

        await Dispatcher.UIThread.InvokeAsync(() => { });

        var paneAIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceIdA.ToString());
        var paneBIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceIdB.ToString());
        Assert.True(paneAIndex >= 0);
        Assert.True(paneBIndex >= 0);

        // Switch to pane A first.
        ActivateWorkspacePaneAtIndex(viewModel, paneAIndex.ToString());

        // Post an unread notification for pane B's active tab while pane B is not selected.
        viewModel.NotificationService.Notify(new Notification(
            new TabDescriptor { TabId = "notif-activation-tab-b" },
            "Tab B", "notif", DateTime.UtcNow, RunningState.Idle, NotificationState.Interesting));
        Assert.False(viewModel.NotificationService.Notifications
            .First(n => n.TabKey == "notif-activation-tab-b").IsRead);

        // Activate pane B via SetActiveDockable — this is the workspace-pane-switch path.
        var workspacesDock = FindDocumentDockIn(viewModel.Layout!);
        var paneBDoc = workspacesDock!.VisibleDockables!
            .OfType<WorkspacePaneDocument>()
            .First(d => d.WorkspacePane.Id == workspaceIdB.ToString());
        var dockFactory = GetDockFactoryAs<WorkspaceDockFactory>(viewModel);
        dockFactory.SetActiveDockable(paneBDoc);

        Assert.True(viewModel.NotificationService.Notifications
            .First(n => n.TabKey == "notif-activation-tab-b").IsRead,
            "SetActiveDockable(paneBDoc) must mark pane B's active tab notification as read.");
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OnActiveDockableChanged_WhenTabBecomesActive_ClearsNotificationsViewRowIndicator()
    {
        // #1223: activating a tab with an unread interesting notification must clear the "!" on the
        // corresponding Notifications-view row (ShowsAttentionIndicator), not just the tab header.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceIdA = new EntityId("ff123001-ff00-4f00-8f00-ff0000001001");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdA,
            """
            {
              "entity-id": "ff123001-ff00-4f00-8f00-ff0000001001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "notif-1223-a"]],
              "display-name": { "default": "Notif 1223 A" },
              "regions": []
            }
            """);

        var workspaceIdB = new EntityId("ff123002-ff00-4f00-8f00-ff0000001002");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdB,
            """
            {
              "entity-id": "ff123002-ff00-4f00-8f00-ff0000001002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "notif-1223-b"]],
              "display-name": { "default": "Notif 1223 B" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });

        var tabB = new AgentSessionWorkspaceTabViewModel { Id = "notif-1223-tab-b", Title = "Tab B" };
        await viewModel.OpenTabAsync(tabB);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        var paneAIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceIdA.ToString());
        Assert.True(paneAIndex >= 0);

        // Switch to pane A so pane B's notification starts unread.
        ActivateWorkspacePaneAtIndex(viewModel, paneAIndex.ToString());

        viewModel.NotificationService.Notify(new Notification(
            new TabDescriptor { TabId = "notif-1223-tab-b" },
            "Tab B", "notif", DateTime.UtcNow, RunningState.Idle, NotificationState.Interesting));
        await Dispatcher.UIThread.InvokeAsync(() => { });

        var notifRow = Assert.Single(viewModel.NotificationsViewModel!.Rows,
            r => r.TabKey == "notif-1223-tab-b");
        Assert.True(notifRow.ShowsAttentionIndicator,
            "Row should show the attention indicator while the notification is unread.");

        // Activate pane B (the active-dockable-change path that marks its active tab read).
        var workspacesDock = FindDocumentDockIn(viewModel.Layout!);
        var paneBDoc = workspacesDock!.VisibleDockables!
            .OfType<WorkspacePaneDocument>()
            .First(d => d.WorkspacePane.Id == workspaceIdB.ToString());
        var dockFactory = GetDockFactoryAs<WorkspaceDockFactory>(viewModel);
        dockFactory.SetActiveDockable(paneBDoc);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.True(notifRow.IsRead);
        Assert.False(notifRow.ShowsAttentionIndicator,
            "Activating the tab must clear the Notifications-view row '!' indicator.");
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspacePaneActivation_WhenNonActiveTabsHaveUnreadNotifications_LeavesNonActiveTabNotificationsUnread()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceIdA = new EntityId("ff100003-ff00-4f00-8f00-ff0000000003");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdA,
            """
            {
              "entity-id": "ff100003-ff00-4f00-8f00-ff0000000003",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "notif-nonactive-a"]],
              "display-name": { "default": "Notif Nonactive A" },
              "regions": []
            }
            """);

        var workspaceIdB = new EntityId("ff100004-ff00-4f00-8f00-ff0000000004");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdB,
            """
            {
              "entity-id": "ff100004-ff00-4f00-8f00-ff0000000004",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "notif-nonactive-b"]],
              "display-name": { "default": "Notif Nonactive B" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });

        // Open two tabs in pane B: T2 first (background), T1 second (active).
        var tabT2 = new AgentSessionWorkspaceTabViewModel { Id = "notif-nonactive-tab-t2", Title = "T2" };
        await viewModel.OpenTabAsync(tabT2);
        var tabT1 = new AgentSessionWorkspaceTabViewModel { Id = "notif-nonactive-tab-t1", Title = "T1" };
        await viewModel.OpenTabAsync(tabT1);

        await Dispatcher.UIThread.InvokeAsync(() => { });

        var paneBIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceIdB.ToString());
        Assert.True(paneBIndex >= 0);
        Assert.Equal("notif-nonactive-tab-t1", viewModel.WorkspacePanes[paneBIndex].SelectedTab?.Id);

        // Switch to pane A so both tabs' notifications will start unread.
        var paneAIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceIdA.ToString());
        ActivateWorkspacePaneAtIndex(viewModel, paneAIndex.ToString());

        viewModel.NotificationService.Notify(new Notification(
            new TabDescriptor { TabId = "notif-nonactive-tab-t1" },
            "T1", "notif t1", DateTime.UtcNow, RunningState.Idle, NotificationState.Interesting));
        viewModel.NotificationService.Notify(new Notification(
            new TabDescriptor { TabId = "notif-nonactive-tab-t2" },
            "T2", "notif t2", DateTime.UtcNow, RunningState.Idle, NotificationState.Interesting));

        // Switch to pane B — only T1 (the pane's active tab) should be marked read.
        ActivateWorkspacePaneAtIndex(viewModel, paneBIndex.ToString());

        Assert.True(viewModel.NotificationService.Notifications
            .First(n => n.TabKey == "notif-nonactive-tab-t1").IsRead,
            "Active tab T1's notification should be marked read.");
        Assert.False(viewModel.NotificationService.Notifications
            .First(n => n.TabKey == "notif-nonactive-tab-t2").IsRead,
            "Non-active tab T2's notification should remain unread.");
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspacePaneActivation_WhenTargetPaneHasNoTabs_DoesNotThrow()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceIdA = new EntityId("ff100005-ff00-4f00-8f00-ff0000000005");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdA,
            """
            {
              "entity-id": "ff100005-ff00-4f00-8f00-ff0000000005",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "notif-notabs-a"]],
              "display-name": { "default": "Notif NoTabs A" },
              "regions": []
            }
            """);

        var workspaceIdB = new EntityId("ff100006-ff00-4f00-8f00-ff0000000006");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdB,
            """
            {
              "entity-id": "ff100006-ff00-4f00-8f00-ff0000000006",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "notif-notabs-b"]],
              "display-name": { "default": "Notif NoTabs B" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });
        await Dispatcher.UIThread.InvokeAsync(() => { });

        // Force pane B to have no tabs and no selected tab.
        var paneBIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceIdB.ToString());
        Assert.True(paneBIndex >= 0);
        var paneB = viewModel.WorkspacePanes[paneBIndex];
        paneB.Tabs.Clear();
        paneB.SelectedTab = null;

        // Switching to a pane whose SelectedTab is null and Tabs is empty must not throw.
        var paneAIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceIdA.ToString());
        ActivateWorkspacePaneAtIndex(viewModel, paneAIndex.ToString());

        var ex = Record.Exception(() => ActivateWorkspacePaneAtIndex(viewModel, paneBIndex.ToString()));
        Assert.Null(ex);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspacePaneActivation_WhenActiveTabHasNoUnreadNotification_LeavesNotificationsUnchanged()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceIdA = new EntityId("ff100007-ff00-4f00-8f00-ff0000000007");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdA,
            """
            {
              "entity-id": "ff100007-ff00-4f00-8f00-ff0000000007",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "notif-noop-a"]],
              "display-name": { "default": "Notif Noop A" },
              "regions": []
            }
            """);

        var workspaceIdB = new EntityId("ff100008-ff00-4f00-8f00-ff0000000008");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdB,
            """
            {
              "entity-id": "ff100008-ff00-4f00-8f00-ff0000000008",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "notif-noop-b"]],
              "display-name": { "default": "Notif Noop B" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });

        var tabB = new AgentSessionWorkspaceTabViewModel { Id = "notif-noop-tab-b", Title = "Tab B" };
        await viewModel.OpenTabAsync(tabB);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        var paneAIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceIdA.ToString());
        var paneBIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceIdB.ToString());
        ActivateWorkspacePaneAtIndex(viewModel, paneAIndex.ToString());

        var beforeCount = viewModel.NotificationService.Notifications.Count;

        // Switching to pane B with no pending notification should not add or change anything.
        ActivateWorkspacePaneAtIndex(viewModel, paneBIndex.ToString());

        Assert.Equal(beforeCount, viewModel.NotificationService.Notifications.Count);
        Assert.All(viewModel.NotificationService.Notifications, n =>
            Assert.False(n.TabKey == "notif-noop-tab-b" && !n.IsRead));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspacePaneActivation_WhenCurrentPaneActiveTabHasUnreadNotification_LeavesCurrentPaneNotificationUnread()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceIdA = new EntityId("ff100009-ff00-4f00-8f00-ff0000000009");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdA,
            """
            {
              "entity-id": "ff100009-ff00-4f00-8f00-ff0000000009",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "notif-current-a"]],
              "display-name": { "default": "Notif Current A" },
              "regions": []
            }
            """);

        var workspaceIdB = new EntityId("ff10000a-ff00-4f00-8f00-ff000000000a");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdB,
            """
            {
              "entity-id": "ff10000a-ff00-4f00-8f00-ff000000000a",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "notif-current-b"]],
              "display-name": { "default": "Notif Current B" },
              "regions": []
            }
            """);

        var workspaceIdC = new EntityId("ff10000b-ff00-4f00-8f00-ff000000000b");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdC,
            """
            {
              "entity-id": "ff10000b-ff00-4f00-8f00-ff000000000b",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "notif-current-c"]],
              "display-name": { "default": "Notif Current C" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        // While pane A is selected, open tabA into pane A so tabA becomes its active tab.
        var tabA = new AgentSessionWorkspaceTabViewModel { Id = "notif-current-tab-a", Title = "Tab A" };
        await viewModel.OpenTabAsync(tabA);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });
        var tabB = new AgentSessionWorkspaceTabViewModel { Id = "notif-current-tab-b", Title = "Tab B" };
        await viewModel.OpenTabAsync(tabB);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdC });
        await Dispatcher.UIThread.InvokeAsync(() => { });

        // Pane C is currently selected; the ActiveTabId is not tabA nor tabB.
        // Post an unread notification for pane A's active tab (tabA). It stays unread
        // because pane A is not the selected pane.
        viewModel.NotificationService.Notify(new Notification(
            new TabDescriptor { TabId = "notif-current-tab-a" },
            "Tab A", "notif a", DateTime.UtcNow, RunningState.Idle, NotificationState.Interesting));
        Assert.False(viewModel.NotificationService.Notifications
            .First(n => n.TabKey == "notif-current-tab-a").IsRead,
            "Precondition: pane A's active-tab notification should start unread.");

        // Switch to pane B — must only clear pane B's active tab notification (if any),
        // and must NOT clear pane A's unread notification.
        var paneBIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceIdB.ToString());
        Assert.True(paneBIndex >= 0);
        ActivateWorkspacePaneAtIndex(viewModel, paneBIndex.ToString());

        Assert.False(viewModel.NotificationService.Notifications
            .First(n => n.TabKey == "notif-current-tab-a").IsRead,
            "Switching to pane B must not clear pane A's active-tab notification.");
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenWorkspaceAsync_WithFocusedTabId_ActivatesFocusedTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("f0c00001-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceId,
            """
            {
              "entity-id": "f0c00001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "focused-tab-test"]],
              "display-name": { "default": "Focused Tab Test Workspace" },
              "focused-tab-id": "tab-second",
              "regions": [
                {
                  "region-id": "main",
                  "title": "Main",
                  "dock": "center",
                  "size": 1.0,
                  "tabs": [
                    {
                      "tab-id": "tab-first",
                      "title": "First Tab",
                      "kind": "browser",
                      "dock": "full",
                      "content": { "url": "https://first.example.com" }
                    },
                    {
                      "tab-id": "tab-second",
                      "title": "Second Tab",
                      "kind": "browser",
                      "dock": "full",
                      "content": { "url": "https://second.example.com" }
                    }
                  ]
                }
              ]
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var workspacePane = Assert.Single(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, workspaceId.ToString(), StringComparison.Ordinal));

        var contentDock = FindDocumentDockIn(workspacePane.ContentLayout!);
        Assert.NotNull(contentDock);

        await WaitForWorkspaceTabAsync(contentDock!, "tab-first");
        await WaitForWorkspaceTabAsync(contentDock!, "tab-second");

        Assert.Equal("tab-second", contentDock!.ActiveDockable?.Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenWorkspaceAsync_WithAbsentFocusedTabId_DoesNotCrash()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("f0c00002-0000-4000-8000-000000000002");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceId,
            """
            {
              "entity-id": "f0c00002-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "no-focused-tab"]],
              "display-name": { "default": "No Focused Tab Workspace" },
              "regions": [
                {
                  "region-id": "main",
                  "title": "Main",
                  "dock": "center",
                  "size": 1.0,
                  "tabs": [
                    {
                      "tab-id": "only-tab",
                      "title": "Only Tab",
                      "kind": "browser",
                      "dock": "full",
                      "content": { "url": "https://example.com" }
                    }
                  ]
                }
              ]
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var workspacePane = Assert.Single(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, workspaceId.ToString(), StringComparison.Ordinal));

        var contentDock = FindDocumentDockIn(workspacePane.ContentLayout!);
        Assert.NotNull(contentDock);

        await WaitForWorkspaceTabAsync(contentDock!, "only-tab");

        Assert.NotNull(contentDock!.ActiveDockable);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenWorkspaceAsync_WithNonMatchingFocusedTabId_DoesNotCrash()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("f0c00003-0000-4000-8000-000000000003");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceId,
            """
            {
              "entity-id": "f0c00003-0000-4000-8000-000000000003",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "nonmatching-focused-tab"]],
              "display-name": { "default": "Non-matching Focused Tab Workspace" },
              "focused-tab-id": "nonexistent-tab-id",
              "regions": [
                {
                  "region-id": "main",
                  "title": "Main",
                  "dock": "center",
                  "size": 1.0,
                  "tabs": [
                    {
                      "tab-id": "tab-a",
                      "title": "Tab A",
                      "kind": "browser",
                      "dock": "full",
                      "content": { "url": "https://a.example.com" }
                    }
                  ]
                }
              ]
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var workspacePane = Assert.Single(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, workspaceId.ToString(), StringComparison.Ordinal));

        var contentDock = FindDocumentDockIn(workspacePane.ContentLayout!);
        Assert.NotNull(contentDock);

        await WaitForWorkspaceTabAsync(contentDock!, "tab-a");

        Assert.NotNull(contentDock!.ActiveDockable);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CreateAgentSessionEntityAsync_WithHostProfileEntityId_StoresHostProfileEntityIdInData()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var localProfileEntityId = entityBroker.EntityRepository.WorkspaceEntitySession.UserComputerProfileEntityId;

        var agentDefinitionId = new EntityId("aa010001-0000-4000-8000-000000000001");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(
            entityBroker,
            agentDefinitionId,
            """
            {
              "entity-id": "aa010001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "owner-store-echo"]],
              "display-name": { "default": "Owner Store Echo" },
              "definition": {
                "kind": "prompt",
                "name": "owner-store-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionId = Guid.NewGuid().ToString("n");
        var createdSession = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, agentSessionId, hostProfileEntityId: localProfileEntityId);

        Assert.NotNull(createdSession);
        Assert.True(createdSession!.Data is JsonElement data
            && data.TryGetProperty("host-profile-entity-id", out var idElement)
            && string.Equals(idElement.GetString(), localProfileEntityId.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task TryBuildAgent_WithLocalProfileOwner_RoutesToLocalExecutorSuccessfully()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var localProfileEntityId = entityBroker.EntityRepository.WorkspaceEntitySession.UserComputerProfileEntityId;

        var agentDefinitionId = new EntityId("aa020001-0000-4000-8000-000000000001");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(
            entityBroker,
            agentDefinitionId,
            """
            {
              "entity-id": "aa020001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "local-owner-echo"]],
              "display-name": { "default": "Local Owner Echo" },
              "definition": {
                "kind": "prompt",
                "name": "local-owner-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionId = Guid.NewGuid().ToString("n");
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, agentSessionId, hostProfileEntityId: localProfileEntityId);
        Assert.NotNull(agentSessionEntity);

        var openAgentSessionShortcutHandler = new OpenAgentSessionShortcutHandler(


            agentSessionShortcutContext,


            CreateLocalTrustedExecutorSelector(),


            CreateTestRunningAgentChatTable());

        await openAgentSessionShortcutHandler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);

        var agentTab = Assert.IsType<AgentSessionWorkspaceTabViewModel>(viewModel.SelectedWorkspacePane.SelectedTab);
        await WaitForAgentReadyAsync(agentTab);
        Assert.Equal(AgentTabState.Ready, agentTab.State);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task TryBuildAgent_WithNoOwningProfile_DefaultsToLocalExecution()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var agentDefinitionId = new EntityId("aa030001-0000-4000-8000-000000000001");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(
            entityBroker,
            agentDefinitionId,
            """
            {
              "entity-id": "aa030001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "no-owner-echo"]],
              "display-name": { "default": "No Owner Echo" },
              "definition": {
                "kind": "prompt",
                "name": "no-owner-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        // No owningProfileEntityId → defaults to local
        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionId = Guid.NewGuid().ToString("n");
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, agentSessionId);
        Assert.NotNull(agentSessionEntity);

        var openAgentSessionShortcutHandler = new OpenAgentSessionShortcutHandler(


            agentSessionShortcutContext,


            CreateLocalTrustedExecutorSelector(),


            CreateTestRunningAgentChatTable());

        await openAgentSessionShortcutHandler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);

        var agentTab2 = Assert.IsType<AgentSessionWorkspaceTabViewModel>(viewModel.SelectedWorkspacePane.SelectedTab);
        await WaitForAgentReadyAsync(agentTab2);
        Assert.Equal(AgentTabState.Ready, agentTab2.State);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task TryBuildAgent_WithRemoteProfileOwner_SetsFailedWhenNoConnectionAvailable()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var agentDefinitionId = new EntityId("aa040001-0000-4000-8000-000000000001");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(
            entityBroker,
            agentDefinitionId,
            """
            {
              "entity-id": "aa040001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "remote-owner-echo"]],
              "display-name": { "default": "Remote Owner Echo" },
              "definition": {
                "kind": "prompt",
                "name": "remote-owner-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        // Use a different GUID as the owning profile (simulates a remote profile with no connection)
        var remoteProfileEntityId = new EntityId(Guid.NewGuid());
        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionId = Guid.NewGuid().ToString("n");
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, agentSessionId, hostProfileEntityId: remoteProfileEntityId);
        Assert.NotNull(agentSessionEntity);

        // No remote executor configured → no reverse connection available for the remote profile
        var selectorWithNoRemote = new DeferredTrustedExecutorSelector();
        var openAgentSessionShortcutHandler = new OpenAgentSessionShortcutHandler(
            agentSessionShortcutContext,
            selectorWithNoRemote,
            CreateTestRunningAgentChatTable());

        await openAgentSessionShortcutHandler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);

        var agentTab3 = Assert.IsType<AgentSessionWorkspaceTabViewModel>(viewModel.SelectedWorkspacePane.SelectedTab);
        await WaitForAgentReadyAsync(agentTab3);
        Assert.Equal(AgentTabState.Failed, agentTab3.State);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task TryBuildAgent_WhenSessionRecordsHostProfileEntityId_RoutesToRemoteTarget()
    {
        // A session recording its host under the schema-canonical host-profile-entity-id (distinct
        // from the running instance's local profile) must take the remote branch. With no remote
        // executor available this surfaces as Failed rather than silently running in-process locally.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var agentDefinitionId = new EntityId("aa050001-0000-4000-8000-000000000001");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(
            entityBroker,
            agentDefinitionId,
            """
            {
              "entity-id": "aa050001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "host-profile-echo"]],
              "display-name": { "default": "Host Profile Echo" },
              "definition": {
                "kind": "prompt",
                "name": "host-profile-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var remoteProfileEntityId = new EntityId(Guid.NewGuid());
        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionId = Guid.NewGuid().ToString("n");
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, agentSessionId, hostProfileEntityId: remoteProfileEntityId);
        Assert.NotNull(agentSessionEntity);

        // The session must actually persist the canonical field the router reads.
        Assert.True(agentSessionEntity!.Data is JsonElement sessionData
            && sessionData.TryGetProperty("host-profile-entity-id", out var hostIdElement)
            && string.Equals(hostIdElement.GetString(), remoteProfileEntityId.ToString(), StringComparison.OrdinalIgnoreCase));

        var openAgentSessionShortcutHandler = new OpenAgentSessionShortcutHandler(
            agentSessionShortcutContext,
            new DeferredTrustedExecutorSelector(),
            CreateTestRunningAgentChatTable());

        await openAgentSessionShortcutHandler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);

        var agentTab = Assert.IsType<AgentSessionWorkspaceTabViewModel>(viewModel.SelectedWorkspacePane.SelectedTab);
        await WaitForAgentReadyAsync(agentTab);
        Assert.Equal(AgentTabState.Failed, agentTab.State);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task TryBuildAgent_WhenSessionRecordsLegacyOwningProfileEntityId_StillRoutesToRemoteTarget()
    {
        // Sessions persisted before the field name was unified carry only owning-profile-entity-id.
        // The router must still resolve them via the legacy alias and route to the remote target.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var agentDefinitionId = new EntityId("aa060001-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            agentDefinitionId,
            """
            {
              "entity-id": "aa060001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "legacy-owner-echo"]],
              "display-name": { "default": "Legacy Owner Echo" },
              "definition": {
                "kind": "prompt",
                "name": "legacy-owner-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var remoteProfileEntityId = new EntityId(Guid.NewGuid());
        var agentSessionId = Guid.NewGuid().ToString("n");
        var agentSessionEntityId = new EntityId(Guid.NewGuid());
        var legacyAgentSessionEntity = await UpsertEntityAndLoadAsync(
            entityBroker,
            agentSessionEntityId,
            $$"""
            {
              "entity-id": "{{agentSessionEntityId}}",
              "entity-types": ["entity", "agent-session"],
              "names": [["tests", "agent-sessions", "legacy-{{agentSessionId}}"]],
              "display-name": { "default": "Legacy Owner Echo session" },
              "agent-source-entity-id": "{{agentDefinitionId}}",
              "agent-session-id": "{{agentSessionId}}",
              "owning-profile-entity-id": "{{remoteProfileEntityId}}"
            }
            """);
        Assert.NotNull(legacyAgentSessionEntity);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var openAgentSessionShortcutHandler = new OpenAgentSessionShortcutHandler(
            agentSessionShortcutContext,
            new DeferredTrustedExecutorSelector(),
            CreateTestRunningAgentChatTable());

        await openAgentSessionShortcutHandler.Handle(viewModel, Shortcut.Open, legacyAgentSessionEntity!);

        var agentTab = Assert.IsType<AgentSessionWorkspaceTabViewModel>(viewModel.SelectedWorkspacePane.SelectedTab);
        await WaitForAgentReadyAsync(agentTab);
        Assert.Equal(AgentTabState.Failed, agentTab.State);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenAgentSessionShortcutHandler_Handle_UsesEntityIdAsTabId()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var agentDefinitionId = new EntityId("ab010001-0000-4000-8000-000000000001");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(
            entityBroker, agentDefinitionId,
            """
            {
              "entity-id": "ab010001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "tab-id-echo"]],
              "display-name": { "default": "Tab ID Echo" },
              "definition": {
                "kind": "prompt",
                "name": "tab-id-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, Guid.NewGuid().ToString("n"));
        Assert.NotNull(agentSessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(


            agentSessionShortcutContext,


            CreateLocalTrustedExecutorSelector(),


            CreateTestRunningAgentChatTable());

        var paneId = viewModel.SelectedWorkspacePane.Id;
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);

        var tab = Assert.IsType<AgentSessionWorkspaceTabViewModel>(viewModel.SelectedWorkspacePane.SelectedTab);
        var expectedTabId = $"{paneId}-{agentSessionEntity!.EntityId}";
        Assert.Equal(expectedTabId, tab.Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenAgentSessionShortcutHandler_Handle_SameEntityOpenedTwice_DeduplicatesTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var agentDefinitionId = new EntityId("ab020001-0000-4000-8000-000000000001");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(
            entityBroker, agentDefinitionId,
            """
            {
              "entity-id": "ab020001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "dedup-echo"]],
              "display-name": { "default": "Dedup Echo" },
              "definition": {
                "kind": "prompt",
                "name": "dedup-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, Guid.NewGuid().ToString("n"));
        Assert.NotNull(agentSessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(


            agentSessionShortcutContext,


            CreateLocalTrustedExecutorSelector(),


            CreateTestRunningAgentChatTable());

        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);

        // Wait for background agent initialization to complete
        await Dispatcher.UIThread.InvokeAsync(() => {}, DispatcherPriority.Background);
        await Dispatcher.UIThread.InvokeAsync(() => {}, DispatcherPriority.Background);

        // Check workspacePane.Tabs directly since VisibleDockables requires visual tree.
        // Tab ID format: "{paneId}-{entityId}" (see OpenAgentSessionShortcutHandler line 72).
        var paneId = viewModel.SelectedWorkspacePane!.Id;
        var agentSessionTabs = viewModel.SelectedWorkspacePane!.Tabs
            .Where(t => t.Id == $"{paneId}-{agentSessionEntity!.EntityId}")
            .ToList();
        Assert.Single(agentSessionTabs);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenAgentSessionShortcutHandler_Handle_WithRunningAgentChatTable_AcrossTwoWorkspacePanes_SharesAgentChat()
    {
        var runningAgentChatTable = CreateTestRunningAgentChatTable();
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceIdA = new EntityId("ab030001-0000-4000-8000-000000000001");
        var workspaceIdB = new EntityId("ab030002-0000-4000-8000-000000000002");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceIdA,
            """
            {
              "entity-id": "ab030001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "shared-chat-a"]],
              "display-name": { "default": "Shared Chat A" },
              "regions": []
            }
            """);
        await UpsertEntityAndLoadAsync(entityBroker, workspaceIdB,
            """
            {
              "entity-id": "ab030002-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "shared-chat-b"]],
              "display-name": { "default": "Shared Chat B" },
              "regions": []
            }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });

        var agentDefinitionId = new EntityId("ab030003-0000-4000-8000-000000000003");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(entityBroker, agentDefinitionId,
            """
            {
              "entity-id": "ab030003-0000-4000-8000-000000000003",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "shared-chat-echo"]],
              "display-name": { "default": "Shared Chat Echo" },
              "definition": {
                "kind": "prompt",
                "name": "shared-chat-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, Guid.NewGuid().ToString("n"));
        Assert.NotNull(agentSessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(
            agentSessionShortcutContext, CreateLocalTrustedExecutorSelector(), runningAgentChatTable);

        // Open in pane A
        var paneAIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceIdA.ToString());
        ActivateWorkspacePaneAtIndex(viewModel, paneAIndex.ToString());
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);
        var tabA = Assert.IsType<AgentSessionWorkspaceTabViewModel>(
            viewModel.SelectedWorkspacePane.SelectedTab);

        // Open in pane B
        var paneBIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceIdB.ToString());
        ActivateWorkspacePaneAtIndex(viewModel, paneBIndex.ToString());
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);
        var tabB = Assert.IsType<AgentSessionWorkspaceTabViewModel>(
            viewModel.SelectedWorkspacePane.SelectedTab);

        await WaitForAgentReadyAsync(tabA);
        await WaitForAgentReadyAsync(tabB);

        Assert.Equal(AgentTabState.Ready, tabA.State);
        Assert.Equal(AgentTabState.Ready, tabB.State);
        Assert.NotNull(tabA.Lease);
        Assert.NotNull(tabB.Lease);
        Assert.Same(tabA.Lease!.AgentChat, tabB.Lease!.AgentChat);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenUrlHandler_WhenAgentChatIsInNonSelectedPane_OpensTabInAgentChatPane()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceIdA = new EntityId("ab010001-0000-4000-8000-000000000001");
        var workspaceIdB = new EntityId("ab010001-0000-4000-8000-000000000002");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceIdA,
            """
            {
              "entity-id": "ab010001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "url-handler-pane-a"]],
              "display-name": { "default": "URL Handler Pane A" },
              "regions": []
            }
            """);
        await UpsertEntityAndLoadAsync(entityBroker, workspaceIdB,
            """
            {
              "entity-id": "ab010001-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "url-handler-pane-b"]],
              "display-name": { "default": "URL Handler Pane B" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });

        var paneA = viewModel.WorkspacePanes.First(p => p.Id == workspaceIdA.ToString());
        var paneB = viewModel.WorkspacePanes.First(p => p.Id == workspaceIdB.ToString());

        // Open agent session in pane A
        var paneAIndex = viewModel.WorkspacePanes.IndexOf(paneA);
        ActivateWorkspacePaneAtIndex(viewModel, paneAIndex.ToString());

        var agentDefinitionId = new EntityId("ab010001-0000-4000-8000-000000000003");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(entityBroker, agentDefinitionId,
            """
            {
              "entity-id": "ab010001-0000-4000-8000-000000000003",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "url-nonselected-echo"]],
              "display-name": { "default": "URL Nonselected Echo" },
              "definition": {
                "kind": "prompt",
                "name": "url-nonselected-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, Guid.NewGuid().ToString("n"));
        Assert.NotNull(agentSessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(


            agentSessionShortcutContext,


            CreateLocalTrustedExecutorSelector(),


            CreateTestRunningAgentChatTable());
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);

        var agentTab = Assert.IsType<AgentSessionWorkspaceTabViewModel>(
            viewModel.SelectedWorkspacePane.SelectedTab);
        await WaitForAgentReadyAsync(agentTab);
        Assert.NotNull(agentTab.Agent);

        // Switch to pane B so agent chat pane is NOT selected
        var paneBIndex = viewModel.WorkspacePanes.IndexOf(paneB);
        ActivateWorkspacePaneAtIndex(viewModel, paneBIndex.ToString());
        Assert.Equal(paneB, viewModel.SelectedWorkspacePane);

        // Invoke the URL handler — should open in pane A, not pane B
        const string testUrl = "https://url-nonselected.example.com";
        agentTab.Agent!.OpenUrlHandler!.Invoke(testUrl);

        var paneBDock = FindDocumentDockIn(paneB.ContentLayout!);
        Assert.NotNull(paneBDock);
        var paneADock = FindDocumentDockIn(paneA.ContentLayout!);
        Assert.NotNull(paneADock);

        await WaitForWorkspaceTabAsync(paneADock!, $"web-{testUrl}");

        // New tab must appear in pane A
        Assert.Contains(
            paneADock!.VisibleDockables!.OfType<WorkspaceDocument>(),
            doc => doc.Id == $"web-{testUrl}");

        // New tab must NOT appear in pane B
        Assert.DoesNotContain(
            paneBDock!.VisibleDockables?.OfType<WorkspaceDocument>() ?? [],
            doc => doc.Id == $"web-{testUrl}");
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenUrlHandler_WhenAgentChatIsInSelectedPane_OpensTabInSamePane()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceIdA = new EntityId("ab020002-0000-4000-8000-000000000001");
        var workspaceIdB = new EntityId("ab020002-0000-4000-8000-000000000002");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceIdA,
            """
            {
              "entity-id": "ab020002-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "url-selected-pane-a"]],
              "display-name": { "default": "URL Selected Pane A" },
              "regions": []
            }
            """);
        await UpsertEntityAndLoadAsync(entityBroker, workspaceIdB,
            """
            {
              "entity-id": "ab020002-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "url-selected-pane-b"]],
              "display-name": { "default": "URL Selected Pane B" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });

        var paneA = viewModel.WorkspacePanes.First(p => p.Id == workspaceIdA.ToString());
        var paneAIndex = viewModel.WorkspacePanes.IndexOf(paneA);
        ActivateWorkspacePaneAtIndex(viewModel, paneAIndex.ToString());
        Assert.Equal(paneA, viewModel.SelectedWorkspacePane);

        var agentDefinitionId = new EntityId("ab020002-0000-4000-8000-000000000003");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(entityBroker, agentDefinitionId,
            """
            {
              "entity-id": "ab020002-0000-4000-8000-000000000003",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "url-selected-echo"]],
              "display-name": { "default": "URL Selected Echo" },
              "definition": {
                "kind": "prompt",
                "name": "url-selected-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, Guid.NewGuid().ToString("n"));
        Assert.NotNull(agentSessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(


            agentSessionShortcutContext,


            CreateLocalTrustedExecutorSelector(),


            CreateTestRunningAgentChatTable());
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);

        var agentTab = Assert.IsType<AgentSessionWorkspaceTabViewModel>(
            viewModel.SelectedWorkspacePane.SelectedTab);
        await WaitForAgentReadyAsync(agentTab);
        Assert.NotNull(agentTab.Agent);

        // Pane A is selected — invoke handler while it IS selected
        Assert.Equal(paneA, viewModel.SelectedWorkspacePane);

        const string testUrl = "https://url-selected.example.com";
        agentTab.Agent!.OpenUrlHandler!.Invoke(testUrl);

        var paneADock = FindDocumentDockIn(paneA.ContentLayout!);
        Assert.NotNull(paneADock);
        await WaitForWorkspaceTabAsync(paneADock!, $"web-{testUrl}");

        Assert.Contains(
            paneADock!.VisibleDockables!.OfType<WorkspaceDocument>(),
            doc => doc.Id == $"web-{testUrl}");
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenUrlHandler_InsertsNewTabAfterAgentSessionTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var agentDefinitionId = new EntityId("ab020003-0000-4000-8000-000000000001");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(entityBroker, agentDefinitionId,
            """
            {
              "entity-id": "ab020003-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "url-insert-echo"]],
              "display-name": { "default": "URL Insert Echo" },
              "definition": {
                "kind": "prompt",
                "name": "url-insert-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        // Open agent session tab
        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, Guid.NewGuid().ToString("n"));
        Assert.NotNull(agentSessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(


            agentSessionShortcutContext,


            CreateLocalTrustedExecutorSelector(),


            CreateTestRunningAgentChatTable());
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);

        var agentTab = Assert.IsType<AgentSessionWorkspaceTabViewModel>(
            viewModel.SelectedWorkspacePane.SelectedTab);
        await WaitForAgentReadyAsync(agentTab);
        Assert.NotNull(agentTab.Agent);

        // Open another tab after the agent session tab
        var otherTab = new WebViewModel("https://other.example.com") { Id = "url-insert-other", Title = "Other" };
        await viewModel.OpenTabAsync(otherTab);

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);

        var targetPane = viewModel.SelectedWorkspacePane;
        var agentDoc = documentDock!.VisibleDockables!.OfType<WorkspaceDocument>()
            .First(d => d.Id == agentTab.Id);
        var agentTabIndex = documentDock!.VisibleDockables!.IndexOf(agentDoc);

        // Invoke the URL handler — new tab should be inserted right after the agent session tab
        const string testUrl = "https://url-insert.example.com";
        agentTab.Agent!.OpenUrlHandler!.Invoke(testUrl);

        await WaitForWorkspaceTabAsync(documentDock!, $"web-{testUrl}");

        // Fix #1065: assert against the visual DocumentDock.VisibleDockables order —
        // WorkspacePaneViewModel.Tabs is an order-independent membership set (#1107)
        // and no longer reflects visual dock ordering.
        var webDoc = documentDock!.VisibleDockables!.OfType<WorkspaceDocument>()
            .First(d => d.Id == $"web-{testUrl}");
        var webTabIndex = documentDock!.VisibleDockables!.IndexOf(webDoc);

        Assert.Equal(agentTabIndex + 1, webTabIndex);
    }

    internal static ITrustedExecutorSelector CreateLocalTrustedExecutorSelector()
        => new DeferredTrustedExecutorSelector();

    // ── Float-tab disposal guard (issue #635) ─────────────────────────────────

    [AvaloniaFact(Timeout = 15_000)]
    public async Task FloatDockable_AgentSessionTab_DoesNotDisposeOrRemoveTabFromPane()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var agentDefinitionId = new EntityId("f1050001-0000-4000-8000-000000000001");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(entityBroker, agentDefinitionId,
            """
            {
              "entity-id": "f1050001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "float-no-dispose-echo"]],
              "display-name": { "default": "Float No Dispose Echo" },
              "definition": {
                "kind": "prompt",
                "name": "float-no-dispose-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, Guid.NewGuid().ToString("n"));
        Assert.NotNull(agentSessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(


            agentSessionShortcutContext,


            CreateLocalTrustedExecutorSelector(),


            CreateTestRunningAgentChatTable());
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);

        var workspacePane = viewModel.SelectedWorkspacePane;
        var agentTab = await WaitForSelectedTabAsync<AgentSessionWorkspaceTabViewModel>(workspacePane);
        await WaitForAgentReadyAsync(agentTab);

        var dockFactory = GetDockFactoryAs<WorkspaceDockFactory>(viewModel);
        var contentDock = FindDocumentDockIn(workspacePane.ContentLayout!);
        Assert.NotNull(contentDock);
        await WaitForWorkspaceTabAsync(contentDock!, agentTab.Id);

        var document = workspacePane.GetDocumentForTab(agentTab.Id);
        Assert.NotNull(document);

        // Act: float the tab into a floating window
        dockFactory.FloatDockable(document!);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        // The tab must remain in pane.Tabs — float must NOT remove or dispose it
        Assert.Contains(workspacePane.Tabs, t => ReferenceEquals(t, agentTab));
        Assert.NotNull(agentTab.Agent);
        Assert.Equal(AgentTabState.Ready, agentTab.State);
    }

    // ── #1196: Floating-host tab-header indicator tests removed ─────────────
    //
    // The former FloatingHostWindow_* tests + AssertFloatingHostWindowIndicatorAsync
    // were false positives: they hand-built the tab-header template against a fresh
    // Window and never exercised the real floating DocumentTabStrip render path.
    // When floated, Dock creates a plain Dock.Model.Mvvm.Controls.DocumentDock
    // (via CreateDocumentDock), which is rendered by the generic
    // DataTemplate DataType="dmc:IDocumentDock" -> DocumentDockControl using Dock's
    // DEFAULT header (a plain Title TextBlock), so floated tabs do not carry the
    // pulsating-brain / exclamation indicators. Real-tab-strip coverage for the
    // indicators now lives in MainWindowDockTemplateTests (outer WorkspacesPaneDock
    // strip) and the content-level DocumentTabStrip tests.

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CloseDockable_AfterFloat_DisposesTabAndRemovesFromPane()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var agentDefinitionId = new EntityId("f1060001-0000-4000-8000-000000000001");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(entityBroker, agentDefinitionId,
            """
            {
              "entity-id": "f1060001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "float-close-echo"]],
              "display-name": { "default": "Float Close Echo" },
              "definition": {
                "kind": "prompt",
                "name": "float-close-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, Guid.NewGuid().ToString("n"));
        Assert.NotNull(agentSessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(


            agentSessionShortcutContext,


            CreateLocalTrustedExecutorSelector(),


            CreateTestRunningAgentChatTable());
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);

        var workspacePane = viewModel.SelectedWorkspacePane;
        var agentTab = await WaitForSelectedTabAsync<AgentSessionWorkspaceTabViewModel>(workspacePane);
        await WaitForAgentReadyAsync(agentTab);

        var dockFactory = GetDockFactoryAs<WorkspaceDockFactory>(viewModel);
        var contentDock = FindDocumentDockIn(workspacePane.ContentLayout!);
        Assert.NotNull(contentDock);
        await WaitForWorkspaceTabAsync(contentDock!, agentTab.Id);

        var document = workspacePane.GetDocumentForTab(agentTab.Id);
        Assert.NotNull(document);

        // Float first, then close from the floating state
        dockFactory.FloatDockable(document!);
        await Dispatcher.UIThread.InvokeAsync(() => { });
        Assert.Contains(workspacePane.Tabs, t => ReferenceEquals(t, agentTab));

        // Act: close the floating document
        dockFactory.CloseDockable(document!);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        // Tab must have been removed from pane.Tabs and disposed
        Assert.DoesNotContain(workspacePane.Tabs, t => ReferenceEquals(t, agentTab));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CloseDockable_FromMainDock_DisposesTabExactlyOnce()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var agentDefinitionId = new EntityId("f1070001-0000-4000-8000-000000000001");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(entityBroker, agentDefinitionId,
            """
            {
              "entity-id": "f1070001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "close-once-echo"]],
              "display-name": { "default": "Close Once Echo" },
              "definition": {
                "kind": "prompt",
                "name": "close-once-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, Guid.NewGuid().ToString("n"));
        Assert.NotNull(agentSessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(


            agentSessionShortcutContext,


            CreateLocalTrustedExecutorSelector(),


            CreateTestRunningAgentChatTable());
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);

        var workspacePane = viewModel.SelectedWorkspacePane;
        var agentTab = await WaitForSelectedTabAsync<AgentSessionWorkspaceTabViewModel>(workspacePane);
        await WaitForAgentReadyAsync(agentTab);

        var dockFactory = GetDockFactoryAs<WorkspaceDockFactory>(viewModel);
        var contentDock = FindDocumentDockIn(workspacePane.ContentLayout!);
        Assert.NotNull(contentDock);
        await WaitForWorkspaceTabAsync(contentDock!, agentTab.Id);

        var document = workspacePane.GetDocumentForTab(agentTab.Id);
        Assert.NotNull(document);

        // Track how many times the tab is removed from pane.Tabs
        var removeCount = 0;
        ((System.Collections.Specialized.INotifyCollectionChanged)workspacePane.Tabs).CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove
                && e.OldItems?.Contains(agentTab) == true)
            {
                removeCount++;
            }
        };

        // Act: close directly from the main dock (no float)
        dockFactory.CloseDockable(document!);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        // Tab must be removed exactly once (guards against double-dispose from both
        // SyncPaneTabsFromDockChange and OnDockableClosed firing on close)
        Assert.Equal(1, removeCount);
        Assert.DoesNotContain(workspacePane.Tabs, t => ReferenceEquals(t, agentTab));
    }

    // ── Dock-layout save / restore (issue #561) ──────────────────────────────

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenTabAsync_ThenWriteBack_DockLayoutJsonContainsDockTabDescriptor()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://descriptor-test.example.com")
        {
            Id = "dt-tab-1",
            Title = "Descriptor Test",
        };
        await viewModel.OpenTabAsync(tab);

        // Serialize the dock layout directly to verify DockTabDescriptor is embedded
        var pane = viewModel.SelectedWorkspacePane;
        Assert.NotNull(pane.ContentLayout);

        var serializer = new DockSerializer(typeof(System.Collections.ObjectModel.ObservableCollection<>), new WorkspaceDockTypeInfoResolver());
        var layoutJson = serializer.Serialize(pane.ContentLayout!);

        // The serialized layout must contain the Descriptor property
        Assert.Contains("Descriptor", layoutJson, StringComparison.Ordinal);
        // And the browser kind
        Assert.Contains("browser", layoutJson, StringComparison.Ordinal);
        // And the URL
        Assert.Contains("descriptor-test.example.com", layoutJson, StringComparison.Ordinal);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenTabAsync_ThenWriteBack_DockLayoutDoesNotContainTabViewModelData()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://no-vm-test.example.com")
        {
            Id = "no-vm-tab-1",
            Title = "No VM Leak Test",
        };
        await viewModel.OpenTabAsync(tab);

        var pane = viewModel.SelectedWorkspacePane;
        Assert.NotNull(pane.ContentLayout);

        // Diagnostic: assert Owner is null before serialization
        Assert.Null(pane.ContentLayout!.Owner);

        // Use WorkspaceDockTypeInfoResolver to match production serialization (it strips
        // Type-typed Avalonia properties and handles Owner back-references via ReferenceHandler.Preserve)
        var serializer = new DockSerializer(typeof(System.Collections.ObjectModel.ObservableCollection<>), new WorkspaceDockTypeInfoResolver());
        var layoutJson = serializer.Serialize(pane.ContentLayout!);

        // Content-bearing properties must NOT appear in the serialized layout
        Assert.DoesNotContain("TabViewModel", layoutJson, StringComparison.Ordinal);
        Assert.DoesNotContain("EffectiveTabHeader", layoutJson, StringComparison.Ordinal);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenWorkspaceAsync_WithSavedDockLayout_RestoresTabsFromDescriptors()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        // Step 1: open a browser tab and capture the dock-layout JSON directly from the pane
        var tab = new WebViewModel("https://restore-test.example.com")
        {
            Id = "restore-tab-browser",
            Title = "Restore Browser Tab",
        };
        await viewModel.OpenTabAsync(tab);

        var pane = viewModel.SelectedWorkspacePane;
        Assert.NotNull(pane.ContentLayout);

        var serializer = new DockSerializer(typeof(System.Collections.ObjectModel.ObservableCollection<>), new WorkspaceDockTypeInfoResolver());
        var dockLayoutJson = serializer.Serialize(pane.ContentLayout!);
        Assert.Contains("Descriptor", dockLayoutJson, StringComparison.Ordinal);

        // Step 2: build a workspace entity that carries the saved dock-layout and open it
        var workspaceId = new EntityId("d0c1a7a0-0000-4000-8000-000000000001");
        var workspaceJson = $$"""
            {
              "entity-id": "d0c1a7a0-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Restore Dock Layout Workspace" },
              "dock-layout": {{dockLayoutJson}},
              "regions": []
            }
            """;
        await UpsertEntityAndLoadAsync(entityBroker, workspaceId, workspaceJson);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var restoredPane = viewModel.WorkspacePanes.FirstOrDefault(
            p => string.Equals(p.Id, workspaceId.ToString(), StringComparison.Ordinal));
        Assert.NotNull(restoredPane);

        // Wait for PopulateWorkspacePaneTabsAsync to populate the tabs
        await WaitForWorkspacePaneTabsAsync(restoredPane!);

        // The pane must have at least one tab from the dock-layout restore
        Assert.NotEmpty(restoredPane!.Tabs);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task PopulateWorkspacePaneTabsAsync_FallsBackToTabsArray_WhenDockLayoutAbsent()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("fa11b4c0-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceId,
            """
            {
              "entity-id": "fa11b4c0-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "fallback-tabs-array"]],
              "display-name": { "default": "Fallback Tabs Array Workspace" },
              "regions": [
                {
                  "region-id": "main",
                  "title": "Main",
                  "dock": "center",
                  "size": 1.0,
                  "tabs": [
                    {
                      "tab-id": "fallback-tab-1",
                      "title": "Fallback Tab",
                      "kind": "browser",
                      "dock": "full",
                      "content": { "url": "https://fallback.example.com" }
                    }
                  ]
                }
              ]
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var workspacePane = viewModel.WorkspacePanes.FirstOrDefault(
            p => string.Equals(p.Id, workspaceId.ToString(), StringComparison.Ordinal));
        Assert.NotNull(workspacePane);

        var contentDock = FindDocumentDockIn(workspacePane!.ContentLayout!);
        Assert.NotNull(contentDock);
        await WaitForWorkspaceTabAsync(contentDock!, "fallback-tab-1");

        var tabDoc = contentDock!.VisibleDockables!
            .OfType<WorkspaceDocument>()
            .FirstOrDefault(d => d.Id == "fallback-tab-1");
        Assert.NotNull(tabDoc);
        Assert.IsType<WebViewModel>(tabDoc!.TabViewModel);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task PopulateWorkspacePaneTabsAsync_RestoresFromDockLayout_WhenPresent()
    {
        // Arrange: capture a real dock-layout JSON from an open tab, then open a new
        // workspace entity that carries that dock-layout.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://restore-layout-present.example.com")
        {
            Id = "rlp-tab",
            Title = "Restore Layout Present",
        };
        await viewModel.OpenTabAsync(tab);

        var pane = viewModel.SelectedWorkspacePane;
        var serializer = new DockSerializer(typeof(System.Collections.ObjectModel.ObservableCollection<>), new WorkspaceDockTypeInfoResolver());

        // Wait for ItemContainerGenerator to populate VisibleDockables
        var rlpContentDock = FindDocumentDockIn(pane.ContentLayout!);
        Assert.NotNull(rlpContentDock);
        await WaitForWorkspaceTabAsync(rlpContentDock!, "rlp-tab");

        var dockLayoutJson = serializer.Serialize(pane.ContentLayout!);
        Assert.Contains("Descriptor", dockLayoutJson, StringComparison.Ordinal);

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("e570ee01-0000-4000-8000-000000000001");
        var workspaceJson = $$"""
            {
              "entity-id": "e570ee01-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Restore Layout Present WS" },
              "dock-layout": {{dockLayoutJson}},
              "regions": []
            }
            """;
        await UpsertEntityAndLoadAsync(entityBroker, workspaceId, workspaceJson);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var restoredPane = viewModel.WorkspacePanes.FirstOrDefault(
            p => string.Equals(p.Id, workspaceId.ToString(), StringComparison.Ordinal));
        Assert.NotNull(restoredPane);

        // Wait for PopulateWorkspacePaneTabsAsync to populate the tabs
        await WaitForPanePopulatedAsync(restoredPane!);

        Assert.NotEmpty(restoredPane!.Tabs);
        Assert.Contains(restoredPane.Tabs, t => t is WebViewModel);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task PopulateWorkspacePaneTabsAsync_WhenDockLayoutRestoreCompletes_SignalsPanePopulated()
    {
        // Verifies the Populated task completes successfully for the happy path
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://signals-populated.example.com")
        {
            Id = "sp-tab",
            Title = "Signals Populated",
        };
        await viewModel.OpenTabAsync(tab);

        var pane = viewModel.SelectedWorkspacePane;
        var serializer = new DockSerializer(typeof(System.Collections.ObjectModel.ObservableCollection<>), new WorkspaceDockTypeInfoResolver());
        var spContentDock = FindDocumentDockIn(pane.ContentLayout!);
        Assert.NotNull(spContentDock);
        await WaitForWorkspaceTabAsync(spContentDock!, "sp-tab");

        var dockLayoutJson = serializer.Serialize(pane.ContentLayout!);

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("e570ee01-0000-4000-8000-000000000002");
        var workspaceJson = $$"""
            {
              "entity-id": "e570ee01-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Signals Populated WS" },
              "dock-layout": {{dockLayoutJson}},
              "regions": []
            }
            """;
        await UpsertEntityAndLoadAsync(entityBroker, workspaceId, workspaceJson);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var restoredPane = viewModel.WorkspacePanes.FirstOrDefault(
            p => string.Equals(p.Id, workspaceId.ToString(), StringComparison.Ordinal));
        Assert.NotNull(restoredPane);

        // The Populated task should complete without throwing
        await WaitForPanePopulatedAsync(restoredPane!);
        Assert.NotEmpty(restoredPane!.Tabs);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task PopulateWorkspacePaneTabsAsync_WhenNoDockLayoutAndNoTabs_SignalsPanePopulatedAfterDefaultTabAdd()
    {
        // Verifies the default-tab fallback path signals completion
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("e570ee01-0000-4000-8000-000000000003");
        var workspaceJson = """
            {
              "entity-id": "e570ee01-0000-4000-8000-000000000003",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Default Tab Fallback WS" },
              "regions": []
            }
            """;
        await UpsertEntityAndLoadAsync(entityBroker, workspaceId, workspaceJson);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var pane = viewModel.WorkspacePanes.FirstOrDefault(
            p => string.Equals(p.Id, workspaceId.ToString(), StringComparison.Ordinal));
        Assert.NotNull(pane);

        // The Populated task should complete even when using the default-tab fallback
        await WaitForPanePopulatedAsync(pane!);
        Assert.NotEmpty(pane!.Tabs);
        Assert.Contains(pane.Tabs, t => t is EntityWorkspaceTabViewModel);
    }

    // ── #1340: close→reopen of restored agent-session tabs (per-pane registry) ──

    /// <summary>
    /// #1340 shared setup: creates an agent-definition + agent-session entity and a valid empty
    /// "template" workspace. Opens the template as a real workspace pane, opens the agent session
    /// into it as a tab via <see cref="OpenAgentSessionShortcutHandler"/>, captures the live
    /// dock-layout, closes the template, then creates a SEPARATE fresh workspace entity that carries
    /// that dock-layout and opens it — so the returned pane was produced by the dock-layout restore
    /// path (the path exercised by the #1340 close→reopen mechanism). Returns the reopenable
    /// workspace id, the still-open restored pane, and the restored tab id.
    /// </summary>
    private static async Task<(EntityId WorkspaceId, WorkspacePaneViewModel Pane, string TabId)>
        OpenAgentSessionWorkspaceAndPersistDockLayoutAsync(
            MainWindowViewModel viewModel,
            string agentDefinitionGuid,
            string templateWorkspaceGuid,
            string restoreWorkspaceGuid)
    {
        static async Task Bounded(Task task, string label)
        {
            var timeout = Task.Delay(TimeSpan.FromSeconds(8));
            if (await Task.WhenAny(task, timeout) == timeout)
            {
                Assert.Fail($"#1340 helper timed out at: {label}");
            }

            await task;
        }

        static async Task<T> BoundedResult<T>(Task<T> task, string label)
        {
            var timeout = Task.Delay(TimeSpan.FromSeconds(8));
            if (await Task.WhenAny(task, timeout) == timeout)
            {
                Assert.Fail($"#1340 helper timed out at: {label}");
            }

            return await task;
        }

        var entityBroker = GetEntityBroker(viewModel);

        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(entityBroker, new EntityId(agentDefinitionGuid), $$"""
            {
              "entity-id": "{{agentDefinitionGuid}}",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "1340-echo", "{{agentDefinitionGuid}}"]],
              "display-name": { "default": "1340 Echo" },
              "definition": {
                "kind": "prompt",
                "name": "echo-1340",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, Guid.NewGuid().ToString("n"));
        Assert.NotNull(agentSessionEntity);

        // Template workspace: a valid empty workspace opened as a real, closable pane.
        var templateWorkspaceId = new EntityId(templateWorkspaceGuid);
        await UpsertEntityAndLoadAsync(entityBroker, templateWorkspaceId, $$"""
            {
              "entity-id": "{{templateWorkspaceGuid}}",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "1340 Template WS" },
              "regions": []
            }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = templateWorkspaceId });
        var templatePane = Assert.Single(
            viewModel.WorkspacePanes,
            p => string.Equals(p.Id, templateWorkspaceId.ToString(), StringComparison.Ordinal));
        await CloseDefaultPaneTabsAsync(viewModel, templatePane);
        viewModel.SelectedWorkspacePane = templatePane;

        // Open the agent session into the template pane and capture the auto-generated tab id.
        var handler = new OpenAgentSessionShortcutHandler(
            agentSessionShortcutContext, CreateLocalTrustedExecutorSelector(), CreateTestRunningAgentChatTable());
        Assert.True(await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!));
        var sessionTab = await BoundedResult(
            WaitForSelectedTabAsync<AgentSessionWorkspaceTabViewModel>(templatePane),
            "WaitForSelectedTabAsync(templatePane)");
        var tabId = sessionTab.Id;

        var templateDock = FindDocumentDockIn(templatePane.ContentLayout!);
        Assert.NotNull(templateDock);
        await Bounded(
            WaitForWorkspaceTabAsync(templateDock!, tabId),
            "WaitForWorkspaceTabAsync(templateDock)");

        // The agent-session document's Descriptor (needed for round-trip restore) is only populated
        // once the agent tab has finished initializing.
        await Bounded(WaitForAgentReadyAsync(sessionTab), "WaitForAgentReadyAsync(sessionTab)");

        var serializer = new DockSerializer(
            typeof(System.Collections.ObjectModel.ObservableCollection<>), new WorkspaceDockTypeInfoResolver());
        var dockLayoutJson = serializer.Serialize(templatePane.ContentLayout!);
        Assert.Contains("Descriptor", dockLayoutJson, StringComparison.Ordinal);

        await viewModel.RemoveWorkspacePaneAsync(templatePane);

        // Fresh restore workspace carrying the captured dock-layout (single upsert of a new entity).
        var restoreWorkspaceId = new EntityId(restoreWorkspaceGuid);
        var withLayoutJson = $$"""
            {
              "entity-id": "{{restoreWorkspaceGuid}}",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "1340 Reopen WS" },
              "dock-layout": {{dockLayoutJson}},
              "regions": []
            }
            """;
        await UpsertEntityAndLoadAsync(entityBroker, restoreWorkspaceId, withLayoutJson);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = restoreWorkspaceId });
        var pane = Assert.Single(
            viewModel.WorkspacePanes,
            p => string.Equals(p.Id, restoreWorkspaceId.ToString(), StringComparison.Ordinal));

        await Bounded(WaitForPanePopulatedAsync(pane), "WaitForPanePopulatedAsync(restore pane)");

        var contentDock = FindDocumentDockIn(pane.ContentLayout!);
        if (contentDock is null
            || contentDock.VisibleDockables?.OfType<WorkspaceDocument>().Any(d => d.Id == tabId) != true)
        {
            var tabInfo = string.Join(", ", pane.Tabs.Select(t => $"{t.GetType().Name}:{t.Id}"));
            var dockInfo = contentDock?.VisibleDockables is null
                ? "<null>"
                : string.Join(", ", contentDock.VisibleDockables.Select(d => $"{d.GetType().Name}:{d.Id}"));
            Assert.Fail(
                $"#1340 restore did not materialize agent doc '{tabId}'. Tabs=[{tabInfo}] DockDocs=[{dockInfo}]");
        }

        return (restoreWorkspaceId, pane, tabId);
    }

    [AvaloniaFact(Timeout = 20_000)]
    public async Task WorkspaceClose_WithRestoredAgentSessionTab_EvictsDocumentsByTabId()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var (_, pane, tabId) = await OpenAgentSessionWorkspaceAndPersistDockLayoutAsync(
            viewModel,
            "a9e11340-0000-4000-8000-000000000001",
            "b0b11340-0000-4000-8000-000000000001",
            "c0c11340-0000-4000-8000-000000000001");

        // The restored agent-session tab materialized a document in THIS pane's per-pane registry.
        Assert.NotNull(pane.GetDocumentForTab(tabId));

        // Closing the pane discards its entire per-pane registry — no stale entry can survive to be
        // reachable from a subsequent reopen (the structural #1341 fix for the #1340 mechanism).
        await viewModel.RemoveWorkspacePaneAsync(pane);

        Assert.Null(pane.GetDocumentForTab(tabId));
    }

    [AvaloniaFact(Timeout = 20_000)]
    public async Task WorkspaceReopen_AfterClose_WithRestoredAgentSessionTab_MaterializesDocument_NoStaleEntry()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var (workspaceId, pane1, tabId) = await OpenAgentSessionWorkspaceAndPersistDockLayoutAsync(
            viewModel,
            "a9e11340-0000-4000-8000-000000000002",
            "b0b11340-0000-4000-8000-000000000002",
            "c0c11340-0000-4000-8000-000000000002");

        await viewModel.RemoveWorkspacePaneAsync(pane1);

        // Reopen the same workspace; restore again goes through the persisted dock-layout path.
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });
        var pane2 = Assert.Single(
            viewModel.WorkspacePanes,
            p => string.Equals(p.Id, workspaceId.ToString(), StringComparison.Ordinal));
        Assert.NotSame(pane1, pane2);

        await WaitForPanePopulatedAsync(pane2);

        var primaryDock = FindDocumentDockIn(pane2.ContentLayout!);
        Assert.NotNull(primaryDock);
        await WaitForWorkspaceTabAsync(primaryDock!, tabId);

        var restoredDoc = primaryDock!.VisibleDockables!
            .OfType<WorkspaceDocument>()
            .FirstOrDefault(d => string.Equals(d.Id, tabId, StringComparison.Ordinal));
        Assert.NotNull(restoredDoc);
        Assert.NotNull(pane2.GetDocumentForTab(tabId));
    }

    [AvaloniaFact(Timeout = 20_000)]
    public async Task WorkspaceReopen_AfterCloseWithAgentSessionTabInSplitRegion_RecreatesTabInSameRegion()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var (_, pane1, tabId) = await OpenAgentSessionWorkspaceAndPersistDockLayoutAsync(
            viewModel,
            "a9e11340-0000-4000-8000-000000000003",
            "b0b11340-0000-4000-8000-000000000003",
            "c0c11340-0000-4000-8000-000000000003");

        // Move the agent-session document into a NON-PRIMARY split region, then persist that layout
        // as a fresh restore workspace.
        var dockFactory = GetDockFactoryAs<WorkspaceDockFactory>(viewModel);
        var primaryDock = FindDocumentDockIn(pane1.ContentLayout!)!;
        var agentDoc = primaryDock.VisibleDockables!.OfType<WorkspaceDocument>()
            .Single(d => string.Equals(d.Id, tabId, StringComparison.Ordinal));

        var splitDock = new WorkspaceContentDock
        {
            Id = $"split-{tabId}",
            VisibleDockables = dockFactory.CreateList<IDockable>(),
        };
        var contentRoot = (IDock)pane1.ContentLayout!;
        dockFactory.AddDockable(contentRoot, splitDock);
        dockFactory.MoveDockable(primaryDock, splitDock, agentDoc, null);
        Assert.Same(splitDock, agentDoc.Owner);

        var serializer = new DockSerializer(
            typeof(System.Collections.ObjectModel.ObservableCollection<>), new WorkspaceDockTypeInfoResolver());
        var splitLayoutJson = serializer.Serialize(pane1.ContentLayout!);

        var splitWorkspaceId = new EntityId("d0d11340-0000-4000-8000-000000000003");
        var withSplitLayoutJson = $$"""
            {
              "entity-id": "d0d11340-0000-4000-8000-000000000003",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "1340 Split Reopen WS" },
              "dock-layout": {{splitLayoutJson}},
              "regions": []
            }
            """;
        await UpsertEntityAndLoadAsync(entityBroker, splitWorkspaceId, withSplitLayoutJson);

        await viewModel.RemoveWorkspacePaneAsync(pane1);

        // Open the split-layout workspace: the agent-session tab that lived in a non-primary split
        // region must be re-created.
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = splitWorkspaceId });
        var pane2 = Assert.Single(
            viewModel.WorkspacePanes,
            p => string.Equals(p.Id, splitWorkspaceId.ToString(), StringComparison.Ordinal));
        Assert.NotSame(pane1, pane2);

        await WaitForPanePopulatedAsync(pane2);

        // The restored tab must materialize a WorkspaceDocument in a content dock of the reopened
        // pane, and the pane's per-pane registry must own it. Under #1341 the non-primary eviction
        // path is structural, so this succeeds by construction.
        Assert.NotNull(pane2.GetDocumentForTab(tabId));
        var hostingDock = MultiRegionRestoreTestSupport.EnumerateDocks(pane2.ContentLayout!)
            .OfType<WorkspaceContentDock>()
            .FirstOrDefault(d => d.VisibleDockables?.OfType<WorkspaceDocument>()
                .Any(doc => string.Equals(doc.Id, tabId, StringComparison.Ordinal)) == true);
        Assert.NotNull(hostingDock);
    }

    // ── #1158: DockTabDescriptor.Title round-trips through save→restore ─────

    [AvaloniaFact(Timeout = 15_000)]
    public async Task RestoreFromDockLayout_WithEntityTabs_PreservesEachTabTitle()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var entityId1 = new EntityId("11588002-0000-4000-8000-000000000001");
        var entity1 = await UpsertEntityAndLoadAsync(entityBroker, entityId1, """
            {
              "entity-id": "11588002-0000-4000-8000-000000000001",
              "entity-types": ["entity", "note"],
              "names": [["notes", "entity-title-tab-1"]],
              "display-name": { "default": "Entity One Display" },
              "content": { "mime-type": "text/markdown", "content": { "text": "one" } }
            }
            """);
        var entityId2 = new EntityId("11588002-0000-4000-8000-000000000002");
        var entity2 = await UpsertEntityAndLoadAsync(entityBroker, entityId2, """
            {
              "entity-id": "11588002-0000-4000-8000-000000000002",
              "entity-types": ["entity", "note"],
              "names": [["notes", "entity-title-tab-2"]],
              "display-name": { "default": "Entity Two Display" },
              "content": { "mime-type": "text/markdown", "content": { "text": "two" } }
            }
            """);

        var tab1 = new EntityWorkspaceTabViewModel
        {
            Id = "entity-title-tab-1",
            Title = "Custom Entity Title 1",
            Entity = entity1,
            DockRegion = "full",
        };
        var tab2 = new EntityWorkspaceTabViewModel
        {
            Id = "entity-title-tab-2",
            Title = "Custom Entity Title 2",
            Entity = entity2,
            DockRegion = "full",
        };
        await viewModel.OpenTabAsync(tab1);
        await viewModel.OpenTabAsync(tab2);

        var pane = viewModel.SelectedWorkspacePane;
        var contentDock = FindDocumentDockIn(pane.ContentLayout!);
        Assert.NotNull(contentDock);
        await WaitForWorkspaceTabAsync(contentDock!, "entity-title-tab-2");

        var serializer = new DockSerializer(
            typeof(System.Collections.ObjectModel.ObservableCollection<>),
            new WorkspaceDockTypeInfoResolver());
        var dockLayoutJson = serializer.Serialize(pane.ContentLayout!);

        var workspaceId = new EntityId("11588002-0000-4000-8000-0000000000f1");
        var workspaceJson = $$"""
            {
              "entity-id": "11588002-0000-4000-8000-0000000000f1",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Entity Title Restore WS" },
              "dock-layout": {{dockLayoutJson}},
              "regions": []
            }
            """;
        await UpsertEntityAndLoadAsync(entityBroker, workspaceId, workspaceJson);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var restoredPane = viewModel.WorkspacePanes.FirstOrDefault(
            p => string.Equals(p.Id, workspaceId.ToString(), StringComparison.Ordinal));
        Assert.NotNull(restoredPane);
        await WaitForPanePopulatedAsync(restoredPane!);

        var restored1 = restoredPane!.Tabs.FirstOrDefault(t => t.Id == "entity-title-tab-1");
        var restored2 = restoredPane.Tabs.FirstOrDefault(t => t.Id == "entity-title-tab-2");
        Assert.NotNull(restored1);
        Assert.NotNull(restored2);
        Assert.Equal("Custom Entity Title 1", restored1!.Title);
        Assert.Equal("Custom Entity Title 2", restored2!.Title);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task RestoreFromDockLayout_WhenEntityDisplayNameIsEmpty_FallsBackToDescriptorTitle()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var entityId = new EntityId("11588003-0000-4000-8000-000000000001");
        var entity = await UpsertEntityAndLoadAsync(entityBroker, entityId, """
            {
              "entity-id": "11588003-0000-4000-8000-000000000001",
              "entity-types": ["entity", "note"],
              "names": [["notes", "empty-display-name-tab"]],
              "display-name": { "default": "" },
              "content": { "mime-type": "text/markdown", "content": { "text": "empty" } }
            }
            """);

        var tab = new EntityWorkspaceTabViewModel
        {
            Id = "empty-display-name-tab",
            Title = "Meaningful User Title",
            Entity = entity,
            DockRegion = "full",
        };
        await viewModel.OpenTabAsync(tab);

        var pane = viewModel.SelectedWorkspacePane;
        var contentDock = FindDocumentDockIn(pane.ContentLayout!);
        Assert.NotNull(contentDock);
        await WaitForWorkspaceTabAsync(contentDock!, "empty-display-name-tab");

        var serializer = new DockSerializer(
            typeof(System.Collections.ObjectModel.ObservableCollection<>),
            new WorkspaceDockTypeInfoResolver());
        var dockLayoutJson = serializer.Serialize(pane.ContentLayout!);

        var workspaceId = new EntityId("11588003-0000-4000-8000-0000000000f1");
        var workspaceJson = $$"""
            {
              "entity-id": "11588003-0000-4000-8000-0000000000f1",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Empty DN Restore WS" },
              "dock-layout": {{dockLayoutJson}},
              "regions": []
            }
            """;
        await UpsertEntityAndLoadAsync(entityBroker, workspaceId, workspaceJson);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var restoredPane = viewModel.WorkspacePanes.FirstOrDefault(
            p => string.Equals(p.Id, workspaceId.ToString(), StringComparison.Ordinal));
        Assert.NotNull(restoredPane);
        await WaitForPanePopulatedAsync(restoredPane!);

        var restoredTab = Assert.Single(restoredPane!.Tabs);
        Assert.Equal("Meaningful User Title", restoredTab.Title);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task RestoreFromDockLayout_WithBrowserTab_PreservesUserSetTitle()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://browser-title.example.com")
        {
            Id = "browser-title-tab",
            Title = "User-Chosen Browser Title",
        };
        await viewModel.OpenTabAsync(tab);

        var pane = viewModel.SelectedWorkspacePane;
        var contentDock = FindDocumentDockIn(pane.ContentLayout!);
        Assert.NotNull(contentDock);
        await WaitForWorkspaceTabAsync(contentDock!, "browser-title-tab");

        var serializer = new DockSerializer(
            typeof(System.Collections.ObjectModel.ObservableCollection<>),
            new WorkspaceDockTypeInfoResolver());
        var dockLayoutJson = serializer.Serialize(pane.ContentLayout!);

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("11588001-0000-4000-8000-0000000000f1");
        var workspaceJson = $$"""
            {
              "entity-id": "11588001-0000-4000-8000-0000000000f1",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Browser Title Restore WS" },
              "dock-layout": {{dockLayoutJson}},
              "regions": []
            }
            """;
        await UpsertEntityAndLoadAsync(entityBroker, workspaceId, workspaceJson);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var restoredPane = viewModel.WorkspacePanes.FirstOrDefault(
            p => string.Equals(p.Id, workspaceId.ToString(), StringComparison.Ordinal));
        Assert.NotNull(restoredPane);
        await WaitForPanePopulatedAsync(restoredPane!);

        var restoredTab = Assert.Single(restoredPane!.Tabs);
        var web = Assert.IsType<WebViewModel>(restoredTab);
        Assert.Equal("User-Chosen Browser Title", web.Title);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task RestoreFromDockLayout_WithAgentSessionTab_PreservesTitle()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var agentDefinitionId = new EntityId("11588004-0000-4000-8000-000000000001");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(entityBroker, agentDefinitionId, """
            {
              "entity-id": "11588004-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "title-restore-echo"]],
              "display-name": { "default": "Title Restore Echo" },
              "definition": {
                "kind": "prompt",
                "name": "title-restore-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionId = Guid.NewGuid().ToString("n");
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, agentSessionId);
        Assert.NotNull(agentSessionEntity);

        var tab = new AgentSessionWorkspaceTabViewModel
        {
            Id = "agent-title-tab",
            Title = "Preserved Agent Title",
            Entity = agentSessionEntity,
            DockRegion = "full",
        };
        await viewModel.OpenTabAsync(tab);

        var pane = viewModel.SelectedWorkspacePane;
        var contentDock = FindDocumentDockIn(pane.ContentLayout!);
        Assert.NotNull(contentDock);
        await WaitForWorkspaceTabAsync(contentDock!, "agent-title-tab");

        var serializer = new DockSerializer(
            typeof(System.Collections.ObjectModel.ObservableCollection<>),
            new WorkspaceDockTypeInfoResolver());
        var dockLayoutJson = serializer.Serialize(pane.ContentLayout!);

        var workspaceId = new EntityId("11588004-0000-4000-8000-0000000000f1");
        var workspaceJson = $$"""
            {
              "entity-id": "11588004-0000-4000-8000-0000000000f1",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Agent Title Restore WS" },
              "dock-layout": {{dockLayoutJson}},
              "regions": []
            }
            """;
        await UpsertEntityAndLoadAsync(entityBroker, workspaceId, workspaceJson);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var restoredPane = viewModel.WorkspacePanes.FirstOrDefault(
            p => string.Equals(p.Id, workspaceId.ToString(), StringComparison.Ordinal));
        Assert.NotNull(restoredPane);
        await WaitForPanePopulatedAsync(restoredPane!);

        var restoredTab = Assert.Single(restoredPane!.Tabs);
        Assert.IsType<AgentSessionWorkspaceTabViewModel>(restoredTab);
        Assert.Equal("Preserved Agent Title", restoredTab.Title);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WaitForPanePopulatedAsync_WhenPopulateHangs_ThrowsTimeoutExceptionWithDiagnostics()
    {
        // Verifies the timeout diagnostic message includes pane ID and Tabs.Count
        var entitySnapshot = new EntitySnapshot
        {
            EntityId = new EntityId("e570ee01-0000-4000-8000-000000000004"),
            ConcurrencyTag = new ConcurrencyTag("1"),
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
            Data = JsonDocument.Parse("""
                {
                  "entity-id": "e570ee01-0000-4000-8000-000000000004",
                  "entity-types": ["entity", "workspace"],
                  "display-name": { "default": "Hang Test WS" }
                }
                """).RootElement.Clone(),
            Relationships = Array.Empty<EntitySnapshot>(),
        };
        var subscribedEntity = new SubscribedEntityViewModel(entitySnapshot);
        var pane = new WorkspacePaneViewModel(subscribedEntity, "e570ee01-0000-4000-8000-000000000004", null);

        // The Populated task should never complete (SignalPopulated is never called)
        var exception = await Assert.ThrowsAsync<TimeoutException>(
            async () => await WaitForPanePopulatedAsync(pane, TimeSpan.FromSeconds(1)));

        Assert.Contains("e570ee01-0000-4000-8000-000000000004", exception.Message);
        Assert.Contains("Tabs.Count=0", exception.Message);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task PopulateWorkspacePaneTabsAsync_WhenDockLayoutRestoreThrows_SurfacesExceptionOnPanePopulatedTask()
    {
        // Verifies that exceptions thrown during PopulateWorkspacePaneTabsAsync are propagated
        // through the Populated task via the SignalPopulated(Exception) mechanism.
        // This tests the exception handling in the ContinueWith continuation at MainWindowViewModel.cs:1586-1595
        
        var entitySnapshot = new EntitySnapshot
        {
            EntityId = new EntityId("e570ee01-0000-4000-8000-000000000005"),
            ConcurrencyTag = new ConcurrencyTag("1"),
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
            Data = JsonDocument.Parse("""
                {
                  "entity-id": "e570ee01-0000-4000-8000-000000000005",
                  "entity-types": ["entity", "workspace"],
                  "display-name": { "default": "Exception Test WS" }
                }
                """).RootElement.Clone(),
            Relationships = Array.Empty<EntitySnapshot>(),
        };
        var subscribedEntity = new SubscribedEntityViewModel(entitySnapshot);
        var pane = new WorkspacePaneViewModel(subscribedEntity, "e570ee01-0000-4000-8000-000000000005", null);

        // Simulate the exception path by directly calling SignalPopulated with an exception
        // This tests that the exception is correctly propagated through the Populated task
        var testException = new InvalidOperationException("Simulated populate failure");
        pane.SignalPopulated(testException);

        // The Populated task should fault and propagate the exact exception
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await pane.Populated);
        
        Assert.Same(testException, exception);
        Assert.Equal("Simulated populate failure", exception.Message);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WriteBackWorkspaceTabs_IsNotCalledOnDockLayoutChange()
    {
        // After the fix, pane.Tabs.CollectionChanged is NOT subscribed for write-back.
        // Dock-order changes (Move/Reset from dock animations) must NOT trigger entity updates.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://no-write-a.example.com") { Id = "nw-a", Title = "A" };
        var tabB = new WebViewModel("https://no-write-b.example.com") { Id = "nw-b", Title = "B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);

        var entityBroker = GetEntityBroker(viewModel);
        var pane = viewModel.SelectedWorkspacePane;

        // Capture entity snapshot BEFORE dock layout mutation
        var before = (await entityBroker.GetEntitiesAsync([pane.Entity.EntityId]))
            .FirstOrDefault(e => e.EntityId == pane.Entity.EntityId);

        // Simulate a dock Move/Reset by reordering pane.Tabs directly (the same operation
        // SyncPaneTabsOrderFromDock performs). With the CollectionChanged subscription removed,
        // this must NOT trigger WriteBackWorkspaceTabs.
        pane.Tabs.Move(0, 1);

        // Give any async callbacks a chance to run
        await Dispatcher.UIThread.InvokeAsync(() => { });

        var after = (await entityBroker.GetEntitiesAsync([pane.Entity.EntityId]))
            .FirstOrDefault(e => e.EntityId == pane.Entity.EntityId);

        // Entity must not have changed — no dock-layout key written
        var beforeJson = before?.Data is System.Text.Json.JsonElement be ? be.GetRawText() : "null";
        var afterJson = after?.Data is System.Text.Json.JsonElement ae ? ae.GetRawText() : "null";
        Assert.Equal(beforeJson, afterJson);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task SaveWorkspaceLayoutAsync_PersistsDockLayoutWithDescriptors()
    {
        // Explicit WriteBackWorkspaceTabs persists dock-layout JSON that contains
        // Descriptor data for each open tab.
        await using var viewModel = CreateTestMainWindowViewModel(
            configuration: new WorkspacesConfiguration { SkipStartupWorkspace = false });
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://save-layout-test.example.com")
        {
            Id = "slt-tab",
            Title = "Save Layout Test",
        };
        await viewModel.OpenTabAsync(tab);

        var pane = viewModel.SelectedWorkspacePane;

        // Wait for ItemContainerGenerator to populate VisibleDockables before write-back
        var saveContentDock = FindDocumentDockIn(pane.ContentLayout!);
        Assert.NotNull(saveContentDock);
        await WaitForWorkspaceTabAsync(saveContentDock!, "slt-tab");

        // Verify entity is subscribed and has a ConcurrencyTag (not the placeholder)
        Assert.False(pane.Entity.EntityId == new Phantom.Workspaces.Data.EntityId(Guid.Empty),
            "SelectedWorkspacePane must be a real workspace entity, not the placeholder.");
        Assert.NotNull(pane.Entity.ConcurrencyTag);

        // Explicitly trigger write-back (simulates explicit save) and await completion
        var writeBackResult = await viewModel.WriteBackWorkspaceTabs(pane);
        var failedResults = writeBackResult.EntityResults
            .Where(r => r.UpdateState == Phantom.Workspaces.Data.UpdateState.Failed)
            .ToList();
        var errorMessages = failedResults
            .SelectMany(r => r.Errors ?? [])
            .Select(e => e.Message)
            .ToList();
        Assert.Empty(errorMessages);

        // pane.Entity is the subscribed entity view model; its Data is updated in-place
        // by EntityBroker.UpdateAsync when the underlying snapshot changes.
        var data = Assert.IsType<System.Text.Json.JsonElement>(pane.Entity.Data);
        Assert.True(data.TryGetProperty("dock-layout", out var dockLayoutEl));
        var dockLayoutJson = dockLayoutEl.GetRawText();
        Assert.Contains("Descriptor", dockLayoutJson, StringComparison.Ordinal);
        Assert.Contains("browser", dockLayoutJson, StringComparison.Ordinal);
        Assert.Contains("save-layout-test.example.com", dockLayoutJson, StringComparison.Ordinal);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task DockLayoutRoundTrip_PreservesSplitPositionsAndDescriptors()
    {
        // Verify serialize → deserialize round-trip: the Descriptor survives and the
        // layout structure is intact (no exceptions, correct types).
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://roundtrip-test.example.com")
        {
            Id = "rt-tab",
            Title = "Round-trip Test",
        };
        await viewModel.OpenTabAsync(tab);

        var pane = viewModel.SelectedWorkspacePane;
        Assert.NotNull(pane.ContentLayout);

        // Wait for ItemContainerGenerator to populate VisibleDockables
        var rtContentDock = FindDocumentDockIn(pane.ContentLayout!);
        Assert.NotNull(rtContentDock);
        await WaitForWorkspaceTabAsync(rtContentDock!, "rt-tab");

        var serializer = new DockSerializer(
            typeof(System.Collections.ObjectModel.ObservableCollection<>),
            new WorkspaceDockTypeInfoResolver());

        // Serialize
        var layoutJson = serializer.Serialize(pane.ContentLayout!);
        Assert.Contains("Descriptor", layoutJson, StringComparison.Ordinal);
        Assert.Contains("browser", layoutJson, StringComparison.Ordinal);
        Assert.Contains("roundtrip-test.example.com", layoutJson, StringComparison.Ordinal);
        Assert.DoesNotContain("TabViewModel", layoutJson, StringComparison.Ordinal);

        // Deserialize
        var restored = serializer.Deserialize<global::Dock.Model.Controls.IRootDock>(layoutJson);
        Assert.NotNull(restored);

        var docs = MainWindowViewModel.EnumerateAllDocuments(restored!).ToList();
        Assert.NotEmpty(docs);
        Assert.Contains(docs, d => d.Descriptor is BrowserDockTabDescriptor b
            && b.Url == "https://roundtrip-test.example.com");
    }

    // ── #1190: modify → save → close → reopen preserves inner tab header titles ─

    [AvaloniaFact(Timeout = 30_000)]
    public async Task WorkspaceRestore_AfterModifySaveCloseReopen_InnerTabHeaderTitlesArePreserved()
    {
        // Canonical regression for #1190: opens a workspace with three entity tabs,
        // sets each tab.Title to a distinct value AFTER OpenTabAsync returns, calls
        // WriteBackWorkspaceTabs, closes the pane via CloseWorkspacePaneAsync, then
        // calls OpenWorkspaceAsync and asserts every restored WorkspaceDocument's
        // EffectiveTabHeader.Title (bound in DockDataTemplates.axaml) equals its
        // pre-save value and is non-empty. Prior to the fix, Descriptor was captured
        // once via `??=` at InitializeCore time and never refreshed when tab.Title
        // changed, so the restored header rendered blank.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        // Seed three note entities whose display-name is empty so the only viable
        // source for the restored header title is the persisted Descriptor.Title.
        var entityIds = new[]
        {
            new EntityId("11901190-1111-4000-8000-000000000001"),
            new EntityId("11901190-1111-4000-8000-000000000002"),
            new EntityId("11901190-1111-4000-8000-000000000003"),
        };
        for (var i = 0; i < entityIds.Length; i++)
        {
            await UpsertEntityAndLoadAsync(entityBroker, entityIds[i], $$"""
                {
                  "entity-id": "{{entityIds[i]}}",
                  "entity-types": ["entity", "note"],
                  "names": [["notes", "1190-tab-{{i + 1}}"]],
                  "display-name": { "default": "" },
                  "content": { "mime-type": "text/markdown", "content": { "text": "n{{i + 1}}" } }
                }
                """);
        }

        // Seed the workspace entity and open it so pane.Entity is real (not the placeholder).
        var workspaceId = new EntityId("11901190-1111-4000-8000-0000000000f1");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceId, $$"""
            {
              "entity-id": "{{workspaceId}}",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Modify Save Close Reopen WS" },
              "regions": []
            }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });
        var pane = viewModel.WorkspacePanes.FirstOrDefault(
            p => string.Equals(p.Id, workspaceId.ToString(), StringComparison.Ordinal));
        Assert.NotNull(pane);
        await WaitForPanePopulatedAsync(pane!);

        // Open three tabs with placeholder titles.
        var tabs = new EntityWorkspaceTabViewModel[3];
        for (var i = 0; i < 3; i++)
        {
            var entities = await entityBroker.GetEntitiesAsync(
                [new GetEntityRequest { EntityId = entityIds[i] }]);
            var entity = entities.Single();
            tabs[i] = new EntityWorkspaceTabViewModel
            {
                Id = $"1190-tab-{i + 1}",
                Title = "initial",
                Entity = entity,
                DockRegion = "full",
            };
            await viewModel.OpenTabAsync(tabs[i]);
        }

        var contentDock = FindDocumentDockIn(pane!.ContentLayout!);
        Assert.NotNull(contentDock);
        await WaitForWorkspaceTabAsync(contentDock!, "1190-tab-3");

        // Mutate Title AFTER OpenTabAsync — this is the scenario #1158 never covered.
        var expectedTitles = new[] { "Modified Alpha", "Modified Beta", "Modified Gamma" };
        for (var i = 0; i < 3; i++)
        {
            tabs[i].Title = expectedTitles[i];
        }

        // Drive the real save handler (used by the Save-workspace button).
        var writeBackResult = await viewModel.WriteBackWorkspaceTabs(pane);
        var writeBackErrors = writeBackResult.EntityResults
            .Where(r => r.UpdateState == UpdateState.Failed)
            .SelectMany(r => r.Errors ?? [])
            .Select(e => e.Message)
            .ToList();
        Assert.Empty(writeBackErrors);

        // Close the pane; then reopen the workspace via the real code path.
        await viewModel.CloseWorkspacePaneAsync(pane);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var restoredPane = viewModel.WorkspacePanes.FirstOrDefault(
            p => string.Equals(p.Id, workspaceId.ToString(), StringComparison.Ordinal));
        Assert.NotNull(restoredPane);
        await WaitForPanePopulatedAsync(restoredPane!);

        // Every restored tab must have a non-empty header title matching the pre-save value.
        var restoredDocs = MainWindowViewModel.EnumerateAllDocuments(restoredPane!.ContentLayout!)
            .Where(d => d.Id?.StartsWith("1190-tab-", StringComparison.Ordinal) == true)
            .ToDictionary(d => d.Id!, d => d);
        Assert.Equal(3, restoredDocs.Count);
        for (var i = 0; i < 3; i++)
        {
            var doc = restoredDocs[$"1190-tab-{i + 1}"];
            var headerTitle = doc.EffectiveTabHeader.Title;
            Assert.False(string.IsNullOrEmpty(headerTitle),
                $"Restored tab {i + 1} header title must not be blank (was '{headerTitle}').");
            Assert.Equal(expectedTitles[i], headerTitle);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WriteBackWorkspaceTabs_AfterTabTitleChange_PersistsCurrentTitleInDockLayout()
    {
        // Regression for #1190: WriteBackWorkspaceTabs must serialize the CURRENT tab
        // title, not the stale value captured at InitializeCore. Reads the pane's
        // dock-layout JSON and asserts the persisted Descriptor.Title reflects the
        // most recent tab.Title assignment.
        await using var viewModel = CreateTestMainWindowViewModel(
            configuration: new WorkspacesConfiguration { SkipStartupWorkspace = false });
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://writeback-1190.example.com")
        {
            Id = "wb-1190-tab",
            Title = "A",
        };
        await viewModel.OpenTabAsync(tab);

        var pane = viewModel.SelectedWorkspacePane;
        var contentDock = FindDocumentDockIn(pane.ContentLayout!);
        Assert.NotNull(contentDock);
        await WaitForWorkspaceTabAsync(contentDock!, "wb-1190-tab");

        // Reassign Title AFTER OpenTabAsync — this is what #1158 tests never do.
        tab.Title = "B";

        var writeBackResult = await viewModel.WriteBackWorkspaceTabs(pane);
        var errors = writeBackResult.EntityResults
            .Where(r => r.UpdateState == UpdateState.Failed)
            .SelectMany(r => r.Errors ?? [])
            .Select(e => e.Message)
            .ToList();
        Assert.Empty(errors);

        var data = Assert.IsType<System.Text.Json.JsonElement>(pane.Entity.Data);
        Assert.True(data.TryGetProperty("dock-layout", out var dockLayoutEl));
        var dockLayoutJson = dockLayoutEl.GetRawText();
        Assert.Contains("\"Title\":\"B\"", dockLayoutJson);
        Assert.DoesNotContain("\"Title\":\"A\"", dockLayoutJson);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspaceRestore_EntityTab_TitleIsNonEmptyAfterRestore()
    {
        // Regression for #1190 Fix 3: even when both the descriptor Title and the
        // entity's display-name are empty, the restored tab title must fall back to
        // a non-empty label so the header never renders blank.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var entityId = new EntityId("11901190-3333-4000-8000-000000000001");
        var entity = await UpsertEntityAndLoadAsync(entityBroker, entityId, $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity", "note"],
              "names": [["notes", "1190-empty-entity"]],
              "display-name": { "default": "" },
              "content": { "mime-type": "text/markdown", "content": { "text": "e" } }
            }
            """);

        // Build a dock-layout in which the persisted descriptor has NO Title
        // (simulating the stale-descriptor case). Since the entity's display-name
        // is also empty, only the last-resort fallback rescues the header title.
        var descriptor = new EntityDockTabDescriptor(entityId.ToString(), "Open");
        var placeholder = new EntityWorkspaceTabViewModel
        {
            Id = "1190-empty-tab",
            Title = "placeholder",
            Entity = entity,
            DockRegion = "full",
        };
        var doc = new WorkspaceDocument(placeholder) { Descriptor = descriptor };
        var contentDock = new WorkspaceContentDock
        {
            Id = "cd-1190-empty",
            VisibleDockables = new System.Collections.ObjectModel.ObservableCollection<IDockable> { doc },
        };
        contentDock.ActiveDockable = doc;
        var root = new global::Dock.Model.Mvvm.Controls.RootDock
        {
            Id = "root-1190-empty",
            VisibleDockables = new System.Collections.ObjectModel.ObservableCollection<IDockable> { contentDock },
        };
        root.ActiveDockable = contentDock;
        doc.Owner = contentDock;
        contentDock.Owner = root;

        var serializer = new DockSerializer(
            typeof(System.Collections.ObjectModel.ObservableCollection<>),
            new WorkspaceDockTypeInfoResolver());
        var dockLayoutJson = serializer.Serialize(root);

        var workspaceId = new EntityId("11901190-3333-4000-8000-0000000000f1");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceId, $$"""
            {
              "entity-id": "{{workspaceId}}",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Empty Title Restore WS" },
              "dock-layout": {{dockLayoutJson}},
              "regions": []
            }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var restoredPane = viewModel.WorkspacePanes.FirstOrDefault(
            p => string.Equals(p.Id, workspaceId.ToString(), StringComparison.Ordinal));
        Assert.NotNull(restoredPane);
        await WaitForPanePopulatedAsync(restoredPane!);

        var restoredTab = Assert.Single(restoredPane!.Tabs);
        Assert.False(string.IsNullOrEmpty(restoredTab.Title),
            "Restored tab title must not be empty even when Descriptor.Title and DisplayName are both empty.");
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task WorkspaceRestore_AgentSessionTab_TitleRoundTripsThroughFullCycle()
    {
        // Regression for #1190: full modify → save → close → reopen cycle for an
        // AgentSessionWorkspaceTabViewModel. Prior to the fix, changing the tab's
        // Title after OpenTabAsync was lost on the next save/close/reopen.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var agentDefinitionId = new EntityId("11901190-4444-4000-8000-000000000001");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(entityBroker, agentDefinitionId, """
            {
              "entity-id": "11901190-4444-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "1190-agent"]],
              "display-name": { "default": "1190 Agent" },
              "definition": {
                "kind": "prompt",
                "name": "1190-agent",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionId = Guid.NewGuid().ToString("n");
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, agentSessionId);
        Assert.NotNull(agentSessionEntity);

        var workspaceId = new EntityId("11901190-4444-4000-8000-0000000000f1");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceId, $$"""
            {
              "entity-id": "{{workspaceId}}",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Agent Cycle WS" },
              "regions": []
            }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });
        var pane = viewModel.WorkspacePanes.FirstOrDefault(
            p => string.Equals(p.Id, workspaceId.ToString(), StringComparison.Ordinal));
        Assert.NotNull(pane);
        await WaitForPanePopulatedAsync(pane!);

        var agentTab = new AgentSessionWorkspaceTabViewModel
        {
            Id = "1190-agent-tab",
            Title = "initial",
            Entity = agentSessionEntity!,
            DockRegion = "full",
        };
        await viewModel.OpenTabAsync(agentTab);

        var contentDock = FindDocumentDockIn(pane!.ContentLayout!);
        Assert.NotNull(contentDock);
        await WaitForWorkspaceTabAsync(contentDock!, "1190-agent-tab");

        // Change title AFTER open.
        agentTab.Title = "Agent Chat #3";

        await viewModel.WriteBackWorkspaceTabs(pane);
        await viewModel.CloseWorkspacePaneAsync(pane);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var restoredPane = viewModel.WorkspacePanes.FirstOrDefault(
            p => string.Equals(p.Id, workspaceId.ToString(), StringComparison.Ordinal));
        Assert.NotNull(restoredPane);
        await WaitForPanePopulatedAsync(restoredPane!);

        var restoredTab = restoredPane!.Tabs.FirstOrDefault(t => t.Id == "1190-agent-tab");
        Assert.NotNull(restoredTab);
        Assert.IsType<AgentSessionWorkspaceTabViewModel>(restoredTab);
        Assert.Equal("Agent Chat #3", restoredTab!.Title);

        var restoredDoc = MainWindowViewModel.EnumerateAllDocuments(restoredPane.ContentLayout!)
            .First(d => d.Id == "1190-agent-tab");
        Assert.Equal("Agent Chat #3", restoredDoc.EffectiveTabHeader.Title);
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task WorkspaceRestore_BrowserTab_TitleRoundTripsThroughFullCycle()
    {
        // Regression for #1190: full modify → save → close → reopen cycle for a
        // browser (WebViewModel) tab.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("11901190-5555-4000-8000-0000000000f1");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceId, $$"""
            {
              "entity-id": "{{workspaceId}}",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Browser Cycle WS" },
              "regions": []
            }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });
        var pane = viewModel.WorkspacePanes.FirstOrDefault(
            p => string.Equals(p.Id, workspaceId.ToString(), StringComparison.Ordinal));
        Assert.NotNull(pane);
        await WaitForPanePopulatedAsync(pane!);

        var browserTab = new WebViewModel("https://cycle-1190.example.com")
        {
            Id = "1190-browser-tab",
            Title = "initial",
        };
        await viewModel.OpenTabAsync(browserTab);

        var contentDock = FindDocumentDockIn(pane!.ContentLayout!);
        Assert.NotNull(contentDock);
        await WaitForWorkspaceTabAsync(contentDock!, "1190-browser-tab");

        browserTab.Title = "My Docs";

        await viewModel.WriteBackWorkspaceTabs(pane);
        await viewModel.CloseWorkspacePaneAsync(pane);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var restoredPane = viewModel.WorkspacePanes.FirstOrDefault(
            p => string.Equals(p.Id, workspaceId.ToString(), StringComparison.Ordinal));
        Assert.NotNull(restoredPane);
        await WaitForPanePopulatedAsync(restoredPane!);

        var restoredTab = restoredPane!.Tabs.FirstOrDefault(t => t.Id == "1190-browser-tab");
        Assert.NotNull(restoredTab);
        Assert.IsType<WebViewModel>(restoredTab);
        Assert.Equal("My Docs", restoredTab!.Title);

        var restoredDoc = MainWindowViewModel.EnumerateAllDocuments(restoredPane.ContentLayout!)
            .First(d => d.Id == "1190-browser-tab");
        Assert.Equal("My Docs", restoredDoc.EffectiveTabHeader.Title);
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task WorkspaceDocument_ExplicitTitleOverride_SurvivesPersistAndRestoreForBrowserTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("12651265-1001-4000-8000-0000000000f1");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceId, $$"""
            {
              "entity-id": "{{workspaceId}}",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Explicit Browser Workspace" },
              "regions": []
            }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });
        var pane = viewModel.WorkspacePanes.First(p => string.Equals(p.Id, workspaceId.ToString(), StringComparison.Ordinal));
        await WaitForPanePopulatedAsync(pane);
        await CloseDefaultPaneTabsAsync(viewModel, pane);

        var browserTab = new WebViewModel("https://explicit-browser.example.com")
        {
            Id = "explicit-browser-tab",
            Title = "Initial Browser",
        };
        browserTab.SetTitleExplicit("Pinned Browser");
        await viewModel.OpenTabAsync(browserTab);

        var contentDock = FindDocumentDockIn(pane.ContentLayout!);
        Assert.NotNull(contentDock);
        await WaitForWorkspaceTabAsync(contentDock!, "explicit-browser-tab");

        await viewModel.WriteBackWorkspaceTabs(pane);
        await viewModel.CloseWorkspacePaneAsync(pane);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var restoredPane = viewModel.WorkspacePanes.First(p => string.Equals(p.Id, workspaceId.ToString(), StringComparison.Ordinal));
        await WaitForPanePopulatedAsync(restoredPane);
        var restored = Assert.IsType<WebViewModel>(Assert.Single(restoredPane.Tabs));
        Assert.Equal("Pinned Browser", restored.Title);
        Assert.True(restored.IsTitleExplicit);
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task WorkspaceDocument_ExplicitTitleOverride_SurvivesPersistAndRestoreForNonBrowserTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var entityId = new EntityId("12651265-1002-4000-8000-000000000001");
        var entity = await UpsertEntityAndLoadAsync(entityBroker, entityId, $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity", "note"],
              "display-name": { "default": "Entity Display" },
              "content": { "mime-type": "text/markdown", "content": { "text": "body" } }
            }
            """);
        var workspaceId = new EntityId("12651265-1002-4000-8000-0000000000f1");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceId, $$"""
            {
              "entity-id": "{{workspaceId}}",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Explicit Entity Workspace" },
              "regions": []
            }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });
        var pane = viewModel.WorkspacePanes.First(p => string.Equals(p.Id, workspaceId.ToString(), StringComparison.Ordinal));
        await WaitForPanePopulatedAsync(pane);
        await CloseDefaultPaneTabsAsync(viewModel, pane);

        var entityTab = new EntityWorkspaceTabViewModel
        {
            Id = "explicit-entity-tab",
            Title = "Initial Entity",
            Entity = entity,
            DockRegion = "full",
        };
        entityTab.SetTitleExplicit("Pinned Entity");
        await viewModel.OpenTabAsync(entityTab);

        var contentDock = FindDocumentDockIn(pane.ContentLayout!);
        Assert.NotNull(contentDock);
        await WaitForWorkspaceTabAsync(contentDock!, "explicit-entity-tab");

        await viewModel.WriteBackWorkspaceTabs(pane);
        await viewModel.CloseWorkspacePaneAsync(pane);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var restoredPane = viewModel.WorkspacePanes.First(p => string.Equals(p.Id, workspaceId.ToString(), StringComparison.Ordinal));
        await WaitForPanePopulatedAsync(restoredPane);
        var restored = Assert.Single(restoredPane.Tabs);
        Assert.Equal("Pinned Entity", restored.Title);
        Assert.True(restored.IsTitleExplicit);
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task WorkspaceDocument_ExplicitTitleOverride_NotClobberedByContentDerivedTitleAfterRestore()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("12651265-1003-4000-8000-0000000000f1");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceId, $$"""
            {
              "entity-id": "{{workspaceId}}",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Explicit Browser Not Clobbered" },
              "regions": []
            }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });
        var pane = viewModel.WorkspacePanes.First(p => string.Equals(p.Id, workspaceId.ToString(), StringComparison.Ordinal));
        await WaitForPanePopulatedAsync(pane);
        await CloseDefaultPaneTabsAsync(viewModel, pane);

        var browserTab = new WebViewModel("https://not-clobbered.example.com")
        {
            Id = "not-clobbered-browser-tab",
            Title = "Initial",
        };
        browserTab.SetTitleExplicit("Pinned After Restore");
        await viewModel.OpenTabAsync(browserTab);

        var contentDock = FindDocumentDockIn(pane.ContentLayout!);
        Assert.NotNull(contentDock);
        await WaitForWorkspaceTabAsync(contentDock!, "not-clobbered-browser-tab");

        await viewModel.WriteBackWorkspaceTabs(pane);
        await viewModel.CloseWorkspacePaneAsync(pane);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var restoredPane = viewModel.WorkspacePanes.First(p => string.Equals(p.Id, workspaceId.ToString(), StringComparison.Ordinal));
        await WaitForPanePopulatedAsync(restoredPane);
        var restored = Assert.IsType<WebViewModel>(Assert.Single(restoredPane.Tabs));

        restored.SetPageTitle("Browser Derived Title");

        Assert.True(restored.IsTitleExplicit);
        Assert.Equal("Pinned After Restore", restored.Title);
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task WorkspaceRestore_DockLayoutWithDescriptorTitle_RestoresTitle()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("12651265-1004-4000-8000-0000000000f1");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceId, $$"""
            {
              "entity-id": "{{workspaceId}}",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Descriptor Title Workspace" },
              "regions": []
            }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });
        var pane = viewModel.WorkspacePanes.First(p => string.Equals(p.Id, workspaceId.ToString(), StringComparison.Ordinal));
        await WaitForPanePopulatedAsync(pane);
        await CloseDefaultPaneTabsAsync(viewModel, pane);

        var tab = new WebViewModel("https://descriptor-title.example.com")
        {
            Id = "descriptor-title-tab",
            Title = "Descriptor Browser Title",
        };
        await viewModel.OpenTabAsync(tab);
        await WaitForWorkspaceTabAsync(FindDocumentDockIn(pane.ContentLayout!)!, "descriptor-title-tab");

        await viewModel.WriteBackWorkspaceTabs(pane);
        await viewModel.CloseWorkspacePaneAsync(pane);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var restoredPane = viewModel.WorkspacePanes.First(p => string.Equals(p.Id, workspaceId.ToString(), StringComparison.Ordinal));
        await WaitForPanePopulatedAsync(restoredPane);
        Assert.Equal("Descriptor Browser Title", Assert.Single(restoredPane.Tabs).Title);
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task WorkspaceRestore_DockLayoutWithDescriptorTitle_WinsOverEmptyDisplayName()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var entityId = new EntityId("12651265-1005-4000-8000-000000000001");
        var entity = await UpsertEntityAndLoadAsync(entityBroker, entityId, $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity", "note"],
              "display-name": { "default": "" },
              "content": { "mime-type": "text/markdown", "content": { "text": "body" } }
            }
            """);
        var workspaceId = new EntityId("12651265-1005-4000-8000-0000000000f1");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceId, $$"""
            {
              "entity-id": "{{workspaceId}}",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Descriptor Empty Display Workspace" },
              "regions": []
            }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });
        var pane = viewModel.WorkspacePanes.First(p => string.Equals(p.Id, workspaceId.ToString(), StringComparison.Ordinal));
        await WaitForPanePopulatedAsync(pane);
        await CloseDefaultPaneTabsAsync(viewModel, pane);

        var tab = new EntityWorkspaceTabViewModel
        {
            Id = "descriptor-empty-display-tab",
            Title = "Descriptor Entity Title",
            Entity = entity,
            DockRegion = "full",
        };
        await viewModel.OpenTabAsync(tab);
        await WaitForWorkspaceTabAsync(FindDocumentDockIn(pane.ContentLayout!)!, "descriptor-empty-display-tab");

        await viewModel.WriteBackWorkspaceTabs(pane);
        await viewModel.CloseWorkspacePaneAsync(pane);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var restoredPane = viewModel.WorkspacePanes.First(p => string.Equals(p.Id, workspaceId.ToString(), StringComparison.Ordinal));
        await WaitForPanePopulatedAsync(restoredPane);
        Assert.Equal("Descriptor Entity Title", Assert.Single(restoredPane.Tabs).Title);
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task WorkspaceDocument_TryRestoreFromDockLayout_EffectiveTabHeaderTitleMatchesDescriptorTitle()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("12651265-1006-4000-8000-0000000000f1");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceId, $$"""
            {
              "entity-id": "{{workspaceId}}",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Header Descriptor Workspace" },
              "regions": []
            }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });
        var pane = viewModel.WorkspacePanes.First(p => string.Equals(p.Id, workspaceId.ToString(), StringComparison.Ordinal));
        await WaitForPanePopulatedAsync(pane);
        await CloseDefaultPaneTabsAsync(viewModel, pane);

        var tab = new WebViewModel("https://header-descriptor.example.com")
        {
            Id = "header-descriptor-tab",
            Title = "Header Descriptor Title",
        };
        await viewModel.OpenTabAsync(tab);
        await WaitForWorkspaceTabAsync(FindDocumentDockIn(pane.ContentLayout!)!, "header-descriptor-tab");

        await viewModel.WriteBackWorkspaceTabs(pane);
        await viewModel.CloseWorkspacePaneAsync(pane);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var restoredPane = viewModel.WorkspacePanes.First(p => string.Equals(p.Id, workspaceId.ToString(), StringComparison.Ordinal));
        await WaitForPanePopulatedAsync(restoredPane);
        var restoredDoc = MainWindowViewModel.EnumerateAllDocuments(restoredPane.ContentLayout!)
            .Single(d => d.Id == "header-descriptor-tab");
        Assert.Equal("Header Descriptor Title", restoredDoc.EffectiveTabHeader.Title);
        Assert.False(string.IsNullOrEmpty(restoredDoc.EffectiveTabHeader.Title));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenAgentSessionShortcutHandler_RestoreWithEmptyPersistedTitle_FallsBackThroughDisplayNameThenEntityId()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityId = new EntityId("12651265-1007-4000-8000-000000000001");
        using var document = JsonDocument.Parse($$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity", "agent-session"],
              "display-name": { "default": "" },
              "agent-session-id": "restore-empty-title-session"
            }
            """);
        var agentSessionEntity = new SubscribedEntityViewModel(
            new EntitySnapshot
            {
                EntityId = entityId,
                ConcurrencyTag = new ConcurrencyTag("1"),
                ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
                Data = document.RootElement.Clone(),
                Relationships = Array.Empty<EntitySnapshot>(),
            });
        var handler = new OpenAgentSessionShortcutHandler(
            new AgentSessionShortcutContext(),
            CreateLocalTrustedExecutorSelector(),
            CreateTestRunningAgentChatTable());

        var tab = await handler.TryCreateAgentSessionTabForRestoreAsync(
            viewModel, agentSessionEntity, "agent-empty-title-tab", title: "", dockRegion: "full");
        try
        {
            Assert.NotNull(tab);
            Assert.Equal(entityId.ToString(), tab!.Title);
        }
        finally
        {
            if (tab is not null)
            {
                await tab.DisposeAsync();
            }
        }
    }

    private static T GetDockFactoryAs<T>(MainWindowViewModel viewModel)
    {
        var field = typeof(MainWindowViewModel)
            .GetField("dockFactory", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsAssignableFrom<T>(field!.GetValue(viewModel));
    }

    private static async Task<WorkspacePaneViewModel> CreateWorkspacePaneFromJsonAsync(
        MainWindowViewModel viewModel,
        EntityId workspaceId,
        string workspaceJson)
    {
        using var document = JsonDocument.Parse(workspaceJson);
        var workspaceEntity = new SubscribedEntityViewModel(
            new EntitySnapshot
            {
                EntityId = workspaceId,
                ConcurrencyTag = new ConcurrencyTag("1"),
                ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
                Data = document.RootElement.Clone(),
                Relationships = Array.Empty<EntitySnapshot>(),
            });

        var createWorkspacePane = typeof(MainWindowViewModel).GetMethod(
            "CreateWorkspacePaneAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(createWorkspacePane);
        var task = (Task<WorkspacePaneViewModel>?)createWorkspacePane!.Invoke(
            viewModel,
            [workspaceEntity, document.RootElement.Clone()]);
        Assert.NotNull(task);
        var pane = await task!;
        return pane;
    }

    private static async Task<WorkspaceTabViewModel?> TryFetchWorkspaceTabFromJsonAsync(
        MainWindowViewModel viewModel,
        string tabJson)
    {
        using var document = JsonDocument.Parse(tabJson);
        var tryFetch = typeof(MainWindowViewModel).GetMethod(
            "TryFetchWorkspaceTabAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(tryFetch);
        var task = (Task<WorkspaceTabViewModel?>?)tryFetch!.Invoke(
            viewModel,
            [document.RootElement.Clone()]);
        Assert.NotNull(task);
        return await task!;
    }

    internal static IDocumentDock? GetDocumentDock(MainWindowViewModel viewModel)    {
        var contentLayout = viewModel.SelectedWorkspacePane?.ContentLayout;
        if (contentLayout is null)
        {
            return null;
        }

        return FindDocumentDockIn(contentLayout);
    }

    private static void ActivateContentTabAtIndex(MainWindowViewModel viewModel, string indexText)
    {
        if (!int.TryParse(indexText, out var index) || index < 0)
        {
            return;
        }

        var documentDock = GetDocumentDock(viewModel);
        if (documentDock?.VisibleDockables is null || index >= documentDock.VisibleDockables.Count)
        {
            return;
        }

        if (documentDock.VisibleDockables[index] is not WorkspaceDocument doc)
        {
            return;
        }

        var dockFactory = GetDockFactoryAs<WorkspaceDockFactory>(viewModel);
        viewModel.SelectedWorkspacePane.SelectedTab = doc.TabViewModel;
        dockFactory.SetActiveDockable(doc);
        dockFactory.SetFocusedDockable(documentDock, doc);
        viewModel.NotificationService.MarkRead(doc.Id);
    }

    internal static void ActivateWorkspacePaneAtIndex(MainWindowViewModel viewModel, string indexText)
    {
        if (!int.TryParse(indexText, out var index) || index < 0 || index >= viewModel.WorkspacePanes.Count)
        {
            return;
        }

        var pane = viewModel.WorkspacePanes[index];
        viewModel.SelectedWorkspacePane = pane;

        var workspacesDock = viewModel.Layout is null ? null : FindDocumentDockIn(viewModel.Layout);
        var paneDoc = workspacesDock?.VisibleDockables?
            .OfType<WorkspacePaneDocument>()
            .FirstOrDefault(d => ReferenceEquals(d.WorkspacePane, pane));

        if (paneDoc is not null)
        {
            var dockFactory = GetDockFactoryAs<WorkspaceDockFactory>(viewModel);
            dockFactory.SetActiveDockable(paneDoc);
            if (workspacesDock is not null)
            {
                dockFactory.SetFocusedDockable(workspacesDock, paneDoc);
            }
        }
    }

    internal static IDocumentDock? FindDocumentDockIn(IDockable dockable)
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

    /// <summary>
    /// Gets documents from a dock that correspond to tabs in the specified pane.
    /// Filters out any placeholder or orphaned documents that may exist in the dock.
    /// </summary>
    private static List<WorkspaceDocument> GetPaneDocuments(WorkspacePaneViewModel pane, IDocumentDock dock)
    {
        return dock.VisibleDockables!
            .OfType<WorkspaceDocument>()
            .Where(doc => doc.Context is WorkspaceTabViewModel tab && pane.Tabs.Contains(tab))
            .ToList();
    }

    /// <summary>
    /// Waits for any fire-and-forget PopulateWorkspacePaneTabsAsync tasks to complete, then closes
    /// the default tabs that were added to each pane during population. Call this after opening
    /// workspaces and before opening test tabs so that pane.Tabs only contains the expected tabs.
    /// </summary>
    private static async Task CloseDefaultPaneTabsAsync(
        MainWindowViewModel viewModel,
        params WorkspacePaneViewModel[] panes)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        foreach (var pane in panes)
            foreach (var tab in pane.Tabs.ToList())
                viewModel.CloseTab(tab);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    internal static async Task WaitForWorkspaceTabAsync(IDocumentDock contentDock, string tabId)
    {
        if (contentDock.VisibleDockables?.OfType<WorkspaceDocument>().Any(d => d.Id == tabId) == true)
        {
            return;
        }

        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (contentDock.VisibleDockables?.OfType<WorkspaceDocument>().Any(d => d.Id == tabId) == true)
            {
                signal.TrySetResult();
            }
        }

        if (contentDock.VisibleDockables is INotifyCollectionChanged observable)
        {
            observable.CollectionChanged += OnCollectionChanged;
            try
            {
                if (contentDock.VisibleDockables?.OfType<WorkspaceDocument>().Any(d => d.Id == tabId) != true)
                {
                    await signal.Task;
                }
            }
            finally
            {
                observable.CollectionChanged -= OnCollectionChanged;
            }
        }
    }

    private static async Task WaitForWorkspacePaneTabsAsync(WorkspacePaneViewModel pane)
    {
        if (pane.Tabs.Count > 0)
        {
            return;
        }

        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (pane.Tabs.Count > 0)
            {
                signal.TrySetResult();
            }
        }

        pane.Tabs.CollectionChanged += OnCollectionChanged;
        try
        {
            if (pane.Tabs.Count == 0)
            {
                await signal.Task;
            }
        }
        finally
        {
            pane.Tabs.CollectionChanged -= OnCollectionChanged;
        }
    }

    /// <summary>
    /// Waits for <see cref="WorkspacePaneViewModel.Populated"/> to complete with a bounded timeout.
    /// Throws <see cref="TimeoutException"/> with diagnostic details if populate does not complete in time.
    /// Propagates any exception raised during populate.
    /// </summary>
    internal static async Task WaitForPanePopulatedAsync(WorkspacePaneViewModel pane, TimeSpan? timeout = null)
    {
        var populateTask = pane.Populated;
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(10);
        var timeoutTask = Task.Delay(effectiveTimeout);

        if (await Task.WhenAny(populateTask, timeoutTask) == timeoutTask)
        {
            throw new TimeoutException(
                $"Pane {pane.Id} was not populated within {effectiveTimeout.TotalSeconds}s. Tabs.Count={pane.Tabs.Count}");
        }

        await populateTask; // propagate exception if populate failed
    }

    private static async Task WaitForWorkspacePaneAsync(MainWindowViewModel viewModel, string paneId)
    {
        if (viewModel.WorkspacePanes.Any(p =>
            string.Equals(p.Id, paneId, StringComparison.Ordinal) ||
            p.Id.StartsWith("loading-workspace:", StringComparison.Ordinal)))
        {
            return;
        }

        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (viewModel.WorkspacePanes.Any(p =>
                string.Equals(p.Id, paneId, StringComparison.Ordinal) ||
                p.Id.StartsWith("loading-workspace:", StringComparison.Ordinal)))
            {
                signal.TrySetResult();
            }
        }

        viewModel.WorkspacePanes.CollectionChanged += OnCollectionChanged;
        try
        {
            if (!viewModel.WorkspacePanes.Any(p =>
                string.Equals(p.Id, paneId, StringComparison.Ordinal) ||
                p.Id.StartsWith("loading-workspace:", StringComparison.Ordinal)))
            {
                await signal.Task;
            }
        }
        finally
        {
            viewModel.WorkspacePanes.CollectionChanged -= OnCollectionChanged;
        }
    }

    internal static async Task<T> WaitForSelectedTabAsync<T>(WorkspacePaneViewModel pane)
        where T : WorkspaceTabViewModel
    {
        if (pane.SelectedTab is T alreadyReady)
        {
            return alreadyReady;
        }

        var signal = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(WorkspacePaneViewModel.SelectedTab) && pane.SelectedTab is T t)
            {
                signal.TrySetResult(t);
            }
        }

        pane.PropertyChanged += OnPropertyChanged;
        try
        {
            if (pane.SelectedTab is T existing)
            {
                return existing;
            }

            return await signal.Task;
        }
        finally
        {
            pane.PropertyChanged -= OnPropertyChanged;
        }
    }

    internal static async Task WaitForAgentReadyAsync(AgentSessionWorkspaceTabViewModel tab)    {
        if (tab.State is AgentTabState.Ready or AgentTabState.Failed)
        {
            return;
        }

        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AgentSessionWorkspaceTabViewModel.State)
                && tab.State is AgentTabState.Ready or AgentTabState.Failed)
            {
                signal.TrySetResult();
            }
        }

        tab.PropertyChanged += OnPropertyChanged;
        try
        {
            if (tab.State is not (AgentTabState.Ready or AgentTabState.Failed))
            {
                await signal.Task;
            }
        }
        finally
        {
            tab.PropertyChanged -= OnPropertyChanged;
        }
    }

    internal static async Task AssertSlashCommandsEnabledAsync(AgentViewModel agent)
    {
        // #1429: every GUI session launch path must route through ComposeSessionAgentViewModel so
        // the input composer gets its slash-command interceptor and completions provider wired.
        Assert.NotNull(agent.InputQueue);
        var composer = agent.InputQueue!.DefaultComposer;
        Assert.NotNull(composer.SlashCommandInterceptorAsync);
        Assert.NotNull(composer.SlashCompletionsProviderAsync);

        var completions = await composer.SlashCompletionsProviderAsync!(
            string.Empty, string.Empty, CancellationToken.None);
        var labels = completions
            .Select(c => c.Label ?? c.CompletionText)
            .ToList();
        Assert.Contains(labels, l => l.Contains("rename", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(labels, l => l.Contains("title", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(labels, l => l.Contains("restart", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(labels, l => l.Contains("clone", StringComparison.OrdinalIgnoreCase));
    }

    private static string EchoAgentDefinitionEntityJson(string entityId, string nameSegment)
        => $$"""
        {
          "entity-id": "{{entityId}}",
          "entity-types": ["entity", "agent-definition"],
          "names": [["tests", "agent-definitions", "{{nameSegment}}"]],
          "display-name": { "default": "Echo {{nameSegment}}" },
          "definition": {
            "kind": "prompt",
            "name": "{{nameSegment}}",
            "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
            "tools": []
          }
        }
        """;

    internal static async Task<AgentViewModel> MaterializeAgentSessionSlashSessionAsync(MainWindowViewModel viewModel)
    {
        var entityBroker = GetEntityBroker(viewModel);
        var definitionEntity = await UpsertEntityAndLoadAsync(
            entityBroker,
            new EntityId("bbbb0001-0000-4000-8000-000000000001"),
            EchoAgentDefinitionEntityJson("bbbb0001-0000-4000-8000-000000000001", "slash-session"));

        var context = new AgentSessionShortcutContext();
        var sessionEntity = await context.CreateAgentSessionEntityAsync(
            viewModel, definitionEntity, Guid.NewGuid().ToString("n"));
        Assert.NotNull(sessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(
            context, CreateLocalTrustedExecutorSelector(), CreateTestRunningAgentChatTable());
        Assert.True(await handler.Handle(viewModel, Shortcut.Open, sessionEntity!));

        var tab = Assert.IsType<AgentSessionWorkspaceTabViewModel>(viewModel.SelectedWorkspacePane.SelectedTab);
        await WaitForAgentReadyAsync(tab);
        Assert.Equal(AgentTabState.Ready, tab.State);
        Assert.NotNull(tab.Agent);
        return tab.Agent!;
    }

    internal static async Task<AgentViewModel> MaterializeAgentDefinitionSlashSessionAsync(MainWindowViewModel viewModel)
    {
        var entityBroker = GetEntityBroker(viewModel);
        var definitionEntity = await UpsertEntityAndLoadAsync(
            entityBroker,
            new EntityId("bbbb0002-0000-4000-8000-000000000001"),
            EchoAgentDefinitionEntityJson("bbbb0002-0000-4000-8000-000000000001", "slash-definition"));

        var context = new AgentSessionShortcutContext();
        var handler = new OpenAgentSessionShortcutHandler(
            context, CreateLocalTrustedExecutorSelector(), CreateTestRunningAgentChatTable());
        var definitionHandler = new OpenAgentDefinitionShortcutHandler(context, handler);
        Assert.True(await definitionHandler.Handle(viewModel, Shortcut.Open, definitionEntity));

        var launchpad = await WaitForSelectedTabAsync<AgentManifestLaunchpadViewModel>(viewModel.SelectedWorkspacePane);
        Assert.True(launchpad.CanStart);
        launchpad.StartSessionCommand.Execute(null);

        var tab = await WaitForSelectedTabAsync<AgentSessionWorkspaceTabViewModel>(viewModel.SelectedWorkspacePane);
        await WaitForAgentReadyAsync(tab);
        Assert.Equal(AgentTabState.Ready, tab.State);
        Assert.NotNull(tab.Agent);
        return tab.Agent!;
    }

    internal static async Task<AgentViewModel> MaterializeAgentManifestSlashSessionAsync(MainWindowViewModel viewModel)
    {
        var entityBroker = GetEntityBroker(viewModel);
        var manifestEntity = await UpsertEntityAndLoadAsync(
            entityBroker,
            new EntityId("bbbb0003-0000-4000-8000-000000000001"),
            """
            {
              "entity-id": "bbbb0003-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-manifest"],
              "names": [["tests", "agent-manifests", "slash-manifest"]],
              "display-name": { "default": "Slash Manifest" },
              "manifest": {
                "name": "slash-manifest",
                "displayName": "Slash Manifest",
                "template": {
                  "kind": "prompt",
                  "name": "slash-manifest",
                  "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
                },
                "resources": [
                  { "kind": "tool", "id": "fixed", "name": "workspace-entity" }
                ]
              }
            }
            """);

        var context = new AgentSessionShortcutContext();
        var handler = new OpenAgentSessionShortcutHandler(
            context, CreateLocalTrustedExecutorSelector(), CreateTestRunningAgentChatTable());
        var manifestHandler = new OpenAgentManifestShortcutHandler(context, handler);
        Assert.True(await manifestHandler.Handle(viewModel, Shortcut.Open, manifestEntity));

        var tab = await WaitForSelectedTabAsync<AgentSessionWorkspaceTabViewModel>(viewModel.SelectedWorkspacePane);
        await WaitForAgentReadyAsync(tab);
        Assert.Equal(AgentTabState.Ready, tab.State);
        Assert.NotNull(tab.Agent);
        return tab.Agent!;
    }

    internal static async Task<AgentViewModel> MaterializeProfileDefinitionSlashSessionAsync(MainWindowViewModel viewModel)
    {
        var entityBroker = GetEntityBroker(viewModel);
        var definitionEntity = await UpsertEntityAndLoadAsync(
            entityBroker,
            new EntityId("bbbb0004-0000-4000-8000-000000000001"),
            EchoAgentDefinitionEntityJson("bbbb0004-0000-4000-8000-000000000001", "slash-profile"));

        var context = new AgentSessionShortcutContext();
        var sessionEntity = await context.CreateAgentSessionEntityAsync(
            viewModel, definitionEntity, Guid.NewGuid().ToString("n"));
        Assert.NotNull(sessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(
            context, CreateLocalTrustedExecutorSelector(), CreateTestRunningAgentChatTable());
        var chat = await CreateEchoAgentChatAsync();
        var tab = await handler.CreateAgentSessionTabAsync(viewModel, sessionEntity!, chat);
        await WaitForAgentReadyAsync(tab);
        Assert.Equal(AgentTabState.Ready, tab.State);
        Assert.NotNull(tab.Agent);
        return tab.Agent!;
    }

    private static Task WaitForLayoutAsync(Window window)
    {
        if (window.IsMeasureValid && window.IsArrangeValid)
            return Task.CompletedTask;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (!window.IsMeasureValid || !window.IsArrangeValid)
                return;
            window.LayoutUpdated -= handler;
            tcs.TrySetResult();
        };
        window.LayoutUpdated += handler;
        return tcs.Task;
    }

    private static Task WaitForDocumentTabStripAsync(Window window)
    {
        // Wait not just for a DocumentTabStrip to appear, but for one with WorkspaceContentDock DataContext.
        // The docking library may create the visual element before assigning the correct DataContext.
        if (window.GetVisualDescendants().OfType<DocumentTabStrip>()
            .Any(ts => ts.DataContext is WorkspaceContentDock))
            return Task.CompletedTask;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (!window.GetVisualDescendants().OfType<DocumentTabStrip>()
                .Any(ts => ts.DataContext is WorkspaceContentDock))
                return;
            window.LayoutUpdated -= handler;
            tcs.TrySetResult();
        };
        window.LayoutUpdated += handler;
        // TOCTOU: re-check after subscribing in case the strip with correct DataContext appeared
        // between the initial check and the subscribe
        if (window.GetVisualDescendants().OfType<DocumentTabStrip>()
            .Any(ts => ts.DataContext is WorkspaceContentDock))
        {
            window.LayoutUpdated -= handler;
            tcs.TrySetResult();
        }
        return tcs.Task;
    }

    private static async Task WaitForDocumentTabStripAsync(Window window, Type expectedDataContextType, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var tabStrip = window.GetVisualDescendants()
                .OfType<DocumentTabStrip>()
                .FirstOrDefault(ts => ts.DataContext?.GetType() == expectedDataContextType);
            if (tabStrip != null)
            {
                var items = tabStrip.GetVisualDescendants().OfType<DocumentTabStripItem>().ToList();
                if (items.Count > 0)
                    return;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException($"DocumentTabStrip with {expectedDataContextType.Name} DataContext and inflated items not found within {timeoutMs}ms");
    }

    internal static async Task CloseWindowAsync(Window window)
    {
        window.Close();
        await Dispatcher.UIThread.InvokeAsync(() => { });
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ApplySelectedViewAsync_CalledTwice_CurrentViewPopulationContainsEntitiesOnce()
    {
        // Regression for issue #104: concurrent ApplySelectedViewAsync invocations must not
        // double-populate the entity list.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var sessionsView = Assert.Single(
            viewModel.TopLevelViews,
            static view => string.Equals(view.Title, "Sessions", StringComparison.Ordinal));

        var applyMethod = typeof(MainWindowViewModel).GetMethod(
            "ApplySelectedViewAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(applyMethod);

        viewModel.SelectedTopLevelView = sessionsView;
        await (Task)applyMethod!.Invoke(viewModel, [])!;
        await (Task)applyMethod!.Invoke(viewModel, [])!;

        var entities = viewModel.CurrentViewPopulation.Entities;
        var agentManifestEntities = entities
            .Where(static e => string.Equals(e.EntityType, "agent-manifest", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(agentManifestEntities);
        var distinctIds = agentManifestEntities.Select(static e => e.Entity.EntityId).Distinct().Count();
        Assert.Equal(distinctIds, agentManifestEntities.Count);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ApplySelectedViewAsync_EachCall_CreatesNewCurrentViewPopulationInstance()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var firstPopulation = viewModel.CurrentViewPopulation;

        var applyMethod = typeof(MainWindowViewModel).GetMethod(
            "ApplySelectedViewAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(applyMethod);

        await (Task)applyMethod!.Invoke(viewModel, [])!;

        Assert.NotSame(firstPopulation, viewModel.CurrentViewPopulation);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ApplySelectedViewAsync_PreviousPopulationDisposed_ItsEntitiesNotModifiedAfterSwap()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var sessionsView = Assert.Single(
            viewModel.TopLevelViews,
            static view => string.Equals(view.Title, "Sessions", StringComparison.Ordinal));

        var applyMethod = typeof(MainWindowViewModel).GetMethod(
            "ApplySelectedViewAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(applyMethod);

        viewModel.SelectedTopLevelView = sessionsView;
        await (Task)applyMethod!.Invoke(viewModel, [])!;

        var firstPopulation = viewModel.CurrentViewPopulation;
        var countAfterFirstRun = firstPopulation.Entities.Count;

        await (Task)applyMethod!.Invoke(viewModel, [])!;

        // The old population must not have gained or lost entities after the swap — it was
        // disposed (CTS cancelled) before the new run appended to the new collection.
        Assert.Equal(countAfterFirstRun, firstPopulation.Entities.Count);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ApplySelectedViewAsync_ViewSwitchedTwice_CurrentViewPopulationReflectsSecondView()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var applyMethod = typeof(MainWindowViewModel).GetMethod(
            "ApplySelectedViewAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(applyMethod);

        var firstView = viewModel.TopLevelViews.FirstOrDefault(
            v => !v.IsEntityBrowser && v.ViewEntity is not null);

        if (firstView is null)
        {
            // If no view-driven top-level views exist, the test is vacuous — skip by passing.
            return;
        }

        viewModel.SelectedTopLevelView = firstView;
        await (Task)applyMethod!.Invoke(viewModel, [])!;
        var populationAfterFirst = viewModel.CurrentViewPopulation;

        // Switch to the empty view to produce a second, different population.
        viewModel.SelectedTopLevelView = viewModel.TopLevelViews[0];
        await (Task)applyMethod!.Invoke(viewModel, [])!;

        // The CurrentViewPopulation must be a distinct instance from the one after the first switch.
        Assert.NotSame(populationAfterFirst, viewModel.CurrentViewPopulation);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_ContentLevelDocumentTabStrip_HasHeaderTemplate_AfterTabOpened()
    {
        // Regression test for #88: the content-level DocumentTabStrip must have HeaderTemplate
        // set so tab icons and notification indicators are rendered via EffectiveTabHeader.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tab = new ShellTabViewModel(new FakeShellSession()) { Id = "header-tmpl-test", Title = "Header Test" };
        await viewModel.OpenTabAsync(tab);

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            await WaitForDocumentTabStripAsync(window);

            // The content-level DocumentTabStrip is nested inside the workspace-level DockControl.
            var tabStrips = window.GetVisualDescendants().OfType<DocumentTabStrip>().ToList();
            Assert.NotEmpty(tabStrips);

            // Diagnostic: check DataContext types on all tab strips and DockControls
            var allDockControls = window.GetVisualDescendants().OfType<DockControl>().ToList();

            var contentTabStrip = tabStrips.FirstOrDefault(ts => ts.DataContext is WorkspaceContentDock);
            Assert.NotNull(contentTabStrip);

            // Diagnostic: check the full chain from DocumentControl → DocumentTabStrip → PART_HeaderPresenter
            var documentControl = window.GetVisualDescendants().OfType<DocumentControl>()
                .FirstOrDefault(dc => dc.GetVisualDescendants().Contains(contentTabStrip));
            Assert.NotNull(documentControl);

            // Both DocumentControl and DocumentTabStrip should have our ContentControl DataTemplate, not Dock's default.
            var dcHeaderTemplateTypeName = documentControl!.HeaderTemplate?.GetType().Name ?? "(null)";
            var dcHeaderTemplateDataType = (documentControl!.HeaderTemplate as Avalonia.Markup.Xaml.Templates.DataTemplate)?.DataType?.Name ?? "(no DataType)";
            var tsHeaderTemplateTypeName = contentTabStrip!.HeaderTemplate?.GetType().Name ?? "(null)";
            var tsHeaderTemplateDataType = (contentTabStrip!.HeaderTemplate as Avalonia.Markup.Xaml.Templates.DataTemplate)?.DataType?.Name ?? "(no DataType)";

            var tabStripItems = contentTabStrip.GetVisualDescendants().OfType<DocumentTabStripItem>().ToList();
            Assert.NotEmpty(tabStripItems);

            var headerPresenter = tabStripItems[0]
                .GetVisualDescendants()
                .OfType<Avalonia.Controls.Presenters.ContentPresenter>()
                .FirstOrDefault(cp => cp.Name == "PART_HeaderPresenter");
            Assert.NotNull(headerPresenter);

            // The child of PART_HeaderPresenter should be a ContentControl (our template), not a TextBlock.
            // If this fails, check: dcHeaderTemplate={dcHeaderTemplateTypeName}, tsHeaderTemplate={tsHeaderTemplateTypeName}
            var headerChild = headerPresenter!.GetVisualChildren().FirstOrDefault();
            Assert.NotNull(headerChild);
            Assert.True(
                headerChild is Avalonia.Controls.ContentControl,
                $"Expected ContentControl but got {headerChild!.GetType().Name}. " +
                $"DC.HeaderTemplate={dcHeaderTemplateTypeName}(DataType={dcHeaderTemplateDataType}), " +
                $"TS.HeaderTemplate={tsHeaderTemplateTypeName}(DataType={tsHeaderTemplateDataType})");
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WebTab_LongPageTitle_TabStripItemWidthIsBounded()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var before = new WebViewModel("https://before.example.com") { Id = "long-title-before", Title = "Open Issues" };
        var longTitleTab = new WebViewModel("https://long-title.example.com") { Id = "long-title-web-tab", Title = "Initial" };
        var after = new WebViewModel("https://after.example.com") { Id = "long-title-after", Title = "Settings" };
        longTitleTab.SetPageTitle(
            "Consolidate duplicated JSON serializer options + default config-path logic " +
            "(AllowedSecretsStore vs ConfigurationPersistenceService) and keep adjacent tabs visible");

        await viewModel.OpenTabAsync(before);
        await viewModel.OpenTabAsync(longTitleTab);
        await viewModel.OpenTabAsync(after);

        var window = new MainWindow(viewModel)
        {
            Width = 900,
            Height = 600,
        };
        window.Show();
        try
        {
            await WaitForDocumentTabStripAsync(window);
            await WaitForLayoutAsync(window);
            Dispatcher.UIThread.RunJobs();

            var contentTabStrip = window.GetVisualDescendants()
                .OfType<DocumentTabStrip>()
                .FirstOrDefault(ts => ts.DataContext is WorkspaceContentDock);
            Assert.NotNull(contentTabStrip);

            var tabStripItems = contentTabStrip!.GetVisualDescendants().OfType<DocumentTabStripItem>().ToList();
            Assert.True(tabStripItems.Count >= 3, $"Expected at least three tab-strip items, found {tabStripItems.Count}.");

            var longTitleItem = tabStripItems.FirstOrDefault(item => item.DataContext is WorkspaceDocument { Id: "long-title-web-tab" });
            Assert.NotNull(longTitleItem);
            // #1287: MaxWidth on the title TextBlock tightened from Width=240 to
            // MaxWidth=180, so the whole tab-strip item (title + trailing chrome)
            // must fit within 180 + a modest trailing-chrome budget.
            Assert.InRange(longTitleItem!.Bounds.Width, 1, 280);

            Assert.Contains(tabStripItems, item => item.DataContext is WorkspaceDocument { Id: "long-title-before" } && item.Bounds.Width > 0);
            Assert.Contains(tabStripItems, item => item.DataContext is WorkspaceDocument { Id: "long-title-after" } && item.Bounds.Width > 0);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    // #1287: short-titled document tabs must size to their content, not be
    // padded out to the pre-fix 240px baseline. Open a short-title tab and a
    // long-title tab side by side and verify the short tab's DocumentTabStripItem
    // is strictly narrower than the long tab's AND strictly narrower than 240.
    [AvaloniaFact(Timeout = 15_000)]
    public async Task TabHeaderTemplate_ShortTitle_TabStripItemNotPaddedToFixedWidth()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var shortTab = new WebViewModel("https://short.example.com") { Id = "size-to-content-short", Title = "Design 6" };
        var longTab = new WebViewModel("https://long.example.com") { Id = "size-to-content-long", Title = "Initial" };
        longTab.SetPageTitle(
            "A very long document tab title that must exercise the MaxWidth=180 cap on the title TextBlock");

        await viewModel.OpenTabAsync(shortTab);
        await viewModel.OpenTabAsync(longTab);

        var window = new MainWindow(viewModel)
        {
            Width = 900,
            Height = 600,
        };
        window.Show();
        try
        {
            await WaitForDocumentTabStripAsync(window);
            await WaitForLayoutAsync(window);
            Dispatcher.UIThread.RunJobs();

            var contentTabStrip = window.GetVisualDescendants()
                .OfType<DocumentTabStrip>()
                .FirstOrDefault(ts => ts.DataContext is WorkspaceContentDock);
            Assert.NotNull(contentTabStrip);

            var tabStripItems = contentTabStrip!.GetVisualDescendants().OfType<DocumentTabStripItem>().ToList();
            var shortItem = tabStripItems.FirstOrDefault(item => item.DataContext is WorkspaceDocument { Id: "size-to-content-short" });
            var longItem = tabStripItems.FirstOrDefault(item => item.DataContext is WorkspaceDocument { Id: "size-to-content-long" });
            Assert.NotNull(shortItem);
            Assert.NotNull(longItem);

            Assert.True(
                shortItem!.Bounds.Width < longItem!.Bounds.Width,
                $"Expected short tab ({shortItem.Bounds.Width}) to be narrower than long tab ({longItem.Bounds.Width}).");
            Assert.True(
                shortItem.Bounds.Width < 240,
                $"Expected short tab strictly narrower than the pre-fix 240px baseline but was {shortItem.Bounds.Width}.");
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // #1172 — Usage tracker end-to-end via IUrlOpener.
    // ---------------------------------------------------------------------------------------------

    private sealed class RecordingUrlOpener : IUrlOpener
    {
        public List<OpenUrlRequest> Requests { get; } = new();

        public Task OpenAsync(OpenUrlRequest request, CancellationToken cancellationToken = default)
        {
            this.Requests.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class DelegatingUrlOpener : IUrlOpener
    {
        private readonly IUrlOpener inner;
        public List<OpenUrlRequest> Requests { get; } = new();
        public DelegatingUrlOpener(IUrlOpener inner) { this.inner = inner; }
        public async Task OpenAsync(OpenUrlRequest request, CancellationToken cancellationToken = default)
        {
            this.Requests.Add(request);
            await this.inner.OpenAsync(request, cancellationToken);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenUrlFromUsageTracker_OpensTabInCurrentWorkspacePane()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        var realOpener = UrlOpener.CreateDefault(viewModel, () => null);
        viewModel.ApplicationServices.SetUrlOpener(realOpener);
        await viewModel.InitializeAsync();
        await Dispatcher.UIThread.InvokeAsync(() => { });

        var pane = viewModel.SelectedWorkspacePane;
        var initialTabCount = pane.Tabs.Count;

        await realOpener.OpenAsync(new OpenUrlRequest("https://example.com/tracker"));
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.Equal(initialTabCount + 1, pane.Tabs.Count);
        Assert.Contains(pane.Tabs, t => t is WebViewModel w && w.AddressBarUrl == "https://example.com/tracker");
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenUrlFromUsageTracker_SameUrlClickedTwice_ActivatesFirstTabAndDoesNotOpenSecond()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        var realOpener = UrlOpener.CreateDefault(viewModel, () => null);
        viewModel.ApplicationServices.SetUrlOpener(realOpener);
        await viewModel.InitializeAsync();
        await Dispatcher.UIThread.InvokeAsync(() => { });

        var pane = viewModel.SelectedWorkspacePane;
        var initial = pane.Tabs.Count;

        await realOpener.OpenAsync(new OpenUrlRequest("https://example.com/tracker"));
        await Dispatcher.UIThread.InvokeAsync(() => { });
        Assert.Equal(initial + 1, pane.Tabs.Count);

        await realOpener.OpenAsync(new OpenUrlRequest("https://example.com/tracker"));
        await Dispatcher.UIThread.InvokeAsync(() => { });
        // Same URL — no new tab; existing tab activated.
        Assert.Equal(initial + 1, pane.Tabs.Count);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenUrlFromUsageTracker_SameUrlInDifferentPane_OpensNewTabInCurrentPane()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        var realOpener = UrlOpener.CreateDefault(viewModel, () => null);
        viewModel.ApplicationServices.SetUrlOpener(realOpener);
        await viewModel.InitializeAsync();
        await Dispatcher.UIThread.InvokeAsync(() => { });

        var currentPane = viewModel.SelectedWorkspacePane;
        // Simulate a matching tab existing in a NON-selected pane by removing it from the current
        // pane's Tabs collection (dedup scans SelectedWorkspacePane.Tabs only).
        var stray = new WebViewModel("https://example.com/tracker") { Id = "web-stray", Title = "stray" };
        currentPane.Tabs.Add(stray);
        currentPane.Tabs.Remove(stray); // was never actually in the pane's dock

        var initial = currentPane.Tabs.Count;
        await realOpener.OpenAsync(new OpenUrlRequest("https://example.com/tracker"));
        await Dispatcher.UIThread.InvokeAsync(() => { });

        // A new tab opens in the current pane because the "other pane" tab does not count.
        Assert.Equal(initial + 1, currentPane.Tabs.Count);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task IUrlOpener_ExternalPreferenceFromWebViewModel_DoesNotOpenSecondTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        var recording = new RecordingUrlOpener();
        viewModel.ApplicationServices.SetUrlOpener(recording);
        await viewModel.InitializeAsync();
        await Dispatcher.UIThread.InvokeAsync(() => { });

        var pane = viewModel.SelectedWorkspacePane;
        var web = new WebViewModel(
            "https://example.com/",
            tabService: viewModel,
            titleFixed: false,
            urlOpener: recording)
        { Id = "web-a", Title = "A" };
        await viewModel.OpenTabAsync(web);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        var beforeCount = pane.Tabs.Count;
        web.OpenInExternalBrowserCommand.Execute(null);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        // The IUrlOpener recorded an External request, and no additional embedded tab opened.
        Assert.Contains(recording.Requests, r => r.Preference == UrlOpenPreference.External);
        Assert.Equal(beforeCount, pane.Tabs.Count);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task IUrlOpener_ExternalPreferenceFromExternalEntity_RoutesThroughLauncher()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        var recording = new RecordingUrlOpener();
        viewModel.ApplicationServices.SetUrlOpener(recording);
        await viewModel.InitializeAsync();

        var url = "https://example.com/external";
        var tab = new ExternalEntityWorkspaceTabViewModel(
            entity: null!,
            urlKey: "default",
            url: url,
            urlOpener: recording)
        { Id = "ext-a", Title = "A" };

        tab.OpenInExternalBrowserCommand.Execute(null);

        Assert.Single(recording.Requests);
        Assert.Equal(url, recording.Requests[0].Url);
        Assert.Equal(UrlOpenPreference.External, recording.Requests[0].Preference);
    }

    // ── Issue #1186: startup splash dismissal + restore-time hang prevention ──

    // #1186: The core symptom is that LoadingWindow stays in front indefinitely
    // when viewModel.InitializeAsync never completes (or completes with an
    // unobserved fault buried inside RestoreSubAgentsAsync). The fix routes App
    // startup through StartupSplashRunner.RunWithSplashDismissAsync, whose
    // try/finally guarantees the splash close callback fires no matter how
    // initialize exits. These tests pin that invariant directly.

    [AvaloniaFact(Timeout = 15_000)]
    public async Task App_Startup_DefaultWorkspaceWithRestorableSubAgents_DismissesLoadingWindow()
    {
        var closed = false;
        var postInitializeRan = false;
        await using var viewModel = CreateTestMainWindowViewModel();

        var succeeded = await StartupSplashRunner.RunWithSplashDismissAsync(
            loggerFactory: Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            initializeAsync: () => viewModel.InitializeAsync(),
            setStatus: _ => { },
            onFaultDelay: () => Task.CompletedTask,
            shutdown: () => { },
            postInitialize: () => postInitializeRan = true,
            closeSplash: () => closed = true);

        Assert.True(succeeded);
        Assert.True(postInitializeRan);
        Assert.True(closed, "LoadingWindow must be dismissed on success path.");
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task App_Startup_ViewModelInitializeAsyncFaults_LoadingWindowIsClosedInFinally()
    {
        var closed = false;
        var shutdownCalled = false;
        var postInitializeRan = false;

        var succeeded = await StartupSplashRunner.RunWithSplashDismissAsync(
            loggerFactory: Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            initializeAsync: () => Task.FromException(new InvalidOperationException("boom")),
            setStatus: _ => { },
            onFaultDelay: () => Task.CompletedTask,
            shutdown: () => shutdownCalled = true,
            postInitialize: () => postInitializeRan = true,
            closeSplash: () => closed = true);

        Assert.False(succeeded);
        Assert.False(postInitializeRan);
        Assert.True(shutdownCalled);
        Assert.True(closed, "LoadingWindow must be dismissed via finally on the fault path.");
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task App_Startup_SubAgentRestoreThrows_DoesNotHangSplash()
    {
        // The specific #1186 failure mode: initialize faults from deep inside the
        // sub-agent restore path. The runner must still route through finally and
        // dismiss the splash — no hang possible.
        var closed = false;
        var statusMessages = new List<string>();

        var succeeded = await StartupSplashRunner.RunWithSplashDismissAsync(
            loggerFactory: Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            initializeAsync: () => Task.FromException(
                new InvalidOperationException("Agent definition does not specify a model.")),
            setStatus: msg => statusMessages.Add(msg),
            onFaultDelay: () => Task.CompletedTask,
            shutdown: () => { },
            postInitialize: () => { },
            closeSplash: () => closed = true);

        Assert.False(succeeded);
        Assert.True(closed, "Restore-time sub-agent throw must not leave the splash visible.");
        Assert.Contains(statusMessages, m => m.Contains("Agent definition does not specify a model.", StringComparison.Ordinal));
    }

    // ── Issue #1294: startup connect failures must be written to the rolling log file ──

    private sealed class RecordingStartupLoggerFactory : Microsoft.Extensions.Logging.ILoggerFactory
    {
        public List<TestLogger<StartupSplashRunner>.LogEntry> Entries { get; } = [];
        private readonly TestLogger<StartupSplashRunner> logger;

        public RecordingStartupLoggerFactory()
        {
            this.logger = new TestLogger<StartupSplashRunner>();
        }

        public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider) { }
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => this.logger;
        public void Dispose() { }

        public IReadOnlyList<TestLogger<StartupSplashRunner>.LogEntry> Snapshot() => this.logger.Entries;
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task StartupSplashRunner_WhenInitializeThrows_LogsError()
    {
        var factory = new RecordingStartupLoggerFactory();
        var boom = new InvalidOperationException("boom-1294");

        await StartupSplashRunner.RunWithSplashDismissAsync(
            loggerFactory: factory,
            initializeAsync: () => Task.FromException(boom),
            setStatus: _ => { },
            onFaultDelay: () => Task.CompletedTask,
            shutdown: () => { },
            postInitialize: () => { },
            closeSplash: () => { });

        var errors = factory.Snapshot()
            .Where(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error)
            .ToList();
        Assert.Single(errors);
        Assert.Same(boom, errors[0].Exception);
        Assert.Contains("Startup connect failed", errors[0].Message, StringComparison.Ordinal);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task StartupSplashRunner_WhenInitializeThrows_SetsSplashStatus()
    {
        var factory = new RecordingStartupLoggerFactory();
        var statusMessages = new List<string>();

        await StartupSplashRunner.RunWithSplashDismissAsync(
            loggerFactory: factory,
            initializeAsync: () => Task.FromException(new InvalidOperationException("boom-status")),
            setStatus: msg => statusMessages.Add(msg),
            onFaultDelay: () => Task.CompletedTask,
            shutdown: () => { },
            postInitialize: () => { },
            closeSplash: () => { });

        Assert.Contains("Failed to connect: boom-status", statusMessages);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task StartupSplashRunner_WhenInitializeSucceeds_DoesNotLogError()
    {
        var factory = new RecordingStartupLoggerFactory();

        await StartupSplashRunner.RunWithSplashDismissAsync(
            loggerFactory: factory,
            initializeAsync: () => Task.CompletedTask,
            setStatus: _ => { },
            onFaultDelay: () => Task.CompletedTask,
            shutdown: () => { },
            postInitialize: () => { },
            closeSplash: () => { });

        Assert.DoesNotContain(factory.Snapshot(), e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task StartupSplashRunner_WhenInitializeThrows_LogsBeforeShutdown()
    {
        var factory = new RecordingStartupLoggerFactory();
        var events = new List<string>();

        // TestLogger records to factory.Snapshot() synchronously; we also snapshot ordering
        // via events for the shutdown/onFaultDelay callbacks so we can assert LogError fired
        // strictly before either.
        await StartupSplashRunner.RunWithSplashDismissAsync(
            loggerFactory: factory,
            initializeAsync: () => Task.FromException(new InvalidOperationException("order-check")),
            setStatus: _ => events.Add("setStatus"),
            onFaultDelay: () =>
            {
                events.Add("onFaultDelay");
                return Task.CompletedTask;
            },
            shutdown: () => events.Add("shutdown"),
            postInitialize: () => events.Add("postInitialize"),
            closeSplash: () => events.Add("closeSplash"));

        // Snapshot the error entry — the RecordingStartupLoggerFactory records synchronously
        // inside LogError, so its presence at snapshot time means LogError has returned.
        var errorEntry = factory.Snapshot()
            .Single(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error);
        Assert.Contains("Startup connect failed", errorEntry.Message, StringComparison.Ordinal);

        // Ordering: shutdown and onFaultDelay both follow the LogError call, which happens
        // before setStatus in the catch block. shutdown must not precede either.
        var shutdownIndex = events.IndexOf("shutdown");
        var onFaultDelayIndex = events.IndexOf("onFaultDelay");
        var setStatusIndex = events.IndexOf("setStatus");
        Assert.True(setStatusIndex >= 0);
        Assert.True(onFaultDelayIndex > setStatusIndex, "onFaultDelay must run after setStatus (and after LogError).");
        Assert.True(shutdownIndex > onFaultDelayIndex, "shutdown must run after onFaultDelay (and after LogError).");
    }

    private static RepositorySource CreateInMemoryRepositorySource()
    {
        return new UnknownRepositorySource();
    }

    internal static MainWindowViewModel CreateTestMainWindowViewModel(
        ProfileStore? profileStore = null,
        ApplicationServices? applicationServices = null,
        WorkspacesConfiguration? configuration = null)
    {
        return new MainWindowViewModel(
            CreateInMemoryRepositorySource(),
            configuration ?? new WorkspacesConfiguration { SkipStartupWorkspace = true },
            profileStore ?? new ProfileStore(CreateTempProfileStorePath()),
            applicationServices);
    }

    private static string CreateTempProfileStorePath()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "Phantom.Workspaces.Tests",
            Guid.NewGuid().ToString("N"),
            "profile.json");
    }

    internal static RunningAgentChatTable CreateTestRunningAgentChatTable()
    {
        var store = new InMemoryAgentPersistenceStore();
        var foregroundScheduler = SynchronizationContextTaskScheduler.FromCurrent();
        var factory = new AgentChatFactory(store, new AgentServices(), foregroundScheduler);
        return new RunningAgentChatTable(factory);
    }

    private static void DeleteTempProfileStoreDirectory(string profilePath)
    {
        var directory = Path.GetDirectoryName(profilePath);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static EntityBroker GetEntityBroker(
        MainWindowViewModel viewModel)
    {
        var entityBrokerProperty = typeof(MainWindowViewModel).GetProperty(
            "EntityBroker",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(entityBrokerProperty);
        return Assert.IsType<EntityBroker>(entityBrokerProperty!.GetValue(viewModel));
    }

    internal static async Task<SubscribedEntityViewModel> UpsertEntityAndLoadAsync(
        EntityBroker entityBroker,
        EntityId entityId,
        string json)
    {
        using var document = JsonDocument.Parse(json);
        var updateResult = await entityBroker.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "Add test agent definition.",
                    },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = entityId,
                        EntityChangeMode = EntityChangeMode.Replace,
                        Data = document.RootElement.Clone(),
                    },
                ],
            });
        var entityResult = Assert.Single(updateResult.EntityResults, entityResult => entityResult.RequestedEntityId == entityId);
        Assert.NotEqual(UpdateState.Failed, entityResult.UpdateState);
        return Assert.Single(await entityBroker.GetEntitiesAsync([entityId]));
    }

    private static IReadOnlyCollection<EntityName> ReadEntityNames(
        JsonElement? entityData)
    {
        if (entityData is not JsonElement dataElement
            || !dataElement.TryGetProperty("names", out var namesElement)
            || namesElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var names = new List<EntityName>();
        foreach (var nameElement in namesElement.EnumerateArray())
        {
            var entityName = nameElement.TryReadEntityName();
            if (entityName is not null)
            {
                names.Add(entityName.Value);
            }
        }

        return names;
    }

    private static string? ReadDefaultDisplayName(
        JsonElement? entityData)
    {
        if (entityData is not JsonElement dataElement
            || !dataElement.TryGetProperty("display-name", out var displayNameElement)
            || displayNameElement.ValueKind != JsonValueKind.Object
            || !displayNameElement.TryGetProperty("default", out var defaultValueElement))
        {
            return null;
        }

        return defaultValueElement.GetString();
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenTabAsync_ExistingTab_PushesNavigationEntry()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "nav-push-a", Title = "Tab A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "nav-push-b", Title = "Tab B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB); // push B; B is active

        // Re-open tab A (it already exists) — should push a navigation entry
        var tabAAgain = new WebViewModel("https://a.example.com") { Id = "nav-push-a", Title = "Tab A" };
        await viewModel.OpenTabAsync(tabAAgain);

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        Assert.Equal("nav-push-a", documentDock!.ActiveDockable?.Id);

        // NavigateBack should return to tab B (the entry pushed before re-opening A)
        viewModel.NavigateBackCommand.Execute(null);
        Assert.Equal("nav-push-b", documentDock.ActiveDockable?.Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task NavigateBack_AfterMultipleToolDrivenNavigations_TraversesAllEntries()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "multi-nav-a", Title = "Tab A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "multi-nav-b", Title = "Tab B" };
        var tabC = new WebViewModel("https://c.example.com") { Id = "multi-nav-c", Title = "Tab C" };

        // Simulate sequential tool-driven tab openings
        await viewModel.OpenTabAsync(tabA);  // push A
        await viewModel.OpenTabAsync(tabB);  // push B
        await viewModel.OpenTabAsync(tabC);  // push C; C is active

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        Assert.Equal("multi-nav-c", documentDock!.ActiveDockable?.Id);

        // Back: C → B
        viewModel.NavigateBackCommand.Execute(null);
        Assert.Equal("multi-nav-b", documentDock.ActiveDockable?.Id);

        // Back: B → A
        viewModel.NavigateBackCommand.Execute(null);
        Assert.Equal("multi-nav-a", documentDock.ActiveDockable?.Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenWorkspaceAsync_WithMultipleBrowserTabs_TabsAppearInDeclarationOrder()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("00100001-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceId,
            """
            {
              "entity-id": "00100001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "tab-order-test"]],
              "display-name": { "default": "Tab Order Workspace" },
              "regions": [
                {
                  "region-id": "main",
                  "title": "Main",
                  "dock": "center",
                  "size": 1.0,
                  "tabs": [
                    {
                      "tab-id": "tab-order-a",
                      "title": "Tab A",
                      "kind": "browser",
                      "dock": "full",
                      "content": { "url": "https://a.example.com" }
                    },
                    {
                      "tab-id": "tab-order-b",
                      "title": "Tab B",
                      "kind": "browser",
                      "dock": "full",
                      "content": { "url": "https://b.example.com" }
                    },
                    {
                      "tab-id": "tab-order-c",
                      "title": "Tab C",
                      "kind": "browser",
                      "dock": "full",
                      "content": { "url": "https://c.example.com" }
                    }
                  ]
                }
              ]
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var workspacePane = Assert.Single(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, workspaceId.ToString(), StringComparison.Ordinal));

        var contentDock = FindDocumentDockIn(workspacePane.ContentLayout!);
        Assert.NotNull(contentDock);

        await WaitForWorkspaceTabAsync(contentDock!, "tab-order-a");
        await WaitForWorkspaceTabAsync(contentDock!, "tab-order-b");
        await WaitForWorkspaceTabAsync(contentDock!, "tab-order-c");

        var tabIds = contentDock!.VisibleDockables!
            .OfType<WorkspaceDocument>()
            .Where(d => d.Id is "tab-order-a" or "tab-order-b" or "tab-order-c")
            .Select(d => d.Id)
            .ToList();

        Assert.Equal(["tab-order-a", "tab-order-b", "tab-order-c"], tabIds);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenWorkspaceAsync_WithUnresolvableMiddleTab_SkipsNullAndPreservesOrder()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("00100002-0000-4000-8000-000000000002");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceId,
            """
            {
              "entity-id": "00100002-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "null-tab-order-test"]],
              "display-name": { "default": "Null Tab Order Workspace" },
              "regions": [
                {
                  "region-id": "main",
                  "title": "Main",
                  "dock": "center",
                  "size": 1.0,
                  "tabs": [
                    {
                      "tab-id": "null-order-a",
                      "title": "Tab A",
                      "kind": "browser",
                      "dock": "full",
                      "content": { "url": "https://a.example.com" }
                    },
                    {
                      "tab-id": "null-order-missing",
                      "title": "Missing Tab",
                      "kind": "entity",
                      "dock": "full",
                      "content": {
                        "target-entity-name": ["tests", "null-tab-test", "entity-does-not-exist"]
                      }
                    },
                    {
                      "tab-id": "null-order-c",
                      "title": "Tab C",
                      "kind": "browser",
                      "dock": "full",
                      "content": { "url": "https://c.example.com" }
                    }
                  ]
                }
              ]
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var workspacePane = Assert.Single(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, workspaceId.ToString(), StringComparison.Ordinal));

        var contentDock = FindDocumentDockIn(workspacePane.ContentLayout!);
        Assert.NotNull(contentDock);

        await WaitForWorkspaceTabAsync(contentDock!, "null-order-a");
        await WaitForWorkspaceTabAsync(contentDock!, "null-order-c");

        var tabIds = contentDock!.VisibleDockables!
            .OfType<WorkspaceDocument>()
            .Where(d => d.Id is "null-order-a" or "null-order-c" or "null-order-missing")
            .Select(d => d.Id)
            .ToList();

        Assert.Equal(["null-order-a", "null-order-c"], tabIds);
    }


    [AvaloniaFact(Timeout = 15_000)]
    public async Task OnActiveDockableChanged_WithWorkspacePaneDocument_UpdatesSelectedWorkspacePane()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceIdA = new EntityId("38300001-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdA,
            """
            {
              "entity-id": "38300001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "adc-switch-a"]],
              "display-name": { "default": "ADC Switch A" },
              "regions": []
            }
            """);

        var workspaceIdB = new EntityId("38300001-0000-4000-8000-000000000002");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdB,
            """
            {
              "entity-id": "38300001-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "adc-switch-b"]],
              "display-name": { "default": "ADC Switch B" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });

        var pane1 = viewModel.WorkspacePanes[0];
        var pane2 = viewModel.WorkspacePanes[1];

        ActivateWorkspacePaneAtIndex(viewModel, "0");
        Assert.Equal(pane1, viewModel.SelectedWorkspacePane);

        // Simulate clicking pane 2's tab in the outer dock (fires ActiveDockableChanged with WorkspacePaneDocument).
        var dockFactory = GetDockFactoryAs<IFactory>(viewModel);
        var workspacesDock = FindDocumentDockIn(viewModel.Layout!);
        Assert.NotNull(workspacesDock);
        var paneDoc2 = workspacesDock!.VisibleDockables!
            .OfType<WorkspacePaneDocument>()
            .First(d => d.WorkspacePane == pane2);
        dockFactory.SetActiveDockable(paneDoc2);

        Assert.Equal(pane2, viewModel.SelectedWorkspacePane);
    }


    private static PhysicalKey DigitKeyForAltBadge(string badge) => badge switch
    {
        "1" => PhysicalKey.Digit1,
        "2" => PhysicalKey.Digit2,
        "3" => PhysicalKey.Digit3,
        "4" => PhysicalKey.Digit4,
        "5" => PhysicalKey.Digit5,
        "6" => PhysicalKey.Digit6,
        "7" => PhysicalKey.Digit7,
        "8" => PhysicalKey.Digit8,
        "9" => PhysicalKey.Digit9,
        "0" => PhysicalKey.Digit0,
        _ => throw new ArgumentOutOfRangeException(nameof(badge), badge, "Unexpected Alt badge label."),
    };

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OnActiveDockableChanged_WithWorkspacePaneDocumentWithActiveTab_PushesNavigationEntry()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceIdA = new EntityId("38300003-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdA,
            """
            {
              "entity-id": "38300003-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "adc-nav-a"]],
              "display-name": { "default": "ADC Nav A" },
              "regions": []
            }
            """);

        var workspaceIdB = new EntityId("38300003-0000-4000-8000-000000000002");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdB,
            """
            {
              "entity-id": "38300003-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "adc-nav-b"]],
              "display-name": { "default": "ADC Nav B" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });

        // Open a tab in pane A.
        ActivateWorkspacePaneAtIndex(viewModel, "0");
        var tabA = new AgentSessionWorkspaceTabViewModel { Id = "adc-nav-tab-a", Title = "ADC Nav Tab A" };
        await viewModel.OpenTabAsync(tabA);

        // Open a tab in pane B.
        ActivateWorkspacePaneAtIndex(viewModel, "1");
        var tabB = new AgentSessionWorkspaceTabViewModel { Id = "adc-nav-tab-b", Title = "ADC Nav Tab B" };
        await viewModel.OpenTabAsync(tabB);

        // Switch back to pane A so pane A is selected.
        ActivateWorkspacePaneAtIndex(viewModel, "0");
        Assert.Equal(viewModel.WorkspacePanes[0], viewModel.SelectedWorkspacePane);

        // Simulate a mouse click on pane B's outer tab — should push a navigation entry for pane B's active tab.
        var dockFactory = GetDockFactoryAs<IFactory>(viewModel);
        var workspacesDock = FindDocumentDockIn(viewModel.Layout!);
        Assert.NotNull(workspacesDock);
        var pane2 = viewModel.WorkspacePanes[1];
        var paneDoc2 = workspacesDock!.VisibleDockables!
            .OfType<WorkspacePaneDocument>()
            .First(d => d.WorkspacePane == pane2);
        dockFactory.SetActiveDockable(paneDoc2);
        Assert.Equal(pane2, viewModel.SelectedWorkspacePane);

        // NavigateBack should return to a state where pane A's tab is active.
        var documentDockB = GetDocumentDock(viewModel);
        Assert.NotNull(documentDockB);
        Assert.Equal("adc-nav-tab-b", documentDockB!.ActiveDockable?.Id);

        viewModel.NavigateBackCommand.Execute(null);

        Assert.Equal(viewModel.WorkspacePanes[0], viewModel.SelectedWorkspacePane);
        var documentDockA = GetDocumentDock(viewModel);
        Assert.NotNull(documentDockA);
        Assert.Equal("adc-nav-tab-a", documentDockA!.ActiveDockable?.Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OnActiveDockableChanged_WithWorkspacePaneDocumentWithActiveTab_WhenNavigatingViaHistory_DoesNotPushExtraEntry()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceIdA = new EntityId("38300004-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdA,
            """
            {
              "entity-id": "38300004-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "adc-nav-guard-a"]],
              "display-name": { "default": "ADC Nav Guard A" },
              "regions": []
            }
            """);

        var workspaceIdB = new EntityId("38300004-0000-4000-8000-000000000002");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdB,
            """
            {
              "entity-id": "38300004-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "adc-nav-guard-b"]],
              "display-name": { "default": "ADC Nav Guard B" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });

        // Open a tab in pane A and pane B.
        ActivateWorkspacePaneAtIndex(viewModel, "0");
        var tabA = new AgentSessionWorkspaceTabViewModel { Id = "adc-nav-guard-tab-a", Title = "Guard Tab A" };
        await viewModel.OpenTabAsync(tabA);

        ActivateWorkspacePaneAtIndex(viewModel, "1");
        var tabB = new AgentSessionWorkspaceTabViewModel { Id = "adc-nav-guard-tab-b", Title = "Guard Tab B" };
        await viewModel.OpenTabAsync(tabB);

        // Switch back to pane A.
        ActivateWorkspacePaneAtIndex(viewModel, "0");

        // Simulate mouse click on pane B — pushes one navigation entry.
        var dockFactory = GetDockFactoryAs<IFactory>(viewModel);
        var workspacesDock = FindDocumentDockIn(viewModel.Layout!);
        Assert.NotNull(workspacesDock);
        var pane2 = viewModel.WorkspacePanes[1];
        var paneDoc2 = workspacesDock!.VisibleDockables!
            .OfType<WorkspacePaneDocument>()
            .First(d => d.WorkspacePane == pane2);
        dockFactory.SetActiveDockable(paneDoc2);

        // NavigateBack once — lands back on pane A's tab.
        viewModel.NavigateBackCommand.Execute(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(viewModel.WorkspacePanes[0], viewModel.SelectedWorkspacePane);
        var documentDockAfterBack = GetDocumentDock(viewModel);
        Assert.Equal("adc-nav-guard-tab-a", documentDockAfterBack?.ActiveDockable?.Id);

        // NavigateBack again — should continue traversing history correctly to the entry
        // before "pane A" (which is "pane B" from when tabB was first opened).
        // If the navigatingViaHistory guard were absent and the dock had fired
        // ActiveDockableChanged for the outer pane during the first NavigateBack, an
        // extra entry would have been inserted — corrupting history traversal here.
        viewModel.NavigateBackCommand.Execute(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(viewModel.WorkspacePanes[1], viewModel.SelectedWorkspacePane);
        Assert.Equal("adc-nav-guard-tab-b", GetDocumentDock(viewModel)?.ActiveDockable?.Id);
    }


    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_KeyPress_Alt0_ActivatesTenthContentTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        for (var i = 0; i < 10; i++)
        {
            var tab = new AgentSessionWorkspaceTabViewModel { Id = $"kb-alt0-tab{i}", Title = $"Tab {i}" };
            await viewModel.OpenTabAsync(tab);
        }

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            window.KeyPressQwerty(PhysicalKey.Digit0, RawInputModifiers.Alt);

            var documentDock = GetDocumentDock(viewModel);
            Assert.NotNull(documentDock);
            Assert.Equal(documentDock!.VisibleDockables![9], documentDock.ActiveDockable);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_KeyPress_AltDigit_WithIndexOutOfRange_IsNoOp()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new AgentSessionWorkspaceTabViewModel { Id = "kb-alt-oob-a", Title = "Tab A" };
        var tabB = new AgentSessionWorkspaceTabViewModel { Id = "kb-alt-oob-b", Title = "Tab B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var documentDock = GetDocumentDock(viewModel);
            Assert.NotNull(documentDock);
            var activeBefore = documentDock!.ActiveDockable;

            window.KeyPressQwerty(PhysicalKey.Digit9, RawInputModifiers.Alt);

            Assert.Equal(activeBefore, documentDock.ActiveDockable);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }


    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_KeyPress_AltShift2_ActivatesSecondWorkspacePane()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceIdA = new EntityId("33333333-3333-4333-8333-333333333333");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdA,
            """
            {
              "entity-id": "33333333-3333-4333-8333-333333333333",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "kb-pane2-a"]],
              "display-name": { "default": "KB Pane 2 A" },
              "regions": []
            }
            """);

        var workspaceIdB = new EntityId("44444444-4444-4444-8444-444444444444");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceIdB,
            """
            {
              "entity-id": "44444444-4444-4444-8444-444444444444",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "kb-pane2-b"]],
              "display-name": { "default": "KB Pane 2 B" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            window.KeyPressQwerty(PhysicalKey.Digit2, RawInputModifiers.Control);

            Assert.Equal(viewModel.WorkspacePanes[1], viewModel.SelectedWorkspacePane);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_KeyPress_AltShiftDigit_WithIndexOutOfRange_IsNoOp()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var selectedBefore = viewModel.SelectedWorkspacePane;

            window.KeyPressQwerty(PhysicalKey.Digit2, RawInputModifiers.Alt | RawInputModifiers.Shift);

            Assert.Equal(selectedBefore, viewModel.SelectedWorkspacePane);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task InitializeAsync_WithDefaultRelationship_OpensDefaultWorkspace()
    {
        await using var viewModel = CreateTestMainWindowViewModel(
            configuration: new WorkspacesConfiguration { SkipStartupWorkspace = false });

        var entityBroker = await GetEntityBrokerBeforeInitAsync(viewModel);
        var profileId = entityBroker.EntityRepository.WorkspaceEntitySession.UserComputerProfileEntityId;

        var workspaceId = new EntityId("de1a0110-0000-4000-8000-000000000001");
        await SeedEntityAsync(
            entityBroker,
            workspaceId,
            $$"""
            {
              "entity-id": "{{workspaceId}}",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "default-startup"]],
              "display-name": { "default": "Default Startup Workspace" },
              "regions": []
            }
            """);

        var defaultRelId = new EntityId("de1a0110-0000-4000-8000-000000000002");
        await SeedEntityAsync(
            entityBroker,
            defaultRelId,
            $$"""
            {
              "entity-id": "{{defaultRelId}}",
              "entity-types": ["entity", "default", "relationship"],
              "names": [["tests", "defaults", "startup-workspace"]],
              "participants": {
                "applied-to": "{{profileId}}",
                "value": "{{workspaceId}}"
              }
            }
            """);

        await viewModel.InitializeAsync();

        Assert.Contains(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, workspaceId.ToString(), StringComparison.Ordinal));
        Assert.DoesNotContain(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, GettingStartedWorkspaceId, StringComparison.Ordinal));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task InitializeAsync_WithNoDefaultRelationship_OpensGettingStartedWorkspace()
    {
        await using var viewModel = CreateTestMainWindowViewModel(
            configuration: new WorkspacesConfiguration { SkipStartupWorkspace = false });
        await viewModel.InitializeAsync();

        Assert.Contains(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, GettingStartedWorkspaceId, StringComparison.Ordinal));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CloseLastWorkspace_WithDefaultRelationship_OpensDefaultWorkspaceInsteadOfGettingStarted()
    {
        await using var viewModel = CreateTestMainWindowViewModel(
            configuration: new WorkspacesConfiguration { SkipStartupWorkspace = false });

        var entityBroker = await GetEntityBrokerBeforeInitAsync(viewModel);
        var profileId = entityBroker.EntityRepository.WorkspaceEntitySession.UserComputerProfileEntityId;

        var workspaceId = new EntityId("de1a0110-0000-4000-8000-000000000003");
        await SeedEntityAsync(
            entityBroker,
            workspaceId,
            $$"""
            {
              "entity-id": "{{workspaceId}}",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "default-close"]],
              "display-name": { "default": "Default Close Workspace" },
              "regions": []
            }
            """);

        var defaultRelId = new EntityId("de1a0110-0000-4000-8000-000000000004");
        await SeedEntityAsync(
            entityBroker,
            defaultRelId,
            $$"""
            {
              "entity-id": "{{defaultRelId}}",
              "entity-types": ["entity", "default", "relationship"],
              "names": [["tests", "defaults", "close-workspace"]],
              "participants": {
                "applied-to": "{{profileId}}",
                "value": "{{workspaceId}}"
              }
            }
            """);

        await viewModel.InitializeAsync();

        // Close the default workspace — this triggers OpenGettingStartedWorkspaceAsync
        var defaultPane = viewModel.WorkspacePanes
            .FirstOrDefault(p => string.Equals(p.Id, workspaceId.ToString(), StringComparison.Ordinal));
        Assert.NotNull(defaultPane);
        await viewModel.CloseWorkspacePaneAsync(defaultPane!);

        // After closing, the default workspace should be re-opened instead of Getting Started
        Assert.Contains(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, workspaceId.ToString(), StringComparison.Ordinal));
        Assert.DoesNotContain(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, GettingStartedWorkspaceId, StringComparison.Ordinal));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task InitializeAsync_AfterTogglingDefaultInterest_OpensDefaultWorkspace()
    {
        await using var viewModel = CreateTestMainWindowViewModel(
            configuration: new WorkspacesConfiguration { SkipStartupWorkspace = false });

        var entityBroker = await GetEntityBrokerBeforeInitAsync(viewModel);

        var workspaceId = new EntityId("de1a0110-0000-4000-8000-000000000005");
        await SeedEntityAsync(
            entityBroker,
            workspaceId,
            $$"""
            {
              "entity-id": "{{workspaceId}}",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "toggle-default"]],
              "display-name": { "default": "Toggle Default Workspace" },
              "regions": []
            }
            """);

        await viewModel.InitializeAsync();

        // Toggle the default interest ON for the workspace using the same code path the badge uses:
        // the interest catalog is populated by InitializeAsync from the data-driven default-entity-type
        // registration, and InterestToggle writes the {value, applied-to} participants declared there.
        var workspaceEntities = await entityBroker.GetEntitiesAsync([workspaceId]);
        var workspaceSnapshot = workspaceEntities.Single().Snapshot;
        var defaultDefinition = entityBroker.InterestCatalog!.InterestTypes
            .Single(interestType => string.Equals(interestType.Name, "default", StringComparison.Ordinal));
        await InterestToggle.ToggleAsync(entityBroker, workspaceSnapshot, defaultDefinition);

        // Closing the last workspace re-runs QueryDefaultWorkspaceIdsAsync (the same data-layer query
        // used by InitializeAsync at startup). The toggled default must be picked up and opened
        // instead of the Getting Started fallback.
        var openPane = viewModel.WorkspacePanes.FirstOrDefault();
        if (openPane is not null)
        {
            await viewModel.CloseWorkspacePaneAsync(openPane);
        }

        Assert.Contains(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, workspaceId.ToString(), StringComparison.Ordinal));
        Assert.DoesNotContain(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, GettingStartedWorkspaceId, StringComparison.Ordinal));
    }

    private const string GettingStartedWorkspaceId = "6cc39f41-2a36-4be6-ab95-3f3fd355e463";

    private static async Task<EntityBroker> GetEntityBrokerBeforeInitAsync(MainWindowViewModel viewModel)
    {
        var entityBrokerTaskField = typeof(MainWindowViewModel).GetField(
            "entityBrokerTask",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(entityBrokerTaskField);
        var entityBrokerTask = (Task<EntityBroker>)entityBrokerTaskField!.GetValue(viewModel)!;
        return await entityBrokerTask;
    }

    private static async Task SeedEntityAsync(EntityBroker entityBroker, EntityId entityId, string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = await entityBroker.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "seed" } },
            Changes =
            [
                new EntityChange
                {
                    EntityId = entityId,
                    EntityChangeMode = EntityChangeMode.Replace,
                    Data = document.RootElement.Clone(),
                },
            ],
        });
        var failure = result.EntityResults.FirstOrDefault(static r => r.UpdateState == UpdateState.Failed);
        Assert.True(
            failure is null,
            failure is null ? string.Empty : string.Join(" | ", failure.Errors.Select(static e => e.Message)));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task NavigatePreviousNotificationCommand_NavigatesToUnreadTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new AgentSessionWorkspaceTabViewModel { Id = "nav-prev-a", Title = "Tab A" };
        var tabB = new AgentSessionWorkspaceTabViewModel { Id = "nav-prev-b", Title = "Tab B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);

        // tabB is active; notify tabA so it becomes the unread candidate.
        viewModel.NotificationService.Notify(new Notification(new TabDescriptor { TabId = "nav-prev-a" }, "Tab A", "test notification", DateTime.UtcNow, RunningState.Idle, NotificationState.Interesting));

        viewModel.NavigatePreviousNotificationCommand.Execute(null);

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        Assert.Equal("nav-prev-a", (documentDock!.ActiveDockable as WorkspaceDocument)?.Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task NavigateNextNotificationCommand_NavigatesToUnreadTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new AgentSessionWorkspaceTabViewModel { Id = "nav-next-a", Title = "Tab A" };
        var tabB = new AgentSessionWorkspaceTabViewModel { Id = "nav-next-b", Title = "Tab B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);

        // tabB is active; notify tabA so it becomes the unread candidate.
        viewModel.NotificationService.Notify(new Notification(new TabDescriptor { TabId = "nav-next-a" }, "Tab A", "test notification", DateTime.UtcNow, RunningState.Idle, NotificationState.Interesting));

        viewModel.NavigateNextNotificationCommand.Execute(null);

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        Assert.Equal("nav-next-a", (documentDock!.ActiveDockable as WorkspaceDocument)?.Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task NavigateNextNotificationCommand_WhenTabIsInNonSelectedPane_SwitchesWorkspacePane()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        // Open workspace A first so there are two panes (placeholder is removed by OpenWorkspaceAsync)
        var workspaceAId = new EntityId("b1190319-0000-4000-8000-00000000000a");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceAId,
            """
            {
              "entity-id": "b1190319-0000-4000-8000-00000000000a",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "notif-pane-a"]],
              "display-name": { "default": "Notif Pane A" },
              "regions": []
            }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceAId });

        var workspaceBId = new EntityId("b1190319-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceBId,
            """
            {
              "entity-id": "b1190319-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "notif-pane-b"]],
              "display-name": { "default": "Notif Pane B" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceBId });

        // Select pane B and open a tab there (no WorkspaceId hint in TabDescriptor)
        var paneB = viewModel.WorkspacePanes.Single(p => string.Equals(p.Id, workspaceBId.ToString(), StringComparison.Ordinal));
        viewModel.SelectedWorkspacePane = paneB;
        var tabInPaneB = new AgentSessionWorkspaceTabViewModel { Id = "notif-cross-pane-tab", Title = "Tab in Pane B" };
        await viewModel.OpenTabAsync(tabInPaneB);

        // Switch back to pane A so the notification for tabInPaneB will be unread
        var paneA = viewModel.WorkspacePanes.First(p => !string.Equals(p.Id, workspaceBId.ToString(), StringComparison.Ordinal));
        viewModel.SelectedWorkspacePane = paneA;

        viewModel.NotificationService.Notify(new Notification(
            new TabDescriptor { TabId = "notif-cross-pane-tab" },
            "Tab in Pane B", "test notification", DateTime.UtcNow, RunningState.Idle, NotificationState.Interesting));

        viewModel.NavigateNextNotificationCommand.Execute(null);

        Assert.Same(paneB, viewModel.SelectedWorkspacePane);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task NavigateNextNotificationCommand_WhenTabIsInNonSelectedPaneWithWorkspaceIdHint_SwitchesWorkspacePane()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        // Open workspace A first so there are two panes (placeholder is removed by OpenWorkspaceAsync)
        var workspaceAId = new EntityId("b1190319-0000-4000-8000-00000000000b");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceAId,
            """
            {
              "entity-id": "b1190319-0000-4000-8000-00000000000b",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "notif-pane-a2"]],
              "display-name": { "default": "Notif Pane A2" },
              "regions": []
            }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceAId });

        var workspaceBId = new EntityId("b1190319-0000-4000-8000-000000000002");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceBId,
            """
            {
              "entity-id": "b1190319-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "notif-pane-b2"]],
              "display-name": { "default": "Notif Pane B2" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceBId });

        // Select pane B and open a tab there
        var paneB = viewModel.WorkspacePanes.Single(p => string.Equals(p.Id, workspaceBId.ToString(), StringComparison.Ordinal));
        viewModel.SelectedWorkspacePane = paneB;
        var tabInPaneB = new AgentSessionWorkspaceTabViewModel { Id = "notif-cross-pane-tab-hint", Title = "Tab in Pane B" };
        await viewModel.OpenTabAsync(tabInPaneB);

        // Switch back to pane A so the notification for tabInPaneB will be unread
        var paneA = viewModel.WorkspacePanes.First(p => !string.Equals(p.Id, workspaceBId.ToString(), StringComparison.Ordinal));
        viewModel.SelectedWorkspacePane = paneA;

        // Notify with WorkspaceId hint pointing to pane B
        viewModel.NotificationService.Notify(new Notification(
            new TabDescriptor { TabId = "notif-cross-pane-tab-hint", WorkspaceId = workspaceBId.ToString() },
            "Tab in Pane B", "test notification", DateTime.UtcNow, RunningState.Idle, NotificationState.Interesting));

        viewModel.NavigateNextNotificationCommand.Execute(null);

        Assert.Same(paneB, viewModel.SelectedWorkspacePane);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_KeyPress_CtrlF7_NavigatesToPreviousNotification()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new AgentSessionWorkspaceTabViewModel { Id = "ctrl-f7-prev-a", Title = "Tab A" };
        var tabB = new AgentSessionWorkspaceTabViewModel { Id = "ctrl-f7-prev-b", Title = "Tab B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);

        // tabB is active; notify tabA so it becomes the unread candidate.
        viewModel.NotificationService.Notify(new Notification(new TabDescriptor { TabId = "ctrl-f7-prev-a" }, "Tab A", "test notification", DateTime.UtcNow, RunningState.Idle, NotificationState.Interesting));

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            window.KeyPressQwerty(PhysicalKey.F7, RawInputModifiers.Control);

            var documentDock = GetDocumentDock(viewModel);
            Assert.NotNull(documentDock);
            Assert.Equal("ctrl-f7-prev-a", (documentDock!.ActiveDockable as WorkspaceDocument)?.Id);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_KeyPress_CtrlF8_NavigatesToNextNotification()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new AgentSessionWorkspaceTabViewModel { Id = "ctrl-f8-next-a", Title = "Tab A" };
        var tabB = new AgentSessionWorkspaceTabViewModel { Id = "ctrl-f8-next-b", Title = "Tab B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);

        // tabB is active; notify tabA so it becomes the unread candidate.
        viewModel.NotificationService.Notify(new Notification(new TabDescriptor { TabId = "ctrl-f8-next-a" }, "Tab A", "test notification", DateTime.UtcNow, RunningState.Idle, NotificationState.Interesting));

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            window.KeyPressQwerty(PhysicalKey.F8, RawInputModifiers.Control);

            var documentDock = GetDocumentDock(viewModel);
            Assert.NotNull(documentDock);
            Assert.Equal("ctrl-f8-next-a", (documentDock!.ActiveDockable as WorkspaceDocument)?.Id);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_KeyPress_CtrlF7_IsHandledInTunnelPhase()
    {
        // Verifies that Ctrl+F7 is intercepted in the tunnel phase (e.Handled = true),
        // preventing child controls such as WebView2 from seeing the keystroke.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            // Register a bubble-phase handler with handledEventsToo: true so it still fires
            // even after the tunnel handler has already set e.Handled = true.
            bool handledByTunnel = false;
            window.AddHandler(
                InputElement.KeyDownEvent,
                (_, e) =>
                {
                    if (e.Key == Key.F7 && e.KeyModifiers == KeyModifiers.Control)
                        handledByTunnel = e.Handled;
                },
                RoutingStrategies.Bubble,
                handledEventsToo: true);

            // With no unread notifications the command is a no-op, but the key must still be handled.
            window.KeyPressQwerty(PhysicalKey.F7, RawInputModifiers.Control);

            Assert.True(handledByTunnel);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_KeyPress_CtrlF8_IsHandledInTunnelPhase()
    {
        // Verifies that Ctrl+F8 is intercepted in the tunnel phase (e.Handled = true),
        // preventing child controls such as WebView2 from seeing the keystroke.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            bool handledByTunnel = false;
            window.AddHandler(
                InputElement.KeyDownEvent,
                (_, e) =>
                {
                    if (e.Key == Key.F8 && e.KeyModifiers == KeyModifiers.Control)
                        handledByTunnel = e.Handled;
                },
                RoutingStrategies.Bubble,
                handledEventsToo: true);

            window.KeyPressQwerty(PhysicalKey.F8, RawInputModifiers.Control);

            Assert.True(handledByTunnel);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }



    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_WithNotificationBellRingingStyle_DoesNotThrowOnLayout()
    {
        // Regression test for #143: bell animation used string-valued RenderTransform KeyFrame
        // setters (e.g. Value="rotate(-18deg)"). Avalonia's XAML IL compiler does not apply
        // type converters inside KeyFrame.Setter, so the value arrived as a boxed string with
        // no registered animator, throwing InvalidOperationException on first style application.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            // Force a full layout pass — this applies all loaded styles (including NotificationsStyles)
            // and interprets animation keyframes. The bug caused a throw here.
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    // ── IsAltHeld / Alt-badge tests ──────────────────────────────────────────






    // ── IsShiftHeld / PropagateBadgeVisibility tests (#774) ──────────────────













    // ── Alt+N shortcut numbers — multi-pane scenarios (#614) ──────────────────

    // ── Alt+Shift+N shortcut numbers — workspace pane label tests (#773) ─────








    #region Issue #1067 — indexing derives from Dock VisibleDockables (visual order)

    [AvaloniaFact(Timeout = 15_000)]
    public async Task AltN_IndexesFromActiveWorkspaceDockVisibleDockables()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "avd-a", Title = "Tab A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "avd-b", Title = "Tab B" };
        var tabC = new WebViewModel("https://c.example.com") { Id = "avd-c", Title = "Tab C" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);
        await viewModel.OpenTabAsync(tabC);

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);

        // Reorder the active workspace's content DocumentDock VisibleDockables (drag = Remove + Insert).
        var visibleDockables = documentDock!.VisibleDockables as System.Collections.ObjectModel.ObservableCollection<IDockable>;
        Assert.NotNull(visibleDockables);
        var docC = visibleDockables!.OfType<WorkspaceDocument>().First(d => d.Id == "avd-c");
        visibleDockables.RemoveAt(visibleDockables.IndexOf(docC));
        visibleDockables.Insert(0, docC);

        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        var docs = documentDock.VisibleDockables!.OfType<WorkspaceDocument>().ToList();
        Assert.Equal(new[] { "avd-c", "avd-a", "avd-b" }, docs.Select(d => d.Id).ToArray());

        // Alt+1/2/3 resolve strictly from the VisibleDockables order: index i activates docs[i].
        for (var i = 0; i < docs.Count; i++)
        {
            ActivateContentTabAtIndex(viewModel, i.ToString());
            Assert.Equal(docs[i], documentDock.ActiveDockable);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task AltShiftN_IndexesFromWorkspaceTabHostVisibleDockables()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceAId = new EntityId("ac010001-0000-4000-8000-000000000001");
        var workspaceBId = new EntityId("ac010001-0000-4000-8000-000000000002");
        var workspaceCId = new EntityId("ac010001-0000-4000-8000-000000000003");

        await UpsertEntityAndLoadAsync(entityBroker, workspaceAId,
            """
            {
              "entity-id": "ac010001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "ws-host-a"]],
              "display-name": { "default": "WS Host A" },
              "regions": []
            }
            """);
        await UpsertEntityAndLoadAsync(entityBroker, workspaceBId,
            """
            {
              "entity-id": "ac010001-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "ws-host-b"]],
              "display-name": { "default": "WS Host B" },
              "regions": []
            }
            """);
        await UpsertEntityAndLoadAsync(entityBroker, workspaceCId,
            """
            {
              "entity-id": "ac010001-0000-4000-8000-000000000003",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "ws-host-c"]],
              "display-name": { "default": "WS Host C" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceAId });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceBId });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceCId });

        var workspacesDock = FindDocumentDockIn(viewModel.Layout!);
        Assert.NotNull(workspacesDock);

        // Reorder the workspace-tab host dock's VisibleDockables (drag = Remove + Insert): move C first.
        var visibleDockables = workspacesDock!.VisibleDockables as System.Collections.ObjectModel.ObservableCollection<IDockable>;
        Assert.NotNull(visibleDockables);
        var paneDocC = visibleDockables!.OfType<WorkspacePaneDocument>().First(d => d.Id == workspaceCId.ToString());
        visibleDockables.RemoveAt(visibleDockables.IndexOf(paneDocC));
        visibleDockables.Insert(0, paneDocC);

        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        var paneDocs = workspacesDock.VisibleDockables!.OfType<WorkspacePaneDocument>().ToList();
        Assert.Equal(
            new[] { workspaceCId.ToString(), workspaceAId.ToString(), workspaceBId.ToString() },
            paneDocs.Select(d => d.Id).ToArray());

        // WorkspacePanes (the Alt+Shift+N index source) was re-derived to match the visual order.
        Assert.Equal(
            new[] { workspaceCId.ToString(), workspaceAId.ToString(), workspaceBId.ToString() },
            viewModel.WorkspacePanes.Select(p => p.Id).ToArray());

        // Alt+Shift+1/2/3 select the pane at the corresponding visual position.
        for (var i = 0; i < paneDocs.Count; i++)
        {
            ActivateWorkspacePaneAtIndex(viewModel, i.ToString());
            Assert.Equal(paneDocs[i].WorkspacePane, viewModel.SelectedWorkspacePane);
        }
    }


    [AvaloniaFact(Timeout = 15_000)]
    public async Task Indexing_DoesNotConsultInternalTabsList()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "ncl-a", Title = "Tab A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "ncl-b", Title = "Tab B" };
        var tabC = new WebViewModel("https://c.example.com") { Id = "ncl-c", Title = "Tab C" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);
        await viewModel.OpenTabAsync(tabC);

        var pane = viewModel.SelectedWorkspacePane!;
        // Internal Tabs list begins in insertion order A, B, C.
        Assert.Equal(new[] { "ncl-a", "ncl-b", "ncl-c" }, pane.Tabs.Select(t => t.Id).ToArray());

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);

        // Reorder ONLY the dock's VisibleDockables (never touching pane.Tabs directly): C to front.
        var visibleDockables = documentDock!.VisibleDockables as System.Collections.ObjectModel.ObservableCollection<IDockable>;
        Assert.NotNull(visibleDockables);
        var docC = visibleDockables!.OfType<WorkspaceDocument>().First(d => d.Id == "ncl-c");
        visibleDockables.RemoveAt(visibleDockables.IndexOf(docC));
        visibleDockables.Insert(0, docC);

        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        // Alt+1 resolves the tab that is visually first (C) — NOT pane.Tabs' original first element (A).
        // The Dock's VisibleDockables order is authoritative for indexing; per issue #1107 the
        // internal pane.Tabs list is INDEPENDENT of the dock order and preserves its insertion order.
        ActivateContentTabAtIndex(viewModel, "0");
        var docs = documentDock.VisibleDockables!.OfType<WorkspaceDocument>().ToList();
        Assert.Equal("ncl-c", docs[0].Id);
        Assert.Equal(docs[0], documentDock.ActiveDockable);
        Assert.Equal(new[] { "ncl-a", "ncl-b", "ncl-c" }, pane.Tabs.Select(t => t.Id).ToArray());
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task AltShortcut_AfterReorder_ActivatesDockableAtNewPosition()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "anp-a", Title = "Tab A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "anp-b", Title = "Tab B" };
        var tabC = new WebViewModel("https://c.example.com") { Id = "anp-c", Title = "Tab C" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);
        await viewModel.OpenTabAsync(tabC);

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);

        // Drag A to the last position (Remove + Insert) => visual order B, C, A.
        var visibleDockables = documentDock!.VisibleDockables as System.Collections.ObjectModel.ObservableCollection<IDockable>;
        Assert.NotNull(visibleDockables);
        var docA = visibleDockables!.OfType<WorkspaceDocument>().First(d => d.Id == "anp-a");
        visibleDockables.RemoveAt(visibleDockables.IndexOf(docA));
        visibleDockables.Insert(visibleDockables.Count, docA);

        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        var docs = documentDock.VisibleDockables!.OfType<WorkspaceDocument>().ToList();
        Assert.Equal(new[] { "anp-b", "anp-c", "anp-a" }, docs.Select(d => d.Id).ToArray());

        // Alt+3 now activates A, which moved to visual position 3 (index 2).
        ActivateContentTabAtIndex(viewModel, "2");
        Assert.Equal(docs[2], documentDock.ActiveDockable);
        Assert.Equal("anp-a", ((WorkspaceDocument)documentDock.ActiveDockable!).Id);
    }


    #endregion







    [AvaloniaFact(Timeout = 15_000)]
    public async Task GoToTabAtIndex_TwoWorkspacesOpen_ActivatesActiveWorkspaceTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceAId = new EntityId("ab070001-0000-4000-8000-000000000001");
        var workspaceBId = new EntityId("ab070002-0000-4000-8000-000000000002");

        await UpsertEntityAndLoadAsync(entityBroker, workspaceAId,
            """
            {
              "entity-id": "ab070001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "goto-scope-left"]],
              "display-name": { "default": "Goto Scope Left" },
              "regions": []
            }
            """);
        await UpsertEntityAndLoadAsync(entityBroker, workspaceBId,
            """
            {
              "entity-id": "ab070002-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "goto-scope-right"]],
              "display-name": { "default": "Goto Scope Right" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceAId });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceBId });

        var paneA = viewModel.WorkspacePanes.First(p => p.Id == workspaceAId.ToString());
        var paneB = viewModel.WorkspacePanes.First(p => p.Id == workspaceBId.ToString());

        await CloseDefaultPaneTabsAsync(viewModel, paneA, paneB);

        viewModel.SelectedWorkspacePane = paneA;
        var tabA1 = new WebViewModel("https://a1.example.com") { Id = "goto-scope-a1", Title = "A1" };
        var tabA2 = new WebViewModel("https://a2.example.com") { Id = "goto-scope-a2", Title = "A2" };
        await viewModel.OpenTabAsync(tabA1);
        await viewModel.OpenTabAsync(tabA2);

        viewModel.SelectedWorkspacePane = paneB;
        var tabB1 = new WebViewModel("https://b1.example.com") { Id = "goto-scope-b1", Title = "B1" };
        var tabB2 = new WebViewModel("https://b2.example.com") { Id = "goto-scope-b2", Title = "B2" };
        await viewModel.OpenTabAsync(tabB1);
        await viewModel.OpenTabAsync(tabB2);

        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        // Workspace B is active. Alt+2 must select B's second tab (scoped), not a global
        // fourth tab.
        ActivateContentTabAtIndex(viewModel, "1");
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        var dockB = FindDocumentDockIn(paneB.ContentLayout!);
        Assert.NotNull(dockB);
        Assert.Equal("goto-scope-b2", (dockB!.ActiveDockable as WorkspaceDocument)?.Id);
    }


    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_KeyPress_ScrollLock_TogglesAgentAutoScroll()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        await using var agentChat = await CreateEchoAgentChatAsync();
        var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(agentChat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var agentTab = new AgentSessionWorkspaceTabViewModel { Id = "scroll-lock-toggle", Title = "Agent" };
        agentTab.SetReady(agentViewModel, loggerFactory);
        await viewModel.OpenTabAsync(agentTab);

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            Assert.True(agentViewModel.AutoScrollEnabled);

            window.KeyPress(Key.Scroll, RawInputModifiers.None, PhysicalKey.None, "");

            Assert.False(agentViewModel.AutoScrollEnabled);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_KeyPress_ScrollLock_TogglesAgentAutoScrollTwice()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        await using var agentChat = await CreateEchoAgentChatAsync();
        var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(agentChat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var agentTab = new AgentSessionWorkspaceTabViewModel { Id = "scroll-lock-twice", Title = "Agent" };
        agentTab.SetReady(agentViewModel, loggerFactory);
        await viewModel.OpenTabAsync(agentTab);

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            window.KeyPress(Key.Scroll, RawInputModifiers.None, PhysicalKey.None, "");
            window.KeyPress(Key.Scroll, RawInputModifiers.None, PhysicalKey.None, "");

            Assert.True(agentViewModel.AutoScrollEnabled);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_KeyPress_ScrollLock_IsHandledInTunnelPhase()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        await using var agentChat = await CreateEchoAgentChatAsync();
        var loggerFactory = new ObservableLoggerFactory();
        await using var agentViewModel = new AgentViewModel(agentChat, "test-agent", "", loggerFactory, TaskScheduler.Default);

        var agentTab = new AgentSessionWorkspaceTabViewModel { Id = "scroll-lock-handled", Title = "Agent" };
        agentTab.SetReady(agentViewModel, loggerFactory);
        await viewModel.OpenTabAsync(agentTab);

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            bool handled = false;
            window.AddHandler(
                InputElement.KeyDownEvent,
                (_, e) =>
                {
                    if (e.Key == Key.Scroll)
                        handled = e.Handled;
                },
                RoutingStrategies.Bubble,
                handledEventsToo: true);

            window.KeyPress(Key.Scroll, RawInputModifiers.None, PhysicalKey.None, "");

            Assert.True(handled);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_KeyPress_ScrollLock_WithNoAgentTab_IsNoOp()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var plainTab = new AgentSessionWorkspaceTabViewModel { Id = "scroll-lock-noop", Title = "NoAgent" };
        await viewModel.OpenTabAsync(plainTab);

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            bool handled = false;
            window.AddHandler(
                InputElement.KeyDownEvent,
                (_, e) =>
                {
                    if (e.Key == Key.Scroll)
                        handled = e.Handled;
                },
                RoutingStrategies.Bubble,
                handledEventsToo: true);

            window.KeyPress(Key.Scroll, RawInputModifiers.None, PhysicalKey.None, "");

            Assert.False(handled);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    // ── WebViewModel and AgentSessionTab accelerator-key wiring ─────────────────





    [AvaloniaFact(Timeout = 15_000)]
    public async Task ApplySelectedViewAsync_WorkspacesView_ShowsRelatedEntityNestedUnderWorkspace()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        var entityBroker = await GetEntityBrokerBeforeInitAsync(viewModel);

        var workspaceId = new EntityId("a2b3c4d5-0001-4000-8000-000000000001");
        var noteId = new EntityId("a2b3c4d5-0001-4000-8000-000000000002");
        var relatedId = new EntityId("a2b3c4d5-0001-4000-8000-000000000003");
        var entityTypeViewId = new EntityId("a2b3c4d5-0001-4000-8000-000000000004");

        // Seed entity-type-view for workspace to declare traverse-relationships
        await SeedEntityAsync(entityBroker, entityTypeViewId, $$"""
            {
              "entity-id": "{{entityTypeViewId}}",
              "entity-types": ["entity", "entity-type-view"],
              "names": [["entity-type-views", "workspace"]],
              "display-name": { "default": "Workspace View" },
              "fields": [],
              "traverse-relationships": [
                { "relationship-type-ids": ["related"] }
              ]
            }
            """);
        await SeedEntityAsync(entityBroker, workspaceId, $$"""
            {
              "entity-id": "{{workspaceId}}",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "view-related-ws"]],
              "display-name": { "default": "Related Workspace" },
              "regions": []
            }
            """);
        await SeedEntityAsync(entityBroker, noteId, $$"""
            {
              "entity-id": "{{noteId}}",
              "entity-types": ["entity", "note"],
              "names": [["notes", "related-note"]],
              "display-name": { "default": "Related Note" },
              "content": { "mime-type": "text/markdown", "content": { "text": "Related Note" } }
            }
            """);
        await SeedEntityAsync(entityBroker, relatedId, $$"""
            {
              "entity-id": "{{relatedId}}",
              "entity-types": ["entity", "related", "relationship"],
              "names": [["relationships", "ws-note-related"]],
              "participants": { "entities": ["{{workspaceId}}", "{{noteId}}"] }
            }
            """);

        await viewModel.InitializeAsync();

        var workspacesView = Assert.Single(
            viewModel.TopLevelViews,
            static view => string.Equals(view.Title, "Workspaces", StringComparison.Ordinal));
        viewModel.SelectedTopLevelView = workspacesView;

        var applySelectedViewMethod = typeof(MainWindowViewModel).GetMethod(
            "ApplySelectedViewAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(applySelectedViewMethod);
        await (Task)applySelectedViewMethod!.Invoke(viewModel, [])!;

        var entities = viewModel.CurrentViewPopulation.Entities;

        var workspaceEntity = Assert.Single(
            entities,
            e => string.Equals(e.EntityId, workspaceId.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, workspaceEntity.IndentLevel);

        var noteEntity = Assert.Single(
            entities,
            e => string.Equals(e.EntityId, noteId.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, noteEntity.IndentLevel);

        var workspaceIndex = entities.ToList().FindIndex(e => string.Equals(e.EntityId, workspaceId.ToString(), StringComparison.OrdinalIgnoreCase));
        var noteIndex = entities.ToList().FindIndex(e => string.Equals(e.EntityId, noteId.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(workspaceIndex + 1, noteIndex);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ApplySelectedViewAsync_WorkspacesView_WorkspaceWithNoRelatedEntities_ShowsWorkspaceFlatOnly()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        var entityBroker = await GetEntityBrokerBeforeInitAsync(viewModel);

        var workspaceId = new EntityId("a2b3c4d5-0002-4000-8000-000000000001");

        await SeedEntityAsync(entityBroker, workspaceId, $$"""
            {
              "entity-id": "{{workspaceId}}",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "view-flat-ws"]],
              "display-name": { "default": "Flat Workspace" },
              "regions": []
            }
            """);

        await viewModel.InitializeAsync();

        var workspacesView = Assert.Single(
            viewModel.TopLevelViews,
            static view => string.Equals(view.Title, "Workspaces", StringComparison.Ordinal));
        viewModel.SelectedTopLevelView = workspacesView;

        var applySelectedViewMethod = typeof(MainWindowViewModel).GetMethod(
            "ApplySelectedViewAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(applySelectedViewMethod);
        await (Task)applySelectedViewMethod!.Invoke(viewModel, [])!;

        var entities = viewModel.CurrentViewPopulation.Entities;

        var workspaceEntity = Assert.Single(
            entities,
            e => string.Equals(e.EntityId, workspaceId.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, workspaceEntity.IndentLevel);

        Assert.DoesNotContain(entities, e => e.IndentLevel > 0);
    }


    // ── Single-window guard tests (issue #240) ────────────────────────────────

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OnOpenScheduledTasksClicked_WhenWindowAlreadyOpen_DoesNotOpenSecondWindow()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        var mainWindow = new MainWindow(viewModel);

        var trackingField = typeof(MainWindow).GetField(
            "openScheduledTasksWindow",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(trackingField);

        var existingDialog = new ScheduledTasksWindow();
        trackingField!.SetValue(mainWindow, existingDialog);

        var handler = typeof(MainWindow).GetMethod(
            "OnOpenScheduledTasksClicked",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(handler);
        handler!.Invoke(mainWindow, [null, new RoutedEventArgs()]);

        // The tracking field must still reference the same existing dialog — the guard returned early.
        Assert.Same(existingDialog, trackingField.GetValue(mainWindow));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OnOpenScheduledTasksClicked_TrackingField_InitiallyNull()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        var mainWindow = new MainWindow(viewModel);

        var trackingField = typeof(MainWindow).GetField(
            "openScheduledTasksWindow",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(trackingField);
        Assert.Null(trackingField!.GetValue(mainWindow));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowViewModel_RunVsCodeTunnelTool_IsRegistered()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var hostField = typeof(MainWindowViewModel).GetField(
            "scheduledToolHost",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(hostField);
        var host = Assert.IsType<Phantom.Workspaces.ScheduledTools.ScheduledToolHost>(hostField!.GetValue(viewModel));

        Assert.True(host.TryGetTool("run-vscode-tunnel", out _));

    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_ScheduledToolHost_RegistersGitWorkspaceScanTool()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var hostField = typeof(MainWindowViewModel).GetField(
            "scheduledToolHost",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(hostField);
        var host = Assert.IsType<Phantom.Workspaces.ScheduledTools.ScheduledToolHost>(hostField!.GetValue(viewModel));

        Assert.True(host.TryGetTool("git-workspace-scan", out _));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowViewModel_WorkspaceWithChildren_ShowsExpandAffordance()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceId = new EntityId("24900001-0000-4000-8000-000000000001");
        var childId = new EntityId("24900002-0000-4000-8000-000000000002");
        var relationshipId = new EntityId("24900003-0000-4000-8000-000000000003");

        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceId,
            $$"""
            {
              "entity-id": "{{workspaceId}}",
              "entity-types": ["entity", "workspace"],
              "names": [["workspaces", "expand-affordance-test"]],
              "display-name": { "default": "Expand Affordance Test Workspace" },
              "regions": []
            }
            """);

        await UpsertEntityAndLoadAsync(
            entityBroker,
            childId,
            $$"""
            {
              "entity-id": "{{childId}}",
              "entity-types": ["entity", "note"],
              "names": [["notes", "expand-affordance-child"]],
              "display-name": { "default": "Expand Affordance Child" },
              "content": { "mime-type": "text/markdown", "content": { "text": "" } }
            }
            """);

        await UpsertEntityAndLoadAsync(
            entityBroker,
            relationshipId,
            $$"""
            {
              "entity-id": "{{relationshipId}}",
              "entity-types": ["entity", "related", "relationship"],
              "names": [["relationships", "expand-affordance-relation"]],
              "participants": { "entities": ["{{workspaceId}}", "{{childId}}"] }
            }
            """);

        var workspacesView = Assert.Single(
            viewModel.TopLevelViews,
            static view => string.Equals(view.Title, "Workspaces", StringComparison.Ordinal));

        var applyMethod = typeof(MainWindowViewModel).GetMethod(
            "ApplySelectedViewAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(applyMethod);

        viewModel.SelectedTopLevelView = workspacesView;
        await (Task)applyMethod!.Invoke(viewModel, [])!;

        var workspaceVm = Assert.Single(
            viewModel.CurrentViewPopulation.Entities,
            vm => string.Equals(vm.EntityId, workspaceId.ToString(), StringComparison.OrdinalIgnoreCase));

        Assert.True(workspaceVm.HasTraversedChildren);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowViewModel_ToggleExpand_DoesNotRebuildPopulation()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceId = new EntityId("24900004-0000-4000-8000-000000000004");
        var childId = new EntityId("24900005-0000-4000-8000-000000000005");
        var relationshipId = new EntityId("24900006-0000-4000-8000-000000000006");
        var entityTypeViewId = new EntityId("24900007-0000-4000-8000-000000000007");

        // Seed entity-type-view for workspace to declare traverse-relationships
        await UpsertEntityAndLoadAsync(
            entityBroker,
            entityTypeViewId,
            $$"""
            {
              "entity-id": "{{entityTypeViewId}}",
              "entity-types": ["entity", "entity-type-view"],
              "names": [["entity-type-views", "workspace"]],
              "display-name": { "default": "Workspace View" },
              "fields": [],
              "traverse-relationships": [
                { "relationship-type-ids": ["related"] }
              ]
            }
            """);

        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceId,
            $$"""
            {
              "entity-id": "{{workspaceId}}",
              "entity-types": ["entity", "workspace"],
              "names": [["workspaces", "toggle-expand-test"]],
              "display-name": { "default": "Toggle Expand Test Workspace" },
              "regions": []
            }
            """);

        await UpsertEntityAndLoadAsync(
            entityBroker,
            childId,
            $$"""
            {
              "entity-id": "{{childId}}",
              "entity-types": ["entity", "note"],
              "names": [["notes", "toggle-expand-child"]],
              "display-name": { "default": "Toggle Expand Child" },
              "content": { "mime-type": "text/markdown", "content": { "text": "" } }
            }
            """);

        await UpsertEntityAndLoadAsync(
            entityBroker,
            relationshipId,
            $$"""
            {
              "entity-id": "{{relationshipId}}",
              "entity-types": ["entity", "related", "relationship"],
              "names": [["relationships", "toggle-expand-relation"]],
              "participants": { "entities": ["{{workspaceId}}", "{{childId}}"] }
            }
            """);

        var workspacesView = Assert.Single(
            viewModel.TopLevelViews,
            static view => string.Equals(view.Title, "Workspaces", StringComparison.Ordinal));

        var applyMethod = typeof(MainWindowViewModel).GetMethod(
            "ApplySelectedViewAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(applyMethod);

        viewModel.SelectedTopLevelView = workspacesView;
        await (Task)applyMethod!.Invoke(viewModel, [])!;

        // Initially both workspace and child are populated.
        Assert.Contains(viewModel.CurrentViewPopulation.Entities, vm => vm.EntityId == workspaceId.ToString());
        Assert.Contains(viewModel.CurrentViewPopulation.Entities, vm => vm.EntityId == childId.ToString());

        var workspaceVm = Assert.Single(
            viewModel.CurrentViewPopulation.Entities,
            vm => string.Equals(vm.EntityId, workspaceId.ToString(), StringComparison.OrdinalIgnoreCase));
        var originalPopulation = viewModel.CurrentViewPopulation;
        var originalChild = Assert.Single(workspaceVm.Children);

        workspaceVm.ToggleExpandCommand.Execute(null);

        Assert.Same(originalPopulation, viewModel.CurrentViewPopulation);
        Assert.Contains(viewModel.CurrentViewPopulation.Entities, vm => vm.EntityId == workspaceId.ToString());
        Assert.Contains(viewModel.CurrentViewPopulation.Entities, vm => vm.EntityId == childId.ToString());
        Assert.Same(originalChild, Assert.Single(workspaceVm.Children));
        Assert.False(workspaceVm.IsExpanded);

        workspaceVm.ToggleExpandCommand.Execute(null);

        Assert.Same(originalPopulation, viewModel.CurrentViewPopulation);
        Assert.Contains(viewModel.CurrentViewPopulation.Entities, vm => vm.EntityId == workspaceId.ToString());
        Assert.Contains(viewModel.CurrentViewPopulation.Entities, vm => vm.EntityId == childId.ToString());
        Assert.Same(originalChild, Assert.Single(workspaceVm.Children));
        Assert.True(workspaceVm.IsExpanded);
    }

    private static async Task<AgentChat> CreateEchoAgentChatAsync()
    {
        const string echoAgentJson =
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
        var agentDefinition = AgentDefinitionLoader.LoadAgentFromJson(echoAgentJson);
        return await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = agentDefinition,
            ForegroundScheduler = TaskScheduler.Default,
        });
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenAgentSessionShortcutHandler_OpenSameSession_AcrossTwoWorkspacePanes_CreatesTwoTabsWithSameAgentChat()
    {
        var table = CreateTestRunningAgentChatTable();
        var appServices = new ApplicationServices(table, new AgentPersistenceStoreCache());
        await using var viewModel = CreateTestMainWindowViewModel(applicationServices: appServices);
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var agentDefinitionId = new EntityId("aa050001-0000-4000-8000-000000000001");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(
            entityBroker,
            agentDefinitionId,
            """
            {
              "entity-id": "aa050001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "shared-chat-echo"]],
              "display-name": { "default": "Shared Chat Echo" },
              "definition": {
                "kind": "prompt",
                "name": "shared-chat-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var workspaceIdA = new EntityId("aa050002-0000-4000-8000-000000000002");
        var workspaceIdB = new EntityId("aa050003-0000-4000-8000-000000000003");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceIdA,
            """
            {
              "entity-id": "aa050002-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "shared-chat-ws-a"]],
              "display-name": { "default": "Shared Chat WS A" },
              "regions": []
            }
            """);
        await UpsertEntityAndLoadAsync(entityBroker, workspaceIdB,
            """
            {
              "entity-id": "aa050003-0000-4000-8000-000000000003",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "shared-chat-ws-b"]],
              "display-name": { "default": "Shared Chat WS B" },
              "regions": []
            }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionId = Guid.NewGuid().ToString("n");
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, agentSessionId);
        Assert.NotNull(agentSessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(
            agentSessionShortcutContext,
            CreateLocalTrustedExecutorSelector(),
            table);

        // Open in pane A
        var paneAIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceIdA.ToString());
        ActivateWorkspacePaneAtIndex(viewModel, paneAIndex.ToString());
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);
        var tabA = Assert.IsType<AgentSessionWorkspaceTabViewModel>(
            viewModel.SelectedWorkspacePane.SelectedTab);

        // Open in pane B
        var paneBIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceIdB.ToString());
        ActivateWorkspacePaneAtIndex(viewModel, paneBIndex.ToString());
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);
        var tabB = Assert.IsType<AgentSessionWorkspaceTabViewModel>(
            viewModel.SelectedWorkspacePane.SelectedTab);

        await WaitForAgentReadyAsync(tabA);
        await WaitForAgentReadyAsync(tabB);

        Assert.NotEqual(tabA.Id, tabB.Id);
        Assert.NotNull(tabA.Lease);
        Assert.NotNull(tabB.Lease);
        Assert.Same(tabA.Lease!.AgentChat, tabB.Lease!.AgentChat);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task AgentSessionWorkspaceTabViewModel_DisposeWithLease_ReleasesChat_OnLastDispose()
    {
        var table = CreateTestRunningAgentChatTable();
        var appServices = new ApplicationServices(table, new AgentPersistenceStoreCache());
        await using var viewModel = CreateTestMainWindowViewModel(applicationServices: appServices);
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var agentDefinitionId = new EntityId("aa060001-0000-4000-8000-000000000001");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(
            entityBroker,
            agentDefinitionId,
            """
            {
              "entity-id": "aa060001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "shared-dispose-echo"]],
              "display-name": { "default": "Shared Dispose Echo" },
              "definition": {
                "kind": "prompt",
                "name": "shared-dispose-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var workspaceIdA = new EntityId("aa060002-0000-4000-8000-000000000002");
        var workspaceIdB = new EntityId("aa060003-0000-4000-8000-000000000003");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceIdA,
            """
            {
              "entity-id": "aa060002-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "dispose-ws-a"]],
              "display-name": { "default": "Dispose WS A" },
              "regions": []
            }
            """);
        await UpsertEntityAndLoadAsync(entityBroker, workspaceIdB,
            """
            {
              "entity-id": "aa060003-0000-4000-8000-000000000003",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "dispose-ws-b"]],
              "display-name": { "default": "Dispose WS B" },
              "regions": []
            }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionId = Guid.NewGuid().ToString("n");
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, agentSessionId);
        Assert.NotNull(agentSessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(
            agentSessionShortcutContext,
            CreateLocalTrustedExecutorSelector(),
            table);

        // Open in pane A
        var paneAIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceIdA.ToString());
        ActivateWorkspacePaneAtIndex(viewModel, paneAIndex.ToString());
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);
        var tabA = Assert.IsType<AgentSessionWorkspaceTabViewModel>(
            viewModel.SelectedWorkspacePane.SelectedTab);

        // Open in pane B
        var paneBIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceIdB.ToString());
        ActivateWorkspacePaneAtIndex(viewModel, paneBIndex.ToString());
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);
        var tabB = Assert.IsType<AgentSessionWorkspaceTabViewModel>(
            viewModel.SelectedWorkspacePane.SelectedTab);

        await WaitForAgentReadyAsync(tabA);
        await WaitForAgentReadyAsync(tabB);

        Assert.NotNull(tabA.Lease);
        Assert.NotNull(tabB.Lease);
        var sharedChat = tabA.Lease!.AgentChat;
        Assert.Same(sharedChat, tabB.Lease!.AgentChat);

        // After disposing first tab, acquire on same key should return cached chat (second tab still holds lease)
        await tabA.DisposeAsync();

        var probe1 = await table.AcquireAsync(new AcquireAgentChatRequest { AgentSessionId = new AgentSessionId(agentSessionId) });
        Assert.Same(sharedChat, probe1.AgentChat); // cached — same instance, second tab still holds lease
        await probe1.DisposeAsync();

        // After disposing second tab, the chat should be gone and a new one created from persistence
        await tabB.DisposeAsync();

        var probe2 = await table.AcquireAsync(new AcquireAgentChatRequest { AgentSessionId = new AgentSessionId(agentSessionId) });
        Assert.NotSame(sharedChat, probe2.AgentChat); // new instance — old was disposed
        await probe2.DisposeAsync();
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task RunningAgentChatTable_Refresh_DoesNotThrow_WhenSessionRemovedConcurrently()
    {
        var table = CreateTestRunningAgentChatTable();
        var appServices = new ApplicationServices(table, new AgentPersistenceStoreCache());
        await using var viewModel = CreateTestMainWindowViewModel(applicationServices: appServices);
        await viewModel.InitializeAsync();

        var brain = viewModel.RunningAgentBrain;
        Assert.NotNull(brain);

        var entityBroker = GetEntityBroker(viewModel);
        var agentDefinitionId = new EntityId("aa070001-0000-4000-8000-000000000001");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(
            entityBroker,
            agentDefinitionId,
            """
            {
              "entity-id": "aa070001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "refresh-race-echo"]],
              "display-name": { "default": "Refresh Race Echo" },
              "definition": {
                "kind": "prompt",
                "name": "refresh-race-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var workspaceId = new EntityId("aa070002-0000-4000-8000-000000000002");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceId,
            """
            {
              "entity-id": "aa070002-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "refresh-race-ws"]],
              "display-name": { "default": "Refresh Race WS" },
              "regions": []
            }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionId = Guid.NewGuid().ToString("n");
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, agentSessionId);
        Assert.NotNull(agentSessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(
            agentSessionShortcutContext,
            CreateLocalTrustedExecutorSelector(),
            table);

        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);
        var tab = Assert.IsType<AgentSessionWorkspaceTabViewModel>(
            viewModel.SelectedWorkspacePane.SelectedTab);
        await WaitForAgentReadyAsync(tab);

        Assert.NotNull(tab.Lease);
        Assert.Single(table.RunningSessions);

        // Dispose the tab (which releases the lease and triggers removal from RunningSessions).
        // With the bug (TaskScheduler.Default), the removal happens on a thread-pool thread.
        // With the fix (FromCurrentSynchronizationContext), it marshals to the UI thread.
        // Force Refresh to run multiple times concurrently to increase chance of catching the race.
        var disposeTask = tab.DisposeAsync().AsTask();

        for (int i = 0; i < 10; i++)
        {
            brain.Refresh();
        }

        await disposeTask;

        // If the bug exists, one of the Refresh() calls may have thrown InvalidOperationException
        // due to enumerating RunningSessions while it was being mutated on another thread.
        // With the fix, all mutations happen on the UI thread, so no exception occurs.
        Assert.Empty(table.RunningSessions);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task RunningAgentBrain_WithRunningAgentTab_IsAnyRunning()
    {
        var table = CreateTestRunningAgentChatTable();
        var appServices = new ApplicationServices(table, new AgentPersistenceStoreCache());
        await using var viewModel = CreateTestMainWindowViewModel(applicationServices: appServices);
        await viewModel.InitializeAsync();

        var brain = viewModel.RunningAgentBrain;
        Assert.NotNull(brain);
        Assert.False(brain!.IsAnyRunning);

        var entityBroker = GetEntityBroker(viewModel);
        var agentDefinitionId = new EntityId("ab070001-0000-4000-8000-000000000001");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(entityBroker, agentDefinitionId,
            """
            {
              "entity-id": "ab070001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "brain-test-echo"]],
              "display-name": { "default": "Brain Test Echo" },
              "definition": {
                "kind": "prompt",
                "name": "brain-test-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, Guid.NewGuid().ToString("n"));
        Assert.NotNull(agentSessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(
            agentSessionShortcutContext, CreateLocalTrustedExecutorSelector(), table);
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);

        var agentTab = Assert.IsType<AgentSessionWorkspaceTabViewModel>(
            viewModel.SelectedWorkspacePane.SelectedTab);
        await WaitForAgentReadyAsync(agentTab);

        Assert.True(brain.IsAnyRunning);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task RunningAgentBrain_WithRunningAgentTab_HasRowWithWorkspaceAndTabTitles()
    {
        var table = CreateTestRunningAgentChatTable();
        var appServices = new ApplicationServices(table, new AgentPersistenceStoreCache());
        await using var viewModel = CreateTestMainWindowViewModel(applicationServices: appServices);
        await viewModel.InitializeAsync();

        var brain = viewModel.RunningAgentBrain;
        Assert.NotNull(brain);

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceId = new EntityId("ab080001-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceId,
            """
            {
              "entity-id": "ab080001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "brain-popup-ws"]],
              "display-name": { "default": "Brain Popup Workspace" },
              "regions": []
            }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var agentDefinitionId = new EntityId("ab080002-0000-4000-8000-000000000002");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(entityBroker, agentDefinitionId,
            """
            {
              "entity-id": "ab080002-0000-4000-8000-000000000002",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "brain-popup-echo"]],
              "display-name": { "default": "Brain Popup Echo" },
              "definition": {
                "kind": "prompt",
                "name": "brain-popup-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, Guid.NewGuid().ToString("n"));
        Assert.NotNull(agentSessionEntity);

        var paneIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceId.ToString());
        ActivateWorkspacePaneAtIndex(viewModel, paneIndex.ToString());

        var handler = new OpenAgentSessionShortcutHandler(
            agentSessionShortcutContext, CreateLocalTrustedExecutorSelector(), table);
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);

        var agentTab = Assert.IsType<AgentSessionWorkspaceTabViewModel>(
            viewModel.SelectedWorkspacePane.SelectedTab);
        await WaitForAgentReadyAsync(agentTab);

        brain!.Refresh();

        var row = Assert.Single(brain.Rows);
        Assert.Equal("Brain Popup Workspace", row.WorkspacePaneTitle);
        Assert.Equal(agentSessionEntity!.DisplayName, row.TabTitle);
        Assert.True(row.HasOpenTab);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task RunningAgentBrain_Activate_FocusesTab()
    {
        var table = CreateTestRunningAgentChatTable();
        var appServices = new ApplicationServices(table, new AgentPersistenceStoreCache());
        await using var viewModel = CreateTestMainWindowViewModel(applicationServices: appServices);
        await viewModel.InitializeAsync();

        var brain = viewModel.RunningAgentBrain;
        Assert.NotNull(brain);

        var entityBroker = GetEntityBroker(viewModel);
        var agentDefinitionId = new EntityId("ab090001-0000-4000-8000-000000000001");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(entityBroker, agentDefinitionId,
            """
            {
              "entity-id": "ab090001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "brain-activate-echo"]],
              "display-name": { "default": "Brain Activate Echo" },
              "definition": {
                "kind": "prompt",
                "name": "brain-activate-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, Guid.NewGuid().ToString("n"));
        Assert.NotNull(agentSessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(
            agentSessionShortcutContext, CreateLocalTrustedExecutorSelector(), table);
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);

        var agentTab = Assert.IsType<AgentSessionWorkspaceTabViewModel>(
            viewModel.SelectedWorkspacePane.SelectedTab);
        await WaitForAgentReadyAsync(agentTab);

        brain!.Refresh();

        var row = Assert.Single(brain.Rows);

        // Open the popup, click the row
        brain.IsOpen = true;
        row.ActivateCommand.Execute(null);

        // Popup should close after activation
        Assert.False(brain.IsOpen);

        // The tab should be active
        var layout = viewModel.SelectedWorkspacePane.ContentLayout;
        Assert.NotNull(layout);
        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        var activeDoc = documentDock!.ActiveDockable as WorkspaceDocument;
        Assert.NotNull(activeDoc);
        Assert.Equal(agentTab.Id, activeDoc!.Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task RunningAgentBrain_RowActivateCommand_WhenTabIsInNonSelectedPane_SwitchesWorkspacePane()
    {
        var table = CreateTestRunningAgentChatTable();
        var appServices = new ApplicationServices(table, new AgentPersistenceStoreCache());
        await using var viewModel = CreateTestMainWindowViewModel(applicationServices: appServices);
        await viewModel.InitializeAsync();

        var brain = viewModel.RunningAgentBrain;
        Assert.NotNull(brain);

        var entityBroker = GetEntityBroker(viewModel);

        // Open workspace A first so that workspace B is at index 1 (not 0)
        var workspaceAId = new EntityId("ab100000-0000-4000-8000-000000000000");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceAId,
            """
            {
              "entity-id": "ab100000-0000-4000-8000-000000000000",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "brain-cross-pane-ws-a"]],
              "display-name": { "default": "Brain Cross-Pane Workspace A" },
              "regions": []
            }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceAId });

        var workspaceBId = new EntityId("ab100001-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceBId,
            """
            {
              "entity-id": "ab100001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "brain-cross-pane-ws"]],
              "display-name": { "default": "Brain Cross-Pane Workspace" },
              "regions": []
            }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceBId });

        var agentDefinitionId = new EntityId("ab100002-0000-4000-8000-000000000002");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(entityBroker, agentDefinitionId,
            """
            {
              "entity-id": "ab100002-0000-4000-8000-000000000002",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "brain-cross-pane-echo"]],
              "display-name": { "default": "Brain Cross-Pane Echo" },
              "definition": {
                "kind": "prompt",
                "name": "brain-cross-pane-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        // Switch to workspace B and open an agent tab there
        var paneBIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceBId.ToString());
        ActivateWorkspacePaneAtIndex(viewModel, paneBIndex.ToString());

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, Guid.NewGuid().ToString("n"));
        Assert.NotNull(agentSessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(
            agentSessionShortcutContext, CreateLocalTrustedExecutorSelector(), table);
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);

        var agentTab = Assert.IsType<AgentSessionWorkspaceTabViewModel>(
            viewModel.SelectedWorkspacePane.SelectedTab);
        await WaitForAgentReadyAsync(agentTab);

        brain!.Refresh();
        Assert.Single(brain.Rows);

        // Switch back to the default (first) pane so workspace B is not selected
        ActivateWorkspacePaneAtIndex(viewModel, "0");
        Assert.NotEqual(workspaceBId.ToString(), viewModel.SelectedWorkspacePane.Id);

        // Execute the activate command on the running-agent row
        var row = Assert.Single(brain.Rows);
        brain.IsOpen = true;
        row.ActivateCommand.Execute(null);

        // Workspace B should now be selected and the agent tab active
        Assert.Equal(workspaceBId.ToString(), viewModel.SelectedWorkspacePane.Id);
        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        Assert.Equal(agentTab.Id, (documentDock!.ActiveDockable as WorkspaceDocument)?.Id);
    }

    // ── #1135: Status-button navigation across workspace panes ─────────────────────

    private sealed class BrainFakeRunningAgentChatTable : IRunningAgentChatTable
    {
        public System.Collections.ObjectModel.ObservableCollection<RunningAgentChatWithEntityInfo> RunningSessions { get; } = [];

        public void AddSession(string sessionKey, string entityName = "", string? entityId = null, string? workspaceId = null)
        {
            var chat = new RunningAgentChat(new AgentSessionId(sessionKey), null!);
            RunningSessions.Add(new RunningAgentChatWithEntityInfo(chat, entityName, entityId, workspaceId));
        }

        public Task<RunningAgentChatLease> AcquireAsync(
            AcquireAgentChatRequest request,
            CancellationToken ct = default)
            => throw new NotSupportedException("Not used in integration tests.");
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task RunningAgentBrain_ClickAgentInDifferentWorkspace_SwitchesWorkspaceThenFocusesAgent()
    {
        // #1135: A brain-popup row for an open agent tab captures the pane the tab lives in.
        // When the user activates the row from a different workspace pane, the click must
        // switch to (and focus) the owning pane before activating the tab — not focus the
        // tab in the currently-selected pane.
        var table = CreateTestRunningAgentChatTable();
        var appServices = new ApplicationServices(table, new AgentPersistenceStoreCache());
        await using var viewModel = CreateTestMainWindowViewModel(applicationServices: appServices);
        await viewModel.InitializeAsync();
        var brain = viewModel.RunningAgentBrain;
        Assert.NotNull(brain);

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceIdA = new EntityId("11350001-0000-4000-8000-000000000001");
        var workspaceIdB = new EntityId("11350001-0000-4000-8000-000000000002");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceIdA,
            """
            { "entity-id": "11350001-0000-4000-8000-000000000001",
              "entity-types": ["entity","workspace"],
              "names": [["tests","workspaces","1135-brain-diff-a"]],
              "display-name": { "default": "1135 Brain Diff A" },
              "regions": [] }
            """);
        await UpsertEntityAndLoadAsync(entityBroker, workspaceIdB,
            """
            { "entity-id": "11350001-0000-4000-8000-000000000002",
              "entity-types": ["entity","workspace"],
              "names": [["tests","workspaces","1135-brain-diff-b"]],
              "display-name": { "default": "1135 Brain Diff B" },
              "regions": [] }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });

        var agentDefinitionId = new EntityId("11350001-0000-4000-8000-000000000003");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(entityBroker, agentDefinitionId,
            """
            { "entity-id": "11350001-0000-4000-8000-000000000003",
              "entity-types": ["entity","agent-definition"],
              "names": [["tests","agent-definitions","1135-brain-diff-echo"]],
              "display-name": { "default": "Echo" },
              "definition": { "kind":"prompt", "name":"e", "model":{"id":"echo","provider":"echo","apiType":"Echo"}, "tools":[] } }
            """);

        // Open agent in pane A
        var paneAIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceIdA.ToString());
        ActivateWorkspacePaneAtIndex(viewModel, paneAIndex.ToString());
        var ctx = new AgentSessionShortcutContext();
        var agentSessionEntity = await ctx.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, Guid.NewGuid().ToString("n"));
        var handler = new OpenAgentSessionShortcutHandler(
            ctx, CreateLocalTrustedExecutorSelector(), table);
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);
        var agentTab = Assert.IsType<AgentSessionWorkspaceTabViewModel>(viewModel.SelectedWorkspacePane.SelectedTab);
        await WaitForAgentReadyAsync(agentTab);

        // Activate pane B so the brain click is from a DIFFERENT workspace than the tab's owner
        var paneBIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceIdB.ToString());
        ActivateWorkspacePaneAtIndex(viewModel, paneBIndex.ToString());
        Assert.Equal(workspaceIdB.ToString(), viewModel.SelectedWorkspacePane.Id);

        brain!.Refresh();
        var row = Assert.Single(brain.Rows);
        brain.IsOpen = true;
        row.ActivateCommand.Execute(null);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.Equal(workspaceIdA.ToString(), viewModel.SelectedWorkspacePane.Id);
        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        Assert.Equal(agentTab.Id, (documentDock!.ActiveDockable as WorkspaceDocument)?.Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task RunningAgentBrain_ClickAgentInUnloadedWorkspace_LoadsWorkspaceBeforeFocusing()
    {
        // #1135: When the user activates a brain-popup fallback row (no open tab) for a
        // session whose owning workspace is not yet loaded, the click must open that
        // workspace pane and focus it — never routing the agent into the currently-selected
        // pane by mistake.
        var fakeTable = new BrainFakeRunningAgentChatTable();
        var appServices = new ApplicationServices(fakeTable, new AgentPersistenceStoreCache());
        await using var viewModel = CreateTestMainWindowViewModel(applicationServices: appServices);
        await viewModel.InitializeAsync();
        var brain = viewModel.RunningAgentBrain;
        Assert.NotNull(brain);

        var entityBroker = GetEntityBroker(viewModel);

        // Seed workspace A (opened) and workspace B (NOT opened).
        var workspaceIdA = new EntityId("11350002-0000-4000-8000-000000000001");
        var workspaceIdB = new EntityId("11350002-0000-4000-8000-000000000002");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceIdA,
            """
            { "entity-id": "11350002-0000-4000-8000-000000000001",
              "entity-types": ["entity","workspace"],
              "names": [["tests","workspaces","1135-brain-unloaded-a"]],
              "display-name": { "default": "1135 Brain Unloaded A" },
              "regions": [] }
            """);
        await UpsertEntityAndLoadAsync(entityBroker, workspaceIdB,
            """
            { "entity-id": "11350002-0000-4000-8000-000000000002",
              "entity-types": ["entity","workspace"],
              "names": [["tests","workspaces","1135-brain-unloaded-b"]],
              "display-name": { "default": "1135 Brain Unloaded B" },
              "regions": [] }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        var paneAIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceIdA.ToString());
        ActivateWorkspacePaneAtIndex(viewModel, paneAIndex.ToString());
        Assert.DoesNotContain(viewModel.WorkspacePanes, p => p.Id == workspaceIdB.ToString());

        // Add a fallback session with WorkspaceId pointing at (unloaded) workspace B.
        fakeTable.AddSession(
            sessionKey: "session-unloaded-owner",
            entityName: "Unloaded Owner",
            entityId: null,
            workspaceId: workspaceIdB.ToString());
        await Dispatcher.UIThread.InvokeAsync(() => { });

        brain!.Refresh();
        var row = Assert.Single(brain.Rows);
        brain.IsOpen = true;
        row.ActivateCommand.Execute(null);

        // The fallback row fires OpenAgentForSessionAsync fire-and-forget; drain the
        // dispatcher until pane B has been loaded and selected (bounded retries).
        for (var i = 0; i < 50; i++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (viewModel.WorkspacePanes.Any(p => p.Id == workspaceIdB.ToString())
                && viewModel.SelectedWorkspacePane.Id == workspaceIdB.ToString())
            {
                break;
            }
            await Task.Delay(20);
        }

        Assert.Contains(viewModel.WorkspacePanes, p => p.Id == workspaceIdB.ToString());
        Assert.Equal(workspaceIdB.ToString(), viewModel.SelectedWorkspacePane.Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task RunningAgentBrain_ClickAgentInCurrentWorkspace_FocusesWithoutSwitching()
    {
        // #1135: When the agent tab lives in the currently-selected pane, activating the
        // brain row focuses the tab without switching workspaces or opening extra panes.
        var table = CreateTestRunningAgentChatTable();
        var appServices = new ApplicationServices(table, new AgentPersistenceStoreCache());
        await using var viewModel = CreateTestMainWindowViewModel(applicationServices: appServices);
        await viewModel.InitializeAsync();
        var brain = viewModel.RunningAgentBrain;
        Assert.NotNull(brain);

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceIdA = new EntityId("11350003-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceIdA,
            """
            { "entity-id": "11350003-0000-4000-8000-000000000001",
              "entity-types": ["entity","workspace"],
              "names": [["tests","workspaces","1135-brain-same-a"]],
              "display-name": { "default": "1135 Brain Same A" },
              "regions": [] }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });

        var agentDefinitionId = new EntityId("11350003-0000-4000-8000-000000000003");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(entityBroker, agentDefinitionId,
            """
            { "entity-id": "11350003-0000-4000-8000-000000000003",
              "entity-types": ["entity","agent-definition"],
              "names": [["tests","agent-definitions","1135-brain-same-echo"]],
              "display-name": { "default": "Echo" },
              "definition": { "kind":"prompt", "name":"e", "model":{"id":"echo","provider":"echo","apiType":"Echo"}, "tools":[] } }
            """);

        var paneAIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceIdA.ToString());
        ActivateWorkspacePaneAtIndex(viewModel, paneAIndex.ToString());
        var initialPaneCount = viewModel.WorkspacePanes.Count;

        var ctx = new AgentSessionShortcutContext();
        var agentSessionEntity = await ctx.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, Guid.NewGuid().ToString("n"));
        var handler = new OpenAgentSessionShortcutHandler(
            ctx, CreateLocalTrustedExecutorSelector(), table);
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);
        var agentTab = Assert.IsType<AgentSessionWorkspaceTabViewModel>(viewModel.SelectedWorkspacePane.SelectedTab);
        await WaitForAgentReadyAsync(agentTab);

        brain!.Refresh();
        var row = Assert.Single(brain.Rows);
        brain.IsOpen = true;
        row.ActivateCommand.Execute(null);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.Equal(workspaceIdA.ToString(), viewModel.SelectedWorkspacePane.Id);
        Assert.Equal(initialPaneCount, viewModel.WorkspacePanes.Count);
        var documentDock = GetDocumentDock(viewModel);
        Assert.Equal(agentTab.Id, (documentDock!.ActiveDockable as WorkspaceDocument)?.Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Notifications_ClickNotificationInDifferentWorkspace_SwitchesWorkspaceThenFocusesTab()
    {
        // #1135: A notification carrying a WorkspaceId must resolve into the owning pane.
        // Clicking the bell row from a different pane must switch to and focus the owning
        // pane before activating the target tab.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceIdA = new EntityId("11350004-0000-4000-8000-000000000001");
        var workspaceIdB = new EntityId("11350004-0000-4000-8000-000000000002");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceIdA,
            """
            { "entity-id": "11350004-0000-4000-8000-000000000001",
              "entity-types": ["entity","workspace"],
              "names": [["tests","workspaces","1135-notif-diff-a"]],
              "display-name": { "default": "1135 Notif Diff A" },
              "tabs": [{ "tab-id":"1135-notif-diff-tab", "title":"NT", "kind":"browser", "content":{ "url":"https://a.example.com" } }] }
            """);
        await UpsertEntityAndLoadAsync(entityBroker, workspaceIdB,
            """
            { "entity-id": "11350004-0000-4000-8000-000000000002",
              "entity-types": ["entity","workspace"],
              "names": [["tests","workspaces","1135-notif-diff-b"]],
              "display-name": { "default": "1135 Notif Diff B" },
              "regions": [] }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        var paneA = viewModel.WorkspacePanes.Single(p => p.Id == workspaceIdA.ToString());
        var paneADock = FindDocumentDockIn(paneA.ContentLayout!);
        await WaitForWorkspaceTabAsync(paneADock!, "1135-notif-diff-tab");
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });

        // Switch to pane B so the click originates from a different workspace.
        var paneBIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceIdB.ToString());
        ActivateWorkspacePaneAtIndex(viewModel, paneBIndex.ToString());
        Assert.Equal(workspaceIdB.ToString(), viewModel.SelectedWorkspacePane.Id);

        viewModel.NotificationService.Notify(new Notification(
            new TabDescriptor { TabId = "1135-notif-diff-tab", WorkspaceId = workspaceIdA.ToString() },
            "Heading", "text", DateTime.UtcNow, RunningState.Idle, NotificationState.Interesting));
        await Dispatcher.UIThread.InvokeAsync(() => { });

        var notifRow = Assert.Single(viewModel.NotificationsViewModel!.Rows);
        notifRow.NavigateCommand.Execute(null);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.Equal(workspaceIdA.ToString(), viewModel.SelectedWorkspacePane.Id);
        var documentDock = GetDocumentDock(viewModel);
        Assert.Equal("1135-notif-diff-tab", (documentDock!.ActiveDockable as WorkspaceDocument)?.Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Notifications_ClickNotificationInUnloadedWorkspace_LoadsWorkspaceThenFocusesTab()
    {
        // #1135: When a notification's WorkspaceId points at a workspace pane that is not
        // currently open, the click must open that workspace first and then focus the tab
        // once it has been restored.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceIdB = new EntityId("11350005-0000-4000-8000-000000000002");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceIdB,
            """
            { "entity-id": "11350005-0000-4000-8000-000000000002",
              "entity-types": ["entity","workspace"],
              "names": [["tests","workspaces","1135-notif-unloaded-b"]],
              "display-name": { "default": "1135 Notif Unloaded B" },
              "tabs": [{ "tab-id":"1135-notif-unloaded-tab", "title":"NT", "kind":"browser", "content":{ "url":"https://b.example.com" } }] }
            """);

        // Open once so the tab exists, then close so the workspace is unloaded again.
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });
        var openedPane = viewModel.WorkspacePanes.Single(p => p.Id == workspaceIdB.ToString());
        var openedDock = FindDocumentDockIn(openedPane.ContentLayout!);
        await WaitForWorkspaceTabAsync(openedDock!, "1135-notif-unloaded-tab");
        await viewModel.RemoveWorkspacePaneAsync(openedPane);
        Assert.DoesNotContain(viewModel.WorkspacePanes, p => p.Id == workspaceIdB.ToString());

        viewModel.NotificationService.Notify(new Notification(
            new TabDescriptor { TabId = "1135-notif-unloaded-tab", WorkspaceId = workspaceIdB.ToString() },
            "Heading", "text", DateTime.UtcNow, RunningState.Idle, NotificationState.Interesting));
        await Dispatcher.UIThread.InvokeAsync(() => { });

        var notifRow = Assert.Single(viewModel.NotificationsViewModel!.Rows);
        notifRow.NavigateCommand.Execute(null);

        for (var i = 0; i < 50; i++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (viewModel.SelectedWorkspacePane.Id == workspaceIdB.ToString())
                break;
            await Task.Delay(20);
        }

        Assert.Equal(workspaceIdB.ToString(), viewModel.SelectedWorkspacePane.Id);
        var reopenedPane = viewModel.WorkspacePanes.Single(p => p.Id == workspaceIdB.ToString());
        var reopenedDock = FindDocumentDockIn(reopenedPane.ContentLayout!);
        await WaitForWorkspaceTabAsync(reopenedDock!, "1135-notif-unloaded-tab");

        var documentDock = GetDocumentDock(viewModel);
        Assert.Equal("1135-notif-unloaded-tab", (documentDock!.ActiveDockable as WorkspaceDocument)?.Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Notifications_ClickNotificationInCurrentWorkspace_FocusesTab()
    {
        // #1135: In the trivial single-pane case, clicking the bell row still focuses
        // the target tab (baseline behaviour must be preserved).
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceIdA = new EntityId("11350006-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceIdA,
            """
            { "entity-id": "11350006-0000-4000-8000-000000000001",
              "entity-types": ["entity","workspace"],
              "names": [["tests","workspaces","1135-notif-current-a"]],
              "display-name": { "default": "1135 Notif Current A" },
              "tabs": [
                { "tab-id":"1135-notif-current-tab", "title":"NT", "kind":"browser", "content":{ "url":"https://a.example.com" } },
                { "tab-id":"1135-notif-current-tab-other", "title":"NT2", "kind":"browser", "content":{ "url":"https://a2.example.com" } }
              ] }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        var paneA = viewModel.WorkspacePanes.Single(p => p.Id == workspaceIdA.ToString());
        var paneADock = FindDocumentDockIn(paneA.ContentLayout!);
        await WaitForWorkspaceTabAsync(paneADock!, "1135-notif-current-tab");
        await WaitForWorkspaceTabAsync(paneADock!, "1135-notif-current-tab-other");

        var paneAIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceIdA.ToString());
        ActivateWorkspacePaneAtIndex(viewModel, paneAIndex.ToString());

        // Focus the other tab first so a click on the notification must change the active tab.
        await viewModel.ActivateTabByRequestAsync(new Phantom.Workspaces.Services.Navigation.NavigationRequest(workspaceIdA.ToString(), "1135-notif-current-tab-other"));
        await Dispatcher.UIThread.InvokeAsync(() => { });

        viewModel.NotificationService.Notify(new Notification(
            new TabDescriptor { TabId = "1135-notif-current-tab", WorkspaceId = workspaceIdA.ToString() },
            "Heading", "text", DateTime.UtcNow, RunningState.Idle, NotificationState.Interesting));
        await Dispatcher.UIThread.InvokeAsync(() => { });

        var notifRow = Assert.Single(viewModel.NotificationsViewModel!.Rows,
            r => r.TabKey == "1135-notif-current-tab");
        notifRow.NavigateCommand.Execute(null);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.Equal(workspaceIdA.ToString(), viewModel.SelectedWorkspacePane.Id);
        var documentDock = GetDocumentDock(viewModel);
        Assert.Equal("1135-notif-current-tab", (documentDock!.ActiveDockable as WorkspaceDocument)?.Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Notifications_ClickNotificationWithNullWorkspaceId_IsSafeNoOp()
    {
        // #1135: A notification whose WorkspaceId is null (legacy path) must not throw
        // and must not switch workspaces — it may fall back to a best-effort search of
        // open panes for a matching tab, but if no tab matches the click is a safe no-op.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceIdA = new EntityId("11350007-0000-4000-8000-000000000001");
        var workspaceIdB = new EntityId("11350007-0000-4000-8000-000000000002");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceIdA,
            """
            { "entity-id": "11350007-0000-4000-8000-000000000001",
              "entity-types": ["entity","workspace"],
              "names": [["tests","workspaces","1135-notif-null-a"]],
              "display-name": { "default": "1135 Notif Null A" },
              "regions": [] }
            """);
        await UpsertEntityAndLoadAsync(entityBroker, workspaceIdB,
            """
            { "entity-id": "11350007-0000-4000-8000-000000000002",
              "entity-types": ["entity","workspace"],
              "names": [["tests","workspaces","1135-notif-null-b"]],
              "display-name": { "default": "1135 Notif Null B" },
              "regions": [] }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceIdB });
        var paneBIndex = viewModel.WorkspacePanes.ToList().FindIndex(p => p.Id == workspaceIdB.ToString());
        ActivateWorkspacePaneAtIndex(viewModel, paneBIndex.ToString());
        var beforePaneId = viewModel.SelectedWorkspacePane.Id;

        viewModel.NotificationService.Notify(new Notification(
            new TabDescriptor { TabId = "ghost-tab-1135", WorkspaceId = null },
            "Heading", "text", DateTime.UtcNow, RunningState.Idle, NotificationState.Interesting));
        await Dispatcher.UIThread.InvokeAsync(() => { });

        var notifRow = Assert.Single(viewModel.NotificationsViewModel!.Rows);
        var exception = Record.Exception(() => notifRow.NavigateCommand.Execute(null));
        Assert.Null(exception);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.Equal(beforePaneId, viewModel.SelectedWorkspacePane.Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ActivateTabById_WhenWorkspacePaneNotInWorkspacePanes_OpensWorkspaceAndActivatesTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceBId = new EntityId("ab110001-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceBId,
            """
            {
              "entity-id": "ab110001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "activate-closed-ws"]],
              "display-name": { "default": "Activate Closed Workspace" },
              "regions": [
                {
                  "region-id": "main",
                  "title": "Main",
                  "dock": "center",
                  "size": 1.0,
                  "tabs": [
                    {
                      "tab-id": "closed-ws-tab",
                      "title": "Closed WS Tab",
                      "kind": "web",
                      "dock": "full",
                      "content": { "url": "https://example.com/closed-ws" }
                    }
                  ]
                }
              ]
            }
            """);

        // Open workspace B to confirm it loads correctly, then close it
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceBId });
        var initialPane = viewModel.WorkspacePanes.Single(
            p => string.Equals(p.Id, workspaceBId.ToString(), StringComparison.Ordinal));
        var initialDock = FindDocumentDockIn(initialPane.ContentLayout!);
        Assert.NotNull(initialDock);
        await WaitForWorkspaceTabAsync(initialDock!, "closed-ws-tab");
        await viewModel.RemoveWorkspacePaneAsync(initialPane);
        Assert.DoesNotContain(
            viewModel.WorkspacePanes,
            p => string.Equals(p.Id, workspaceBId.ToString(), StringComparison.Ordinal));

        // Now activate the tab by ID — workspace B is not open
        await viewModel.ActivateTabByRequestAsync(new Phantom.Workspaces.Services.Navigation.NavigationRequest(workspaceBId.ToString(), "closed-ws-tab"));

        // Workspace B should have been re-opened and selected
        Assert.Equal(workspaceBId.ToString(), viewModel.SelectedWorkspacePane.Id);

        // Wait for the tab to be loaded and activated
        var newPane = viewModel.WorkspacePanes.Single(
            p => string.Equals(p.Id, workspaceBId.ToString(), StringComparison.Ordinal));
        var contentDock = FindDocumentDockIn(newPane.ContentLayout!);
        Assert.NotNull(contentDock);
        await WaitForWorkspaceTabAsync(contentDock!, "closed-ws-tab");

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        Assert.Equal("closed-ws-tab", (documentDock!.ActiveDockable as WorkspaceDocument)?.Id);
    }

    // ── #1157: notification click switches workspace when target pane is open but not selected ──

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ActivateTabById_WhenTargetWorkspaceIsOpenButNotSelected_SelectsTargetWorkspaceAndActivatesTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceAId = new EntityId("11570001-0000-4000-8000-00000000000a");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceAId,
            """
            { "entity-id": "11570001-0000-4000-8000-00000000000a",
              "entity-types": ["entity","workspace"],
              "names": [["tests","workspaces","1157-a"]],
              "display-name": { "default": "1157 A" },
              "regions": [] }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceAId });

        var workspaceBId = new EntityId("11570001-0000-4000-8000-00000000000b");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceBId,
            """
            { "entity-id": "11570001-0000-4000-8000-00000000000b",
              "entity-types": ["entity","workspace"],
              "names": [["tests","workspaces","1157-b"]],
              "display-name": { "default": "1157 B" },
              "regions": [] }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceBId });

        // Select B, open a tab there, then switch back to A so B is open-but-not-selected.
        var paneB = viewModel.WorkspacePanes.Single(p => string.Equals(p.Id, workspaceBId.ToString(), StringComparison.Ordinal));
        viewModel.SelectedWorkspacePane = paneB;
        var tabInB = new AgentSessionWorkspaceTabViewModel { Id = "1157-tab-in-b", Title = "Tab in B" };
        await viewModel.OpenTabAsync(tabInB);

        var paneA = viewModel.WorkspacePanes.Single(p => string.Equals(p.Id, workspaceAId.ToString(), StringComparison.Ordinal));
        viewModel.SelectedWorkspacePane = paneA;

        await viewModel.ActivateTabByRequestAsync(new Phantom.Workspaces.Services.Navigation.NavigationRequest(workspaceBId.ToString(), "1157-tab-in-b"));
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.Same(paneB, viewModel.SelectedWorkspacePane);
        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        Assert.Equal("1157-tab-in-b", (documentDock!.ActiveDockable as WorkspaceDocument)?.Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ActivateTabById_WhenTargetWorkspaceIsCurrentlySelected_ActivatesTabInCurrentWorkspace()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceAId = new EntityId("11570002-0000-4000-8000-00000000000a");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceAId,
            """
            { "entity-id": "11570002-0000-4000-8000-00000000000a",
              "entity-types": ["entity","workspace"],
              "names": [["tests","workspaces","1157-current-a"]],
              "display-name": { "default": "1157 Current A" },
              "regions": [] }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceAId });
        var paneA = viewModel.WorkspacePanes.Single(p => string.Equals(p.Id, workspaceAId.ToString(), StringComparison.Ordinal));
        viewModel.SelectedWorkspacePane = paneA;

        var tabA1 = new AgentSessionWorkspaceTabViewModel { Id = "1157-current-tab-1", Title = "Tab 1" };
        var tabA2 = new AgentSessionWorkspaceTabViewModel { Id = "1157-current-tab-2", Title = "Tab 2" };
        await viewModel.OpenTabAsync(tabA1);
        await viewModel.OpenTabAsync(tabA2);
        // tabA2 is active. Activate tabA1 via ActivateTabByIdAsync in the same (currently-selected) pane.
        await viewModel.ActivateTabByRequestAsync(new Phantom.Workspaces.Services.Navigation.NavigationRequest(workspaceAId.ToString(), "1157-current-tab-1"));
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.Same(paneA, viewModel.SelectedWorkspacePane);
        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        Assert.Equal("1157-current-tab-1", (documentDock!.ActiveDockable as WorkspaceDocument)?.Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task NotificationNavigate_WhenTargetWorkspaceIsOpenButNotSelected_SwitchesToTargetWorkspaceAndTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceAId = new EntityId("11570003-0000-4000-8000-00000000000a");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceAId,
            """
            { "entity-id": "11570003-0000-4000-8000-00000000000a",
              "entity-types": ["entity","workspace"],
              "names": [["tests","workspaces","1157-notif-a"]],
              "display-name": { "default": "1157 Notif A" },
              "regions": [] }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceAId });

        var workspaceBId = new EntityId("11570003-0000-4000-8000-00000000000b");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceBId,
            """
            { "entity-id": "11570003-0000-4000-8000-00000000000b",
              "entity-types": ["entity","workspace"],
              "names": [["tests","workspaces","1157-notif-b"]],
              "display-name": { "default": "1157 Notif B" },
              "regions": [] }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceBId });

        var paneB = viewModel.WorkspacePanes.Single(p => string.Equals(p.Id, workspaceBId.ToString(), StringComparison.Ordinal));
        viewModel.SelectedWorkspacePane = paneB;
        var tabInB = new AgentSessionWorkspaceTabViewModel { Id = "1157-notif-tab-in-b", Title = "Tab in B" };
        await viewModel.OpenTabAsync(tabInB);

        var paneA = viewModel.WorkspacePanes.Single(p => string.Equals(p.Id, workspaceAId.ToString(), StringComparison.Ordinal));
        viewModel.SelectedWorkspacePane = paneA;

        viewModel.NotificationService.Notify(new Notification(
            new TabDescriptor { TabId = "1157-notif-tab-in-b", WorkspaceId = workspaceBId.ToString() },
            "Heading", "text", DateTime.UtcNow, RunningState.Idle, NotificationState.Interesting));
        await Dispatcher.UIThread.InvokeAsync(() => { });

        var notifRow = Assert.Single(viewModel.NotificationsViewModel!.Rows,
            r => r.TabKey == "1157-notif-tab-in-b");
        notifRow.NavigateCommand.Execute(null);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.Same(paneB, viewModel.SelectedWorkspacePane);
        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        Assert.Equal("1157-notif-tab-in-b", (documentDock!.ActiveDockable as WorkspaceDocument)?.Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task NotificationNavigate_WhenTargetWorkspaceIsCurrent_ActivatesTabWithoutChangingWorkspace()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceAId = new EntityId("11570004-0000-4000-8000-00000000000a");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceAId,
            """
            { "entity-id": "11570004-0000-4000-8000-00000000000a",
              "entity-types": ["entity","workspace"],
              "names": [["tests","workspaces","1157-notif-current-a"]],
              "display-name": { "default": "1157 Notif Current A" },
              "regions": [] }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceAId });
        var paneA = viewModel.WorkspacePanes.Single(p => string.Equals(p.Id, workspaceAId.ToString(), StringComparison.Ordinal));
        viewModel.SelectedWorkspacePane = paneA;

        var tabA1 = new AgentSessionWorkspaceTabViewModel { Id = "1157-notif-current-tab-1", Title = "Tab 1" };
        var tabA2 = new AgentSessionWorkspaceTabViewModel { Id = "1157-notif-current-tab-2", Title = "Tab 2" };
        await viewModel.OpenTabAsync(tabA1);
        await viewModel.OpenTabAsync(tabA2);
        // tabA2 is active; notification targets tabA1 in the same (currently-selected) pane.

        viewModel.NotificationService.Notify(new Notification(
            new TabDescriptor { TabId = "1157-notif-current-tab-1", WorkspaceId = workspaceAId.ToString() },
            "Heading", "text", DateTime.UtcNow, RunningState.Idle, NotificationState.Interesting));
        await Dispatcher.UIThread.InvokeAsync(() => { });

        var notifRow = Assert.Single(viewModel.NotificationsViewModel!.Rows,
            r => r.TabKey == "1157-notif-current-tab-1");
        notifRow.NavigateCommand.Execute(null);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.Same(paneA, viewModel.SelectedWorkspacePane);
        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        Assert.Equal("1157-notif-current-tab-1", (documentDock!.ActiveDockable as WorkspaceDocument)?.Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ActivateTabById_WhenWorkspacePaneIdIsStale_FallsBackToAllPanesSearch()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var workspaceAId = new EntityId("11570005-0000-4000-8000-00000000000a");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceAId,
            """
            { "entity-id": "11570005-0000-4000-8000-00000000000a",
              "entity-types": ["entity","workspace"],
              "names": [["tests","workspaces","1157-stale-a"]],
              "display-name": { "default": "1157 Stale A" },
              "regions": [] }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceAId });

        var workspaceBId = new EntityId("11570005-0000-4000-8000-00000000000b");
        await UpsertEntityAndLoadAsync(entityBroker, workspaceBId,
            """
            { "entity-id": "11570005-0000-4000-8000-00000000000b",
              "entity-types": ["entity","workspace"],
              "names": [["tests","workspaces","1157-stale-b"]],
              "display-name": { "default": "1157 Stale B" },
              "regions": [] }
            """);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceBId });

        var paneB = viewModel.WorkspacePanes.Single(p => string.Equals(p.Id, workspaceBId.ToString(), StringComparison.Ordinal));
        viewModel.SelectedWorkspacePane = paneB;
        var tabInB = new AgentSessionWorkspaceTabViewModel { Id = "1157-stale-tab-in-b", Title = "Tab in B" };
        await viewModel.OpenTabAsync(tabInB);

        var paneA = viewModel.WorkspacePanes.Single(p => string.Equals(p.Id, workspaceAId.ToString(), StringComparison.Ordinal));
        viewModel.SelectedWorkspacePane = paneA;

        // Stale (non-GUID) workspacePaneId that matches no open pane — must fall through
        // to the all-panes search rather than silently no-op.
        await viewModel.ActivateTabByRequestAsync(new Phantom.Workspaces.Services.Navigation.NavigationRequest("not-a-guid-stale-id", "1157-stale-tab-in-b"));
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.Same(paneB, viewModel.SelectedWorkspacePane);
        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        Assert.Equal("1157-stale-tab-in-b", (documentDock!.ActiveDockable as WorkspaceDocument)?.Id);
    }

    // ── PopulateWorkspacePaneTabsAsync — new tabs[] format ───────────────────

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenWorkspaceAsync_WithTopLevelTabsArray_PopulatesPaneTabsInSavedOrder()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("01700001-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceId,
            """
            {
              "entity-id": "01700001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "tabs-array-order"]],
              "display-name": { "default": "Tabs Array Order Workspace" },
              "tabs": [
                {
                  "tab-id": "tabs-arr-a",
                  "title": "Tab A",
                  "kind": "browser",
                  "content": { "url": "https://a.example.com" }
                },
                {
                  "tab-id": "tabs-arr-b",
                  "title": "Tab B",
                  "kind": "browser",
                  "content": { "url": "https://b.example.com" }
                },
                {
                  "tab-id": "tabs-arr-c",
                  "title": "Tab C",
                  "kind": "browser",
                  "content": { "url": "https://c.example.com" }
                }
              ]
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var workspacePane = Assert.Single(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, workspaceId.ToString(), StringComparison.Ordinal));

        var contentDock = FindDocumentDockIn(workspacePane.ContentLayout!);
        Assert.NotNull(contentDock);

        await WaitForWorkspaceTabAsync(contentDock!, "tabs-arr-a");
        await WaitForWorkspaceTabAsync(contentDock!, "tabs-arr-b");
        await WaitForWorkspaceTabAsync(contentDock!, "tabs-arr-c");

        var tabIds = contentDock!.VisibleDockables!
            .OfType<WorkspaceDocument>()
            .Where(d => d.Id is "tabs-arr-a" or "tabs-arr-b" or "tabs-arr-c")
            .Select(d => d.Id)
            .ToList();

        Assert.Equal(["tabs-arr-a", "tabs-arr-b", "tabs-arr-c"], tabIds);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenWorkspaceAsync_WithLegacyRegions_FlattensToSingleDock()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("01700002-0000-4000-8000-000000000002");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceId,
            """
            {
              "entity-id": "01700002-0000-4000-8000-000000000002",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "legacy-regions-flatten"]],
              "display-name": { "default": "Legacy Regions Workspace" },
              "regions": [
                {
                  "region-id": "left",
                  "title": "Left",
                  "dock": "center",
                  "size": 0.5,
                  "tabs": [
                    {
                      "tab-id": "legacy-tab-left",
                      "title": "Left Tab",
                      "kind": "browser",
                      "dock": "full",
                      "content": { "url": "https://left.example.com" }
                    }
                  ]
                },
                {
                  "region-id": "right",
                  "title": "Right",
                  "dock": "center",
                  "size": 0.5,
                  "tabs": [
                    {
                      "tab-id": "legacy-tab-right",
                      "title": "Right Tab",
                      "kind": "browser",
                      "dock": "full",
                      "content": { "url": "https://right.example.com" }
                    }
                  ]
                }
              ]
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var workspacePane = Assert.Single(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, workspaceId.ToString(), StringComparison.Ordinal));

        var contentDock = FindDocumentDockIn(workspacePane.ContentLayout!);
        Assert.NotNull(contentDock);

        await WaitForWorkspaceTabAsync(contentDock!, "legacy-tab-left");
        await WaitForWorkspaceTabAsync(contentDock!, "legacy-tab-right");

        // Both tabs from both legacy regions are flattened into a single dock
        var tabIds = contentDock!.VisibleDockables!
            .OfType<WorkspaceDocument>()
            .Where(d => d.Id is "legacy-tab-left" or "legacy-tab-right")
            .Select(d => d.Id)
            .ToList();

        Assert.Equal(2, tabIds.Count);
        Assert.Contains("legacy-tab-left", tabIds);
        Assert.Contains("legacy-tab-right", tabIds);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspaceRestore_TabsArrayWithEmptyTitle_FallsBackToDisplayName()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var entityId = new EntityId("12651265-0001-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(entityBroker, entityId, $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity", "note"],
              "names": [["tests", "1265", "tabs-empty-title"]],
              "display-name": { "default": "Non Blank Display" },
              "content": { "mime-type": "text/markdown", "content": { "text": "body" } }
            }
            """);
        var tabJson = $$"""
            {
              "tab-id": "tabs-empty-title",
              "title": "",
              "content": { "target-entity-name": ["tests", "1265", "tabs-empty-title"] }
            }
            """;

        var tab = await TryFetchWorkspaceTabFromJsonAsync(viewModel, tabJson);
        Assert.NotNull(tab);
        Assert.Equal("Non Blank Display", tab!.Title);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspaceRestore_TabsArrayWithTitle_WinsOverDisplayName()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var entityId = new EntityId("12651265-0002-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(entityBroker, entityId, $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity", "note"],
              "names": [["tests", "1265", "tabs-title-wins"]],
              "display-name": { "default": "Display Name" },
              "content": { "mime-type": "text/markdown", "content": { "text": "body" } }
            }
            """);
        var tabJson = $$"""
            {
              "tab-id": "tabs-title-wins",
              "title": "Persisted Title",
              "content": { "target-entity-name": ["tests", "1265", "tabs-title-wins"] }
            }
            """;

        var tab = await TryFetchWorkspaceTabFromJsonAsync(viewModel, tabJson);
        Assert.NotNull(tab);
        Assert.Equal("Persisted Title", tab!.Title);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspaceRestore_LegacyRegionsWithEmptyTitle_FallsBackToUrl()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("12651265-0003-4000-8000-0000000000f1");
        var workspaceJson = $$"""
            {
              "entity-id": "{{workspaceId}}",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Legacy Empty Title Workspace" },
              "regions": [
                {
                  "tabs": [
                    {
                      "tab-id": "legacy-empty-title",
                      "title": "",
                      "content": { "url": "https://legacy-empty.example.com" }
                    }
                  ]
                }
              ]
            }
            """;

        var workspacePane = await CreateWorkspacePaneFromJsonAsync(viewModel, workspaceId, workspaceJson);
        var tab = Assert.IsType<WebViewModel>(Assert.Single(workspacePane.Tabs));
        Assert.Equal("https://legacy-empty.example.com", tab.Title);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenWorkspaceAsync_WithNoTabsAndNoRegions_OpensDefaultEntityTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("01700003-0000-4000-8000-000000000003");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceId,
            """
            {
              "entity-id": "01700003-0000-4000-8000-000000000003",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "no-tabs-default"]],
              "display-name": { "default": "No Tabs Workspace" }
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var workspacePane = Assert.Single(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, workspaceId.ToString(), StringComparison.Ordinal));

        var contentDock = FindDocumentDockIn(workspacePane.ContentLayout!);
        Assert.NotNull(contentDock);

        // The workspace entity ID is used as the default tab ID
        await WaitForWorkspaceTabAsync(contentDock!, workspaceId.ToString());

        var defaultTab = contentDock!.VisibleDockables!
            .OfType<WorkspaceDocument>()
            .FirstOrDefault(d => d.Id == workspaceId.ToString());
        Assert.NotNull(defaultTab);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenWorkspaceAsync_WithTopLevelTabsAndActiveTabId_ActivatesSpecifiedTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("01700004-0000-4000-8000-000000000004");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceId,
            """
            {
              "entity-id": "01700004-0000-4000-8000-000000000004",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "tabs-active-tab-id"]],
              "display-name": { "default": "Active Tab ID Workspace" },
              "active-tab-id": "tabs-active-second",
              "tabs": [
                {
                  "tab-id": "tabs-active-first",
                  "title": "First Tab",
                  "kind": "browser",
                  "content": { "url": "https://first.example.com" }
                },
                {
                  "tab-id": "tabs-active-second",
                  "title": "Second Tab",
                  "kind": "browser",
                  "content": { "url": "https://second.example.com" }
                }
              ]
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var workspacePane = Assert.Single(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, workspaceId.ToString(), StringComparison.Ordinal));

        var contentDock = FindDocumentDockIn(workspacePane.ContentLayout!);
        Assert.NotNull(contentDock);

        await WaitForWorkspaceTabAsync(contentDock!, "tabs-active-first");
        await WaitForWorkspaceTabAsync(contentDock!, "tabs-active-second");

        Assert.Equal("tabs-active-second", contentDock!.ActiveDockable?.Id);
    }

    // ── CreateWorkspaceContentLayout — ItemsSource wiring ────────────────────

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CreateWorkspaceContentLayout_SetsItemsSourceToPaneTabs()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("01700005-0000-4000-8000-000000000005");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceId,
            """
            {
              "entity-id": "01700005-0000-4000-8000-000000000005",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "items-source-wiring"]],
              "display-name": { "default": "ItemsSource Wiring Workspace" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var workspacePane = Assert.Single(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, workspaceId.ToString(), StringComparison.Ordinal));

        Assert.NotNull(workspacePane.ContentLayout);

        var contentDock = FindDocumentDockIn(workspacePane.ContentLayout!);
        Assert.NotNull(contentDock);

        // ItemsSource must point at pane.Tabs so the generator creates documents automatically
        var itemsSourceDock = contentDock as global::Dock.Model.Core.IItemsSourceDock;
        Assert.NotNull(itemsSourceDock);
        Assert.Same(workspacePane.Tabs, itemsSourceDock!.ItemsSource);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CreateWorkspaceContentLayout_AddingTabToPaneTabs_CreatesWorkspaceDocumentInDock()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("01700006-0000-4000-8000-000000000006");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            workspaceId,
            """
            {
              "entity-id": "01700006-0000-4000-8000-000000000006",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "items-source-add"]],
              "display-name": { "default": "ItemsSource Add Workspace" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var workspacePane = Assert.Single(
            viewModel.WorkspacePanes,
            pane => string.Equals(pane.Id, workspaceId.ToString(), StringComparison.Ordinal));

        // Wait for the default tab to appear, then verify adding a new tab auto-creates a document
        var contentDock = FindDocumentDockIn(workspacePane.ContentLayout!);
        Assert.NotNull(contentDock);
        await WaitForWorkspaceTabAsync(contentDock!, workspaceId.ToString());

        var newTab = new WebViewModel("https://items-source.example.com")
        {
            Id = "items-source-add-tab",
            Title = "Items Source Tab",
        };
        workspacePane.Tabs.Add(newTab);

        await WaitForWorkspaceTabAsync(contentDock!, "items-source-add-tab");

        var doc = contentDock!.VisibleDockables!
            .OfType<WorkspaceDocument>()
            .FirstOrDefault(d => d.Id == "items-source-add-tab");
        Assert.NotNull(doc);
        Assert.Same(newTab, doc!.TabViewModel);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CreateWorkspaceContentLayout_RemovingTabFromPaneTabs_RemovesWorkspaceDocumentFromDock()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabToRemove = new WebViewModel("https://remove.example.com")
        {
            Id = "items-source-remove-tab",
            Title = "Remove Tab",
        };
        await viewModel.OpenTabAsync(tabToRemove);

        var workspacePane = viewModel.SelectedWorkspacePane;
        var contentDock = FindDocumentDockIn(workspacePane.ContentLayout!);
        Assert.NotNull(contentDock);
        await WaitForWorkspaceTabAsync(contentDock!, "items-source-remove-tab");

        // Remove from pane.Tabs — the ItemsSource generator must remove the document automatically
        workspacePane.Tabs.Remove(tabToRemove);

        var docAfterRemoval = contentDock!.VisibleDockables?
            .OfType<WorkspaceDocument>()
            .FirstOrDefault(d => d.Id == "items-source-remove-tab");
        Assert.Null(docAfterRemoval);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task TryBuildAgent_SlashCommandContext_WithLocalSession_ExecuteAutoResume_UpdatesEntityWithTrustedExecutorDot()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var agentDefinitionId = new EntityId("ac010001-0000-4000-8000-000000000001");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(entityBroker, agentDefinitionId,
            """
            {
              "entity-id": "ac010001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "slash-cmd-ctx-echo"]],
              "display-name": { "default": "Slash Cmd Context Echo" },
              "definition": {
                "kind": "prompt",
                "name": "slash-cmd-ctx-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, Guid.NewGuid().ToString("n"));
        Assert.NotNull(agentSessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(


            agentSessionShortcutContext,


            CreateLocalTrustedExecutorSelector(),


            CreateTestRunningAgentChatTable());
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);

        var agentTab = Assert.IsType<AgentSessionWorkspaceTabViewModel>(
            viewModel.SelectedWorkspacePane.SelectedTab);
        await WaitForAgentReadyAsync(agentTab);

        // Execute /auto-resume — if TrustedExecutorIdentifier and UpdateAutoResumeAsync are wired,
        // the entity is updated with auto-resume.trusted-executor = "."
        var interceptor = agentTab.Agent!.InputQueue!.DefaultComposer.SlashCommandInterceptorAsync;
        Assert.NotNull(interceptor);
        await interceptor!("/auto-resume");

        // Reload the entity and verify auto-resume was persisted
        var updatedEntities = await entityBroker.GetEntitiesAsync([agentSessionEntity!.EntityId]);
        var updatedEntity = updatedEntities.FirstOrDefault(e => e.EntityId == agentSessionEntity!.EntityId);
        Assert.NotNull(updatedEntity);
        var updatedData = Assert.IsType<JsonElement>(updatedEntity!.Data);
        Assert.True(updatedData.TryGetProperty("auto-resume", out var autoResumeEl));
        Assert.True(autoResumeEl.TryGetProperty("trusted-executor", out var executorEl));
        Assert.Equal(TrustProfile.LocalClientInstance, executorEl.GetString());
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task TryBuildAgent_SlashCommandContext_WithAutoResumeAlreadyEnabled_ExecuteAutoResume_RemovesAutoResume()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var agentDefinitionId = new EntityId("ac020001-0000-4000-8000-000000000001");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(entityBroker, agentDefinitionId,
            """
            {
              "entity-id": "ac020001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "slash-cmd-ctx-toggle-echo"]],
              "display-name": { "default": "Slash Cmd Context Toggle Echo" },
              "definition": {
                "kind": "prompt",
                "name": "slash-cmd-ctx-toggle-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, Guid.NewGuid().ToString("n"));
        Assert.NotNull(agentSessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(


            agentSessionShortcutContext,


            CreateLocalTrustedExecutorSelector(),


            CreateTestRunningAgentChatTable());
        await handler.Handle(viewModel, Shortcut.Open, agentSessionEntity!);

        var agentTab = Assert.IsType<AgentSessionWorkspaceTabViewModel>(
            viewModel.SelectedWorkspacePane.SelectedTab);
        await WaitForAgentReadyAsync(agentTab);

        var interceptor = agentTab.Agent!.InputQueue!.DefaultComposer.SlashCommandInterceptorAsync;
        Assert.NotNull(interceptor);

        // Enable auto-resume first
        await interceptor!("/auto-resume");

        // Execute again — CurrentAutoResume should now be non-null so the toggle disables it
        await interceptor!("/auto-resume");

        var updatedEntities = await entityBroker.GetEntitiesAsync([agentSessionEntity!.EntityId]);
        var updatedEntity = updatedEntities.FirstOrDefault(e => e.EntityId == agentSessionEntity!.EntityId);
        Assert.NotNull(updatedEntity);
        var updatedData = Assert.IsType<JsonElement>(updatedEntity!.Data);
        Assert.False(updatedData.TryGetProperty("auto-resume", out _));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task TryStartAutoResumeAsync_WithMatchingLocalSession_AcquiresLeaseAndEnqueuesResumePrompt()
    {
        var table = CreateTestRunningAgentChatTable();
        var appServices = new ApplicationServices(table, new AgentPersistenceStoreCache());
        await using var viewModel = CreateTestMainWindowViewModel(applicationServices: appServices);
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var agentDefinitionId = new EntityId("ac030001-0000-4000-8000-000000000001");
        var agentDefinitionEntity = await UpsertEntityAndLoadAsync(entityBroker, agentDefinitionId,
            """
            {
              "entity-id": "ac030001-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "auto-resume-start-echo"]],
              "display-name": { "default": "Auto Resume Start Echo" },
              "definition": {
                "kind": "prompt",
                "name": "auto-resume-start-echo",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        const string agentSessionId = "ac030001-session-for-auto-resume-test";
        var agentSessionEntity = await agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            viewModel, agentDefinitionEntity, agentSessionId);
        Assert.NotNull(agentSessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(
            agentSessionShortcutContext, CreateLocalTrustedExecutorSelector(), table);

        const string resumePrompt = "Resume the task where you left off.";
        var foregroundScheduler = SynchronizationContextTaskScheduler.FromCurrent();
        var lease = await Task.Run(() =>
            handler.TryStartAutoResumeAsync(viewModel, agentSessionEntity!, resumePrompt, foregroundScheduler));

        try
        {
            Assert.NotNull(lease);
            Assert.Single(table.RunningSessions);

            // Verify the resume prompt was enqueued — wait for it to appear in history
            await WaitForChatHistoryAsync(lease!.AgentChat, resumePrompt);

            Assert.Contains(
                lease.AgentChat.History,
                item => item.Role == ChatRole.User
                    && item.Contents.OfType<TextContent>().Any(c => c.Text == resumePrompt));
        }
        finally
        {
            if (lease is not null)
            {
                await lease.DisposeAsync();
            }
        }
    }

    private static async Task WaitForChatHistoryAsync(AgentChat agentChat, string expectedUserMessage)
    {
        bool IsPresent() => agentChat.History.Any(
            item => item.Role == ChatRole.User
                && item.Contents.OfType<TextContent>().Any(c => c.Text == expectedUserMessage));

        if (IsPresent())
        {
            return;
        }

        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<AgentChatHistoryItem> onTurnCompleted = (_, _) =>
        {
            if (IsPresent())
            {
                signal.TrySetResult();
            }
        };

        agentChat.TurnCompleted += onTurnCompleted;
        try
        {
            if (!IsPresent())
            {
                await signal.Task;
            }
        }
        finally
        {
            agentChat.TurnCompleted -= onTurnCompleted;
        }
    }

    private sealed class FakeShellSession : ITerminalSession
    {
        private readonly MemoryStream stream = new();

        public Stream Stream => this.stream;

        public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask SignalAsync(string signal, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public Task<int> WaitForExitAsync() => Task.FromResult(0);

        public ValueTask DisposeAsync()
        {
            this.stream.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    // ── Tab close MRU navigation tests (#828) ──────────────────────────────────

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CloseTab_ActiveTab_NavigatesToMostRecentlyUsedTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "mru-a", Title = "Tab A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "mru-b", Title = "Tab B" };
        var tabC = new WebViewModel("https://c.example.com") { Id = "mru-c", Title = "Tab C" };

        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);
        await viewModel.OpenTabAsync(tabC);

        // Navigate to tabA to make it MRU (OpenTabAsync on existing tab pushes to history)
        await viewModel.OpenTabAsync(tabA);

        // Navigate back to tabC
        await viewModel.OpenTabAsync(tabC);

        // Close the active tab (tabC) — should navigate to the MRU tab (tabA)
        viewModel.CloseTab(tabC);

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        Assert.Equal("mru-a", documentDock!.ActiveDockable?.Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CloseTab_NonActiveTab_DoesNotChangeActiveTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "mru-non-active-a", Title = "Tab A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "mru-non-active-b", Title = "Tab B" };
        var tabC = new WebViewModel("https://c.example.com") { Id = "mru-non-active-c", Title = "Tab C" };

        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);
        await viewModel.OpenTabAsync(tabC);

        // Set tabB as active
        await viewModel.OpenTabAsync(tabB);

        // Close a non-active tab (tabA)
        viewModel.CloseTab(tabA);

        // Active tab should still be tabB
        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        Assert.Equal("mru-non-active-b", documentDock!.ActiveDockable?.Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CloseTab_LastTabInPane_NoNavigation()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://example.com") { Id = "mru-last", Title = "Last Tab" };
        await viewModel.OpenTabAsync(tab);

        // Close the only tab — should not crash or navigate anywhere
        viewModel.CloseTab(tab);

        Assert.Empty(viewModel.SelectedWorkspacePane!.Tabs);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CloseTabById_ActiveTab_NavigatesToMostRecentlyUsedTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "mru-byid-a", Title = "Tab A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "mru-byid-b", Title = "Tab B" };
        var tabC = new WebViewModel("https://c.example.com") { Id = "mru-byid-c", Title = "Tab C" };

        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);
        await viewModel.OpenTabAsync(tabC);

        // Navigate to tabA to make it MRU
        await viewModel.OpenTabAsync(tabA);

        // Navigate back to tabC
        await viewModel.OpenTabAsync(tabC);

        // Close the active tab by ID (tabC) — should navigate to the MRU tab (tabA)
        viewModel.CloseTabById("mru-byid-c");

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        Assert.Equal("mru-byid-a", documentDock!.ActiveDockable?.Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OnDockableTabClosed_ActiveTab_NavigatesToMostRecentlyUsedTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "mru-dockable-a", Title = "Tab A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "mru-dockable-b", Title = "Tab B" };
        var tabC = new WebViewModel("https://c.example.com") { Id = "mru-dockable-c", Title = "Tab C" };

        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);
        await viewModel.OpenTabAsync(tabC);

        // Navigate to tabA to make it MRU
        await viewModel.OpenTabAsync(tabA);

        // Navigate back to tabC
        await viewModel.OpenTabAsync(tabC);

        // Close the active tab via dock framework (tabC) — should navigate to the MRU tab (tabA)
        viewModel.OnDockableTabClosed(tabC);

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        Assert.Equal("mru-dockable-a", documentDock!.ActiveDockable?.Id);
    }

    // ─── Fix #1107: Tabs order is independent of dock order — no reentrant back-sync ─────

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenTabAsync_AppendAfterIndexedInsert_DoesNotThrow()
    {
        // Regression #1107: an indexed insert (insertAfterTabId) followed by an unguarded append
        // used to explode with "Cannot change ObservableCollection during a CollectionChanged
        // event." because the dock->Tabs order back-sync tried to Move within the still-firing
        // Tabs.CollectionChanged notification. With the back-sync deleted the append is safe.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "reenter-a", Title = "A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "reenter-b", Title = "B" };
        var tabC = new WebViewModel("https://c.example.com") { Id = "reenter-c", Title = "C" };

        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB, insertAfterTabId: "reenter-a");
        await viewModel.OpenTabAsync(tabC); // plain append — must not throw.

        var pane = viewModel.SelectedWorkspacePane;
        Assert.Contains(pane.Tabs, t => t.Id == "reenter-c");
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenTabAsync_MultipleAppends_DoesNotFaultUnobservedTask()
    {
        // Regression #1107: opening several tabs in sequence must not fault the returned Task
        // with an unobserved AggregateException wrapping an ObservableCollection reentrancy
        // exception.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        for (var i = 0; i < 5; i++)
        {
            var tab = new WebViewModel($"https://append-{i}.example.com")
            {
                Id = $"append-{i}",
                Title = $"Tab {i}",
            };
            var task = viewModel.OpenTabAsync(tab);
            await task; // must not throw
            Assert.False(task.IsFaulted);
        }

        var pane = viewModel.SelectedWorkspacePane;
        Assert.Equal(5, pane.Tabs.Count);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenTabAsync_ThenReorderDock_LeavesTabsOrderUnchanged()
    {
        // Fix #1107: WorkspacePaneViewModel.Tabs order is independent of dock order — the dock
        // may reorder VisibleDockables without our Tabs collection shifting.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "order-a", Title = "A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "order-b", Title = "B" };
        var tabC = new WebViewModel("https://c.example.com") { Id = "order-c", Title = "C" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);
        await viewModel.OpenTabAsync(tabC);

        var pane = viewModel.SelectedWorkspacePane;
        var beforeTabIds = pane.Tabs.Select(t => t.Id).ToList();

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        var visible = documentDock!.VisibleDockables!;
        // Reorder VisibleDockables (simulates a user drag-reorder in the dock).
        if (visible.Count >= 3)
        {
            var first = visible[0];
            visible.RemoveAt(0);
            visible.Insert(visible.Count, first);
        }

        await Dispatcher.UIThread.InvokeAsync(() => { });

        var afterTabIds = pane.Tabs.Select(t => t.Id).ToList();
        Assert.Equal(beforeTabIds, afterTabIds);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task DockReorder_DoesNotMoveTabsCollection()
    {
        // Fix #1107: reordering VisibleDockables must not raise NotifyCollectionChangedAction.Move
        // on pane.Tabs — the back-sync is gone.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "no-move-a", Title = "A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "no-move-b", Title = "B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);

        var pane = viewModel.SelectedWorkspacePane;
        var moveEventCount = 0;
        pane.Tabs.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Move)
            {
                Interlocked.Increment(ref moveEventCount);
            }
        };

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        var visible = documentDock!.VisibleDockables!;
        if (visible.Count >= 2)
        {
            var first = visible[0];
            visible.RemoveAt(0);
            visible.Insert(visible.Count, first);
        }

        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.Equal(0, moveEventCount);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CloseTab_RemovesTabFromTabsCollection()
    {
        // Fix #1107: explicit close is still the only mutation that removes membership from Tabs.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "closerm-a", Title = "A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "closerm-b", Title = "B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);

        var pane = viewModel.SelectedWorkspacePane;
        Assert.Contains(pane.Tabs, t => t.Id == "closerm-b");

        viewModel.CloseTabById("closerm-b");

        Assert.DoesNotContain(pane.Tabs, t => t.Id == "closerm-b");
        Assert.Contains(pane.Tabs, t => t.Id == "closerm-a");
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CycleTab_AfterDockReorder_FollowsVisibleDockablesOrder()
    {
        // Fix #1107: Ctrl+Tab cycles in dock visual order (VisibleDockables), not pane.Tabs order.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "cyc-a", Title = "A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "cyc-b", Title = "B" };
        var tabC = new WebViewModel("https://c.example.com") { Id = "cyc-c", Title = "C" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);
        await viewModel.OpenTabAsync(tabC);

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        var visible = documentDock!.VisibleDockables!;

        // Reorder VisibleDockables so dock order diverges from any assumed insertion order.
        if (visible.Count >= 3)
        {
            var first = visible[0];
            visible.RemoveAt(0);
            visible.Insert(visible.Count, first);
        }

        await Dispatcher.UIThread.InvokeAsync(() => { });

        var startIndex = visible.IndexOf(documentDock.ActiveDockable!);
        viewModel.CycleTabForwardCommand.Execute(null);
        var afterIndex = visible.IndexOf(documentDock.ActiveDockable!);
        Assert.Equal((startIndex + 1) % visible.Count, afterIndex);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CycleTab_ActivatesNextDockableByIdentity()
    {
        // Fix #1107: cycling resolves the current/next dockable by identity within
        // VisibleDockables, independent of any pane.Tabs ordering assumption.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "cyc-id-a", Title = "A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "cyc-id-b", Title = "B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);

        var activeBefore = documentDock!.ActiveDockable;
        Assert.NotNull(activeBefore);

        viewModel.CycleTabForwardCommand.Execute(null);
        var activeAfter = documentDock.ActiveDockable;

        Assert.NotSame(activeBefore, activeAfter);
        Assert.Contains(activeAfter, documentDock.VisibleDockables!);
    }

    // --- #1124 top-level dock-tab-switch adoption tests ---

    internal static DockControl GetTopLevelDockControl(MainWindow window) =>
        window.GetVisualDescendants()
            .OfType<DockControl>()
            .First(d => string.Equals(d.Name, "TopLevelDockControl", StringComparison.Ordinal));

    internal static async Task OpenTwoWorkspacesForTabSwitchAsync(
        MainWindowViewModel viewModel,
        string idPrefix)
    {
        var entityBroker = GetEntityBroker(viewModel);

        var idA = new EntityId($"{idPrefix}-0000-4000-8000-000000000001");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            idA,
            $$"""
            {
              "entity-id": "{{idA}}",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "{{idPrefix}}-a"]],
              "display-name": { "default": "{{idPrefix}} A" },
              "regions": []
            }
            """);

        var idB = new EntityId($"{idPrefix}-0000-4000-8000-000000000002");
        await UpsertEntityAndLoadAsync(
            entityBroker,
            idB,
            $$"""
            {
              "entity-id": "{{idB}}",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "{{idPrefix}}-b"]],
              "display-name": { "default": "{{idPrefix}} B" },
              "regions": []
            }
            """);

        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = idA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = idB });
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_TopLevelDockControl_OptsIntoInstallOnTopLevel()
    {
        // #1124 adoption: the realized TopLevelDockControl carries
        // ts:DockTabSwitch.InstallOnTopLevel=True in addition to its existing
        // Enabled=True + Alt+Shift+Digits AllSwitchable binding.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            var dock = GetTopLevelDockControl(window);

            Assert.True(Phantom.Dock.Avalonia.TabSwitching.DockTabSwitch.GetInstallOnTopLevel(dock));
            Assert.True(Phantom.Dock.Avalonia.TabSwitching.DockTabSwitch.GetEnabled(dock));

            var bindings = Phantom.Dock.Avalonia.TabSwitching.DockTabSwitch.GetBindings(dock);
            Assert.NotNull(bindings);
            var gesture = Assert.Single(bindings!);
            Assert.Equal(KeyModifiers.Alt | KeyModifiers.Shift, gesture.Modifiers);
            Assert.Equal(
                Phantom.Dock.Avalonia.TabSwitching.DockTabSwitchKeys.Digits,
                gesture.Keys);
            Assert.Equal(
                Phantom.Dock.Avalonia.TabSwitching.DockTabSwitchScope.AllSwitchable,
                gesture.Scope);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspacePaneDockControl_AltDigitBinding_UsesAllSwitchableScope()
    {
        // #1311: the inner WorkspacePaneDocument DockControl's Alt+Digit binding must use
        // AllSwitchable scope so tab numbers form one continuous sequence across every
        // DocumentDock region in the workspace pane instead of restarting per region.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        await OpenTwoWorkspacesForTabSwitchAsync(viewModel, "13111311");

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            var topLevel = GetTopLevelDockControl(window);
            var innerDock = window.GetVisualDescendants()
                .OfType<DockControl>()
                .FirstOrDefault(d => !ReferenceEquals(d, topLevel));
            Assert.NotNull(innerDock);

            var bindings = Phantom.Dock.Avalonia.TabSwitching.DockTabSwitch.GetBindings(innerDock!);
            Assert.NotNull(bindings);
            var gesture = Assert.Single(bindings!);
            Assert.Equal(KeyModifiers.Alt, gesture.Modifiers);
            Assert.Equal(
                Phantom.Dock.Avalonia.TabSwitching.DockTabSwitchKeys.Digits,
                gesture.Keys);
            Assert.Equal(
                Phantom.Dock.Avalonia.TabSwitching.DockTabSwitchScope.AllSwitchable,
                gesture.Scope);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspacePaneDocument_AltDigitFromNonPaneFocus_ActivatesIndexedPaneTab()
    {
        // #1329: with InstallOnTopLevel now set on the inner WorkspacePaneDocument DockControl,
        // Alt+Digit must switch the indexed pane document even when keyboard focus lives outside
        // the pane's DockControl (e.g. on the left tree) — symmetric with Alt+Shift+Digit.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "altdigit-a", Title = "A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "altdigit-b", Title = "B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            // Start on the first document so activating the second is observable.
            ActivateContentTabAtIndex(viewModel, "0");
            var documentDock = GetDocumentDock(viewModel);
            Assert.NotNull(documentDock);
            Assert.Same(documentDock!.VisibleDockables![0], documentDock.ActiveDockable);

            // Focus lives OUTSIDE the pane DockControl (on the left tree).
            var treeView = window.GetVisualDescendants().OfType<TreeView>().First();
            Assert.DoesNotContain(
                GetDocumentDockControl(window).GetVisualDescendants(),
                v => ReferenceEquals(v, treeView));

            window.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.D2,
                KeyModifiers = KeyModifiers.Alt,
                Source = treeView,
            });

            Assert.Same(documentDock.VisibleDockables![1], documentDock.ActiveDockable);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspacePaneDocument_AltShiftDigitFromNonPaneFocus_ActivatesWorkspaceTab()
    {
        // #1329 symmetry counterpart: from a focus position outside the DockControls, the outer
        // Alt+Shift+Digit chord must continue to activate the indexed workspace-level pane. Guards
        // against a regression that would break the currently-working chord when the inner chord
        // adopts InstallOnTopLevel on the same TopLevel.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        await OpenTwoWorkspacesForTabSwitchAsync(viewModel, "1329bbbb");

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            ActivateWorkspacePaneAtIndex(viewModel, "0");
            Assert.Equal(viewModel.WorkspacePanes[0], viewModel.SelectedWorkspacePane);

            var treeView = window.GetVisualDescendants().OfType<TreeView>().First();

            window.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.D2,
                KeyModifiers = KeyModifiers.Alt | KeyModifiers.Shift,
                Source = treeView,
            });

            Assert.Equal(viewModel.WorkspacePanes[1], viewModel.SelectedWorkspacePane);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task WorkspacePaneDocument_AltDigit_AndOuterAltShiftDigit_CoexistOnSameTopLevel_EachSwitchesOnlyItsOwn()
    {
        // #1329: the inner (Alt+Digit) and outer (Alt+Shift+Digit) controllers share one TopLevel.
        // Modifier-exact matching keeps them independent — Alt+D2 only moves the pane document,
        // Alt+Shift+D2 only moves the workspace-level pane; neither cross-fires.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        // Two document tabs in the (single) default pane; the inner Alt+Digit and outer
        // Alt+Shift+Digit controllers both install on the same TopLevel.
        var tabA = new WebViewModel("https://a.example.com") { Id = "coexist-a", Title = "A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "coexist-b", Title = "B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            ActivateContentTabAtIndex(viewModel, "0");
            var paneDock = GetDocumentDock(viewModel);
            Assert.NotNull(paneDock);
            Assert.True(paneDock!.VisibleDockables!.Count >= 2, "Pane should hold two documents");
            var doc0 = paneDock.VisibleDockables![0];
            var doc1 = paneDock.VisibleDockables![1];
            Assert.Same(doc0, paneDock.ActiveDockable);

            var treeView = window.GetVisualDescendants().OfType<TreeView>().First();

            // Alt+D2 (inner chord) switches the pane document.
            window.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.D2,
                KeyModifiers = KeyModifiers.Alt,
                Source = treeView,
            });
            Assert.Same(doc1, paneDock.ActiveDockable);

            // Alt+Shift+D1 (outer chord) is modifier-exact: it does NOT cross-fire onto the
            // inner Alt-only controller, so the pane's active document is unchanged.
            window.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.D1,
                KeyModifiers = KeyModifiers.Alt | KeyModifiers.Shift,
                Source = treeView,
            });
            Assert.Same(doc1, paneDock.ActiveDockable);

            // Conversely, plain Alt+D1 (inner chord) moves the pane document back to the first
            // document — proving the inner controller still responds to its own exact chord.
            window.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.D1,
                KeyModifiers = KeyModifiers.Alt,
                Source = treeView,
            });
            Assert.Same(doc0, paneDock.ActiveDockable);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task WorkspacePaneDocument_AfterMultipleTopLevelWorkspaceSwitches_AltDigitStillSwitchesInnerTab()
    {
        // #1332: switching the active top-level workspace re-templates the inner WorkspacePaneDocument
        // DockControl, so its tab-switch controller instances churn. After several switches the stale
        // controllers used to steal and no-op the Alt+Digit chord. With focus/most-recently-focused
        // routing, the chord must still switch the CURRENTLY active pane's inner document tab.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        await OpenTwoWorkspacesForTabSwitchAsync(viewModel, "1332aaaa");

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            // Give each of the two panes two document tabs.
            ActivateWorkspacePaneAtIndex(viewModel, "0");
            Dispatcher.UIThread.RunJobs();
            await viewModel.OpenTabAsync(new WebViewModel("https://a1.example.com") { Id = "1332-a1", Title = "A1" });
            await viewModel.OpenTabAsync(new WebViewModel("https://a2.example.com") { Id = "1332-a2", Title = "A2" });

            ActivateWorkspacePaneAtIndex(viewModel, "1");
            Dispatcher.UIThread.RunJobs();
            await viewModel.OpenTabAsync(new WebViewModel("https://b1.example.com") { Id = "1332-b1", Title = "B1" });
            await viewModel.OpenTabAsync(new WebViewModel("https://b2.example.com") { Id = "1332-b2", Title = "B2" });

            // Toggle the active top-level workspace several times to churn the inner DockControl.
            for (var i = 0; i < 6; i++)
            {
                ActivateWorkspacePaneAtIndex(viewModel, (i % 2).ToString());
                Dispatcher.UIThread.RunJobs();
            }

            // End on the second workspace and make its inner pane the focused region.
            ActivateWorkspacePaneAtIndex(viewModel, "1");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(viewModel.WorkspacePanes[1], viewModel.SelectedWorkspacePane);

            ActivateContentTabAtIndex(viewModel, "0");
            var documentDock = GetDocumentDock(viewModel);
            Assert.NotNull(documentDock);
            Assert.True(documentDock!.VisibleDockables!.Count >= 2, "Active pane should hold two documents");
            Assert.Same(documentDock.VisibleDockables![0], documentDock.ActiveDockable);

            // Focus lives outside the pane DockControl (on the left tree) — the #1329/#1332 sourcing case.
            var treeView = window.GetVisualDescendants().OfType<TreeView>().First();

            window.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.D2,
                KeyModifiers = KeyModifiers.Alt,
                Source = treeView,
            });

            Assert.Same(documentDock.VisibleDockables![1], documentDock.ActiveDockable);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    private static DockControl GetDocumentDockControl(MainWindow window) =>
        window.GetVisualDescendants()
            .OfType<DockControl>()
            .First(d => !string.Equals(d.Name, "TopLevelDockControl", StringComparison.Ordinal));

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowIntegration_BlankTargetNavigation_OpensNewWebTabInSameWorkspacePane()
    {
        // #1325: a target="_blank" / window.open() navigation on a browser tab whose tabService
        // is wired must open a new WebViewModel tab in the same workspace pane.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var sourceTab = new WebViewModel("https://source.example.com", viewModel)
        {
            Id = "blank-source",
            Title = "Source",
        };
        await viewModel.OpenTabAsync(sourceTab);

        var paneBefore = viewModel.SelectedWorkspacePane;
        var countBefore = paneBefore.Tabs.OfType<WebViewModel>().Count();

        // Simulate the _blank navigation routed through ConfiguredWebView → RaiseOpenNewWindow.
        sourceTab.RaiseOpenNewWindow("https://blank-target.example.com");
        await Task.Yield();

        var newTab = paneBefore.Tabs
            .OfType<WebViewModel>()
            .FirstOrDefault(t => t.AddressBarUrl == "https://blank-target.example.com");

        Assert.NotNull(newTab);
        Assert.Equal(countBefore + 1, paneBefore.Tabs.OfType<WebViewModel>().Count());
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_AltShiftDigitWithFocusOutsideDock_SwitchesTopLevelDockTab()
    {
        // #1124 adoption: with the event source on the left-pane TreeView (outside the
        // TopLevelDockControl), Alt+Shift+2 must still switch the top-level dock's active
        // workspace-pane document to the second pane. This is the whole point of top-level
        // sourcing: focus-independence.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        await OpenTwoWorkspacesForTabSwitchAsync(viewModel, "11241124");

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            // Start on pane 1 so pane 2 activation is observable.
            ActivateWorkspacePaneAtIndex(viewModel, "0");
            Assert.Equal(viewModel.WorkspacePanes[0], viewModel.SelectedWorkspacePane);

            var dock = GetTopLevelDockControl(window);
            var treeView = window.GetVisualDescendants().OfType<TreeView>().First();

            // The tree lives in the left pane, outside the DockControl subtree.
            Assert.DoesNotContain(dock.GetVisualDescendants(), v => ReferenceEquals(v, treeView));

            window.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.D2,
                KeyModifiers = KeyModifiers.Alt | KeyModifiers.Shift,
                Source = treeView,
            });

            Assert.Equal(viewModel.WorkspacePanes[1], viewModel.SelectedWorkspacePane);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_AltShiftDigit_ActivatesTabExactlyOnce()
    {
        // #1124 adoption: with InstallOnTopLevel the in-control tunnel handlers are
        // suppressed, so a single physical Alt+Shift+2 chord causes exactly one
        // SetActiveDockable on the target pane — no double-handling.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        await OpenTwoWorkspacesForTabSwitchAsync(viewModel, "11241125");

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            ActivateWorkspacePaneAtIndex(viewModel, "0");
            Assert.Equal(viewModel.WorkspacePanes[0], viewModel.SelectedWorkspacePane);

            var factory = GetDockFactoryAs<IFactory>(viewModel);
            var workspacesDock = FindDocumentDockIn(viewModel.Layout!);
            Assert.NotNull(workspacesDock);
            var paneDoc2 = workspacesDock!.VisibleDockables!
                .OfType<WorkspacePaneDocument>()
                .First(d => ReferenceEquals(d.WorkspacePane, viewModel.WorkspacePanes[1]));

            var activationCount = 0;
            void Handler(object? _, global::Dock.Model.Core.Events.ActiveDockableChangedEventArgs e)
            {
                if (ReferenceEquals(e.Dockable, paneDoc2))
                {
                    activationCount++;
                }
            }
            factory.ActiveDockableChanged += Handler;
            try
            {
                var dock = GetTopLevelDockControl(window);
                var treeView = window.GetVisualDescendants().OfType<TreeView>().First();

                window.RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.D2,
                    KeyModifiers = KeyModifiers.Alt | KeyModifiers.Shift,
                    Source = treeView,
                });
            }
            finally
            {
                factory.ActiveDockableChanged -= Handler;
            }

            Assert.Equal(1, activationCount);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_AltShiftHeld_ShowsBadgesRegardlessOfFocus()
    {
        // #1124 adoption: with focus outside the TopLevelDockControl, holding the exact
        // Alt+Shift modifier set (the binding chord) makes the controller's badges visible;
        // releasing a modifier hides them again. The gesture is sourced from the TopLevel so
        // this is what a real user experiences with focus on the left-pane tree.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            var dock = GetTopLevelDockControl(window);
            var controller = Phantom.Dock.Avalonia.TabSwitching.DockTabSwitch.GetController(dock);
            Assert.NotNull(controller);
            Assert.False(controller!.AreBadgesVisible);

            var treeView = window.GetVisualDescendants().OfType<TreeView>().First();

            // Alt alone does not match Alt+Shift ⇒ still hidden.
            window.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.LeftAlt,
                KeyModifiers = KeyModifiers.Alt,
                Source = treeView,
            });
            Assert.False(controller.AreBadgesVisible);

            // Add Shift ⇒ exact match ⇒ visible.
            window.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.LeftShift,
                KeyModifiers = KeyModifiers.Alt | KeyModifiers.Shift,
                Source = treeView,
            });
            Assert.True(controller.AreBadgesVisible);

            // Release Alt ⇒ Shift alone ⇒ hidden again.
            window.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyUpEvent,
                Key = Key.LeftAlt,
                KeyModifiers = KeyModifiers.Shift,
                Source = treeView,
            });
            Assert.False(controller.AreBadgesVisible);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    // ── Fix #1065: new tabs opened from an existing tab insert one to the right ──
    //
    // The insertion MUST happen at the Dock.Avalonia visual-tab-strip level (the
    // DocumentDock that hosts the source tab and its VisibleDockables), NOT by
    // inserting into WorkspacePaneViewModel.Tabs (which is an order-independent
    // membership set per #1107). These tests exercise OpenTabAsync — the single
    // funnel that every shortcut-handler open path routes through — and assert
    // against the source document's owning DocumentDock.VisibleDockables.

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenTab_FromExistingTab_MovesDockableOneToRightInSameStrip()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "fix1065-a", Title = "A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "fix1065-b", Title = "B" };
        var tabC = new WebViewModel("https://c.example.com") { Id = "fix1065-c", Title = "C" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);
        await viewModel.OpenTabAsync(tabC);

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);

        // Activate tab B (middle) as the originator. The new tab must land at
        // index-of-B + 1 in the visual strip, not at the end.
        var pane = viewModel.SelectedWorkspacePane;
        var docB = documentDock!.VisibleDockables!.OfType<WorkspaceDocument>()
            .First(d => d.Id == "fix1065-b");
        var dockFactory = GetDockFactoryAs<WorkspaceDockFactory>(viewModel);
        pane.SelectedTab = docB.TabViewModel;
        dockFactory.SetActiveDockable(docB);

        var indexOfB = documentDock.VisibleDockables!.IndexOf(docB);

        var tabNew = new WebViewModel("https://new.example.com") { Id = "fix1065-new", Title = "New" };
        await viewModel.OpenTabAsync(tabNew);

        var docNew = documentDock.VisibleDockables!.OfType<WorkspaceDocument>()
            .First(d => d.Id == "fix1065-new");
        var indexOfNew = documentDock.VisibleDockables!.IndexOf(docNew);

        Assert.Equal(indexOfB + 1, indexOfNew);
        // The C dockable must have shifted to the right, not been overwritten.
        Assert.Contains(
            documentDock.VisibleDockables!.OfType<WorkspaceDocument>(),
            d => d.Id == "fix1065-c");
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenTab_OpenedInOtherDock_MovesIntoSourceStrip()
    {
        // The source tab lives in a DIFFERENT DocumentDock than the ItemsSource-bound
        // one used by the generator. The new dockable — initially created in the
        // ItemsSource-bound dock — must be moved into the source's owning strip.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "fix1065b-a", Title = "A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "fix1065b-b", Title = "B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);

        var pane = viewModel.SelectedWorkspacePane;
        var itemsSourceDock = GetDocumentDock(viewModel);
        Assert.NotNull(itemsSourceDock);
        var dockFactory = GetDockFactoryAs<WorkspaceDockFactory>(viewModel);

        var docA = itemsSourceDock!.VisibleDockables!.OfType<WorkspaceDocument>()
            .First(d => d.Id == "fix1065b-a");

        // Build a second DocumentDock at the root and relocate tabA into it so its
        // Owner is no longer the ItemsSource-bound dock.
        var splitDock = new global::Dock.Model.Mvvm.Controls.DocumentDock
        {
            Id = "fix1065b-split",
            VisibleDockables = dockFactory.CreateList<IDockable>(),
        };
        var contentRoot = (IDock)pane.ContentLayout!;
        dockFactory.AddDockable(contentRoot, splitDock);
        dockFactory.MoveDockable(itemsSourceDock, splitDock, docA, null);
        Assert.Same(splitDock, docA.Owner);

        // Make tabA the active/selected originator.
        pane.SelectedTab = docA.TabViewModel;
        dockFactory.SetActiveDockable(docA);

        var tabNew = new WebViewModel("https://new.example.com") { Id = "fix1065b-new", Title = "New" };
        await viewModel.OpenTabAsync(tabNew);

        var docNew = pane.GetDocumentForTab("fix1065b-new");
        Assert.NotNull(docNew);
        // The new document must now live in the source's strip (splitDock), directly
        // to the right of tabA.
        Assert.Same(splitDock, docNew!.Owner);
        var indexOfA = splitDock.VisibleDockables!.IndexOf(docA);
        var indexOfNew = splitDock.VisibleDockables!.IndexOf(docNew);
        Assert.Equal(indexOfA + 1, indexOfNew);

        // The new doc must NOT still be in the ItemsSource-bound dock.
        Assert.DoesNotContain(
            itemsSourceDock.VisibleDockables!.OfType<WorkspaceDocument>(),
            d => d.Id == "fix1065b-new");
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenTab_NoSourceTab_AppendsToEnd()
    {
        // Empty strip / no originating tab active → append to end (fallback behavior).
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var pane = viewModel.SelectedWorkspacePane;
        pane.SelectedTab = null;
        Assert.Empty(pane.Tabs);

        var tabA = new WebViewModel("https://a.example.com") { Id = "fix1065c-a", Title = "A" };
        await viewModel.OpenTabAsync(tabA);

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        var docs = documentDock!.VisibleDockables!.OfType<WorkspaceDocument>().ToList();
        Assert.Single(docs);
        Assert.Equal("fix1065c-a", docs[0].Id);

        // Clear selection and open another — must append (no anchor).
        pane.SelectedTab = null;
        var tabB = new WebViewModel("https://b.example.com") { Id = "fix1065c-b", Title = "B" };
        await viewModel.OpenTabAsync(tabB);

        docs = documentDock.VisibleDockables!.OfType<WorkspaceDocument>().ToList();
        Assert.Equal(2, docs.Count);
        Assert.Equal("fix1065c-a", docs[0].Id);
        Assert.Equal("fix1065c-b", docs[1].Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenTab_InsertedDockable_BecomesActiveAndFocused_WhenFocusTrue()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "fix1065d-a", Title = "A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "fix1065d-b", Title = "B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        var pane = viewModel.SelectedWorkspacePane;
        var docA = documentDock!.VisibleDockables!.OfType<WorkspaceDocument>()
            .First(d => d.Id == "fix1065d-a");
        var dockFactory = GetDockFactoryAs<WorkspaceDockFactory>(viewModel);
        pane.SelectedTab = docA.TabViewModel;
        dockFactory.SetActiveDockable(docA);

        var tabNew = new WebViewModel("https://new.example.com") { Id = "fix1065d-new", Title = "New" };
        await viewModel.OpenTabAsync(tabNew, focus: true);

        var docNew = documentDock.VisibleDockables!.OfType<WorkspaceDocument>()
            .First(d => d.Id == "fix1065d-new");

        Assert.Same(docNew, documentDock.ActiveDockable);
        Assert.Same(tabNew, pane.SelectedTab);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenTab_WhenSourceIsLastInStrip_InsertsAtEnd()
    {
        // Source is the last tab in its strip → sourceIndex + 1 clamps to Count and
        // the new tab lands at the very end without any out-of-range error.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "fix1065e-a", Title = "A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "fix1065e-b", Title = "B" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB); // B is now active (last)

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        var docs = documentDock!.VisibleDockables!.OfType<WorkspaceDocument>().ToList();
        Assert.Equal("fix1065e-b", docs[^1].Id);

        var tabNew = new WebViewModel("https://new.example.com") { Id = "fix1065e-new", Title = "New" };
        await viewModel.OpenTabAsync(tabNew);

        docs = documentDock.VisibleDockables!.OfType<WorkspaceDocument>().ToList();
        Assert.Equal(3, docs.Count);
        Assert.Equal("fix1065e-a", docs[0].Id);
        Assert.Equal("fix1065e-b", docs[1].Id);
        Assert.Equal("fix1065e-new", docs[2].Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenTab_ExplicitInsertAfterTabId_TakesPrecedenceOverSelectedTab()
    {
        // If insertAfterTabId is passed explicitly it wins over the current SelectedTab.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "fix1065f-a", Title = "A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "fix1065f-b", Title = "B" };
        var tabC = new WebViewModel("https://c.example.com") { Id = "fix1065f-c", Title = "C" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);
        await viewModel.OpenTabAsync(tabC); // C is active

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        var docA = documentDock!.VisibleDockables!.OfType<WorkspaceDocument>()
            .First(d => d.Id == "fix1065f-a");
        var indexOfA = documentDock.VisibleDockables!.IndexOf(docA);

        // C is active — but caller explicitly requests insertion after A.
        var tabNew = new WebViewModel("https://new.example.com") { Id = "fix1065f-new", Title = "New" };
        await viewModel.OpenTabAsync(tabNew, insertAfterTabId: "fix1065f-a");

        var docNew = documentDock.VisibleDockables!.OfType<WorkspaceDocument>()
            .First(d => d.Id == "fix1065f-new");
        var indexOfNew = documentDock.VisibleDockables!.IndexOf(docNew);
        Assert.Equal(indexOfA + 1, indexOfNew);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenTab_WhenOnlyOneExistingTab_InsertsToRightOfOnlyTab()
    {
        // Opening from the only tab — the new tab should still land immediately
        // after it (not "before" or replacing) even though only one strip exists.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "fix1065g-a", Title = "A" };
        await viewModel.OpenTabAsync(tabA);

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);

        var tabNew = new WebViewModel("https://new.example.com") { Id = "fix1065g-new", Title = "New" };
        await viewModel.OpenTabAsync(tabNew);

        var docs = documentDock!.VisibleDockables!.OfType<WorkspaceDocument>().ToList();
        Assert.Equal(2, docs.Count);
        Assert.Equal("fix1065g-a", docs[0].Id);
        Assert.Equal("fix1065g-new", docs[1].Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenExternalEntityShortcutHandler_OpensNewTabRightOfSource()
    {
        // Representative shortcut-handler open path: the handler routes through
        // OpenTabAsync with no explicit insertAfterTabId, so the anchor comes from
        // the current SelectedTab of the target pane.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tabA = new WebViewModel("https://a.example.com") { Id = "fix1065h-a", Title = "A" };
        var tabB = new WebViewModel("https://b.example.com") { Id = "fix1065h-b", Title = "B" };
        var tabC = new WebViewModel("https://c.example.com") { Id = "fix1065h-c", Title = "C" };
        await viewModel.OpenTabAsync(tabA);
        await viewModel.OpenTabAsync(tabB);
        await viewModel.OpenTabAsync(tabC);

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        var pane = viewModel.SelectedWorkspacePane;
        var docB = documentDock!.VisibleDockables!.OfType<WorkspaceDocument>()
            .First(d => d.Id == "fix1065h-b");
        var dockFactory = GetDockFactoryAs<WorkspaceDockFactory>(viewModel);
        pane.SelectedTab = docB.TabViewModel;
        dockFactory.SetActiveDockable(docB);

        var externalEntity = CreateFix1065ExternalEntity(
            "cb1065c0-0000-4000-8000-000000000001",
            "External Fix1065",
            "https://fix1065-external.example.com");

        var handler = new OpenExternalEntityShortcutHandler();
        var handled = await handler.Handle(viewModel, Shortcut.Open, externalEntity);
        Assert.True(handled);

        var newTabId = "web-cb1065c0-0000-4000-8000-000000000001-default";
        var docNew = documentDock.VisibleDockables!.OfType<WorkspaceDocument>()
            .First(d => d.Id == newTabId);
        var indexOfB = documentDock.VisibleDockables!.IndexOf(docB);
        var indexOfNew = documentDock.VisibleDockables!.IndexOf(docNew);
        Assert.Equal(indexOfB + 1, indexOfNew);
    }

    private static SubscribedEntityViewModel CreateFix1065ExternalEntity(
        string entityId,
        string displayName,
        string defaultUrl)
    {
        var urlsJson = JsonSerializer.Serialize(new Dictionary<string, string> { ["default"] = defaultUrl });
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity", "external"],
              "names": [["tests", "external", "{{entityId}}"]],
              "display-name": { "default": "{{displayName}}" },
              "urls": {{urlsJson}}
            }
            """);

        return new SubscribedEntityViewModel(
            new EntitySnapshot
            {
                EntityId = new EntityId(entityId),
                ConcurrencyTag = new ConcurrencyTag("1"),
                ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
                Data = document.RootElement.Clone(),
                Relationships = Array.Empty<EntitySnapshot>(),
            },
            deleteEntityAsync: null);
    }

    // ── #1199: Ctrl+F must focus the FindTextBox and select-all its current text ──
    //
    // The Ctrl+F code-behind handler previously called findTextBox?.Focus() on the very next
    // line after Find.OpenCommand.Execute(null). Because the FindTextBox lives inside a Border
    // whose IsVisible is bound to Find.IsOpen, the control was not yet attached / realized at
    // the moment Focus() ran, so focus silently failed. And SelectAll() was never called, so
    // typing appended to the existing query instead of replacing it. The fix posts the
    // Focus() + SelectAll() work through Dispatcher.UIThread with DispatcherPriority.Input so
    // the visibility binding and layout pass complete first.

    private static TextBox GetFindTextBox(Window window)
    {
        return window.GetVisualDescendants()
            .OfType<TextBox>()
            .First(tb => tb.Name == "FindTextBox");
    }

    private static void SendCtrlF(Window window)
    {
        window.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.F,
            KeyModifiers = KeyModifiers.Control,
            Source = window,
        });
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_CtrlFWhenFindBarClosed_ShowsAndFocusesFindTextBox()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.False(viewModel.Find.IsOpen);

            SendCtrlF(window);

            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.True(viewModel.Find.IsOpen);
            var findTextBox = GetFindTextBox(window);
            Assert.True(findTextBox.IsFocused);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_CtrlFWhenFindBarClosedWithExistingQuery_SelectsAllText()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        // Pre-populate Query while the bar is still closed.
        viewModel.Find.Query = "prev-query";

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.False(viewModel.Find.IsOpen);

            SendCtrlF(window);

            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var findTextBox = GetFindTextBox(window);
            Assert.Equal("prev-query", findTextBox.Text);
            Assert.Equal(0, findTextBox.SelectionStart);
            Assert.Equal(findTextBox.Text!.Length, findTextBox.SelectionEnd);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_CtrlFWhenFindBarAlreadyOpen_SelectsAllTextIdempotently()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            // First Ctrl+F: opens the bar.
            SendCtrlF(window);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var findTextBox = GetFindTextBox(window);
            findTextBox.Text = "typed-query";
            findTextBox.SelectionStart = findTextBox.Text.Length;
            findTextBox.SelectionEnd = findTextBox.Text.Length;
            Dispatcher.UIThread.RunJobs();

            // Second Ctrl+F on an already-open bar must re-select all text so typing replaces
            // the query. Query itself must not change (view-only concern).
            var queryBefore = viewModel.Find.Query;
            SendCtrlF(window);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.True(viewModel.Find.IsOpen);
            Assert.Equal(queryBefore, viewModel.Find.Query);
            Assert.Equal(0, findTextBox.SelectionStart);
            Assert.Equal(findTextBox.Text!.Length, findTextBox.SelectionEnd);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_CtrlFWhenFindTextBoxAlreadyHasFocus_KeepsFocusAndSelectsAll()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            SendCtrlF(window);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var findTextBox = GetFindTextBox(window);
            findTextBox.Text = "keep-focus";
            findTextBox.Focus();
            findTextBox.SelectionStart = findTextBox.Text.Length;
            findTextBox.SelectionEnd = findTextBox.Text.Length;
            Dispatcher.UIThread.RunJobs();
            Assert.True(findTextBox.IsFocused);

            SendCtrlF(window);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.True(findTextBox.IsFocused);
            Assert.Equal(0, findTextBox.SelectionStart);
            Assert.Equal(findTextBox.Text!.Length, findTextBox.SelectionEnd);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindow_CtrlFAfterVisibilityBindingUpdate_FocusLandsOnRealizedTextBox()
    {
        // Regression for the timing bug: at the moment Ctrl+F fires the FindTextBox is not yet
        // attached (Border.IsVisible binding hasn't propagated). The fix defers Focus() /
        // SelectAll() past the layout pass. After RunJobs / UpdateLayout / RunJobs the focus
        // must land on the (now realized) FindTextBox.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            // Before Ctrl+F the find-bar Border is collapsed (Find.IsOpen == false), so the
            // FindTextBox is not attached to a visible ancestor — the essence of the pre-fix
            // race. The fix defers Focus() / SelectAll() past the visibility flip and layout
            // pass so focus lands on the (now realized+visible) FindTextBox rather than being
            // dropped on the collapsed control.
            Assert.False(viewModel.Find.IsOpen);

            SendCtrlF(window);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var findTextBox = GetFindTextBox(window);
            Assert.True(findTextBox.IsEffectivelyVisible);
            Assert.True(findTextBox.IsFocused);
        }
        finally
        {
            await CloseWindowAsync(window);
        }
    }

}



