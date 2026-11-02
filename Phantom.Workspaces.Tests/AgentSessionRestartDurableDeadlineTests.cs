using System.Text.Json;
using System.Threading.Channels;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Remote;

namespace Phantom.Workspaces.Tests;

public sealed partial class DataAccessAgentSessionRuntimeHostFactoryTests
{
    [Theory]
    [InlineData("throw", false)]
    [InlineData("throw", true)]
    [InlineData("after-commit", false)]
    [InlineData("after-commit", true)]
    [InlineData("before-commit", false)]
    [InlineData("before-commit", true)]
    public async Task RestartSession_FinalConfirmationAmbiguous_OrdinaryRenewalKeepsReplacementHealthy(
        string acknowledgement,
        bool disconnectCaller)
    {
        var time = new RestartRenewalTimeProvider();
        await using var environment = new RestartEnvironment(timeProvider: time);
        await using var viewer = await environment.AcquireAsync();
        await time.RenewalScheduled.Reader.ReadAsync(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var intercepted = false;
        var replacements = 0;
        environment.BeforePublication = _ => replacements++;
        async Task InterceptAsync(UpdateRequest update)
        {
            if (intercepted || !IsFinalConfirmation(update))
                return;
            intercepted = true;
            entered.SetResult();
            if (acknowledgement == "throw")
            {
                finished.SetResult();
                throw new IOException("Confirmation committed but acknowledgement was lost.");
            }
            await release.Task;
            finished.SetResult();
        }
        if (acknowledgement == "before-commit")
            environment.BeforeUpdateAsync = InterceptAsync;
        else
            environment.AfterUpdateAsync = InterceptAsync;
        using var caller = new CancellationTokenSource();
        var request = new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(), ExpectedRuntime = viewer.RestartIdentity!,
        };
        var restarting = viewer.RestartRuntimeAsync!(request, caller.Token);
        try
        {
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            await time.RenewalScheduled.Reader.ReadAsync(TestContext.Current.CancellationToken);
            var originalExpiry = DateTimeOffset.Parse(
                environment.Persisted.Data!.Value.GetProperty("runtime-lease-expiry").GetString()!);
            await using (var earlyRecovery = new RestartEnvironment(environment.Store, environment.Persisted))
            {
                Assert.Equal(acknowledgement == "before-commit"
                        ? AgentSessionOwnerRestartState.Blocked : AgentSessionOwnerRestartState.Succeeded,
                    (await earlyRecovery.Host.GetRestartStatusAsync(
                        RestartPeer(), request, TestContext.Current.CancellationToken))!.State);
                Assert.Empty(earlyRecovery.Table.RunningSessions);
            }
            if (disconnectCaller)
                caller.Cancel();
            if (acknowledgement != "throw")
                environment.Time.Advance(TimeSpan.FromSeconds(15));
            var result = await restarting.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(AgentSessionOwnerRestartState.Succeeded, result.State);
            if (acknowledgement != "throw")
            {
                // At t=15 the in-flight renewal also schedules its remaining ten-second
                // safety deadline. Wait for the next interval, not that deadline timer.
                await time.RenewalScheduled.Reader.ReadAsync(TestContext.Current.CancellationToken);
                await time.RenewalScheduled.Reader.ReadAsync(TestContext.Current.CancellationToken);
            }
            var current = (await environment.Registry.TryGetAsync("session", 3, TestContext.Current.CancellationToken))!;
            environment.Time.Advance(TimeSpan.FromSeconds(10));
            await time.RenewalScheduled.Reader.ReadAsync(TestContext.Current.CancellationToken);
            var renewedExpiry = DateTimeOffset.Parse(
                environment.Persisted.Data!.Value.GetProperty("runtime-lease-expiry").GetString()!);
            Assert.True(renewedExpiry > originalExpiry);
            Assert.False(ReadRestartOperation(environment).StartupUnconfirmed);
            Assert.NotNull(environment.Host.GetIdentity(current.Chat));

            // Cross the original safety boundary with ordinary renewals, never a repair restart.
            environment.Time.Advance(TimeSpan.FromSeconds(10));
            await time.RenewalScheduled.Reader.ReadAsync(TestContext.Current.CancellationToken);
            environment.Time.Advance(TimeSpan.FromSeconds(10));
            await time.RenewalScheduled.Reader.ReadAsync(TestContext.Current.CancellationToken);
            Assert.True(environment.Time.GetUtcNow() >= originalExpiry);
            Assert.False(current.IsFenced);
            Assert.False(Assert.IsType<AgentChat>(current.Chat).IsTerminal);
            Assert.Same(current, await environment.Registry.TryGetAsync("session", 3, TestContext.Current.CancellationToken));
            Assert.Single(environment.Table.RunningSessions);
            Assert.Equal(1, replacements);
            var renewedTag = environment.Persisted.ConcurrencyTag;
            var pending = PendingPersistenceOperations();
            release.TrySetResult();
            await finished.Task.WaitAsync(TestContext.Current.CancellationToken);
            await Record.ExceptionAsync(() => Task.WhenAll(pending).WaitAsync(TestContext.Current.CancellationToken));
            Assert.Equal(renewedTag, environment.Persisted.ConcurrencyTag);
            Assert.False(ReadRestartOperation(environment).StartupUnconfirmed);

            await using var recovered = new RestartEnvironment(environment.Store, environment.Persisted);
            Assert.Equal(AgentSessionOwnerRestartState.Succeeded,
                (await recovered.Host.GetRestartStatusAsync(RestartPeer(), request, TestContext.Current.CancellationToken))!.State);
            Assert.Empty(recovered.Table.RunningSessions);
        }
        finally { release.TrySetResult(); }
    }

    private sealed class RestartRenewalTimeProvider() : Microsoft.Extensions.Time.Testing.FakeTimeProvider(
        DateTimeOffset.Parse("2026-09-09T00:00:00Z"))
    {
        internal Channel<bool> RenewalScheduled { get; } = Channel.CreateUnbounded<bool>();
        internal Channel<bool> RetryScheduled { get; } = Channel.CreateUnbounded<bool>();

        public override ITimer CreateTimer(
            TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            if (dueTime == TimeSpan.FromSeconds(10))
                this.RenewalScheduled.Writer.TryWrite(true);
            if (dueTime == TimeSpan.FromSeconds(1))
                this.RetryScheduled.Writer.TryWrite(true);
            return timer;
        }
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("generation")]
    [InlineData("epoch")]
    [InlineData("expiry")]
    [InlineData("state")]
    [InlineData("journal")]
    [InlineData("replacement")]
    [InlineData("command")]
    public async Task RestartSession_FinalConfirmationLost_RenewalRejectsChangedAuthority(string change)
    {
        var time = new RestartRenewalTimeProvider();
        await using var environment = new RestartEnvironment(timeProvider: time);
        await using var viewer = await environment.AcquireAsync();
        await time.RenewalScheduled.Reader.ReadAsync(TestContext.Current.CancellationToken);
        environment.AfterUpdateAsync = update => IsFinalConfirmation(update)
            ? Task.FromException(new IOException("Committed confirmation acknowledgement lost."))
            : Task.CompletedTask;
        var request = new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(), ExpectedRuntime = viewer.RestartIdentity!,
        };
        Assert.Equal(AgentSessionOwnerRestartState.Succeeded,
            (await viewer.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken)).State);
        await time.RenewalScheduled.Reader.ReadAsync(TestContext.Current.CancellationToken);
        var current = (await environment.Registry.TryGetAsync("session", 3, TestContext.Current.CancellationToken))!;
        var operation = ReadRestartOperation(environment);
        var updates = change switch
        {
            "owner" => new Dictionary<string, JsonElement>
            {
                ["owning-profile-entity-id"] = JsonSerializer.SerializeToElement(NewOwner),
            },
            "generation" => new Dictionary<string, JsonElement>
            {
                ["ownership-generation"] = JsonSerializer.SerializeToElement(4),
            },
            "epoch" => new Dictionary<string, JsonElement>
            {
                ["runtime-epoch"] = JsonSerializer.SerializeToElement(Guid.NewGuid().ToString("D")),
            },
            "expiry" => new Dictionary<string, JsonElement>
            {
                ["runtime-lease-expiry"] = JsonSerializer.SerializeToElement(time.GetUtcNow().ToString("O")),
            },
            "state" => new Dictionary<string, JsonElement>
            {
                ["runtime-state"] = JsonSerializer.SerializeToElement("stopped"),
            },
            _ => new Dictionary<string, JsonElement>
            {
                ["restart-operations"] = JsonSerializer.SerializeToElement(new Dictionary<string,
                    Services.AgentSessions.DataAccessAgentSessionRuntimeHostFactory.RestartOperation>
                {
                    [request.CommandId.ToString("D")] = change switch
                    {
                        "command" => operation with { Request = request with { CommandId = Guid.NewGuid() } },
                        "replacement" => operation with
                        {
                            Replacement = operation.Replacement with { RuntimeEpoch = new RuntimeEpoch { Value = Guid.NewGuid() } },
                        },
                        _ => operation with { State = AgentSessionOwnerRestartState.Blocked },
                    },
                }, Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions),
            },
        };
        environment.ChangeAuthority(updates);
        var changed = environment.Persisted;
        time.Advance(TimeSpan.FromSeconds(10));
        await time.RetryScheduled.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Null(environment.Host.GetIdentity(current.Chat));
        time.Advance(TimeSpan.FromSeconds(15));
        await Record.ExceptionAsync(async () => await current.DisposeAsync());
        Assert.True(current.IsFenced);
        Assert.Empty(environment.Table.RunningSessions);
        Assert.Equal(changed.ConcurrencyTag, environment.Persisted.ConcurrencyTag);
        Assert.Equal(changed.Data!.Value.GetRawText(), environment.Persisted.Data!.Value.GetRawText());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartSession_FinalConfirmationLost_RenewalMergesMetadataAndRetriesConcurrentUpdate(
        bool conflictDuringRenewal)
    {
        var time = new RestartRenewalTimeProvider();
        await using var environment = new RestartEnvironment(timeProvider: time);
        await using var viewer = await environment.AcquireAsync();
        await time.RenewalScheduled.Reader.ReadAsync(TestContext.Current.CancellationToken);
        environment.AfterUpdateAsync = update => IsFinalConfirmation(update)
            ? Task.FromException(new IOException("Committed confirmation acknowledgement lost."))
            : Task.CompletedTask;
        var request = new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(), ExpectedRuntime = viewer.RestartIdentity!,
        };
        Assert.Equal(AgentSessionOwnerRestartState.Succeeded,
            (await viewer.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken)).State);
        await time.RenewalScheduled.Reader.ReadAsync(TestContext.Current.CancellationToken);
        var current = (await environment.Registry.TryGetAsync("session", 3, TestContext.Current.CancellationToken))!;
        void ChangeMetadata() => environment.ChangeAuthority(new Dictionary<string, JsonElement>
        {
            ["display-name"] = JsonSerializer.SerializeToElement("Changed concurrently"),
        });
        if (conflictDuringRenewal)
            environment.OnUpdate = update =>
            {
                if (update.UpdateMetadata?.Comment?.Text != "Renew agent session runtime lease")
                    return;
                environment.OnUpdate = null;
                ChangeMetadata();
            };
        else
            ChangeMetadata();
        time.Advance(TimeSpan.FromSeconds(10));
        if (conflictDuringRenewal)
        {
            await time.RetryScheduled.Reader.ReadAsync(TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromSeconds(1));
        }
        await time.RenewalScheduled.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Changed concurrently", environment.Persisted.Data!.Value.GetProperty("display-name").GetString());
        Assert.Equal(time.GetUtcNow() + TimeSpan.FromSeconds(30), DateTimeOffset.Parse(
            environment.Persisted.Data!.Value.GetProperty("runtime-lease-expiry").GetString()!));
        Assert.NotNull(environment.Host.GetIdentity(current.Chat));
        Assert.False(current.IsFenced);
        Assert.False(ReadRestartOperation(environment).StartupUnconfirmed);
        Assert.Single(environment.Table.RunningSessions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartSession_FinalConfirmationLost_StalledReconciliationIsBoundedAndLateQueryCannotWrite(
        bool cancelOwnership)
    {
        var time = new RestartRenewalTimeProvider();
        await using var environment = new RestartEnvironment(timeProvider: time);
        await using var viewer = await environment.AcquireAsync();
        await time.RenewalScheduled.Reader.ReadAsync(TestContext.Current.CancellationToken);
        environment.AfterUpdateAsync = update => IsFinalConfirmation(update)
            ? Task.FromException(new IOException("Committed confirmation acknowledgement lost."))
            : Task.CompletedTask;
        var request = new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(), ExpectedRuntime = viewer.RestartIdentity!,
        };
        Assert.Equal(AgentSessionOwnerRestartState.Succeeded,
            (await viewer.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken)).State);
        await time.RenewalScheduled.Reader.ReadAsync(TestContext.Current.CancellationToken);
        var current = (await environment.Registry.TryGetAsync("session", 3, TestContext.Current.CancellationToken))!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        environment.BeforeQueryAsync = async () =>
        {
            entered.TrySetResult();
            await release.Task;
            finished.TrySetResult();
        };
        try
        {
            time.Advance(TimeSpan.FromSeconds(10));
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            if (cancelOwnership)
                await Record.ExceptionAsync(async () => await current.DisposeAsync());
            else
                time.Advance(TimeSpan.FromSeconds(15));
            await Record.ExceptionAsync(async () => await current.DisposeAsync());
            Assert.True(current.IsFenced);
            Assert.Null(environment.Host.GetIdentity(current.Chat));
            Assert.Empty(environment.Table.RunningSessions);
            var fenced = environment.Persisted;
            var pending = PendingPersistenceOperations();
            release.TrySetResult();
            await finished.Task.WaitAsync(TestContext.Current.CancellationToken);
            await Record.ExceptionAsync(() => Task.WhenAll(pending).WaitAsync(TestContext.Current.CancellationToken));
            Assert.Equal(fenced.ConcurrencyTag, environment.Persisted.ConcurrencyTag);
            environment.BeforeQueryAsync = null;
            await using var recovered = new RestartEnvironment(environment.Store, environment.Persisted);
            recovered.Time.Advance(TimeSpan.FromMinutes(2));
            Assert.Equal(AgentSessionOwnerRestartState.Succeeded,
                (await recovered.Host.GetRestartStatusAsync(RestartPeer(), request, TestContext.Current.CancellationToken))!.State);
            Assert.Empty(recovered.Table.RunningSessions);
        }
        finally { release.TrySetResult(); }
    }

    private static bool IsFinalConfirmation(UpdateRequest update)
        => update.UpdateMetadata?.Comment?.Text == "Record agent session restart operation"
            && update.Changes.Single().Data!.Value.TryGetProperty("restart-operations", out var journal)
            && JsonSerializer.Deserialize<Dictionary<string,
                Services.AgentSessions.DataAccessAgentSessionRuntimeHostFactory.RestartOperation>>(
                    journal, Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions)!.Values.Last() is
                { State: AgentSessionOwnerRestartState.Succeeded, StartupUnconfirmed: false };

    private static Task[] PendingPersistenceOperations()
        => ((System.Collections.Concurrent.ConcurrentDictionary<Task, byte>)
            typeof(Services.AgentSessions.DataAccessAgentSessionRuntimeHostFactory)
                .GetField("pendingPersistence", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .GetValue(null)!).Keys.ToArray();

    private static Services.AgentSessions.DataAccessAgentSessionRuntimeHostFactory.RestartOperation ReadRestartOperation(
        RestartEnvironment environment)
        => JsonSerializer.Deserialize<Dictionary<string,
            Services.AgentSessions.DataAccessAgentSessionRuntimeHostFactory.RestartOperation>>(
                environment.Persisted.Data!.Value.GetProperty("restart-operations"),
                Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions)!.Values.Last();

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
