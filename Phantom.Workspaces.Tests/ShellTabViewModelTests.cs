using Avalonia.Headless.XUnit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm.Shell;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class ShellTabViewModelTests
{
    [AvaloniaFact(Timeout = 15_000)]
    public async Task StopCommand_WhenExecuted_SignalsRunningSession()
    {
        var session = new RecordingTerminalSession();
        var tab = new ShellTabViewModel(
            session,
            MakeSpec("pwsh"),
            sessionFactory: null,
            sourceEntityId: null,
            concurrencyTag: null,
            sourceEntityData: null,
            entityWriter: null,
            dialogOpener: null)
        {
            Id = "t",
            Title = "t",
        };

        tab.StopCommand.Execute(null);
        await (tab.StopCommand.LastExecutionTask ?? Task.CompletedTask);

        var signal = Assert.Single(session.SentSignals);
        Assert.Equal("SIGTERM", signal);
        Assert.False(session.Disposed);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task RestartCommand_WhenExecuted_DisposesOldSessionAndCreatesNew()
    {
        var oldSession = new RecordingTerminalSession();
        var newSession = new RecordingTerminalSession();
        int factoryCalls = 0;
        Func<ShellEntityOpenSpec, CancellationToken, Task<ITerminalSession>> factory = (_, _) =>
        {
            factoryCalls++;
            return Task.FromResult<ITerminalSession>(newSession);
        };

        var tab = new ShellTabViewModel(
            oldSession,
            MakeSpec("pwsh"),
            factory,
            sourceEntityId: null,
            concurrencyTag: null,
            sourceEntityData: null,
            entityWriter: null,
            dialogOpener: null)
        {
            Id = "t",
            Title = "t",
        };

        tab.RestartCommand.Execute(null);
        await (tab.RestartCommand.LastExecutionTask ?? Task.CompletedTask);

        Assert.Equal(1, factoryCalls);
        Assert.True(oldSession.Disposed);
        Assert.False(newSession.Disposed);
        Assert.Same(newSession.Stream, tab.TerminalSession.Stream);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task RestartCommand_WhenExecuted_LaunchesWithEditedSpec()
    {
        var oldSession = new RecordingTerminalSession();
        ShellEntityOpenSpec? received = null;
        Func<ShellEntityOpenSpec, CancellationToken, Task<ITerminalSession>> factory = (spec, _) =>
        {
            received = spec;
            return Task.FromResult<ITerminalSession>(new RecordingTerminalSession());
        };

        var tab = new ShellTabViewModel(
            oldSession,
            MakeSpec("pwsh"),
            factory,
            sourceEntityId: null,
            concurrencyTag: null,
            sourceEntityData: null,
            entityWriter: null,
            dialogOpener: null)
        {
            Id = "t",
            Title = "t",
        };

        tab.CommandLine = "bash";

        tab.RestartCommand.Execute(null);
        await (tab.RestartCommand.LastExecutionTask ?? Task.CompletedTask);

        Assert.NotNull(received);
        Assert.Equal("bash", received!.Command);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void CommandLine_WhenEdited_UpdatesSpecCommand()
    {
        var tab = new ShellTabViewModel(
            new RecordingTerminalSession(),
            MakeSpec("pwsh"),
            sessionFactory: null,
            sourceEntityId: null,
            concurrencyTag: null,
            sourceEntityData: null,
            entityWriter: null,
            dialogOpener: null)
        {
            Id = "t",
            Title = "t",
        };

        tab.CommandLine = "bash -l";

        Assert.Equal("bash -l", tab.Spec.Command);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task SaveCommand_WhenExecuted_WritesConfigurationToSourceEntity()
    {
        var entityId = new EntityId(Guid.NewGuid());
        var tag = new ConcurrencyTag("tag-1");
        var existingData = JsonDocument.Parse("""{"entity-types":["entity","shell"],"display-name":{"default":"my"},"mode":"pty","command":"pwsh"}""").RootElement.Clone();

        UpdateRequest? captured = null;
        Func<UpdateRequest, CancellationToken, Task<UpdateResult>> writer = (req, _) =>
        {
            captured = req;
            return Task.FromResult(new UpdateResult { EntityResults = Array.Empty<EntityUpdateResult>() });
        };

        var tab = new ShellTabViewModel(
            new RecordingTerminalSession(),
            new ShellEntityOpenSpec
            {
                Mode = "pty",
                Command = "pwsh",
                CommandArguments = new[] { "-NoLogo" },
                WorkingDirectory = "/tmp",
                Environment = new Dictionary<string, string> { ["FOO"] = "bar" },
            },
            sessionFactory: null,
            sourceEntityId: entityId,
            concurrencyTag: tag,
            sourceEntityData: existingData,
            entityWriter: writer,
            dialogOpener: null)
        {
            Id = "t",
            Title = "t",
        };

        tab.CommandLine = "bash";
        tab.SaveCommand.Execute(null);
        await (tab.SaveCommand.LastExecutionTask ?? Task.CompletedTask);

        Assert.NotNull(captured);
        var change = Assert.Single(captured!.Changes);
        Assert.Equal(entityId, change.EntityId);
        Assert.Equal(tag, change.ConcurrencyTag);
        Assert.Equal(EntityChangeMode.Replace, change.EntityChangeMode);
        var data = change.Data;
        Assert.NotNull(data);
        var doc = (JsonElement)data!;
        Assert.Equal("bash", doc.GetProperty("command").GetString());
        Assert.Equal("pty", doc.GetProperty("mode").GetString());
        Assert.Equal("-NoLogo", doc.GetProperty("command-arguments")[0].GetString());
        Assert.Equal("/tmp", doc.GetProperty("working-directory").GetString());
        Assert.Equal("bar", doc.GetProperty("environment").GetProperty("FOO").GetString());
        // Preserved from the existing entity data:
        Assert.Equal("my", doc.GetProperty("display-name").GetProperty("default").GetString());
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void SaveCommand_WhenNoEntityContext_IsDisabled()
    {
        var tab = new ShellTabViewModel(
            new RecordingTerminalSession(),
            MakeSpec("pwsh"),
            sessionFactory: null,
            sourceEntityId: null,
            concurrencyTag: null,
            sourceEntityData: null,
            entityWriter: null,
            dialogOpener: null)
        {
            Id = "t",
            Title = "t",
        };

        Assert.False(tab.SaveCommand.CanExecute(null));
        Assert.False(tab.CanSave);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenSettingsCommand_WhenExecuted_SeedsDialogFromCurrentSpec()
    {
        ShellSettingsDialogViewModel? seen = null;
        Func<ShellSettingsDialogViewModel, Task<ShellEntityOpenSpec?>> opener = vm =>
        {
            seen = vm;
            return Task.FromResult<ShellEntityOpenSpec?>(null);
        };

        var tab = new ShellTabViewModel(
            new RecordingTerminalSession(),
            new ShellEntityOpenSpec
            {
                Mode = "pty",
                Command = "pwsh",
                CommandArguments = new[] { "-c", "echo hi" },
                WorkingDirectory = "/work",
                Environment = new Dictionary<string, string> { ["X"] = "1" },
            },
            sessionFactory: null,
            sourceEntityId: null,
            concurrencyTag: null,
            sourceEntityData: null,
            entityWriter: null,
            dialogOpener: opener)
        {
            Id = "t",
            Title = "t",
        };

        tab.OpenSettingsCommand.Execute(null);
        await (tab.OpenSettingsCommand.LastExecutionTask ?? Task.CompletedTask);

        Assert.NotNull(seen);
        Assert.Equal("pwsh", seen!.CommandLine);
        Assert.Equal("/work", seen.WorkingDirectory);
        Assert.Equal("-c echo hi", seen.Arguments);
        var row = Assert.Single(seen.EnvironmentVariables);
        Assert.Equal("X", row.Name);
        Assert.Equal("1", row.Value);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task DialogSave_WhenReturnsUpdatedSpec_UpdatesInlineCommandLine()
    {
        var updated = new ShellEntityOpenSpec { Mode = "pty", Command = "bash" };
        Func<ShellSettingsDialogViewModel, Task<ShellEntityOpenSpec?>> opener = _ =>
            Task.FromResult<ShellEntityOpenSpec?>(updated);

        var tab = new ShellTabViewModel(
            new RecordingTerminalSession(),
            MakeSpec("pwsh"),
            sessionFactory: null,
            sourceEntityId: null,
            concurrencyTag: null,
            sourceEntityData: null,
            entityWriter: null,
            dialogOpener: opener)
        {
            Id = "t",
            Title = "t",
        };

        tab.OpenSettingsCommand.Execute(null);
        await (tab.OpenSettingsCommand.LastExecutionTask ?? Task.CompletedTask);

        Assert.Equal("bash", tab.CommandLine);
        Assert.Equal("bash", tab.Spec.Command);
    }

    private static ShellEntityOpenSpec MakeSpec(string command) =>
        new() { Mode = "pty", Command = command };

    internal sealed class RecordingTerminalSession : ITerminalSession
    {
        private readonly MemoryStream stream = new();

        public List<string> SentSignals { get; } = new();

        public bool Disposed { get; private set; }

        public Stream Stream => this.stream;

        public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask SignalAsync(string signal, CancellationToken cancellationToken)
        {
            this.SentSignals.Add(signal);
            return ValueTask.CompletedTask;
        }

        public Task<int> WaitForExitAsync() => Task.FromResult(0);

        public ValueTask DisposeAsync()
        {
            this.Disposed = true;
            this.stream.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}



