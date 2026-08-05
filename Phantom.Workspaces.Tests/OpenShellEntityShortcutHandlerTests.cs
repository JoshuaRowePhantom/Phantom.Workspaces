using Avalonia.Headless.XUnit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using global::Dock.Model.Controls;
using global::Dock.Model.Core;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm.Shell;
using Phantom.Workspaces.ViewModels;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Tests;

public sealed class OpenShellEntityShortcutHandlerTests
{
    // ---- ShouldApplyTo -----------------------------------------------------------------------

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ShouldApplyTo_OpenShortcutOnShellEntity_ReturnsTrue()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var snapshot = MakeSnapshot("""{"entity-types":["entity","shell"],"command":"pwsh"}""");
        var entityViewModel = new SubscribedEntityViewModel(snapshot);
        var handler = new OpenShellEntityShortcutHandler((_, _, _) => throw new InvalidOperationException());

        Assert.True(await handler.ShouldApplyTo(viewModel, Shortcut.Open, entityViewModel));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ShouldApplyTo_OpenShortcutOnNonShellEntity_ReturnsFalse()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var snapshot = MakeSnapshot("""{"entity-types":["entity","git-worktree"],"path":"/repo"}""");
        var entityViewModel = new SubscribedEntityViewModel(snapshot);
        var handler = new OpenShellEntityShortcutHandler((_, _, _) => throw new InvalidOperationException());

        Assert.False(await handler.ShouldApplyTo(viewModel, Shortcut.Open, entityViewModel));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ShouldApplyTo_NonOpenShortcutOnShellEntity_ReturnsFalse()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var snapshot = MakeSnapshot("""{"entity-types":["entity","shell"],"command":"pwsh"}""");
        var entityViewModel = new SubscribedEntityViewModel(snapshot);
        var handler = new OpenShellEntityShortcutHandler((_, _, _) => throw new InvalidOperationException());

        Assert.False(await handler.ShouldApplyTo(viewModel, Shortcut.StartShell, entityViewModel));
        Assert.False(await handler.ShouldApplyTo(viewModel, Shortcut.Delete, entityViewModel));
        Assert.False(await handler.ShouldApplyTo(viewModel, Shortcut.OpenWorkspace, entityViewModel));
    }

    // ---- Handle: produces a ShellTabViewModel ------------------------------------------------

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Handle_OnShellEntity_OpensShellTabViewModel()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var snapshot = MakeSnapshot("""{"entity-types":["entity","shell"],"display-name":{"default":"my-shell"},"mode":"pty","command":"pwsh"}""");
        var entityViewModel = new SubscribedEntityViewModel(snapshot);

        var fakeSession = new FakeTerminalSession();
        var handler = new OpenShellEntityShortcutHandler((_, _, _) => Task.FromResult<ITerminalSession>(fakeSession));

        var handled = await handler.Handle(viewModel, Shortcut.Open, entityViewModel);

        Assert.True(handled);
        Assert.NotNull(FindShellTab(viewModel));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Handle_OnShellEntity_DoesNotOpenEntityCardTab()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var snapshot = MakeSnapshot("""{"entity-types":["entity","shell"],"display-name":{"default":"my-shell"},"command":"pwsh"}""");
        var entityViewModel = new SubscribedEntityViewModel(snapshot);

        var handler = new OpenShellEntityShortcutHandler(
            (_, _, _) => Task.FromResult<ITerminalSession>(new FakeTerminalSession()));

        await handler.Handle(viewModel, Shortcut.Open, entityViewModel);

        // Ensure the shell entity's own tab is a ShellTabViewModel, not an entity card. Other
        // seeded tabs (e.g. the "Getting Started" welcome note) are ignored — we only care that
        // opening THIS entity produced a terminal, not a card.
        Assert.Null(FindEntityCardTabForEntity(viewModel, entityViewModel.EntityId));
        Assert.NotNull(FindShellTab(viewModel));
    }

    // ---- Handle: passes entity fields to the session opener ----------------------------------

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Handle_OnShellEntity_StartsShellSessionFromEntityFields()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var snapshot = MakeSnapshot("""
            {
              "entity-types": ["entity", "shell"],
              "display-name": {"default": "my-shell"},
              "mode": "pipe",
              "command": "bash",
              "command-arguments": ["-l", "-c", "echo hi"],
              "working-directory": "/work/dir",
              "environment": {"FOO": "bar", "BAZ": "qux"}
            }
            """);
        var entityViewModel = new SubscribedEntityViewModel(snapshot);

        ShellEntityOpenSpec? receivedSpec = null;
        var handler = new OpenShellEntityShortcutHandler(
            (_, spec, _) =>
            {
                receivedSpec = spec;
                return Task.FromResult<ITerminalSession>(new FakeTerminalSession());
            });

        await handler.Handle(viewModel, Shortcut.Open, entityViewModel);

        Assert.NotNull(receivedSpec);
        Assert.Equal("pipe", receivedSpec!.Mode);
        Assert.Equal("bash", receivedSpec.Command);
        Assert.Equal(new[] { "-l", "-c", "echo hi" }, receivedSpec.CommandArguments);
        Assert.Equal("/work/dir", receivedSpec.WorkingDirectory);
        Assert.NotNull(receivedSpec.Environment);
        Assert.Equal("bar", receivedSpec.Environment!["FOO"]);
        Assert.Equal("qux", receivedSpec.Environment!["BAZ"]);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Handle_OnShellEntityWithoutCommand_UsesDefaultShellCommand()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var snapshot = MakeSnapshot("""{"entity-types":["entity","shell"],"display-name":{"default":"my-shell"}}""");
        var entityViewModel = new SubscribedEntityViewModel(snapshot);

        ShellEntityOpenSpec? receivedSpec = null;
        var handler = new OpenShellEntityShortcutHandler(
            (_, spec, _) =>
            {
                receivedSpec = spec;
                return Task.FromResult<ITerminalSession>(new FakeTerminalSession());
            });

        await handler.Handle(viewModel, Shortcut.Open, entityViewModel);

        Assert.NotNull(receivedSpec);
        var expectedDefault = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.Windows) ? "pwsh" : "bash";
        Assert.Equal(expectedDefault, receivedSpec!.Command);
    }

    // ---- Handle: dedup and error paths -------------------------------------------------------

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Handle_OnShellEntityOpenedTwice_DeduplicatesTab()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var snapshot = MakeSnapshot("""{"entity-types":["entity","shell"],"display-name":{"default":"my-shell"},"command":"pwsh"}""");
        var entityViewModel = new SubscribedEntityViewModel(snapshot);

        var handler = new OpenShellEntityShortcutHandler(
            (_, _, _) => Task.FromResult<ITerminalSession>(new FakeTerminalSession()));

        await handler.Handle(viewModel, Shortcut.Open, entityViewModel);
        await handler.Handle(viewModel, Shortcut.Open, entityViewModel);

        var shellTabs = FindAllShellTabs(viewModel).ToArray();
        Assert.Single(shellTabs);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Handle_WhenSessionFailsToStart_DoesNotFallBackToEntityCard()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var snapshot = MakeSnapshot("""{"entity-types":["entity","shell"],"display-name":{"default":"my-shell"},"command":"pwsh"}""");
        var entityViewModel = new SubscribedEntityViewModel(snapshot);

        var handler = new OpenShellEntityShortcutHandler(
            (_, _, _) => Task.FromException<ITerminalSession>(new InvalidOperationException("boom")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.Handle(viewModel, Shortcut.Open, entityViewModel));

        Assert.Null(FindEntityCardTabForEntity(viewModel, entityViewModel.EntityId));
        Assert.Null(FindShellTab(viewModel));
    }

    // ---- Registration ordering --------------------------------------------------------------

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ShortcutHandlers_OpenShellEntityHandler_RegisteredBeforeOpenEntityHandler()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var handlersField = typeof(ShortcutManager).GetField(
            "shortcutHandlers",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(handlersField);
        var handlers = (List<ShortcutHandler>)handlersField!.GetValue(viewModel.ShortcutManager)!;

        var shellIndex = handlers.FindIndex(h => h is OpenShellEntityShortcutHandler);
        var openIndex = handlers.FindIndex(h => h is OpenEntityShortcutHandler);

        Assert.True(shellIndex >= 0, "OpenShellEntityShortcutHandler is not registered.");
        Assert.True(openIndex >= 0, "OpenEntityShortcutHandler is not registered.");
        Assert.True(
            shellIndex < openIndex,
            $"OpenShellEntityShortcutHandler (index {shellIndex}) must be registered before OpenEntityShortcutHandler (index {openIndex}).");
    }

    // ---- Helpers ----------------------------------------------------------------------------

    private static EntitySnapshot MakeSnapshot(string json) =>
        new()
        {
            EntityId = new EntityId(Guid.NewGuid()),
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, Guid.NewGuid().ToString()),
            Data = JsonDocument.Parse(json).RootElement.Clone(),
            Relationships = [],
        };

    private static ShellTabViewModel? FindShellTab(MainWindowViewModel viewModel)
        => FindAllShellTabs(viewModel).FirstOrDefault();

    private static IEnumerable<ShellTabViewModel> FindAllShellTabs(MainWindowViewModel viewModel)
    {
        var contentLayout = viewModel.SelectedWorkspacePane?.ContentLayout;
        if (contentLayout is null)
        {
            yield break;
        }

        var documentDock = FindDocumentDock(contentLayout);
        if (documentDock?.VisibleDockables is null)
        {
            yield break;
        }

        foreach (var doc in documentDock.VisibleDockables.OfType<WorkspaceDocument>())
        {
            if (doc.TabViewModel is ShellTabViewModel shellTab)
            {
                yield return shellTab;
            }
        }
    }

    private static EntityWorkspaceTabViewModel? FindEntityCardTab(MainWindowViewModel viewModel)
    {
        var contentLayout = viewModel.SelectedWorkspacePane?.ContentLayout;
        if (contentLayout is null)
        {
            return null;
        }

        var documentDock = FindDocumentDock(contentLayout);
        return documentDock?.VisibleDockables?
            .OfType<WorkspaceDocument>()
            .Select(doc => doc.TabViewModel)
            .OfType<EntityWorkspaceTabViewModel>()
            .FirstOrDefault();
    }

    private static EntityWorkspaceTabViewModel? FindEntityCardTabForEntity(
        MainWindowViewModel viewModel,
        EntityId entityId)
    {
        var contentLayout = viewModel.SelectedWorkspacePane?.ContentLayout;
        if (contentLayout is null)
        {
            return null;
        }

        var documentDock = FindDocumentDock(contentLayout);
        var expectedId = entityId.ToString();
        return documentDock?.VisibleDockables?
            .OfType<WorkspaceDocument>()
            .Select(doc => doc.TabViewModel)
            .OfType<EntityWorkspaceTabViewModel>()
            .FirstOrDefault(tab => string.Equals(tab.Id, expectedId, StringComparison.Ordinal));
    }

    private static IDocumentDock? FindDocumentDock(IDockable dockable)
    {
        if (dockable is IDocumentDock documentDock)
        {
            return documentDock;
        }

        if (dockable is IDock dock && dock.VisibleDockables is not null)
        {
            foreach (var child in dock.VisibleDockables)
            {
                var result = FindDocumentDock(child);
                if (result is not null)
                {
                    return result;
                }
            }
        }

        return null;
    }

    private sealed class FakeTerminalSession : ITerminalSession
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
}
