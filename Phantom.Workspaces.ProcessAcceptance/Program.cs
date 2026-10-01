using System.Text.Json;
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
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Transport;
using Phantom.Workspaces.Transport.Http;
using Phantom.Workspaces.Transport.ReverseHttp;

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
        if (args.Length != 7 || args[0] is not ("worker" or "caller"))
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
                await RunCallerAsync(data, session, args[4], args[1], args[5],
                    new EntityId(args[6]), value => stage = value, timeout.Token);
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
            null, null, clock, routeLeaseDuration: TimeSpan.FromSeconds(20));
        var liveHub = new ReverseHttpClientTransportFactory(
            new FixtureAuthenticatedHttpFactory(liveUrl), liveUrl,
            session.UserComputerProfileEntityId.ToString(),
            null, null, routeLeaseDuration: TimeSpan.FromSeconds(20));
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
            clock.Advance(TimeSpan.FromSeconds(10));
            await renewed.Task.WaitAsync(ct);
            if (hub.LastReachabilityPublicationStatus?.Persisted != "true")
                throw new InvalidOperationException("Route renewal failed.");
            Console.WriteLine("RENEWED");
        }
    }

    private static async Task RunCallerAsync(
        IDataAccessLayer data, WorkspaceEntitySession session, string sessionId,
        string dataEndpoint, string listenUrl, EntityId workerProfile,
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

        reportStage("caller-owner-query");
        var query = await data.QueryAsync(new QueryRequest
        {
            Clauses = [new TopLevelQueryClause
            {
                ClauseIdentifier = new QueryClauseIdentifier("sessions"),
                Clause = new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet(["agent-session"]) },
            }],
            Timestamps = [null],
        }, ct);
        var entity = query.Batches.SelectMany(batch => batch.Entities)
            .Single(item => item.Data is JsonElement value
                && value.GetProperty("agent-session-id").GetString() == sessionId);
        var persisted = (JsonElement)entity.Data!;
        var owner = new EntityId(persisted.GetProperty("host-profile-entity-id").GetString()!);
        var registry = new TransportFactoryRegistry();
        var routeLog = new RouteLogger<UserComputerProfileTransportFactory>();
        registry.Register(new UserComputerProfileTransportFactory(
            data, session, registry, liveInboundRegistry: liveHub,
            reachabilityRouteStore: new DataAccessReachabilityRouteStore(data),
            logger: routeLog));
        var peer = new TransportPeerIdentity
        {
            AuthenticationScheme = "local-workspace-session",
            StablePeerId = session.UserComputerProfileEntityId.ToString(),
            UserEntityId = session.UserEntityId.ToString(),
            UserComputerProfileEntityId = session.UserComputerProfileEntityId.ToString(),
        };
        registry.Register(new ReverseHttpForwardingTransportFactory(
            new FixtureAuthenticatedHttpFactory(dataEndpoint), authenticatedPeer: peer));
        await using var factory = new AgentChatFactory(
            new InMemoryAgentPersistenceStore(), new AgentServices(), TaskScheduler.Default);
        var chats = new RunningAgentChatTable(factory, new AgentSessionRuntimeContextFactory(registry));
        using var ownerDescriptor = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["type"] = "user-computer-profile",
            ["entity-id"] = owner.ToString(),
        }));
        using var attempt = SessionAttachDiagnosticScope.Begin();
        reportStage("caller-route-selection");
        var transport = await registry.ConnectToAsync(ownerDescriptor.RootElement, ct)
            ?? throw new InvalidOperationException("Owner transport was not selected.");
        if (!routeLog.PersistedSelected)
            throw new InvalidOperationException("Caller did not select the persisted route.");

        reportStage("caller-remote-acquisition");
        await using var lease = await chats.AcquireAsync(new AcquireAgentChatRequest
        {
            AgentSessionId = new AgentSessionId(sessionId),
            AgentSessionEntity = persisted,
            AgentServices = new AgentServices(),
            ForegroundScheduler = TaskScheduler.Default,
            EntityName = "Static worker",
            EntityId = entity.EntityId.ToString(),
            AcquisitionMode = AgentChatAcquisitionMode.StartOrAttachRemote,
            OwningProfileTransport = transport,
        }, ct);
        var chat = (RemoteAgentChat)lease.AgentChat;
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
