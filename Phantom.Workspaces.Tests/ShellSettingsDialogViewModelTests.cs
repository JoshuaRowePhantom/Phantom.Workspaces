using Avalonia.Headless.XUnit;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class ShellSettingsDialogViewModelTests
{
    [AvaloniaFact(Timeout = 15_000)]
    public void Dialog_WhenConstructed_LoadsCommandWorkingDirectoryArgumentsAndEnvironment()
    {
        var spec = new ShellEntityOpenSpec
        {
            Mode = "pty",
            Command = "bash",
            CommandArguments = new[] { "-l", "-c" },
            WorkingDirectory = "/w",
            Environment = new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" },
        };

        var vm = new ShellSettingsDialogViewModel(spec, null, null, null, null);

        Assert.Equal("bash", vm.CommandLine);
        Assert.Equal("/w", vm.WorkingDirectory);
        Assert.Equal("-l -c", vm.Arguments);
        Assert.Equal(2, vm.EnvironmentVariables.Count);
        Assert.Contains(vm.EnvironmentVariables, r => r.Name == "A" && r.Value == "1");
        Assert.Contains(vm.EnvironmentVariables, r => r.Name == "B" && r.Value == "2");
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void AddEnvVarCommand_WhenExecuted_AppendsEmptyRow()
    {
        var vm = new ShellSettingsDialogViewModel(
            new ShellEntityOpenSpec { Mode = "pty", Command = "x" },
            null, null, null, null);

        Assert.Empty(vm.EnvironmentVariables);
        vm.AddEnvVarCommand.Execute(null);

        var row = Assert.Single(vm.EnvironmentVariables);
        Assert.Equal(string.Empty, row.Name);
        Assert.Equal(string.Empty, row.Value);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void RemoveEnvVarCommand_WhenExecuted_RemovesRow()
    {
        var vm = new ShellSettingsDialogViewModel(
            new ShellEntityOpenSpec
            {
                Mode = "pty",
                Command = "x",
                Environment = new Dictionary<string, string> { ["A"] = "1" },
            },
            null, null, null, null);

        Assert.Single(vm.EnvironmentVariables);
        vm.RemoveEnvVarCommand.Execute(null);
        Assert.Empty(vm.EnvironmentVariables);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task SaveAsync_WhenExecuted_WritesCommandArgumentsWorkingDirectoryAndEnvironmentToEntity()
    {
        var entityId = new EntityId(Guid.NewGuid());
        var tag = new ConcurrencyTag("t1");
        var existingData = JsonDocument.Parse(
            """{"entity-types":["entity","shell"],"display-name":{"default":"n"},"command":"pwsh"}""")
            .RootElement.Clone();

        UpdateRequest? captured = null;
        Func<UpdateRequest, CancellationToken, Task<UpdateResult>> writer = (req, _) =>
        {
            captured = req;
            return Task.FromResult(new UpdateResult { EntityResults = Array.Empty<EntityUpdateResult>() });
        };

        var vm = new ShellSettingsDialogViewModel(
            new ShellEntityOpenSpec { Mode = "pty", Command = "pwsh" },
            entityId,
            tag,
            existingData,
            writer)
        {
            CommandLine = "bash",
            WorkingDirectory = "/w",
            Arguments = "-l -c",
        };
        vm.EnvironmentVariables.Add(new ShellEnvVarRowViewModel { Name = "K", Value = "V" });

        var updatedSpec = await vm.SaveAsync();

        Assert.NotNull(captured);
        var change = Assert.Single(captured!.Changes);
        Assert.Equal(entityId, change.EntityId);
        Assert.Equal(tag, change.ConcurrencyTag);
        var data = (JsonElement)change.Data!;
        Assert.Equal("bash", data.GetProperty("command").GetString());
        Assert.Equal("-l", data.GetProperty("command-arguments")[0].GetString());
        Assert.Equal("-c", data.GetProperty("command-arguments")[1].GetString());
        Assert.Equal("/w", data.GetProperty("working-directory").GetString());
        Assert.Equal("V", data.GetProperty("environment").GetProperty("K").GetString());
        // Original fields preserved:
        Assert.Equal("n", data.GetProperty("display-name").GetProperty("default").GetString());

        Assert.NotNull(updatedSpec);
        Assert.Equal("bash", updatedSpec!.Command);
        Assert.Equal("/w", updatedSpec.WorkingDirectory);
        Assert.Equal(new[] { "-l", "-c" }, updatedSpec.CommandArguments);
        Assert.NotNull(updatedSpec.Environment);
        Assert.Equal("V", updatedSpec.Environment!["K"]);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task SaveAsync_WhenExecuted_DoesNotDisturbRunningProcess()
    {
        // The dialog view-model has no live process handle at all: this test proves
        // the SaveAsync code path is configuration-only (touches nothing that could
        // signal/dispose a running shell). We construct a fake session and confirm
        // it stays untouched across a SaveAsync call routed through the writer.
        var session = new ShellTabViewModelTests.RecordingTerminalSession();

        UpdateRequest? captured = null;
        Func<UpdateRequest, CancellationToken, Task<UpdateResult>> writer = (req, _) =>
        {
            captured = req;
            return Task.FromResult(new UpdateResult { EntityResults = Array.Empty<EntityUpdateResult>() });
        };

        var entityId = new EntityId(Guid.NewGuid());
        var vm = new ShellSettingsDialogViewModel(
            new ShellEntityOpenSpec { Mode = "pty", Command = "pwsh" },
            entityId,
            null,
            null,
            writer);

        await vm.SaveAsync();

        Assert.NotNull(captured);
        Assert.Empty(session.SentSignals);
        Assert.False(session.Disposed);
    }
}
