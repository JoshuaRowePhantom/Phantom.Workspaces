using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Gui.Shared.Utilities;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Core.Manifest;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Services.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Phantom.Workspaces.Transport;
using Phantom.Workspaces.ViewModels;
using Xunit;
using AgentViewModel = Phantom.Workspaces.Agent.Gui.ViewModels.AgentViewModel;

namespace Phantom.Workspaces.Tests;

public sealed class OpenAgentSessionShortcutHandlerTests
{
    [AvaloniaTheory(Timeout = 15_000)]
    [InlineData("complete")]
    [InlineData("cancel")]
    [InlineData("fail")]
    [InlineData("cancel-after-acquire")]
    public async Task AutoResume_RemotePromptEnqueue_TransfersOrDisposesLeaseWithCancellation(
        string outcome)
    {
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        var session = await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
            viewModel.EntityBroker,
            new EntityId(Guid.NewGuid()),
            """
            {
              "entity-types": ["entity", "agent-session"],
              "names": [["tests", "gated-enqueue-session"]],
              "agent-session-id": "gated-enqueue-session"
            }
            """);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<AgentInputQueueCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var queue = new Moq.Mock<IAgentInputQueue>();
        queue.SetupGet(value => value.Snapshot).Returns(new AgentInputQueueSnapshot
        {
            QueueId = "default", Name = "Default", IsDefault = true, IsImmediate = false,
            Immediacy = AgentInputQueueImmediacy.Queue, Priority = 0, Revision = 7, Items = [],
        });
        var queues = new Moq.Mock<IAgentInputQueues>();
        queues.SetupGet(value => value.DefaultQueue).Returns(queue.Object);
        queues.SetupGet(value => value.Snapshot).Returns(new AgentInputQueuesSnapshot
        {
            Revision = 7, Queues = [queue.Object.Snapshot],
        });
        queues.Setup(value => value.EnqueueAsync(
                Moq.It.IsAny<EnqueueAgentInputRequest>(), Moq.It.IsAny<CancellationToken>()))
            .Returns(async (EnqueueAgentInputRequest request, CancellationToken token) =>
            {
                Assert.Equal(cancellation.Token, token);
                Assert.Equal("default", request.TargetQueueId);
                Assert.Equal(7, request.ExpectedRevision);
                Assert.Equal("Continue gated resume", Assert.Single(request.Messages).Text);
                entered.TrySetResult();
                return await release.Task.WaitAsync(token);
            });
        var chat = new Moq.Mock<IAgentChat>();
        chat.SetupGet(value => value.InputQueues).Returns(queues.Object);
        var disposed = 0;
        var lease = new RunningAgentChatLease(
            new AgentSessionId("gated-enqueue-session"),
            chat.Object,
            () =>
            {
                Interlocked.Increment(ref disposed);
                return ValueTask.CompletedTask;
            });
        var table = new Moq.Mock<IRunningAgentChatTable>();
        table.Setup(value => value.AcquireAsync(
                Moq.It.IsAny<AcquireAgentChatRequest>(), Moq.It.IsAny<CancellationToken>()))
            .Returns((AcquireAgentChatRequest request, CancellationToken token) =>
            {
                Assert.Equal(cancellation.Token, token);
                Assert.NotNull(request.AgentServices!.CurrentSessionContext);
                if (outcome == "cancel-after-acquire")
                {
                    cancellation.Cancel();
                }
                return Task.FromResult(lease);
            });
        await using var handler = new OpenAgentSessionShortcutHandler(
            new AgentSessionShortcutContext(),
            MainWindowIntegrationTests.CreateLocalTrustedExecutorSelector(),
            table.Object);
        var resume = handler.TryStartAutoResumeAsync(
            viewModel, session, "Continue gated resume", TaskScheduler.Default, cancellation.Token);
        try
        {
            if (outcome != "cancel-after-acquire")
            {
                await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
                Assert.False(resume.IsCompleted);
                Assert.Equal(0, disposed);
            }
            if (outcome == "cancel")
            {
                await cancellation.CancelAsync();
            }
            if (outcome.StartsWith("cancel", StringComparison.Ordinal))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resume);
                Assert.False(release.Task.IsCompleted);
                Assert.Equal(1, disposed);
            }
            else if (outcome == "fail")
            {
                var failure = new InvalidOperationException("Gated enqueue failed");
                release.SetException(failure);
                Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => resume));
                Assert.Equal(1, disposed);
            }
            else
            {
                release.SetResult(new AgentInputQueueCommandResult
                {
                    CommandId = Guid.NewGuid(), Status = AgentInputQueueCommandStatus.Applied, Revision = 8,
                });
                Assert.Same(lease, await resume);
                Assert.Equal(0, disposed);
            }
            queues.Verify(value => value.EnqueueAsync(
                Moq.It.IsAny<EnqueueAgentInputRequest>(), cancellation.Token),
                outcome == "cancel-after-acquire" ? Moq.Times.Never() : Moq.Times.Once());
        }
        finally
        {
            release.TrySetCanceled();
            await lease.DisposeAsync();
        }
        Assert.Equal(1, disposed);
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task Handle_RemoteStatusStalls_RetriesAndAllowsOwnerDecisionBeyondLoadingDeadline()
    {
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        var broker = MainWindowIntegrationTests.GetEntityBroker(viewModel);
        var definition = await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
            broker, new EntityId("bbbb1611-0000-4000-8000-000000000003"),
            """
            {
              "entity-id": "bbbb1611-0000-4000-8000-000000000003",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "remote-stage-retry"]],
              "display-name": { "default": "Remote Stage Retry" },
              "definition": {
                "kind": "prompt",
                "name": "remote-stage-retry",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);
        var sessionId = Guid.NewGuid().ToString("N");
        var session = await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
            broker, new EntityId("bbbb1611-0000-4000-8000-000000000004"),
            $$"""
            {
              "entity-id": "bbbb1611-0000-4000-8000-000000000004",
              "entity-types": ["entity", "agent-session"],
              "names": [["tests", "agent-sessions", "{{sessionId}}"]],
              "display-name": { "default": "Remote Stage Retry" },
              "agent-source-entity-id": "{{definition.EntityId}}",
              "agent-session-id": "{{sessionId}}",
              "host-profile-entity-id": "11111111-1111-1111-1111-111111111111",
              "ownership-generation": 7
            }
            """);
        Assert.NotNull(session);
        var clock = new FakeTimeProvider();
        var stalled = new StalledStatusTransport();
        var retryTransport = new OwnerDecisionTransport(AgentSessionRemoteStatus.Running);
        var choices = new CompletableOwnerDecisionProvider();
        var attempts = 0;
        var registry = new Moq.Mock<ITransportFactoryRegistry>();
        registry.Setup(value => value.ConnectToAsync(
                Moq.It.IsAny<JsonElement>(), Moq.It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult<ITransport>(
                Interlocked.Increment(ref attempts) == 1 ? stalled : retryTransport));
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var handler = new OpenAgentSessionShortcutHandler(
            new AgentSessionShortcutContext(),
            MainWindowIntegrationTests.CreateLocalTrustedExecutorSelector(),
            MainWindowIntegrationTests.CreateTestRunningAgentChatTable(),
            choices,
            registry.Object,
            callback =>
            {
                callback();
                var tabs = viewModel.WorkspacePanes.SelectMany(pane => pane.Tabs)
                    .OfType<AgentSessionWorkspaceTabViewModel>();
                if (tabs.Any(tab => tab.State == AgentTabState.Failed)) failed.TrySetResult();
                if (tabs.Any(tab => tab.State == AgentTabState.Ready)) ready.TrySetResult();
                return Task.CompletedTask;
            },
            clock, TimeSpan.FromSeconds(5));
        Assert.True(await handler.Handle(viewModel, Shortcut.Open, session!));
        await stalled.ReadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(5));
        await failed.Task.WaitAsync(TestContext.Current.CancellationToken);
        var tab = Assert.Single(viewModel.WorkspacePanes.SelectMany(pane => pane.Tabs)
            .OfType<AgentSessionWorkspaceTabViewModel>());
        Assert.Equal(AgentTabState.Failed, tab.State);
        Assert.Contains("status-first-frame", tab.LoadError);
        Assert.True(stalled.IsDisposed);
        Assert.False(stalled.Channel.Writer.TryWrite(
            JsonDocument.Parse("""{"type":"late-status"}""").RootElement.Clone()));
        Assert.Null(tab.Lease);
        Assert.True(await handler.Handle(viewModel, Shortcut.Open, session!));
        await choices.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(40));
        Assert.Equal(AgentTabState.Loading, tab.State);
        choices.Complete(AgentSessionOwnerDecision.ResumeLocally);
        await ready.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AgentTabState.Ready, tab.State);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ResolveRemoteOwner_StatusFirstFrameStalls_ReportsStageAndDisposesTransport()
    {
        var clock = new FakeTimeProvider();
        var stages = new List<string>();
        var firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var choices = new RecordingOwnerDecisionProvider(AgentSessionOwnerDecision.ConnectOnOwner);
        var handler = new OpenAgentSessionShortcutHandler(
            new AgentSessionShortcutContext(),
            MainWindowIntegrationTests.CreateLocalTrustedExecutorSelector(),
            Moq.Mock.Of<IRunningAgentChatTable>(), choices,
            timeProvider: clock, stageDeadline: TimeSpan.FromSeconds(5));
        var transport = new StalledStatusTransport();
        var opening = handler.ResolveRemoteOwnerAsync(
            RemoteEntity(),
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            transport, CancellationToken.None, stage =>
            {
                stages.Add(stage);
                if (stage == "status-first-frame") firstFrame.TrySetResult();
            });
        await firstFrame.Task;
        clock.Advance(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<RemoteAgentStatusTimeoutException>(() => opening);
        Assert.Equal("status-first-frame", error.Stage);
        Assert.True(transport.IsDisposed);
        Assert.Null(choices.Status);
        Assert.Contains("status-channel-open", stages);
        Assert.DoesNotContain("owner-decision", stages);
        Assert.False(transport.Channel.Writer.TryWrite(
            JsonDocument.Parse("""{"type":"late-status"}""").RootElement.Clone()));
    }

    private sealed class StalledStatusTransport : ITransport
    {
        internal Channel<JsonElement> Channel { get; } =
            System.Threading.Channels.Channel.CreateUnbounded<JsonElement>();
        internal TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool IsDisposed { get; private set; }
        public Task<IMessageChannel> ConnectToMessageChannelAsync(
            JsonElement request, CancellationToken ct = default)
            => Task.FromResult<IMessageChannel>(new StalledStatusChannel(this.Channel, this.ReadStarted));
        public Task<Stream> ConnectToStreamAsync(JsonElement request, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask DisposeAsync()
        {
            this.IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StalledStatusChannel(
        Channel<JsonElement> channel, TaskCompletionSource readStarted) : IMessageChannel
    {
        public ChannelReader<JsonElement> Reader
        {
            get
            {
                readStarted.TrySetResult();
                return channel.Reader;
            }
        }
        public ChannelWriter<JsonElement> Writer => channel.Writer;
        public ValueTask DisposeAsync()
        {
            channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task AgentManifestSessionLauncher_UserWaitExceedsFormerDeadline_PolicyCloseCancelsAndDisposesLateLeaseOnce()
    {
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        var broker = MainWindowIntegrationTests.GetEntityBroker(viewModel);
        var definition = await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
            broker, new EntityId("bbbb1611-0000-4000-8000-000000000002"),
            """
            {
              "entity-id": "bbbb1611-0000-4000-8000-000000000002",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "launcher-deadline"]],
              "display-name": { "default": "Launcher Deadline" },
              "definition": {
                "kind": "prompt",
                "name": "launcher-deadline",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);
        var session = await new AgentSessionShortcutContext()
            .CreateAgentSessionEntityAsync(viewModel, definition, "launcher-deadline");
        Assert.NotNull(session);
        var tab = new AgentSessionWorkspaceTabViewModel
        {
            Id = session.EntityId.ToString(),
            Title = session.DisplayName,
            Entity = session,
        };
        var clock = new FakeTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<(RunningAgentChatLease, ObservableLoggerFactory)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposalCount = 0;
        var cancellation = tab.StartupCancellation;
        await viewModel.OpenTabAsync(tab);
        var closeDecision = new StartupCloseDecisionProvider();
        viewModel.AgentSessionCloseDecisionProvider = closeDecision;
        await using var handler = new OpenAgentSessionShortcutHandler(
            new AgentSessionShortcutContext(),
            MainWindowIntegrationTests.CreateLocalTrustedExecutorSelector(),
            MainWindowIntegrationTests.CreateTestRunningAgentChatTable());
        var initialize = AgentManifestSessionLauncher.InitializeSessionTabAsync(
            handler, viewModel, (_, stage) =>
            {
                stage("remote-flight-chat-acquisition");
                entered.TrySetResult();
                return completed.Task;
            }, session!, tab, TaskScheduler.Default, CancellationToken.None,
            clock);
        await entered.Task;
        clock.Advance(TimeSpan.FromHours(1));
        Assert.False(initialize.IsCompleted);
        Assert.Equal(AgentTabState.Loading, tab.State);
        Assert.False(await viewModel.RequestCloseTabAsync(tab));
        Assert.False(cancellation.IsCancellationRequested);
        Assert.False(initialize.IsCompleted);
        Assert.Contains(tab, viewModel.WorkspacePanes.SelectMany(pane => pane.Tabs));
        closeDecision.Decision = AgentSessionCloseDecision.Terminate;
        Assert.True(await viewModel.RequestCloseTabAsync(tab));
        Assert.True(cancellation.IsCancellationRequested);
        Assert.DoesNotContain(tab, viewModel.WorkspacePanes.SelectMany(pane => pane.Tabs));
        await initialize;
        var lease = new RunningAgentChatLease(
            new AgentSessionId("late-lease"), null!, () =>
            {
                Interlocked.Increment(ref disposalCount);
                disposed.TrySetResult();
                return ValueTask.CompletedTask;
            });
        completed.TrySetResult((lease, new ObservableLoggerFactory()));
        await disposed.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AgentTabState.Loading, tab.State);
        Assert.Null(tab.Lease);
        await tab.DisposeAsync();
        Assert.Equal(1, disposalCount);
    }

    private sealed class StartupCloseDecisionProvider : IAgentSessionCloseDecisionProvider
    {
        internal AgentSessionCloseDecision Decision { get; set; } = AgentSessionCloseDecision.Cancel;
        public Task<AgentSessionCloseDecision> ChooseAsync(string sessionName, CancellationToken cancellationToken)
            => Task.FromResult(this.Decision);
    }

    // ── #1670 session-loading diagnostics (launcher/acquisition boundary) ──────────

    private sealed record LoadingDiagnosticsFixture(
        IDisposable Process,
        MainWindowViewModel ViewModel,
        SubscribedEntityViewModel Session,
        AgentSessionWorkspaceTabViewModel Tab,
        OpenAgentSessionShortcutHandler Handler,
        string Directory);

    private static async Task<LoadingDiagnosticsFixture> CreateLoadingDiagnosticsFixtureAsync(string idSuffix)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "launcher-diagnostics-tests", Guid.NewGuid().ToString("N"));
        var process = HostFileLoggerFactory.Create(directory);
        var table = MainWindowIntegrationTests.CreateTestRunningAgentChatTable();
        var appServices = new ApplicationServices(
            table,
            new AgentPersistenceStoreCache(),
            process,
            accessBroker: Moq.Mock.Of<Phantom.Workspaces.Llm.Interfaces.Access.IAccessBroker>());
        var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel(applicationServices: appServices);
        await viewModel.InitializeAsync();
        var broker = MainWindowIntegrationTests.GetEntityBroker(viewModel);
        var entityId = $"bb701670-0000-4000-8000-{idSuffix}";
        var definition = await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
            broker,
            new EntityId(entityId),
            $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "diag-{{idSuffix}}"]],
              "display-name": { "default": "Diag {{idSuffix}}" },
              "definition": {
                "kind": "prompt",
                "name": "diag-{{idSuffix}}",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);
        var session = await new AgentSessionShortcutContext()
            .CreateAgentSessionEntityAsync(viewModel, definition, "diag-" + idSuffix);
        Assert.NotNull(session);
        var tab = new AgentSessionWorkspaceTabViewModel
        {
            Id = session!.EntityId.ToString(),
            Title = session.DisplayName,
            Entity = session,
        };
        await viewModel.OpenTabAsync(tab);
        var handler = new OpenAgentSessionShortcutHandler(
            new AgentSessionShortcutContext(),
            MainWindowIntegrationTests.CreateLocalTrustedExecutorSelector(),
            table);
        return new LoadingDiagnosticsFixture(process, viewModel, session, tab, handler, directory);
    }

    private static string[] AttemptTokens(string log)
        => System.Text.RegularExpressions.Regex.Matches(log, @"attempt (\S+);")
            .Select(match => match.Groups[1].Value)
            .ToArray();

    private sealed class CanaryProtectedValue : ISensitiveDiagnosticValue
    {
        public string Plaintext => "canary-protected-material";
        public string ToDiagnosticMarker() => "[protected:canary]";
    }

    private sealed class ThrowingDiagnosticValue
    {
        public string Boom => throw new InvalidOperationException("formatter-boom-should-not-surface");
    }

    private static SecureString MakeSecure(string value)
    {
        var secure = new SecureString();
        foreach (var character in value)
        {
            secure.AppendChar(character);
        }

        secure.MakeReadOnly();
        return secure;
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task Handle_RemoteAcquisitionFails_LogsSessionIdentityAndFullException()
    {
        var fixture = await CreateLoadingDiagnosticsFixtureAsync("000000000001");
        using var process = fixture.Process;
        await using var viewModel = fixture.ViewModel;
        await using var handler = fixture.Handler;

        await AgentManifestSessionLauncher.InitializeSessionTabAsync(
            handler, viewModel, (_, stage) =>
            {
                stage("remote-flight-chat-acquisition");
                var aggregate = new AggregateException(
                    "aggregate-root",
                    new TimeoutException("inner-timeout status=504"),
                    new InvalidOperationException("second-branch ordinary-detail"));
                throw new InvalidOperationException("remote attach failed ordinary-detail", aggregate);
            },
            fixture.Session, fixture.Tab, TaskScheduler.Default, CancellationToken.None);

        var log = ProcessLogTestFile.ReadAll(fixture.Directory);
        Assert.Contains(fixture.Session.EntityId.ToString(), log, StringComparison.Ordinal);
        Assert.Contains("agent-session-id", log, StringComparison.Ordinal);
        Assert.Contains("owner-profile-entity-id", log, StringComparison.Ordinal);
        Assert.NotEmpty(AttemptTokens(log));
        Assert.Contains("stage remote-flight-chat-acquisition", log, StringComparison.Ordinal);
        Assert.Contains("remote attach failed ordinary-detail", log, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", log, StringComparison.Ordinal);
        Assert.Contains("AggregateException", log, StringComparison.Ordinal);
        Assert.Contains("aggregate-root", log, StringComparison.Ordinal);
        Assert.Contains("inner-timeout status=504", log, StringComparison.Ordinal);
        Assert.Contains("second-branch ordinary-detail", log, StringComparison.Ordinal);
        Assert.Contains("operational-snapshot", log, StringComparison.Ordinal);
        Assert.Contains("AgentManifestSessionLauncher", log, StringComparison.Ordinal);
        Assert.Equal(AgentTabState.Failed, fixture.Tab.State);
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task Handle_RemoteAcquisitionSucceeds_LogsCorrelatedStageTransitionsAndReady()
    {
        var fixture = await CreateLoadingDiagnosticsFixtureAsync("000000000002");
        using var process = fixture.Process;
        await using var viewModel = fixture.ViewModel;
        await using var handler = fixture.Handler;

        var chat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(
                """
                {
                  "kind": "prompt",
                  "name": "diag-ready",
                  "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                  "tools": []
                }
                """),
            ForegroundScheduler = TaskScheduler.Default,
        });
        var disposed = 0;
        var lease = new RunningAgentChatLease(
            new AgentSessionId("ready-session"), chat, () =>
            {
                Interlocked.Increment(ref disposed);
                return ValueTask.CompletedTask;
            });

        try
        {
            await AgentManifestSessionLauncher.InitializeSessionTabAsync(
                handler, viewModel, (_, stage) =>
                {
                    stage("remote-flight-chat-acquisition");
                    stage("owner-authorization");
                    return Task.FromResult((lease, new ObservableLoggerFactory()));
                },
                fixture.Session, fixture.Tab, TaskScheduler.Default, CancellationToken.None);

            Assert.Equal(AgentTabState.Ready, fixture.Tab.State);
            Assert.Same(lease, fixture.Tab.Lease);
            Assert.Equal(0, disposed);

            var log = ProcessLogTestFile.ReadAll(fixture.Directory);
            Assert.Contains("stage remote-flight-chat-acquisition", log, StringComparison.Ordinal);
            Assert.Contains("stage owner-authorization", log, StringComparison.Ordinal);
            Assert.Contains("stage ready-publication", log, StringComparison.Ordinal);
            Assert.DoesNotContain("reason session-load-failure", log, StringComparison.Ordinal);
            var attempts = AttemptTokens(log);
            Assert.NotEmpty(attempts);
            Assert.Single(attempts.Distinct());
        }
        finally
        {
            await fixture.Tab.DisposeAsync();
        }
    }

    [AvaloniaFact(Timeout = 45_000)]
    public async Task Handle_ConcurrentSessionsFail_KeepsDiagnosticScopesIsolated()
    {
        var fixture = await CreateLoadingDiagnosticsFixtureAsync("000000000003");
        using var process = fixture.Process;
        await using var viewModel = fixture.ViewModel;
        await using var handler = fixture.Handler;
        var broker = MainWindowIntegrationTests.GetEntityBroker(viewModel);
        var secondDefinition = await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
            broker,
            new EntityId("bb701670-0000-4000-8000-000000000103"),
            """
            {
              "entity-id": "bb701670-0000-4000-8000-000000000103",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "diag-concurrent-b"]],
              "display-name": { "default": "Diag concurrent B" },
              "definition": {
                "kind": "prompt",
                "name": "diag-concurrent-b",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);
        var sessionB = await new AgentSessionShortcutContext()
            .CreateAgentSessionEntityAsync(viewModel, secondDefinition, "diag-concurrent-b");
        Assert.NotNull(sessionB);
        var tabB = new AgentSessionWorkspaceTabViewModel
        {
            Id = sessionB!.EntityId.ToString(),
            Title = sessionB.DisplayName,
            Entity = sessionB,
        };
        await viewModel.OpenTabAsync(tabB);

        async Task RunFailureAsync(SubscribedEntityViewModel session, AgentSessionWorkspaceTabViewModel tab, string canary)
            => await AgentManifestSessionLauncher.InitializeSessionTabAsync(
                handler, viewModel, async (_, stage) =>
                {
                    stage("remote-flight-chat-acquisition");
                    await Task.Yield();
                    throw new InvalidOperationException($"acquisition failed {canary}");
                },
                session, tab, TaskScheduler.Default, CancellationToken.None);

        await Task.WhenAll(
            RunFailureAsync(fixture.Session, fixture.Tab, "canary-session-a"),
            RunFailureAsync(sessionB, tabB, "canary-session-b"));

        var log = ProcessLogTestFile.ReadAll(fixture.Directory);
        Assert.Contains("canary-session-a", log, StringComparison.Ordinal);
        Assert.Contains("canary-session-b", log, StringComparison.Ordinal);
        Assert.Contains(fixture.Session.EntityId.ToString(), log, StringComparison.Ordinal);
        Assert.Contains(sessionB.EntityId.ToString(), log, StringComparison.Ordinal);
        Assert.Equal(AgentTabState.Failed, fixture.Tab.State);
        Assert.Equal(AgentTabState.Failed, tabB.State);
        var concurrentAttempts = AttemptTokens(log).Distinct().ToArray();
        Assert.Equal(2, concurrentAttempts.Length);

        // A retry on the first session must receive a fresh, distinct attempt scope.
        await RunFailureAsync(fixture.Session, fixture.Tab, "canary-session-a-retry");
        var afterRetry = AttemptTokens(ProcessLogTestFile.ReadAll(fixture.Directory)).Distinct().ToArray();
        Assert.Equal(3, afterRetry.Length);
    }

    [AvaloniaFact(Timeout = 45_000)]
    public async Task Handle_LoadingIsCanceledOrTimesOut_LogsDistinctTerminalOutcomes()
    {
        var fixture = await CreateLoadingDiagnosticsFixtureAsync("000000000004");
        using var process = fixture.Process;
        await using var viewModel = fixture.ViewModel;
        await using var handler = fixture.Handler;

        // Supported timeout outcome.
        await AgentManifestSessionLauncher.InitializeSessionTabAsync(
            handler, viewModel, (_, stage) =>
            {
                stage("remote-flight-chat-acquisition");
                throw new TimeoutException("acquisition stage timed out");
            },
            fixture.Session, fixture.Tab, TaskScheduler.Default, CancellationToken.None);
        Assert.Equal(AgentTabState.Failed, fixture.Tab.State);

        // Lifetime cancellation outcome on a second session.
        var broker = MainWindowIntegrationTests.GetEntityBroker(viewModel);
        var cancelDefinition = await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
            broker,
            new EntityId("bb701670-0000-4000-8000-000000000104"),
            """
            {
              "entity-id": "bb701670-0000-4000-8000-000000000104",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "diag-cancel"]],
              "display-name": { "default": "Diag cancel" },
              "definition": {
                "kind": "prompt",
                "name": "diag-cancel",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);
        var cancelSession = await new AgentSessionShortcutContext()
            .CreateAgentSessionEntityAsync(viewModel, cancelDefinition, "diag-cancel");
        var cancelTab = new AgentSessionWorkspaceTabViewModel
        {
            Id = cancelSession!.EntityId.ToString(),
            Title = cancelSession.DisplayName,
            Entity = cancelSession,
        };
        await viewModel.OpenTabAsync(cancelTab);
        using var cts = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelLoad = AgentManifestSessionLauncher.InitializeSessionTabAsync(
            handler, viewModel, async (ct, stage) =>
            {
                stage("remote-flight-chat-acquisition");
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("unreachable");
            },
            cancelSession, cancelTab, TaskScheduler.Default, cts.Token);
        await entered.Task;
        cts.Cancel();
        await cancelLoad;

        var log = ProcessLogTestFile.ReadAll(fixture.Directory);
        Assert.Contains("reason stage-timeout", log, StringComparison.Ordinal);
        Assert.Contains("outcome cancelled", log, StringComparison.Ordinal);
        Assert.DoesNotContain("outcome ready", log, StringComparison.Ordinal);
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task AgentManifestSessionLauncher_AcquisitionFails_UsesSameDetailedDiagnosticContract()
    {
        var fixture = await CreateLoadingDiagnosticsFixtureAsync("000000000005");
        using var process = fixture.Process;
        await using var viewModel = fixture.ViewModel;
        await using var handler = fixture.Handler;

        await AgentManifestSessionLauncher.InitializeSessionTabAsync(
            handler, viewModel, (_, stage) =>
            {
                stage("remote-flight-chat-acquisition");
                throw new InvalidOperationException(
                    "launcher acquisition failed ordinary-detail",
                    new TimeoutException("inner-timeout status=504"));
            },
            fixture.Session, fixture.Tab, TaskScheduler.Default, CancellationToken.None);

        // Same identity/timing/exception contract as restored-session loading.
        var log = ProcessLogTestFile.ReadAll(fixture.Directory);
        foreach (var token in new[]
        {
            fixture.Session.EntityId.ToString(), "agent-session-id", "owner-profile-entity-id",
            "elapsed-ms", "launcher acquisition failed ordinary-detail", "inner-timeout status=504",
            "TimeoutException", "operational-snapshot",
        })
        {
            Assert.Contains(token, log, StringComparison.Ordinal);
        }

        Assert.NotEmpty(AttemptTokens(log));
        Assert.Equal(AgentTabState.Failed, fixture.Tab.State);
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task Handle_OperationalSnapshotsContainOrdinaryValues_PreservesDetailedData()
    {
        var fixture = await CreateLoadingDiagnosticsFixtureAsync("000000000006");
        using var process = fixture.Process;
        await using var viewModel = fixture.ViewModel;
        await using var handler = fixture.Handler;

        await AgentManifestSessionLauncher.InitializeSessionTabAsync(
            handler, viewModel, (_, stage) =>
            {
                stage("remote-flight-chat-acquisition");
                throw new InvalidOperationException(
                    "config https://example.com/agents?region=westus&retries=3 status=200 ordinary-diagnostic-text");
            },
            fixture.Session, fixture.Tab, TaskScheduler.Default, CancellationToken.None);

        var log = ProcessLogTestFile.ReadAll(fixture.Directory);
        foreach (var token in new[]
        {
            "region=westus", "retries=3", "status=200", "ordinary-diagnostic-text", "example.com",
            "operational-snapshot",
        })
        {
            Assert.Contains(token, log, StringComparison.Ordinal);
        }

        // Snapshot limits are explicit (truncation/cycle), not a field allowlist.
        var longText = new string('x', 10_000);
        var cyclic = new Dictionary<string, object?>(StringComparer.Ordinal) { ["ordinary"] = "kept-value" };
        cyclic["self"] = cyclic;
        cyclic["long"] = longText;
        var rendered = DiagnosticSnapshot.Capture(cyclic);
        Assert.Contains("kept-value", rendered, StringComparison.Ordinal);
        Assert.Contains("cycle", rendered, StringComparison.Ordinal);
        Assert.Contains("truncated", rendered, StringComparison.Ordinal);
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task Handle_ProtectedValuesAreNested_DoesNotLeakCanaries()
    {
        var fixture = await CreateLoadingDiagnosticsFixtureAsync("000000000007");
        using var process = fixture.Process;
        await using var viewModel = fixture.ViewModel;
        await using var handler = fixture.Handler;

        await AgentManifestSessionLauncher.InitializeSessionTabAsync(
            handler, viewModel, (_, stage) =>
            {
                stage("remote-flight-chat-acquisition");
                var error = new InvalidOperationException(
                    "attach failed https://user:canary-url-secret@host/x ordinary-tail");
                error.Data["secure"] = MakeSecure("canary-secure-material");
                error.Data["typed"] = new CanaryProtectedValue();
                error.Data["nested"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["ordinary-nested"] = "ordinary-nested-value",
                    ["deep-secure"] = MakeSecure("canary-deep-material"),
                };
                throw error;
            },
            fixture.Session, fixture.Tab, TaskScheduler.Default, CancellationToken.None);

        var log = ProcessLogTestFile.ReadAll(fixture.Directory);
        Assert.Contains("ordinary-tail", log, StringComparison.Ordinal);
        Assert.Contains("ordinary-nested-value", log, StringComparison.Ordinal);
        Assert.Contains("[protected:SecureString]", log, StringComparison.Ordinal);
        Assert.Contains("[protected:canary]", log, StringComparison.Ordinal);
        foreach (var canary in new[]
        {
            "canary-url-secret", "canary-secure-material", "canary-protected-material", "canary-deep-material",
        })
        {
            Assert.DoesNotContain(canary, log, StringComparison.Ordinal);
        }

        Assert.Equal(AgentTabState.Failed, fixture.Tab.State);
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task Handle_UpstreamExceptionContainsCredentialFragments_RedactsOnlyProtectedParts()
    {
        var fixture = await CreateLoadingDiagnosticsFixtureAsync("000000000008");
        using var process = fixture.Process;
        await using var viewModel = fixture.ViewModel;
        await using var handler = fixture.Handler;

        await AgentManifestSessionLauncher.InitializeSessionTabAsync(
            handler, viewModel, (_, stage) =>
            {
                stage("remote-flight-chat-acquisition");
                var inner = new TimeoutException(
                    "upstream https://api.example.com/v1?access_token=secretcanarytoken status=502 ordinary-inner");
                throw new InvalidOperationException(
                    "sdk failure Authorization: Bearer secretbearercanary ordinary-outer status=401", inner);
            },
            fixture.Session, fixture.Tab, TaskScheduler.Default, CancellationToken.None);

        var log = ProcessLogTestFile.ReadAll(fixture.Directory);
        foreach (var kept in new[]
        {
            "ordinary-outer", "ordinary-inner", "status=401", "status=502", "api.example.com", "TimeoutException",
        })
        {
            Assert.Contains(kept, log, StringComparison.Ordinal);
        }

        foreach (var removed in new[] { "secretcanarytoken", "secretbearercanary" })
        {
            Assert.DoesNotContain(removed, log, StringComparison.Ordinal);
        }
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task Handle_DiagnosticFormattingFails_PreservesOriginalFailureAndRollback()
    {
        var fixture = await CreateLoadingDiagnosticsFixtureAsync("000000000017");
        using var process = fixture.Process;
        await using var viewModel = fixture.ViewModel;
        await using var handler = fixture.Handler;

        // Phase 1: a formatting hazard in the exception must degrade to a marker, never replacing
        // the original failure or preventing the friendly Failed publication.
        await AgentManifestSessionLauncher.InitializeSessionTabAsync(
            handler, viewModel, (_, stage) =>
            {
                stage("remote-flight-chat-acquisition");
                var error = new InvalidOperationException("original load failure ordinary-detail");
                error.Data["hazard"] = new ThrowingDiagnosticValue();
                throw error;
            },
            fixture.Session, fixture.Tab, TaskScheduler.Default, CancellationToken.None);

        var log = ProcessLogTestFile.ReadAll(fixture.Directory);
        Assert.Contains("original load failure ordinary-detail", log, StringComparison.Ordinal);
        Assert.Contains("getter-failed", log, StringComparison.Ordinal);
        Assert.DoesNotContain("formatter-boom-should-not-surface", log, StringComparison.Ordinal);
        Assert.Equal(AgentTabState.Failed, fixture.Tab.State);

        // Phase 2: an acquired-but-unpublishable lease (null chat fails composition) must be
        // disposed exactly once — never leaked — while the original failure is still recorded.
        var broker = MainWindowIntegrationTests.GetEntityBroker(viewModel);
        var leakDefinition = await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
            broker,
            new EntityId("bb701670-0000-4000-8000-000000000117"),
            """
            {
              "entity-id": "bb701670-0000-4000-8000-000000000117",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "diag-leak"]],
              "display-name": { "default": "Diag leak" },
              "definition": {
                "kind": "prompt",
                "name": "diag-leak",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);
        var leakSession = await new AgentSessionShortcutContext()
            .CreateAgentSessionEntityAsync(viewModel, leakDefinition, "diag-leak");
        var leakTab = new AgentSessionWorkspaceTabViewModel
        {
            Id = leakSession!.EntityId.ToString(),
            Title = leakSession.DisplayName,
            Entity = leakSession,
        };
        await viewModel.OpenTabAsync(leakTab);
        var disposed = 0;
        var lease = new RunningAgentChatLease(
            new AgentSessionId("leak-session"), null!, () =>
            {
                Interlocked.Increment(ref disposed);
                return ValueTask.CompletedTask;
            });
        await AgentManifestSessionLauncher.InitializeSessionTabAsync(
            handler, viewModel, (_, stage) =>
            {
                stage("remote-flight-chat-acquisition");
                return Task.FromResult((lease, new ObservableLoggerFactory()));
            },
            leakSession, leakTab, TaskScheduler.Default, CancellationToken.None);

        Assert.Equal(1, disposed);
        Assert.Equal(AgentTabState.Failed, leakTab.State);
        Assert.Null(leakTab.Lease);
    }

    [AvaloniaFact]
    public async Task StartupCancellationAction_QueuedOldAttempt_CannotCancelReplacementAttempt()
    {
        await using var main = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await using var tab = new AgentSessionWorkspaceTabViewModel { Id = "stale-startup", Title = "Startup" };
        Action? posted = null;
        await using var handler = new OpenAgentSessionShortcutHandler(
            new AgentSessionShortcutContext(),
            MainWindowIntegrationTests.CreateLocalTrustedExecutorSelector(),
            MainWindowIntegrationTests.CreateTestRunningAgentChatTable(),
            new AgentSessionOwnerDecisionProvider(), null,
            action => { posted = action; return Task.CompletedTask; });
        handler.CreateStartupCancellationAction(main, tab)();
        var oldAction = Assert.IsType<Action>(posted);
        tab.CancelStartup();
        tab.ResetFailedLoading();
        var currentCancellation = tab.StartupCancellation;
        oldAction();
        Assert.False(currentCancellation.IsCancellationRequested);
        Assert.Equal(AgentTabState.Loading, tab.State);
        handler.CreateStartupCancellationAction(main, tab)();
        Assert.IsType<Action>(posted)();
        Assert.True(currentCancellation.IsCancellationRequested);
        Assert.Equal(AgentTabState.Failed, tab.State);
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task Handle_InitializationPublicationExceedsFormerDeadline_RemainsInitializing()
    {
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        var broker = MainWindowIntegrationTests.GetEntityBroker(viewModel);
        var definition = await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
            broker,
            new EntityId("bbbb1611-0000-4000-8000-000000000001"),
            """
            {
              "entity-id": "bbbb1611-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "loading-deadline"]],
              "display-name": { "default": "Loading Deadline" },
              "definition": {
                "kind": "prompt",
                "name": "loading-deadline",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);
        var session = await new AgentSessionShortcutContext()
            .CreateAgentSessionEntityAsync(viewModel, definition, "loading-deadline");
        Assert.NotNull(session);
        var clock = new FakeTimeProvider();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var handler = new OpenAgentSessionShortcutHandler(
            new AgentSessionShortcutContext(),
            MainWindowIntegrationTests.CreateLocalTrustedExecutorSelector(),
            MainWindowIntegrationTests.CreateTestRunningAgentChatTable(),
            new AgentSessionOwnerDecisionProvider(), null,
            async callback =>
            {
                if (!waiting.Task.IsCompleted)
                {
                    waiting.TrySetResult();
                    await release.Task;
                }
                callback();
                if (viewModel.WorkspacePanes.SelectMany(pane => pane.Tabs)
                    .OfType<AgentSessionWorkspaceTabViewModel>()
                    .Any(tab => tab.State == AgentTabState.Failed))
                    failed.TrySetResult();
                if (viewModel.WorkspacePanes.SelectMany(pane => pane.Tabs)
                    .OfType<AgentSessionWorkspaceTabViewModel>()
                    .Any(tab => tab.State == AgentTabState.Ready))
                    ready.TrySetResult();
            },
            clock);
        Assert.True(await handler.Handle(viewModel, Shortcut.Open, session!));
        await waiting.Task;
        clock.Advance(TimeSpan.FromHours(1));
        var tab = Assert.Single(viewModel.WorkspacePanes.SelectMany(pane => pane.Tabs)
            .OfType<AgentSessionWorkspaceTabViewModel>());
        Assert.Equal(AgentTabState.Loading, tab.State);
        Assert.False(failed.Task.IsCompleted);
        Assert.Null(tab.Agent);
        release.TrySetResult();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(AgentTabState.Ready, tab.State);
    }

    [Fact]
    public void CreateAgentSessionTabRequest_PublicContract_HasExactlySpecifiedProperties()
    {
        var properties = typeof(CreateAgentSessionTabRequest)
            .GetProperties()
            .Select(static property => property.Name)
            .Order()
            .ToArray();

        Assert.Equal(
            [
                nameof(CreateAgentSessionTabRequest.AgentChat),
                nameof(CreateAgentSessionTabRequest.AgentSessionEntity),
                nameof(CreateAgentSessionTabRequest.MainWindowViewModel),
            ],
            properties);
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task DisposeAsync_InitializationInFlight_CancelsWithoutPublishingReadyTab()
    {
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = MainWindowIntegrationTests.GetEntityBroker(viewModel);
        var definitionEntity = await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
            entityBroker,
            new EntityId("bbbb1488-0000-4000-8000-000000000001"),
            """
            {
              "entity-id": "bbbb1488-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "cancel-before-ready"]],
              "display-name": { "default": "Cancel Before Ready" },
              "definition": {
                "kind": "prompt",
                "name": "cancel-before-ready",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);
        var context = new AgentSessionShortcutContext();
        var sessionEntity = await context.CreateAgentSessionEntityAsync(
            viewModel,
            definitionEntity,
            "cancel-before-ready");
        Assert.NotNull(sessionEntity);

        var table = MainWindowIntegrationTests.CreateTestRunningAgentChatTable();
        var publicationQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowPublication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new OpenAgentSessionShortcutHandler(
            context,
            MainWindowIntegrationTests.CreateLocalTrustedExecutorSelector(),
            table,
            new AgentSessionOwnerDecisionProvider(),
            transportFactoryRegistry: null,
            async callback =>
            {
                publicationQueued.TrySetResult();
                await allowPublication.Task;
                callback();
            });

        Assert.True(await handler.Handle(viewModel, Shortcut.Open, sessionEntity!));
        await publicationQueued.Task.WaitAsync(
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);

        var tab = Assert.Single(viewModel.WorkspacePanes
            .SelectMany(static pane => pane.Tabs)
            .OfType<AgentSessionWorkspaceTabViewModel>());
        var disposeTask = handler.DisposeAsync().AsTask();
        allowPublication.TrySetResult();
        await disposeTask;

        Assert.Equal(AgentTabState.Loading, tab.State);
        Assert.Null(tab.Agent);
        Assert.Null(tab.Lease);
        Assert.Empty(table.RunningSessions);
    }

    [Theory]
    [InlineData(AgentSessionRemoteStatus.Running)]
    [InlineData(AgentSessionRemoteStatus.NotRunning)]
    [InlineData(AgentSessionRemoteStatus.Unavailable)]
    public async Task Handle_RemoteOwnerPrompt_ShowsAuthorizedStatusAndConnectsOnOwner(
        AgentSessionRemoteStatus status)
    {
        var choices = new RecordingOwnerDecisionProvider(
            AgentSessionOwnerDecision.ConnectOnOwner);
        var handler = Handler(choices);
        var transport = new OwnerDecisionTransport(status);

        var result = await handler.ResolveRemoteOwnerAsync(
            RemoteEntity(),
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            transport,
            TestContext.Current.CancellationToken);

        Assert.Equal(status, choices.Status);
        Assert.Equal(AgentChatAcquisitionMode.StartOrAttachRemote, result.Mode);
        Assert.Same(transport, result.Transport);
        Assert.Equal(["attach-agent-session"], transport.RequestTypes);
    }

    [Fact]
    public async Task ResolveRemoteOwner_CancelledDecision_DisposesUntransferredTransport()
    {
        var choices = new BlockingOwnerDecisionProvider();
        var handler = Handler(choices);
        var transport = new OwnerDecisionTransport(AgentSessionRemoteStatus.Running);
        using var cancellation = new CancellationTokenSource();
        var resolving = handler.ResolveRemoteOwnerAsync(
            RemoteEntity(),
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            transport,
            cancellation.Token);
        await choices.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolving);
        Assert.True(transport.IsDisposed);
    }

    [Fact]
    public async Task TryCreateAgentSessionTabForRestoreAsync_RemoteOwner_ResumesLocallyAfterTakeover()
    {
        var handler = Handler(new RecordingOwnerDecisionProvider(
            AgentSessionOwnerDecision.ResumeLocally));
        var transport = new OwnerDecisionTransport(AgentSessionRemoteStatus.Running);
        var localOwner = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var result = await handler.ResolveRemoteOwnerAsync(
            RemoteEntity(),
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            localOwner,
            transport,
            TestContext.Current.CancellationToken);

        Assert.Equal(AgentChatAcquisitionMode.Local, result.Mode);
        Assert.Null(result.Transport);
        Assert.Equal(localOwner.ToString(), result.Entity.GetProperty("host-profile-entity-id").GetString());
        Assert.Equal(8, result.Entity.GetProperty("ownership-generation").GetInt64());
        Assert.Equal(["attach-agent-session", "take-over-agent-session"], transport.RequestTypes);
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task ComposeSessionAgentViewModel_AlwaysConfiguresSlashCommands()
    {
        // #1429: ComposeSessionAgentViewModel is the single seam every GUI session launch path
        // routes through. Calling it directly must always leave slash commands wired, so no future
        // launch path can bypass slash-command configuration by depending on caller-side wiring.
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = MainWindowIntegrationTests.GetEntityBroker(viewModel);
        var definitionEntity = await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
            entityBroker,
            new EntityId("bbbb0005-0000-4000-8000-000000000001"),
            """
            {
              "entity-id": "bbbb0005-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "slash-seam"]],
              "display-name": { "default": "Echo slash-seam" },
              "definition": {
                "kind": "prompt",
                "name": "slash-seam",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var context = new AgentSessionShortcutContext();
        var sessionEntity = await context.CreateAgentSessionEntityAsync(
            viewModel, definitionEntity, Guid.NewGuid().ToString("n"));
        Assert.NotNull(sessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(
            context,
            MainWindowIntegrationTests.CreateLocalTrustedExecutorSelector(),
            MainWindowIntegrationTests.CreateTestRunningAgentChatTable());

        var chat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(
                """
                {
                  "kind": "prompt",
                  "name": "slash-seam",
                  "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                  "tools": []
                }
                """),
            ForegroundScheduler = TaskScheduler.Default,
        });

        var loggerFactory = new ObservableLoggerFactory();
        var tab = new AgentSessionWorkspaceTabViewModel
        {
            Id = sessionEntity!.EntityId.ToString(),
            Title = sessionEntity.DisplayName,
            Entity = sessionEntity,
        };

        AgentViewModel agent = handler.ComposeSessionAgentViewModel(
            new ComposeSessionAgentViewModelOptions
            {
                MainWindowViewModel = viewModel,
                LoggerFactory = loggerFactory,
                AgentChat = chat,
                AgentSessionEntity = sessionEntity,
                Tab = tab,
                ForegroundScheduler = TaskScheduler.Default,
            });

        try
        {
            await MainWindowIntegrationTests.AssertSlashCommandsEnabledAsync(agent);
        }
        finally
        {
            await agent.DisposeAsync();
            await chat.DisposeAsync();
            loggerFactory.Dispose();
        }
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task FirstOpen_UsesPersistedSplitBindings()
    {
        // Regression pin for #1481 — the first-open path (Open shortcut → TryBuildAgentAsync) must
        // route through the shared IAgentSessionRuntimeContextFactory hydrator, reconstructing the
        // persisted executor-bindings before definition resolution / chat creation. No GUI code
        // may parse executor bindings on this path.
        var legacyHostProfileEntityId = new EntityId("bbbb1481-0000-4000-8000-000000000001");
        const string WorkerProfileEntityId = "cccc1481-0000-4000-8000-000000000001";
        var registry = new TransportFactoryRegistry();
        var registryProvider = new TransportFactoryRegistryProvider(registry);
        var innerRuntimeFactory = AgentSessionRuntimeContextFactory.FromProvider(registryProvider);
        var spyRuntimeFactory = new SpyRuntimeContextFactory(innerRuntimeFactory);
        var runningChatFactory = new AgentChatFactory(
            new InMemoryAgentPersistenceStore(),
            new AgentServices(),
            SynchronizationContextTaskScheduler.FromCurrent());
        var table = new RunningAgentChatTable(runningChatFactory, spyRuntimeFactory);
        var appServices = new ApplicationServices(table, new AgentPersistenceStoreCache(), Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            accessBroker: Moq.Mock.Of<Phantom.Workspaces.Llm.Interfaces.Access.IAccessBroker>());
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel(applicationServices: appServices);
        await viewModel.InitializeAsync();

        var entityBroker = MainWindowIntegrationTests.GetEntityBroker(viewModel);
        var agentDefinitionEntity = await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
            entityBroker,
            new EntityId("dd811481-0000-4000-8000-000000000001"),
            """
            {
              "entity-id": "dd811481-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "first-open-split-bindings"]],
              "display-name": { "default": "First-Open Split Bindings" },
              "definition": {
                "kind": "prompt",
                "name": "first-open-split-bindings",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var context = new AgentSessionShortcutContext();
        var sessionEntityId = new EntityId("eeee1481-0000-4000-8000-000000000001");
        var sessionEntity = await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
            entityBroker,
            sessionEntityId,
            $$"""
            {
              "entity-id": "{{sessionEntityId}}",
              "entity-types": ["entity", "agent-session"],
              "names": [["tests", "agent-sessions", "first-open-legacy-split-bindings"]],
              "display-name": { "default": "First-Open Legacy Split Bindings" },
              "agent-source-entity-id": "{{agentDefinitionEntity.EntityId}}",
              "agent-session-id": "{{Guid.NewGuid():n}}",
              "host-profile-entity-id": "{{legacyHostProfileEntityId}}",
              "executor-bindings": {
                "components": {
                  "worker": {
                    "type": "user-computer-profile",
                    "entity-id": "{{WorkerProfileEntityId}}"
                  }
                }
              }
            }
            """);
        Assert.NotNull(sessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(
            context,
            MainWindowIntegrationTests.CreateLocalTrustedExecutorSelector(),
            table);

        var tab = new AgentSessionWorkspaceTabViewModel
        {
            Id = sessionEntity!.EntityId.ToString(),
            Title = sessionEntity.DisplayName,
            Entity = sessionEntity,
        };
        var foregroundScheduler = SynchronizationContextTaskScheduler.FromCurrent();

        var result = await Task.Run(() =>
            handler.TryBuildAgentAsync(viewModel, sessionEntity!, tab, foregroundScheduler));

        try
        {
            Assert.NotNull(result);
            Assert.Equal(1, spyRuntimeFactory.CreateCallCount);
            var lastContext = Assert.IsType<AgentSessionRuntimeContext>(spyRuntimeFactory.LastContext);
            Assert.Equal(
                legacyHostProfileEntityId.ToString(),
                lastContext.Intent.ExecutorBindings.SessionExecutor.GetProperty("entity-id").GetString());
            var workerBinding = lastContext.Intent.ExecutorBindings.ResolveComponent("worker");
            Assert.Equal("user-computer-profile", workerBinding.GetProperty("type").GetString());
            Assert.Equal(WorkerProfileEntityId, workerBinding.GetProperty("entity-id").GetString());
            Assert.Same(registry, lastContext.TransportFactoryRegistry);
            Assert.Same(result.Value.loggerFactory,
                handler.GetSessionLogger(result.Value.agent.AgentSessionId));
        }
        finally
        {
            if (result?.lease is { } lease)
            {
                await lease.DisposeAsync();
            }
            if (result?.agent is { } createdAgent)
            {
                await createdAgent.DisposeAsync();
            }
            result?.loggerFactory.Dispose();
        }
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task AutoResume_UsesPersistedSplitBindings()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "resume-logging-tests", Guid.NewGuid().ToString("N"));
        using var process = HostFileLoggerFactory.Create(directory);
        // Regression pin for #1481 — the auto-resume path must go through the same hydrator, so a
        // persisted split-executor session that auto-resumes reconstructs the same runtime context
        // as first-open.
        const string WorkerProfileEntityId = "cccc1481-0000-4000-8000-000000000002";
        var registry = new TransportFactoryRegistry();
        var registryProvider = new TransportFactoryRegistryProvider(registry);
        var innerRuntimeFactory = AgentSessionRuntimeContextFactory.FromProvider(registryProvider);
        var spyRuntimeFactory = new SpyRuntimeContextFactory(innerRuntimeFactory);
        var runningChatFactory = new AgentChatFactory(
            new InMemoryAgentPersistenceStore(),
            new AgentServices(),
            SynchronizationContextTaskScheduler.FromCurrent());
        var table = new RunningAgentChatTable(runningChatFactory, spyRuntimeFactory);
        var appServices = new ApplicationServices(table, new AgentPersistenceStoreCache(), process,
            accessBroker: Moq.Mock.Of<Phantom.Workspaces.Llm.Interfaces.Access.IAccessBroker>());
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel(applicationServices: appServices);
        await viewModel.InitializeAsync();

        var entityBroker = MainWindowIntegrationTests.GetEntityBroker(viewModel);
        var agentDefinitionEntity = await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
            entityBroker,
            new EntityId("dd811481-0000-4000-8000-000000000002"),
            """
            {
              "entity-id": "dd811481-0000-4000-8000-000000000002",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "auto-resume-split-bindings"]],
              "display-name": { "default": "Auto-Resume Split Bindings" },
              "definition": {
                "kind": "prompt",
                "name": "auto-resume-split-bindings",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var context = new AgentSessionShortcutContext();
        var sessionExecutor = ExecutorBindings.LocalDescriptor();
        var componentBindings = BuildWorkerComponentBindings(WorkerProfileEntityId);
        var sessionEntity = await context.CreateAgentSessionEntityAsync(
            viewModel,
            agentDefinitionEntity,
            Guid.NewGuid().ToString("n"),
            sessionExecutor: sessionExecutor,
            executorComponentBindings: componentBindings);
        Assert.NotNull(sessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(
            context,
            MainWindowIntegrationTests.CreateLocalTrustedExecutorSelector(),
            table);

        const string resumePrompt = "Resume with persisted split bindings.";
        var foregroundScheduler = SynchronizationContextTaskScheduler.FromCurrent();
        var lease = await Task.Run(() =>
            handler.TryStartAutoResumeAsync(viewModel, sessionEntity!, resumePrompt, foregroundScheduler));

        try
        {
            Assert.NotNull(lease);
            Assert.Equal(1, spyRuntimeFactory.CreateCallCount);
            var lastContext = Assert.IsType<AgentSessionRuntimeContext>(spyRuntimeFactory.LastContext);
            var workerBinding = lastContext.Intent.ExecutorBindings.ResolveComponent("worker");
            Assert.Equal("user-computer-profile", workerBinding.GetProperty("type").GetString());
            Assert.Equal(WorkerProfileEntityId, workerBinding.GetProperty("entity-id").GetString());
            Assert.Same(registry, lastContext.TransportFactoryRegistry);
            var memory = handler.GetSessionLogger(lease.AgentChat.Information.AgentSessionId);
            var request = typeof(AgentChat)
                .GetField("request", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(lease.LocalAgentChat!)!;
            var services = (AgentServices)request.GetType().GetProperty("AgentServices")!.GetValue(request)!;
            Assert.IsType<SessionTeeLoggerFactory>(services.LoggerFactory);
            services.LoggerFactory!.CreateLogger("SafeLifecycle").LogInformation("resume-stage-safe");
            Assert.Single(memory.Entries, entry => entry.Contains("resume-stage-safe", StringComparison.Ordinal));
            Assert.Equal(1, ProcessLogTestFile.ReadAll(directory)
                .Split("resume-stage-safe", StringSplitOptions.None).Length - 1);
            var attachedView = new AgentViewModel(new Phantom.Workspaces.Agent.Gui.ViewModels.AgentViewModelOptions
            {
                AgentChat = lease.AgentChat,
                DisplayName = "restored",
                Description = "",
                LoggerFactory = memory,
                ForegroundScheduler = foregroundScheduler,
            });
            try
            {
                attachedView.SelectedEditorItem = attachedView.EditorItems.Single().Children
                    .Single(item => item.Id == "chat-logs");
                Assert.Single(attachedView.LogsDetail.Entries, entry =>
                    entry.Contains("resume-stage-safe", StringComparison.Ordinal));
            }
            finally
            {
                await attachedView.DisposeViewResourcesAsync();
            }
        }
        finally
        {
            if (lease is not null)
            {
                await lease.DisposeAsync();
            }
            process.Dispose();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static JsonElement BuildWorkerComponentBindings(string workerProfileEntityId)
    {
        using var document = JsonDocument.Parse(
            $$"""
            {
              "worker": {
                "type": "user-computer-profile",
                "entity-id": "{{workerProfileEntityId}}"
              }
            }
            """);
        return document.RootElement.Clone();
    }

    private static OpenAgentSessionShortcutHandler Handler(
        IAgentSessionOwnerDecisionProvider decisionProvider)
        => new(
            new AgentSessionShortcutContext(),
            MainWindowIntegrationTests.CreateLocalTrustedExecutorSelector(),
            Moq.Mock.Of<IRunningAgentChatTable>(),
            decisionProvider);

    private static JsonElement RemoteEntity()
        => JsonDocument.Parse(
            """
            {
              "agent-session-id":"remote-choice",
              "host-profile-entity-id":"11111111-1111-1111-1111-111111111111",
              "ownership-generation":7
            }
            """).RootElement.Clone();

    private sealed class RecordingOwnerDecisionProvider(AgentSessionOwnerDecision decision)
        : IAgentSessionOwnerDecisionProvider
    {
        internal AgentSessionRemoteStatus? Status { get; private set; }

        public Task<AgentSessionOwnerDecision> ChooseAsync(
            AgentSessionOwnerDecisionContext context,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            this.Status = context.Status;
            return Task.FromResult(decision);
        }
    }

    private sealed class BlockingOwnerDecisionProvider : IAgentSessionOwnerDecisionProvider
    {
        internal TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AgentSessionOwnerDecision> ChooseAsync(
            AgentSessionOwnerDecisionContext context,
            CancellationToken ct)
        {
            this.Started.TrySetResult();
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = ct.Register(cancelled.SetResult);
            await cancelled.Task;
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The cancellation token should have stopped the decision.");
        }
    }

    private sealed class CompletableOwnerDecisionProvider : IAgentSessionOwnerDecisionProvider
    {
        internal TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<AgentSessionOwnerDecision> choice =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Complete(AgentSessionOwnerDecision decision) => this.choice.SetResult(decision);

        public async Task<AgentSessionOwnerDecision> ChooseAsync(
            AgentSessionOwnerDecisionContext context,
            CancellationToken ct)
        {
            this.Started.TrySetResult();
            return await this.choice.Task.WaitAsync(ct);
        }
    }

    private sealed class OwnerDecisionTransport(AgentSessionRemoteStatus status) : ITransport
    {
        internal List<string> RequestTypes { get; } = [];
        internal bool IsDisposed { get; private set; }

        public Task<IMessageChannel> ConnectToMessageChannelAsync(
            JsonElement request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var type = request.GetProperty("type").GetString()!;
            this.RequestTypes.Add(type);
            var channel = new OwnerDecisionChannel();
            if (type == "attach-agent-session")
            {
                channel.Send(new SessionStatusEvent { Status = status }, Guid.NewGuid());
            }
            else
            {
                var takeover = AgentSessionProtocolCodec.DeserializeTakeover(request);
                channel.Send(
                    new CommandCompletedEvent { CommandId = takeover.CorrelationId },
                    takeover.CorrelationId);
            }
            return Task.FromResult<IMessageChannel>(channel);
        }

        public Task<Stream> ConnectToStreamAsync(
            JsonElement request,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            this.IsDisposed = true;
            return ValueTask.CompletedTask;
        }

        private sealed class OwnerDecisionChannel : IMessageChannel
        {
            private readonly Channel<JsonElement> incoming = Channel.CreateUnbounded<JsonElement>();
            private readonly Channel<JsonElement> outgoing = Channel.CreateUnbounded<JsonElement>();

            public ChannelWriter<JsonElement> Writer => this.outgoing.Writer;
            public ChannelReader<JsonElement> Reader => this.incoming.Reader;

            internal void Send(AgentSessionServerEvent value, Guid correlationId)
                => this.incoming.Writer.TryWrite(AgentSessionProtocolCodec.SerializeFrame(
                    AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.CreateFrame(
                        new RuntimeEpoch { Value = Guid.NewGuid() },
                        1,
                        correlationId,
                        value)));

            public ValueTask DisposeAsync()
            {
                this.incoming.Writer.TryComplete();
                this.outgoing.Writer.TryComplete();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class SpyRuntimeContextFactory : IAgentSessionRuntimeContextFactory
    {
        private readonly IAgentSessionRuntimeContextFactory inner;
        private int createCallCount;

        public SpyRuntimeContextFactory(IAgentSessionRuntimeContextFactory inner)
        {
            this.inner = inner;
        }

        public int CreateCallCount => Volatile.Read(ref this.createCallCount);
        public AgentSessionRuntimeContext? LastContext { get; private set; }

        public AgentSessionRuntimeContext Create(JsonElement agentSessionEntity)
        {
            Interlocked.Increment(ref this.createCallCount);
            var context = this.inner.Create(agentSessionEntity);
            this.LastContext = context;
            return context;
        }
    }
}
