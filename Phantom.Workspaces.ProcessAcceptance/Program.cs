using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Web.Client;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Transport;
using Phantom.Workspaces.Transport.Http;
using Phantom.Workspaces.Transport.ReverseHttp;
using Phantom.Workspaces.Trust;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.ProcessAcceptance;

// These constants only exist in the test executable. Neither token nor authenticated descriptor
// is stored in a profile, reachability route, Git repository, or process log.
public static class ProcessAcceptanceCredentials
{
    public const string Token = "synthetic-split-fixture-1611-1612";
}

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 8 || args[0] is not ("worker" or "caller"))
            return 2;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var stage = "bootstrap";
        try
        {
            var profile = new EntityId(args[3]);
            var session = new WorkspaceEntitySession
            {
                UserEntityId = new EntityId(args[2]),
                ComputerEntityId = profile,
                UserComputerProfileEntityId = profile,
            };
            using var http = new HttpClient { BaseAddress = new Uri(args[1]) };
            http.DefaultRequestHeaders.TryAddWithoutValidation(
                "X-Tunnel-Authorization", "tunnel " + ProcessAcceptanceCredentials.Token);
            using var data = new WebClientDataAccessLayer(args[1], http);
            if (args[0] == "worker")
                await RunWorkerAsync(data, session, args[1], args[5],
                    value => stage = value, timeout.Token);
            else
                await RunCallerAsync(session, args[4], args[1], args[5],
                    new EntityId(args[6]), args[7], value => stage = value, timeout.Token);
            return 0;
        }
        catch (Exception error)
        {
            // No raw exceptions: endpoint, profile, and request contents may be embedded in them.
            Console.Error.WriteLine("FAIL " + stage + " " + error.GetType().Name + " at "
                + error.TargetSite?.DeclaringType?.Name + "." + error.TargetSite?.Name);
            return 1;
        }
    }

    private static async Task RunWorkerAsync(
        IDataAccessLayer data, WorkspaceEntitySession session, string hubUrl,
        string liveUrl, Action<string> reportStage, CancellationToken ct)
    {
        reportStage("worker-registration");
        var chatClient = new DeterministicTestChatClient();
        var response = chatClient.EnqueueStreamingResponse();
        response.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "worker-response"));
        response.Complete();
        await using var factory = new AgentChatFactory(
            new InMemoryAgentPersistenceStore(),
            new AgentServices { ChatClientOverride = chatClient },
            TaskScheduler.Default);
        var chats = new RunningAgentChatTable(factory);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var hub = new ReverseHttpClientTransportFactory(
            new FixtureAuthenticatedHttpFactory(hubUrl), hubUrl,
            session.UserComputerProfileEntityId.ToString(),
            null, null, clock, routeLeaseDuration: TimeSpan.FromMinutes(2));
        var liveHub = new ReverseHttpClientTransportFactory(
            new FixtureAuthenticatedHttpFactory(liveUrl), liveUrl,
            session.UserComputerProfileEntityId.ToString(),
            null, null, routeLeaseDuration: TimeSpan.FromMinutes(2));
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var livePublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.PublicationStatusChanged += (_, status) =>
        {
            if (status.Persisted == "true")
                published.TrySetResult();
            if (status.Persisted == "true" && status.Attempt >= 2)
                renewed.TrySetResult();
        };
        liveHub.PublicationStatusChanged += (_, status) =>
        {
            if (status.Persisted == "true")
                livePublished.TrySetResult();
        };
        await using var composition = new WorkspacesTransportComposition(
            data, session, NullLoggerFactory.Instance, [hub, liveHub],
            agentServices: new AgentServices { ChatClientOverride = chatClient },
            runningAgentChats: chats);
        await composition.StartAsync(ct);
        reportStage("worker-route-publication");
        await published.Task.WaitAsync(ct);
        await livePublished.Task.WaitAsync(ct);
        if (!hub.IsRegistered || !liveHub.IsRegistered)
            throw new InvalidOperationException("Worker registration/route publication failed.");
        Console.WriteLine("READY worker");
        while (true)
        {
            var command = await Console.In.ReadLineAsync(ct);
            if (command == "STOP" || command is null)
                break;
            if (command == "VERIFY")
            {
                reportStage("worker-turn");
                await chatClient.WaitForRequestAsync(ct);
                if (!chatClient.LastRequestMessages.Any(message =>
                    message.Contents.OfType<TextContent>().Any(content => content.Text == "fixture-prompt")))
                    throw new InvalidOperationException("Worker did not receive the fixture turn.");
                Console.WriteLine("TURN worker");
                continue;
            }
            if (command != "RENEW")
                throw new InvalidOperationException("Unexpected fixture command.");
            reportStage("worker-route-renewal");
            clock.Advance(TimeSpan.FromMinutes(1));
            await renewed.Task.WaitAsync(ct);
            if (hub.LastReachabilityPublicationStatus?.Persisted != "true")
                throw new InvalidOperationException("Route renewal failed.");
            Console.WriteLine("RENEWED");
        }
    }

    private static async Task RunCallerAsync(
        WorkspaceEntitySession session, string sessionId,
        string dataEndpoint, string listenUrl, EntityId workerProfile, string repositoryPath,
        Action<string> reportStage, CancellationToken ct)
    {
        reportStage("caller-live-ingress");
        await using var liveHub = new ReverseHttpServerTransportFactory(
            new ReverseConnectionStatusRegistry(), requireAuthenticatedRelays: true,
            hubProfileEntityId: session.UserComputerProfileEntityId);
        var listeners = new TransportRegistry();
        listeners.Register(liveHub);
        await using var server = new HttpServerTransportFactory(listeners);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(listenUrl);
        await using var app = builder.Build();
        app.UseWebSockets();
        app.Use(async (context, next) =>
        {
            if (!string.Equals(context.Request.Headers["X-Tunnel-Authorization"],
                "tunnel " + ProcessAcceptanceCredentials.Token, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next(context);
        });
        server.Map(app);
        await app.StartAsync(ct);
        Console.WriteLine("READY caller");
        if (await Console.In.ReadLineAsync(ct) != "DROP" || !liveHub.IsRegistered(workerProfile.ToString()))
            throw new InvalidOperationException("Caller live registration was not established.");
        reportStage("caller-live-loss");
        await liveHub.DisconnectRegistrationAsync(workerProfile.ToString());
        await app.StopAsync(ct);
        Console.WriteLine("DROPPED live");

        reportStage("caller-gui-bootstrap");
        AppBuilder.Configure<FixtureApplication>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .SetupWithoutStarting();
        using var loop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await RunCallerTabAsync(
                    session, sessionId, repositoryPath, dataEndpoint, reportStage, ct);
                finished.TrySetResult();
            }
            catch (Exception error)
            {
                finished.TrySetException(error);
            }
            finally
            {
                loop.Cancel();
            }
        });
        Dispatcher.UIThread.MainLoop(loop.Token);
        await finished.Task.WaitAsync(ct);
    }

    private static async Task RunCallerTabAsync(
        WorkspaceEntitySession session, string sessionId, string repositoryPath, string dataEndpoint,
        Action<string> reportStage, CancellationToken ct)
    {
        await using var factory = new AgentChatFactory(
            new InMemoryAgentPersistenceStore(), new AgentServices(),
            TaskScheduler.FromCurrentSynchronizationContext());
        var provider = new TransportFactoryRegistryProvider();
        var chats = new RunningAgentChatTable(
            factory, AgentSessionRuntimeContextFactory.FromProvider(provider));
        var routeLog = new RouteLogger<UserComputerProfileTransportFactory>();
        var services = new ApplicationServices(
            chats, new AgentPersistenceStoreCache(), new FixtureLoggerFactory(routeLog),
            transportFactoryRegistryProvider: provider,
            outboundHttpTransportFactory: new FixtureAuthenticatedHttpFactory(dataEndpoint));
        var configuration = new WorkspacesConfiguration
        {
            SkipStartupWorkspace = true,
            UserComputerProfileOverride = "fixture-caller",
        };
        await using var window = new MainWindowViewModel(
            new LocalGitRepositorySource(repositoryPath), configuration,
            new ProfileStore(Path.Combine(Path.GetDirectoryName(repositoryPath)!, "caller-profile.json")),
            services);
        reportStage("caller-gui-initialization");
        await window.InitializeAsync();
        if (window.EntityBroker.EntityRepository.WorkspaceEntitySession.UserComputerProfileEntityId
            != session.UserComputerProfileEntityId)
            throw new InvalidOperationException("Caller GUI profile does not match the fixture.");
        reportStage("caller-persisted-session-query");
        var query = await window.EntityBroker.EntityRepository.DataAccessLayer.QueryAsync(new QueryRequest
        {
            Clauses = [new TopLevelQueryClause
            {
                ClauseIdentifier = new QueryClauseIdentifier("sessions"),
                Clause = new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet(["agent-session"]) },
            }],
            Timestamps = [null],
        }, ct);
        var persisted = query.Batches.SelectMany(batch => batch.Entities)
            .Single(item => item.Data is JsonElement value
                && value.GetProperty("agent-session-id").GetString() == sessionId);
        reportStage("caller-persisted-session-load");
        var entity = (await window.EntityBroker.GetEntitiesAsync([persisted.EntityId], ct)).Single();
        var stalled = new OneShotStalledProfileRegistry(
            window.TransportComposition?.TransportFactoryRegistry
                ?? throw new InvalidOperationException("Caller production transport was not initialized."));
        var clock = new FakeTimeProvider();
        await using var handler = new OpenAgentSessionShortcutHandler(
            new AgentSessionShortcutContext(
                userComputerProfileOverride: "fixture-caller",
                persistenceStoreCache: services.AgentPersistenceStoreCache),
            new DeferredTrustedExecutorSelector(),
            chats, new AgentSessionOwnerDecisionProvider(), stalled,
            action => { action(); return Task.CompletedTask; },
            loadingDeadline: TimeSpan.FromSeconds(90),
            timeProvider: clock,
            stageDeadline: TimeSpan.FromSeconds(45));
        reportStage("caller-tab-open");
        if (!await handler.Handle(window, Shortcut.Open, entity))
            throw new InvalidOperationException("Session shortcut did not open a tab.");
        reportStage("caller-tab-discovery");
        var tab = window.WorkspacePanes.SelectMany(pane => pane.Tabs)
            .OfType<AgentSessionWorkspaceTabViewModel>()
            .Single(item => item.Entity?.EntityId == entity.EntityId);
        reportStage("caller-tab-state-" + tab.State);
        if (tab.State != AgentTabState.Loading)
            throw new InvalidOperationException("Session tab did not enter Loading.");
        Console.WriteLine("TAB Loading");
        var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(AgentSessionWorkspaceTabViewModel.State)
                && tab.State != AgentTabState.Loading)
                terminal.TrySetResult();
        }
        tab.PropertyChanged += OnStateChanged;
        try
        {
            await stalled.Entered.WaitAsync(ct);
            clock.Advance(TimeSpan.FromSeconds(45));
            await terminal.Task.WaitAsync(ct);
        }
        finally
        {
            tab.PropertyChanged -= OnStateChanged;
        }
        if (tab.State != AgentTabState.Failed
            || tab.LoadError?.Contains("transport-choice", StringComparison.Ordinal) != true)
            throw new InvalidOperationException("The stalled GUI tab did not report transport-choice.");
        Console.WriteLine("TAB Failed transport-choice");
        stalled.Release();
        await stalled.LateTransportDisposed.WaitAsync(ct);
        if (tab.State != AgentTabState.Failed || tab.Lease is not null)
            throw new InvalidOperationException("Late transport revived the failed tab.");
        reportStage("caller-tab-retry");
        terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tab.PropertyChanged += OnStateChanged;
        try
        {
            if (!await handler.Handle(window, Shortcut.Open, entity)
                || tab.State != AgentTabState.Loading)
                throw new InvalidOperationException("Failed tab did not retry through the shortcut.");
            await terminal.Task.WaitAsync(ct);
        }
        finally
        {
            tab.PropertyChanged -= OnStateChanged;
        }
        reportStage("caller-tab-result-" + tab.State + "-" + tab.LoadError);
        if (tab.State != AgentTabState.Ready || !tab.IsRemote
            || tab.Agent?.AgentChat is not RemoteAgentChat chat
            || !routeLog.PersistedSelected)
            throw new InvalidOperationException("Caller tab failed to open the persisted remote route.");
        Console.WriteLine("TAB Ready remote");
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        chat.TurnCompleted += OnCompleted;
        try
        {
            reportStage("caller-worker-turn");
            var result = await chat.InputQueues.EnqueueAsync(new EnqueueAgentInputRequest
            {
                TargetQueueId = chat.InputQueues.DefaultQueue.Snapshot.QueueId,
                ExpectedRevision = chat.InputQueues.Snapshot.Revision,
                Messages = [new ChatMessage(ChatRole.User, "fixture-prompt")],
                CommandId = Guid.NewGuid(),
            }, ct);
            if (result.Status != AgentInputQueueCommandStatus.Applied)
                throw new InvalidOperationException("Worker did not apply the turn.");
            await completed.Task.WaitAsync(ct);
            if (!chat.History.Any(item => item.Contents.OfType<TextContent>()
                .Any(content => content.Text == "worker-response")))
                throw new InvalidOperationException("Worker answer not projected.");
            Console.WriteLine("RESULT persisted worker-response");
        }
        finally
        {
            chat.TurnCompleted -= OnCompleted;
        }

        void OnCompleted(object? sender, AgentChatHistoryItem item)
        {
            if (item.Contents.OfType<TextContent>()
                .Any(content => content.Text == "worker-response"))
                completed.TrySetResult();
        }
    }

    private sealed class FixtureApplication : Application;

    private sealed class FixtureLoggerFactory(RouteLogger<UserComputerProfileTransportFactory> routeLogger)
        : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName)
            => categoryName == typeof(UserComputerProfileTransportFactory).FullName
                ? routeLogger : NullLogger.Instance;
        public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();
        public void Dispose() { }
    }

    private sealed class OneShotStalledProfileRegistry(ITransportFactoryRegistry inner)
        : ITransportFactoryRegistry
    {
        private readonly TaskCompletionSource<ITransport> release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource disposed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int attempts;

        internal Task Entered => this.entered.Task;
        internal Task LateTransportDisposed => this.disposed.Task;
        internal void Release() => this.release.TrySetResult(this.pending
            ?? throw new InvalidOperationException("No pending transport."));
        private ITransport? pending;

        public void Register(ITransportFactory factory) => inner.Register(factory);

        public async Task<ITransport> ConnectToAsync(JsonElement descriptor, CancellationToken ct = default)
        {
            var transport = await inner.ConnectToAsync(descriptor, ct);
            if (Interlocked.Increment(ref this.attempts) != 1)
                return transport;
            this.pending = new DisposalObservedTransport(transport, this.disposed);
            this.entered.TrySetResult();
            return await this.release.Task;
        }
    }

    private sealed class DisposalObservedTransport(
        ITransport inner, TaskCompletionSource disposed) : ITransport
    {
        public Task<IMessageChannel> ConnectToMessageChannelAsync(
            JsonElement request, CancellationToken ct = default)
            => inner.ConnectToMessageChannelAsync(request, ct);
        public Task<Stream> ConnectToStreamAsync(JsonElement request, CancellationToken ct = default)
            => inner.ConnectToStreamAsync(request, ct);
        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            disposed.TrySetResult();
        }
    }

    private sealed class FixtureAuthenticatedHttpFactory : ITransportFactory
    {
        private readonly HttpClientTransportFactory inner = new();
        private readonly Uri expected;

        internal FixtureAuthenticatedHttpFactory(string expectedEndpoint)
        {
            this.expected = new Uri(expectedEndpoint);
            if (!this.expected.IsLoopback || this.expected.Scheme != Uri.UriSchemeHttp)
                throw new InvalidOperationException("Fixture hub must be loopback HTTP.");
        }

        public Task<ITransport?> ConnectToAsync(JsonElement descriptor, CancellationToken ct = default)
        {
            if (descriptor.GetProperty("type").GetString() != "http")
                return Task.FromResult<ITransport?>(null);
            var url = descriptor.GetProperty("url").GetString()!;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var target)
                || !string.Equals(target.AbsoluteUri.TrimEnd('/'),
                    this.expected.AbsoluteUri.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fixture credential target differs from the configured hub.");
            using var request = JsonDocument.Parse(JsonSerializer.Serialize(
                new Dictionary<string, string>
                {
                    ["type"] = "http",
                    ["url"] = url,
                    ["dev-tunnel-token"] = ProcessAcceptanceCredentials.Token,
                }));
            return this.inner.ConnectToAsync(request.RootElement, ct);
        }

        public ValueTask DisposeAsync() => this.inner.DisposeAsync();
    }

    private sealed class RouteLogger<T> : ILogger<T>
    {
        internal bool PersistedSelected { get; private set; }
        public IDisposable BeginScope<TState>(TState state) where TState : notnull
            => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).Contains("route persisted; outcome selected", StringComparison.Ordinal))
                this.PersistedSelected = true;
        }

        private sealed class NullScope : IDisposable
        {
            internal static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
