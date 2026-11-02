using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Remote;

namespace Phantom.Workspaces.Tests;

public sealed partial class DataAccessAgentSessionRuntimeHostFactoryTests
{
    [Theory]
    [InlineData("stopped", false)]
    [InlineData("stopped", true)]
    [InlineData("epoch", false)]
    [InlineData("epoch", true)]
    [InlineData("epoch-query", false)]
    [InlineData("epoch-query", true)]
    [InlineData("blocked", false)]
    [InlineData("blocked", true)]
    [InlineData("reconciliation", false)]
    [InlineData("reconciliation", true)]
    [InlineData("succeeded", false)]
    [InlineData("succeeded", true)]
    [InlineData("succeeded-ack", false)]
    [InlineData("succeeded-ack", true)]
    public async Task RestartSession_DurableDeadline_ReleasesGatesAndFencesLateCompletion(
        string boundary,
        bool disconnectCaller)
    {
        await using var environment = new RestartEnvironment();
        await using var viewer = await environment.AcquireAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var durablyBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockedOnce = false;
        var creations = 0;
        var stoppedSaved = false;
        environment.OnUpdated = update =>
        {
            if (update.Changes.Single().Data!.Value.TryGetProperty("restart-operations", out var journal)
                && JsonSerializer.Deserialize<Dictionary<string,
                    Services.AgentSessions.DataAccessAgentSessionRuntimeHostFactory.RestartOperation>>(
                        journal, Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions) is { } operations)
            {
                stoppedSaved = operations.Values.Any(value => value.State == AgentSessionOwnerRestartState.Stopped);
                if (operations.Values.Any(value => value.State == AgentSessionOwnerRestartState.Blocked))
                    durablyBlocked.TrySetResult();
            }
        };
        environment.BeforePublication = _ =>
        {
            creations++;
            if (boundary is "blocked" or "reconciliation")
                throw new IOException("Publication failed.");
        };
        environment.BeforeQueryAsync = async () =>
        {
            if (blockedOnce || !(boundary == "reconciliation" && creations != 0
                || boundary == "epoch-query" && stoppedSaved))
                return;
            blockedOnce = true;
            entered.TrySetResult();
            await release.Task;
            finished.TrySetResult();
        };
        environment.BeforeUpdateAsync = async update =>
        {
            var data = update.Changes.Single().Data!.Value;
            var journalState = data.TryGetProperty("restart-operations", out var journal)
                ? JsonSerializer.Deserialize<Dictionary<string,
                    Services.AgentSessions.DataAccessAgentSessionRuntimeHostFactory.RestartOperation>>(
                        journal, Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions)!.Values.Last().State
                : (AgentSessionOwnerRestartState?)null;
            var matches = boundary switch
            {
                "stopped" => journalState == AgentSessionOwnerRestartState.Stopped,
                "blocked" => journalState == AgentSessionOwnerRestartState.Blocked,
                "epoch" => update.UpdateMetadata?.Comment?.Text == "Fence replacement agent session runtime epoch",
                "succeeded" => journalState == AgentSessionOwnerRestartState.Succeeded,
                _ => false,
            };
            if (!matches || blockedOnce)
                return;
            blockedOnce = true;
            entered.TrySetResult();
            await release.Task;
            finished.TrySetResult();
        };
        environment.AfterUpdateAsync = async update =>
        {
            if (boundary != "succeeded-ack" || blockedOnce
                || !update.Changes.Single().Data!.Value.TryGetProperty("restart-operations", out var journal))
                return;
            var operation = JsonSerializer.Deserialize<Dictionary<string,
                Services.AgentSessions.DataAccessAgentSessionRuntimeHostFactory.RestartOperation>>(
                    journal, Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions)!.Values.Last();
            if (operation.State != AgentSessionOwnerRestartState.Succeeded)
                return;
            blockedOnce = true;
            entered.TrySetResult();
            await release.Task;
            finished.TrySetResult();
        };
        using var caller = new CancellationTokenSource();
        var request = new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(),
            ExpectedRuntime = viewer.RestartIdentity!,
        };
        var restarting = viewer.RestartRuntimeAsync!(request, caller.Token);
        try
        {
            await Task.WhenAny(entered.Task, restarting).WaitAsync(TestContext.Current.CancellationToken);
            if (!entered.Task.IsCompleted)
                Assert.Fail($"The intended durable boundary was not reached: {await restarting}");
            if (disconnectCaller)
                caller.Cancel();
            environment.Time.Advance(TimeSpan.FromSeconds(15));
            await Record.ExceptionAsync(() => restarting.WaitAsync(TestContext.Current.CancellationToken));
            Assert.Equal(AgentSessionOwnerRestartState.Blocked,
                (await viewer.RestartStatusAsync!(request, TestContext.Current.CancellationToken))!.State);
            Assert.Equal(AgentSessionOwnerRestartState.Blocked,
                (await viewer.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken)).State);
            Assert.Empty(environment.Table.RunningSessions);
            await Assert.ThrowsAnyAsync<Exception>(() => environment.AcquireAsync());
            await using var recovered = new RestartEnvironment(environment.Store, environment.Persisted);
            recovered.Time.Advance(TimeSpan.FromMinutes(2));
            if (boundary == "succeeded-ack")
                Assert.Equal(AgentSessionOwnerRestartState.Blocked,
                    (await recovered.Host.GetRestartStatusAsync(RestartPeer(), request, TestContext.Current.CancellationToken))!.State);
            Assert.Equal(AgentSessionOwnerRestartState.Blocked,
                (await recovered.Host.RestartAsync(RestartPeer(), request, TestContext.Current.CancellationToken)).State);
            Assert.Empty(recovered.Table.RunningSessions);
        }
        finally
        {
            release.TrySetResult();
        }
        await finished.Task.WaitAsync(TestContext.Current.CancellationToken);
        await durablyBlocked.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AgentSessionOwnerRestartState.Blocked,
            (await viewer.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken)).State);
        Assert.Equal(boundary is "blocked" or "reconciliation" or "succeeded" or "succeeded-ack" ? 1 : 0, creations);
    }

    [Fact]
    public async Task RestartSession_ConfirmedSuccessAcknowledgementDeadline_DoesNotCleanUpHealthyRuntime()
    {
        await using var environment = new RestartEnvironment();
        await using var viewer = await environment.AcquireAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockedOnce = false;
        environment.BeforeUpdateAsync = async update =>
        {
            if (blockedOnce || !update.Changes.Single().Data!.Value.TryGetProperty("restart-operations", out var journal))
                return;
            var operation = JsonSerializer.Deserialize<Dictionary<string,
                Services.AgentSessions.DataAccessAgentSessionRuntimeHostFactory.RestartOperation>>(
                    journal, Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions)!.Values.Last();
            if (operation.State != AgentSessionOwnerRestartState.Succeeded || operation.StartupUnconfirmed)
                return;
            blockedOnce = true;
            entered.TrySetResult();
            await release.Task;
            finished.TrySetResult();
        };
        var request = new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(), ExpectedRuntime = viewer.RestartIdentity!,
        };
        var restarting = viewer.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            environment.Time.Advance(TimeSpan.FromSeconds(15));
            var result = await restarting.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(AgentSessionOwnerRestartState.Succeeded, result.State);
            var current = (await environment.Registry.TryGetAsync("session", 3, TestContext.Current.CancellationToken))!;
            Assert.False(Assert.IsType<AgentChat>(current.Chat).IsTerminal);
            Assert.Equal(result, await viewer.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken));
            Assert.Same(current, await environment.Registry.TryGetAsync("session", 3, TestContext.Current.CancellationToken));
            Assert.Single(environment.Table.RunningSessions);
        }
        finally { release.TrySetResult(); }
        await finished.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AgentSessionOwnerRestartState.Succeeded,
            (await viewer.RestartStatusAsync!(request, TestContext.Current.CancellationToken))!.State);
        Assert.Single(environment.Table.RunningSessions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartSession_LateDurableCompletion_DoesNotDowngradeNewOwnerOrGeneration(bool changeOwner)
    {
        await using var environment = new RestartEnvironment();
        await using var viewer = await environment.AcquireAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        environment.BeforeUpdateAsync = async update =>
        {
            if (update.UpdateMetadata?.Comment?.Text != "Fence replacement agent session runtime epoch")
                return;
            entered.TrySetResult();
            await release.Task;
        };
        var request = new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(), ExpectedRuntime = viewer.RestartIdentity!,
        };
        var restarting = viewer.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken);
        Task[] pending = [];
        JsonElement changed = default;
        try
        {
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            environment.Time.Advance(TimeSpan.FromSeconds(15));
            Assert.Equal(AgentSessionOwnerRestartState.Blocked,
                (await restarting.WaitAsync(TestContext.Current.CancellationToken)).State);
            var tasks = (System.Collections.Concurrent.ConcurrentDictionary<Task, byte>)
                typeof(Services.AgentSessions.DataAccessAgentSessionRuntimeHostFactory)
                    .GetField("pendingPersistence", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                    .GetValue(null)!;
            pending = tasks.Keys.ToArray();
            Assert.NotEmpty(pending);
            environment.ChangeAuthority(new Dictionary<string, JsonElement>
            {
                ["ownership-generation"] = JsonSerializer.SerializeToElement(changeOwner ? 3 : 4),
                ["owning-profile-entity-id"] = JsonSerializer.SerializeToElement(changeOwner
                    ? "99999999-9999-9999-9999-999999999999" : Owner),
                ["runtime-epoch"] = JsonSerializer.SerializeToElement(Guid.NewGuid().ToString("D")),
            });
            changed = environment.Persisted.Data!.Value.Clone();
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(pending).WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(changed.GetRawText(), environment.Persisted.Data!.Value.GetRawText());
        Assert.Empty(environment.Table.RunningSessions);
    }
}
