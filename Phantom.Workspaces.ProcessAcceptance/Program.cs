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
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using GitHub.Copilot;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Web.Client;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Copilot;
using Phantom.Workspaces.Llm.Core.Manifest;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Services.Logging;
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
        if (args.Length != 9 || args[0] is not (
            "worker" or "caller" or "worker-failure" or "caller-failure" or "worker-child" or "caller-child"))
            return 2;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var loggerFactory = LoggingBootstrap.CreateLoggerFactory(
            new LogDirectoryProvider(new WorkspacesConfiguration { LogDirectory = args[8] }, null));
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
            var startupFailure = args[0].EndsWith("-failure", StringComparison.Ordinal);
            if (args[0].StartsWith("worker", StringComparison.Ordinal))
                await RunWorkerAsync(data, session, args[4], args[1], args[5],
                    loggerFactory, value => stage = value, startupFailure, args[0] == "worker-child", timeout.Token);
            else
                await RunCallerAsync(session, args[4], args[1], args[5],
                    new EntityId(args[6]), args[7], loggerFactory, value => stage = value,
                    startupFailure, args[0] == "caller-child", timeout.Token);
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
        IDataAccessLayer data, WorkspaceEntitySession session, string sessionId, string hubUrl,
        string liveUrl, ILoggerFactory loggerFactory, Action<string> reportStage,
        bool startupFailure, bool childAttachment, CancellationToken ct)
    {
        reportStage("worker-registration");
        var chatClient = new DeterministicTestChatClient();
        var response = chatClient.EnqueueStreamingResponse();
        response.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "worker-response"));
        response.Complete();
        var persistence = new InMemoryAgentPersistenceStore();
        await using var factory = new AgentChatFactory(
            persistence,
            new AgentServices { ChatClientOverride = chatClient },
            TaskScheduler.Default);
        var chats = new RunningAgentChatTable(factory);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var hub = new ReverseHttpClientTransportFactory(
            new FixtureAuthenticatedHttpFactory(hubUrl), hubUrl,
            session.UserComputerProfileEntityId.ToString(),
            null, null, clock, loggerFactory.CreateLogger<ReverseHttpClientTransportFactory>(),
            routeLeaseDuration: TimeSpan.FromMinutes(2));
        var liveHub = new ReverseHttpClientTransportFactory(
            new FixtureAuthenticatedHttpFactory(liveUrl), liveUrl,
            session.UserComputerProfileEntityId.ToString(),
            null, null, logger: loggerFactory.CreateLogger<ReverseHttpClientTransportFactory>(),
            routeLeaseDuration: TimeSpan.FromMinutes(2));
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
            data, session, loggerFactory, [hub, liveHub],
            agentServices: new AgentServices
            {
                ChatClientOverride = chatClient,
                CopilotClientFactory = startupFailure ? new FailingFixtureCopilotFactory() : null,
            },
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
                loggerFactory.CreateLogger("Phantom.Workspaces.ProcessAcceptance.Worker")
                    .LogInformation("Worker turn; stage worker-turn; outcome request-verified.");
                Console.WriteLine("TURN worker");
                continue;
            }
            if (command == "PREPARE-CHILD" && childAttachment)
            {
                reportStage("worker-child-preparation");
                var childId = await PrepareChildAsync(
                    data, session, sessionId, chats, persistence, ct);
                Console.WriteLine("CHILD ready " + childId);
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

    private static async Task<string> PrepareChildAsync(
        IDataAccessLayer data, WorkspaceEntitySession session, string sessionId,
        RunningAgentChatTable chats, InMemoryAgentPersistenceStore persistence, CancellationToken ct)
    {
        var rootId = new AgentSessionId(sessionId);
        if (!chats.RunningSessions.Any(item => item.SessionId == rootId))
            throw new InvalidOperationException("The remote parent has not started on the worker.");
        await using var lease = await chats.AcquireAsync(new AcquireAgentChatRequest
        {
            AgentSessionId = rootId,
            EntityName = sessionId,
        }, ct);
        var owner = lease.AgentChat as AgentChat
            ?? throw new InvalidOperationException("The worker parent is not a local chat.");
        var definition = PhantomAgentSchema.AgentDefinitionFromJson(
            """
            {"kind":"prompt","name":"fixture-child","model":{"id":"echo","provider":"echo","apiType":"Echo"},
             "instructions":"Fixture child transcript.","tools":[]}
            """);
        await owner.GetOrCreateAsync("fixture-child", definition, "fixture-child-tool", ct);
        var child = owner.SubAgents.OfType<AgentChat>().Single(item => item.AgentId == "fixture-child");
        var childId = child.AgentSessionId;
        await persistence.StoreAsync(new StoreRequestAgent
        {
            Agent = new PersistedAgent
            {
                AgentSessionId = childId,
                AgentDefinitionJson = BsonDocument.Parse(definition.ToJson()),
            },
            NewMessages =
            [
                new ChatMessage(ChatRole.User, "child persisted question"),
                new ChatMessage(ChatRole.Assistant, "child persisted answer"),
            ],
        }, ct);
        var parent = await data.QueryAsync(new QueryRequest
        {
            Clauses = [new TopLevelQueryClause
            {
                ClauseIdentifier = new QueryClauseIdentifier("sessions"),
                Clause = new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet(["agent-session"]) },
            }],
            Timestamps = [null],
        }, ct);
        var root = parent.Batches.SelectMany(batch => batch.Entities)
            .Single(item => item.Data is JsonElement value
                && value.GetProperty("agent-session-id").GetString() == sessionId);
        var source = ((JsonElement)root.Data!).GetProperty("agent-source-entity-id").GetString()
            ?? throw new InvalidOperationException("The parent source entity is missing.");
        var childEntity = AgentSessionEntityFactory.CreateEntityData(new CreateAgentSessionEntityDataRequest
        {
            AgentDefinitionEntityId = new EntityId(source),
            AgentDisplayName = "Fixture child",
            AgentSessionId = childId,
            AgentSessionNames = [new EntityName("tests", "sessions", childId)],
            CurrentTime = DateTimeOffset.UtcNow,
            ComputerName = "worker",
            HostProfileEntityId = session.UserComputerProfileEntityId,
            ParameterValues = new Dictionary<string, string>(),
        });
        var update = await data.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata
            {
                Comment = new Markdown { Text = "Seed fixture child session." },
            },
            Changes = [new EntityChange
            {
                EntityId = new EntityId(childEntity.GetProperty("entity-id").GetString()!),
                EntityChangeMode = EntityChangeMode.Replace,
                Data = childEntity,
            }],
        }, ct);
        if (update.EntityResults.Any(item => item.UpdateState == UpdateState.Failed))
            throw new InvalidOperationException("The fixture child was not persisted.");
        return childId;
    }

    private static async Task RunCallerAsync(
        WorkspaceEntitySession session, string sessionId,
        string dataEndpoint, string listenUrl, EntityId workerProfile, string repositoryPath,
        ILoggerFactory loggerFactory, Action<string> reportStage,
        bool startupFailure, bool childAttachment, CancellationToken ct)
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
        builder.Logging.AddProvider(new ForwardingLoggerProvider(loggerFactory));
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
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
                    session, sessionId, repositoryPath, dataEndpoint, loggerFactory, reportStage,
                    workerProfile, startupFailure, childAttachment, ct);
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
        ILoggerFactory loggerFactory, Action<string> reportStage,
        EntityId workerProfile, bool startupFailure, bool childAttachment, CancellationToken ct)
    {
        await using var factory = new AgentChatFactory(
            new InMemoryAgentPersistenceStore(), new AgentServices(),
            TaskScheduler.FromCurrentSynchronizationContext());
        var provider = new TransportFactoryRegistryProvider();
        var chats = new RunningAgentChatTable(
            factory, AgentSessionRuntimeContextFactory.FromProvider(provider));
        var services = new ApplicationServices(
            chats, new AgentPersistenceStoreCache(), loggerFactory,
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
        var registry = window.TransportComposition?.TransportFactoryRegistry
            ?? throw new InvalidOperationException("Caller production transport was not initialized.");
        var stalled = childAttachment ? null : new OneShotStalledProfileRegistry(registry);
        var clock = new FakeTimeProvider();
        await using var handler = new OpenAgentSessionShortcutHandler(
            new AgentSessionShortcutContext(
                userComputerProfileOverride: "fixture-caller",
                persistenceStoreCache: services.AgentPersistenceStoreCache),
            new DeferredTrustedExecutorSelector(),
            chats, new AgentSessionOwnerDecisionProvider(), stalled ?? registry,
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
            if (!childAttachment)
            {
                await stalled!.Entered.WaitAsync(ct);
                clock.Advance(TimeSpan.FromSeconds(45));
            }
            await terminal.Task.WaitAsync(ct);
        }
        finally
        {
            tab.PropertyChanged -= OnStateChanged;
        }
        if (!childAttachment)
        {
            if (tab.State != AgentTabState.Failed
                || tab.LoadError?.Contains("transport-choice", StringComparison.Ordinal) != true)
                throw new InvalidOperationException("The stalled GUI tab did not report transport-choice.");
            Console.WriteLine("TAB Failed transport-choice");
            stalled!.Release();
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
        }
        reportStage("caller-tab-result-" + tab.State);
        if (tab.State != AgentTabState.Ready || !tab.IsRemote
            || tab.Agent?.AgentChat is not RemoteAgentChat chat)
            throw new InvalidOperationException("Caller tab failed to open the persisted remote route.");
        Console.WriteLine("TAB Ready remote");
        if (childAttachment)
        {
            if (await Console.In.ReadLineAsync(ct) != "OPEN-CHILD")
                throw new InvalidOperationException("Expected the fixture child open command.");
            reportStage("caller-child-attachment");
            await VerifyChildAttachmentAsync(window, tab, chat, ct);
        }
        if (startupFailure)
        {
            await VerifyRemoteStartupFailureInCallerUiAsync(
                window, workerProfile, loggerFactory, reportStage, ct);
            return;
        }
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

    private static async Task VerifyChildAttachmentAsync(
        MainWindowViewModel window, AgentSessionWorkspaceTabViewModel tab,
        RemoteAgentChat parent, CancellationToken ct)
    {
        var projected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnSubagentsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
        {
            if (parent.SubAgents.Any(child => child.AgentId == "fixture-child"))
                projected.TrySetResult();
        }
        var changes = (System.Collections.Specialized.INotifyCollectionChanged)parent.SubAgents;
        changes.CollectionChanged += OnSubagentsChanged;
        try
        {
            OnSubagentsChanged(null, new System.Collections.Specialized.NotifyCollectionChangedEventArgs(
                System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
            await projected.Task.WaitAsync(ct);
        }
        finally
        {
            changes.CollectionChanged -= OnSubagentsChanged;
        }
        var view = tab.Agent ?? throw new InvalidOperationException("The shortcut did not compose a GUI agent.");
        view.NavigateToAgent("fixture-child");
        await view.WaitForRemoteChildAsync("fixture-child").WaitAsync(ct);
        var slot = view.SubAgentsContainer.Slots.Single();
        var child = slot.SubAgentViewModel.AgentChat as RemoteAgentChat
            ?? throw new InvalidOperationException("The child was not attached remotely.");
        var childId = child.Information.AgentSessionId;
        if (childId == "fixture-child"
            || view.SelectedEditorItem?.Id != "sub-agent-fixture-child"
            || !ReferenceEquals(slot.SubAgentViewModel.ConversationDetail, view.SelectedEditorItem.DetailContent)
            || child.ViewerCount != 1
            || !slot.SubAgentViewModel.History.Any(item => item.Contents.OfType<TextContent>()
                .Any(text => text.Text == "child persisted answer"))
            || !slot.SubAgentViewModel.History.Any(item => item.Contents.OfType<TextContent>()
                .Any(text => text.Text == "child persisted question"))
            || view.History.Any(item => item.Contents.OfType<TextContent>()
                .Any(text => text.Text == "child persisted answer")))
            throw new InvalidOperationException("The composed child viewer did not render its own transcript.");
        Console.WriteLine("CHILD rendered " + childId + " viewer-1");

        await view.DisposeViewResourcesAsync();
        var descriptor = await parent.OpenSubagentAsync("fixture-child", ct);
        if (descriptor.AgentSessionId != childId)
            throw new InvalidOperationException("The child owner changed after viewer release.");
        using var profile = JsonDocument.Parse(
            $$"""{"type":"user-computer-profile","entity-id":"{{descriptor.OwningProfileEntityId}}"}""");
        var registry = window.TransportComposition?.TransportFactoryRegistry
            ?? throw new InvalidOperationException("The caller production transport is unavailable.");
        var transport = await registry.ConnectToAsync(profile.RootElement, ct);
        var client = new RemoteAgentSessionClient(transport);
        await using var reopened = await RemoteAgentChat.AttachAsync(new RemoteAgentChatAttachOptions
        {
            Client = client,
            ForegroundScheduler = TaskScheduler.FromCurrentSynchronizationContext(),
            OpenRequest = new AgentSessionOpenRequest
            {
                ProtocolVersion = 1,
                AgentSessionId = descriptor.AgentSessionId,
                ExpectedOwningProfileEntityId = descriptor.OwningProfileEntityId,
                ExpectedOwnershipGeneration = descriptor.OwnershipGeneration,
                OpenIntent = AgentSessionOpenIntent.Attach,
                AttachmentToken = Guid.NewGuid().ToString("N"),
                Capabilities = [],
            },
        }, ct);
        if (reopened.ViewerCount != 1
            || !reopened.History.Any(item => item.Contents.OfType<TextContent>()
                .Any(text => text.Text == "child persisted answer")))
            throw new InvalidOperationException("Re-attaching did not confirm child viewer release.");
        Console.WriteLine("CHILD reopened " + childId + " viewer-1");
        await reopened.DetachAsync(ct);
        if (!parent.IsConnected)
            throw new InvalidOperationException("Closing the child viewer terminated the parent owner.");
        Console.WriteLine("CHILD parent-alive");
    }

    private static async Task VerifyRemoteStartupFailureInCallerUiAsync(
        MainWindowViewModel window, EntityId workerProfile, ILoggerFactory loggerFactory,
        Action<string> reportStage, CancellationToken ct)
    {
        using var descriptor = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["type"] = "user-computer-profile",
            ["entity-id"] = workerProfile.ToString(),
        }));
        var services = new AgentServices
        {
            ExecutorBindings = new ExecutorBindings
            {
                Bindings = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["model-worker"] = descriptor.RootElement.Clone(),
                },
            },
            ExecutorTransportFactoryRegistry = window.TransportComposition?.TransportFactoryRegistry
                ?? throw new InvalidOperationException("Caller transport registry unavailable."),
            LoggerFactory = loggerFactory,
        };
        await using var factory = new AgentChatFactory(
            new InMemoryAgentPersistenceStore(), services,
            TaskScheduler.FromCurrentSynchronizationContext());
        var chats = new RunningAgentChatTable(factory);
        var definition = PhantomAgentSchema.AgentDefinitionFromJson(
            """
            {"kind":"prompt","name":"synthetic-split","model":{"id":"gpt-5","provider":"github-copilot",
              "options":{"additionalProperties":{"executor":"model-worker"}}},
              "instructions":"Fixture-only startup test.","tools":[]}
            """);
        await using var lease = await chats.AcquireAsync(new AcquireAgentChatRequest
        {
            AgentSessionId = new AgentSessionId("synthetic-split-startup"),
            AgentDefinition = definition,
            AgentServices = services,
            EntityName = "Synthetic split startup",
        }, ct);
        using var uiLogs = new Phantom.Workspaces.Agent.Gui.ObservableLoggerFactory();
        await using var ui = new Phantom.Workspaces.Agent.Gui.ViewModels.AgentViewModel(
            new Phantom.Workspaces.Agent.Gui.ViewModels.AgentViewModelOptions
            {
                AgentChat = lease.AgentChat,
                DisplayName = "Synthetic split startup",
                Description = "Fixture",
                LoggerFactory = uiLogs,
                ForegroundScheduler = TaskScheduler.FromCurrentSynchronizationContext(),
            });
        var diagnostic = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnHistoryChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
        {
            foreach (var item in ui.History)
            {
                if (item.Role != AgentChatHistoryItem.DiagnosticChatRole)
                    continue;
                var message = item.Contents.OfType<ErrorContent>().FirstOrDefault()?.Message;
                if (message is not null)
                    diagnostic.TrySetResult(message);
            }
        }
        var historyChanges = (System.Collections.Specialized.INotifyCollectionChanged)ui.History;
        historyChanges.CollectionChanged += OnHistoryChanged;
        try
        {
            reportStage("caller-split-worker-start");
            lease.LocalAgentChat.EnqueueUserMessage("synthetic-startup-prompt");
            var message = await diagnostic.Task.WaitAsync(ct);
            if (!message.Contains("System.InvalidOperationException", StringComparison.Ordinal)
                || !message.Contains("Synthetic worker CLI start failed.", StringComparison.Ordinal)
                || !message.Contains("System.IO.IOException", StringComparison.Ordinal)
                || !message.Contains("Synthetic worker inner cause.", StringComparison.Ordinal)
                || !message.Contains(nameof(FailingFixtureCopilotClient.StartAsync), StringComparison.Ordinal))
                throw new InvalidOperationException("Caller chat UI did not display the detailed worker cause.");
            Console.WriteLine("RESULT caller-ui startup-type-message-stack-inner");
        }
        finally
        {
            historyChanges.CollectionChanged -= OnHistoryChanged;
        }
    }

    private sealed class FailingFixtureCopilotFactory : ICopilotClientFactory
    {
        public ICopilotClient Create(CopilotClientOptions options) => new FailingFixtureCopilotClient();
    }

    private sealed class FailingFixtureCopilotClient : ICopilotClient
    {
        public Task StartAsync(CancellationToken ct)
            => throw new InvalidOperationException("Synthetic worker CLI start failed.",
                new IOException("Synthetic worker inner cause."));
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ModelInfo>>([]);
        public Task<ICopilotSession> CreateSessionAsync(SessionConfig config, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ICopilotSession> ResumeSessionAsync(string sessionId, ResumeSessionConfig config, CancellationToken ct)
            => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixtureApplication : Application;

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
}
