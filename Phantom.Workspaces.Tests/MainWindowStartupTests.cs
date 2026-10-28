using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Data.Web.Server;
using Phantom.Workspaces.Transport;
using Phantom.Workspaces.Transport.Http;
using Phantom.Workspaces.Transport.ReverseHttp;
using Phantom.Workspaces.Web.Server;
using Microsoft.AspNetCore.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Phantom.Workspaces.Tests;

public sealed class MainWindowStartupTests(ITestOutputHelper output)
{
    [AvaloniaTheory(Timeout = 15_000)]
    [InlineData("complete", "query")]
    [InlineData("cancel", "query")]
    [InlineData("fail", "query")]
    [InlineData("cancel", "entity-descriptor")]
    [InlineData("cancel", "agent-descriptor")]
    [InlineData("cancel", "declaration")]
    [InlineData("cancel", "runtime-context")]
    [InlineData("cancel", "remote-host")]
    [InlineData("cancel", "host-name")]
    [InlineData("cancel", "pause")]
    [InlineData("cancel", "reconcile")]
    [InlineData("cancel", "history")]
    [InlineData("cancel", "auto-resume")]
    public async Task ApplicationStartup_WorkspaceQueryIsGated_VisibleShellOwnsResponsiveCancellableLoading(
        string outcome, string stage)
    {
        var token = TestContext.Current.CancellationToken;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = 0;
        var target = new EntityId(Guid.NewGuid());
        var workspace = new EntityId(Guid.NewGuid());
        var hostId = string.Empty;
        var hostReads = 0;
        var toolQueries = 0;
        var dal = await WebServerDataAccessLayerFactory.CreateDefaultAsync(token);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IDataAccessLayer>(dal);
        var connectionStatus = new ReverseConnectionStatusRegistry();
        await using var reverse = new ReverseHttpServerTransportFactory(connectionStatus);
        var transports = new TransportRegistry();
        transports.Register(reverse);
        await using var transportServer = new HttpServerTransportFactory(transports);
        await using var server = builder.Build();
        server.UseWebSockets();
        server.Use(async (context, next) =>
        {
            var matches = stage == "query" && context.Request.Path == "/data/query";
            if (Volatile.Read(ref armed) == 1 && stage != "query"
                && (context.Request.Path == "/data/get" || context.Request.Path == "/data/query"))
            {
                context.Request.EnableBuffering();
                using var reader = new System.IO.StreamReader(context.Request.Body, leaveOpen: true);
                var body = await reader.ReadToEndAsync(context.RequestAborted);
                context.Request.Body.Position = 0;
                matches = stage switch
                {
                    "entity-descriptor" or "agent-descriptor" or "declaration" =>
                        context.Request.Path == "/data/get" && body.Contains(target.ToString(), StringComparison.OrdinalIgnoreCase),
                    "runtime-context" or "remote-host" or "host-name" or "pause" => context.Request.Path == "/data/get"
                        && body.Contains(hostId, StringComparison.OrdinalIgnoreCase)
                        && Interlocked.Increment(ref hostReads) == (stage == "runtime-context" ? 1 : stage == "pause" ? 3 : 2),
                    "reconcile" or "history" => context.Request.Path == "/data/query"
                        && body.Contains("tool-execution-result", StringComparison.Ordinal)
                        && Interlocked.Increment(ref toolQueries) == (stage == "reconcile" ? 1 : 2),
                    "auto-resume" => context.Request.Path == "/data/query" && body.Contains("agent-sessions", StringComparison.Ordinal),
                    _ => false,
                };
            }
            if (matches && Interlocked.CompareExchange(ref armed, 0, 1) == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(context.RequestAborted);
                if (outcome == "fail")
                {
                    context.Response.StatusCode = 500;
                    return;
                }
            }
            await next(context);
        });
        server.MapWebDataAccessEndpoints();
        transportServer.Map(server);
        server.MapTransportReverseEndpoints(reverse, connectionStatus);
        await server.StartAsync(token);
        var address = server.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var viewModel = new MainWindowViewModel(new WebRepositorySource(address),
            new WorkspacesConfiguration
            {
                SkipStartupWorkspace = false,
                RemoteHosting = new RemoteHostingSettings
                {
                    Enabled = stage == "remote-host",
                    ListenUrls = ["http://127.0.0.1:0"],
                },
            });
        var window = new MainWindow(viewModel);
        var splash = new Avalonia.Controls.Window();
        await using var access = Services.Access.AccessHostServices.Create(new Services.Access.AccessHostOptions
        {
            PlatformSecretStore = new Phantom.Workspaces.Llm.Secrets.NullPlatformSecretStore(),
            Dispatch = action => action(),
            ReleaseMemoryFilePath = System.IO.Path.Combine(Environment.CurrentDirectory, ".nonexistent-startup-access", "memory.json"),
        });
        await using var popup = new AuthorizationRequestsPopupViewModel { Rows = access.AuthorizationRows };
        try
        {
            splash.Show();
            await viewModel.PrepareShellAsync();
            hostId = viewModel.EntityBroker!.EntityRepository.WorkspaceEntitySession.UserComputerProfileEntityId.ToString();
            if (stage is "entity-descriptor" or "agent-descriptor" or "declaration")
            {
                var data = JsonNode.Parse($$"""
                    {"entity-id":"{{workspace}}","entity-types":["entity","workspace"],"names":[["tests","gated-workspace"]],
                     "display-name":{"default":"Gated startup workspace"},
                     "tabs":[{"tab-id":"gated-tab","title":"Gated target","kind":"entity-view",
                        "content":{"target-entity-name":"{{target}}"} }]}
                    """)!;
                if (stage != "declaration")
                {
                    var placeholder = new EntityWorkspaceTabViewModel { Id = "gated-tab", Title = "Gated target" };
                    var document = new WorkspaceDocument(placeholder)
                    {
                        Descriptor = stage == "agent-descriptor"
                            ? new AgentSessionDockTabDescriptor(target.ToString())
                            : new EntityDockTabDescriptor(target.ToString(), "Open"),
                    };
                    var content = new WorkspaceContentDock
                    {
                        Id = "gated-content",
                        VisibleDockables = new System.Collections.ObjectModel.ObservableCollection<global::Dock.Model.Core.IDockable> { document },
                        ActiveDockable = document,
                    };
                    var layout = new global::Dock.Model.Mvvm.Controls.RootDock
                    {
                        Id = "gated-root",
                        VisibleDockables = new System.Collections.ObjectModel.ObservableCollection<global::Dock.Model.Core.IDockable> { content },
                        ActiveDockable = content,
                    };
                    document.Owner = content;
                    content.Owner = layout;
                    var serializer = new global::Dock.Serializer.SystemTextJson.DockSerializer(
                        typeof(System.Collections.ObjectModel.ObservableCollection<>), new WorkspaceDockTypeInfoResolver());
                    data["dock-layout"] = JsonNode.Parse(serializer.Serialize(layout));
                }
                var relationship = new EntityId(Guid.NewGuid());
                var update = await dal.UpdateAsync(new UpdateRequest
                {
                    UpdateMetadata = new() { Comment = new() { Text = "Seed deterministic startup cancellation case" } },
                    Changes =
                    [
                        new() { EntityId = workspace, EntityChangeMode = EntityChangeMode.Replace,
                            Data = JsonSerializer.SerializeToElement(data) },
                        new() { EntityId = relationship, EntityChangeMode = EntityChangeMode.Replace,
                            Data = JsonSerializer.SerializeToElement(JsonNode.Parse($$"""
                                {"entity-id":"{{relationship}}","entity-types":["entity","default","relationship"],
                                 "names":[["tests","gated-default"]],
                                 "participants":{"applied-to":"{{hostId}}","value":"{{workspace}}"} }
                                """)) },
                    ],
                }, token);
                Assert.True(update.EntityResults.All(result => result.UpdateState != UpdateState.Failed),
                    string.Join(" | ", update.EntityResults.SelectMany(result => result.Errors).Select(error => error.Message)));
            }
            window.AttachAccessHost(access, popup);
            window.Show();
            splash.Close();
            var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(() => rendered.TrySetResult(), DispatcherPriority.Render);
            Volatile.Write(ref armed, 1);
            var startup = viewModel.CompleteVisibleStartupAsync();
            output.WriteLine($"Awaiting gated {stage} request.");
            await entered.Task.WaitAsync(token);
            output.WriteLine($"Entered gated {stage} request.");
            Assert.True(rendered.Task.IsCompletedSuccessfully);
            Assert.True(window.IsVisible);
            Assert.False(splash.IsVisible);
            Assert.NotNull(viewModel.Layout);
            Assert.Equal(WorkspaceStartupState.LoadingWorkspaces, viewModel.StartupState);
            Assert.False(viewModel.IsStartupReady);
            Assert.False(startup.IsCompleted);
            var heartbeat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(() => heartbeat.TrySetResult());
            await heartbeat.Task.WaitAsync(token);
            var registry = Assert.IsType<Services.Authorization.UserAuthorizationRegistry>(access.AuthorizationRegistry);
            registry.Add(AuthorizationRequestRowsViewModelTests.Snapshot());
            window.FindControl<Avalonia.Controls.Button>("AuthorizationKey")!
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            await window.AccessAdministrationReady;
            Assert.Same(access.AuthorizationRows, window.LastAccessAdministrationWindow!.RequestRows);
            var request = Assert.Single(access.AuthorizationRows.Rows);
            await request.CancelCommand.ExecuteAsync(null);
            Assert.False(request.CanAct);
            if (outcome == "cancel")
            {
                var disposal = viewModel.DisposeAsync().AsTask();
                output.WriteLine($"Awaiting concurrent disposal during {stage}.");
                await Task.WhenAll(disposal, viewModel.DisposeAsync().AsTask());
                output.WriteLine($"Disposed during {stage}.");
                Assert.False(release.Task.IsCompleted);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup);
                Assert.Equal(WorkspaceStartupState.Cancelled, viewModel.StartupState);
                var panes = viewModel.WorkspacePanes.ToArray();
                release.TrySetResult();
                await Dispatcher.UIThread.InvokeAsync(() => Assert.Equal(panes, viewModel.WorkspacePanes));
                Assert.False(viewModel.IsStartupReady);
            }
            else
            {
                release.TrySetResult();
                if (outcome == "fail")
                {
                    await Assert.ThrowsAnyAsync<Exception>(() => startup);
                    Assert.Equal(WorkspaceStartupState.Failed, viewModel.StartupState);
                    Assert.Contains("failed", viewModel.StartupStatus, StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    await startup;
                    Assert.True(viewModel.IsStartupReady);
                }
            }
        }
        finally
        {
            release.TrySetResult();
            splash.Close();
            await window.DisposeAccessAdministrationAsync();
            window.Close();
            try { await viewModel.DisposeAsync(); }
            catch (AggregateException) when (outcome == "fail") { }
            await server.StopAsync(token);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ApplicationStartup_WorkspaceMaterializes_VisibleWindowRemainsLoadingUntilCompleted()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource(),
            new WorkspacesConfiguration { SkipStartupWorkspace = false });
        await viewModel.PrepareShellAsync();
        var window = new MainWindow(viewModel);
        var loadingObserved = false;
        viewModel.WorkspacePanes.CollectionChanged += (_, _) =>
        {
            if (viewModel.WorkspacePanes.Any(p => p.Id.StartsWith("loading-workspace:", StringComparison.Ordinal)))
            {
                Assert.True(window.IsVisible);
                Assert.Equal(WorkspaceStartupState.LoadingWorkspaces, viewModel.StartupState);
                Assert.False(viewModel.IsStartupReady);
                loadingObserved = true;
            }
        };
        try
        {
            window.Show();
            await viewModel.CompleteVisibleStartupAsync();
            Assert.True(loadingObserved);
            Assert.True(viewModel.IsStartupReady);
            Assert.All(viewModel.WorkspacePanes, pane => Assert.True(pane.Populated.IsCompletedSuccessfully));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ApplicationStartup_WorkspaceLoadFails_ReportsFailureAndCompletesTeardown()
    {
        var viewModel = new MainWindowViewModel(new UnknownRepositorySource(),
            new WorkspacesConfiguration { SkipStartupWorkspace = false });
        await viewModel.PrepareShellAsync();
        var window = new MainWindow(viewModel);
        var failure = new InvalidOperationException("Controlled workspace materialization failure");
        System.Collections.Specialized.NotifyCollectionChangedEventHandler failRestore = (_, _) => throw failure;
        viewModel.WorkspacePanes.CollectionChanged += failRestore;
        try
        {
            window.Show();
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(viewModel.CompleteVisibleStartupAsync));
            Assert.True(window.IsVisible);
            Assert.Equal(WorkspaceStartupState.Failed, viewModel.StartupState);
            Assert.Contains("failed", viewModel.StartupStatus, StringComparison.OrdinalIgnoreCase);
            window.UpdateLayout();
            Assert.Contains(window.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>(),
                block => block.Text == viewModel.StartupStatus);
            var disposal = await Assert.ThrowsAsync<AggregateException>(() => viewModel.DisposeAsync().AsTask());
            Assert.Contains(failure, disposal.InnerExceptions);
        }
        finally
        {
            viewModel.WorkspacePanes.CollectionChanged -= failRestore;
            window.Close();
            try { await viewModel.DisposeAsync(); }
            catch (AggregateException) { }
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ApplicationStartup_MainWindowIsShown_RenderOpportunityPrecedesWorkspaceLoad()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource(),
            new WorkspacesConfiguration { SkipStartupWorkspace = false });
        await viewModel.PrepareShellAsync();
        Assert.NotNull(viewModel.Layout);
        Assert.Equal(WorkspaceStartupState.ShellPrepared, viewModel.StartupState);
        var window = new MainWindow(viewModel);
        var splash = new Avalonia.Controls.Window();
        try
        {
            splash.Show();
            window.Show();
            splash.Close();
            var renderOpportunity = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(() =>
            {
                Assert.True(window.IsVisible);
                Assert.False(splash.IsVisible);
                Assert.False(viewModel.IsStartupReady);
                renderOpportunity.TrySetResult();
            }, DispatcherPriority.Render);
            var startup = viewModel.CompleteVisibleStartupAsync();
            await renderOpportunity.Task.WaitAsync(TestContext.Current.CancellationToken);
            await startup;
            Assert.True(viewModel.IsStartupReady);
            Assert.Same(startup, viewModel.CompleteVisibleStartupAsync());
            Assert.Same(viewModel.InitializeAsync(), viewModel.InitializeAsync());
            Assert.DoesNotContain(viewModel.WorkspacePanes, pane => pane.Id == "loading");
        }
        finally
        {
            splash.Close();
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowViewModel_DisposedAfterShellPreparation_CancelsVisibleStartup()
    {
        var viewModel = new MainWindowViewModel(new UnknownRepositorySource(),
            new WorkspacesConfiguration { SkipStartupWorkspace = false });
        await viewModel.PrepareShellAsync();
        var startup = viewModel.CompleteVisibleStartupAsync();
        var disposal = viewModel.DisposeAsync().AsTask();
        await Task.WhenAll(disposal, viewModel.DisposeAsync().AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup);
        Assert.Equal(WorkspaceStartupState.Cancelled, viewModel.StartupState);
        Assert.False(viewModel.IsStartupReady);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => viewModel.CompleteVisibleStartupAsync());
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowViewModel_SkipStartupWorkspaceIsTrue_PreparesShellWithoutClaimingReady()
    {
        await using var viewModel = CreateViewModel();
        await viewModel.PrepareShellAsync();
        Assert.False(viewModel.IsStartupReady);
        var panes = viewModel.WorkspacePanes.ToArray();
        await viewModel.InitializeAsync();
        Assert.True(viewModel.IsStartupReady);
        Assert.Equal(panes, viewModel.WorkspacePanes);
    }

    [AvaloniaFact]
    public async Task MainWindowViewModel_RepositoryStartupIsBlocked_DispatcherRemainsResponsive()
    {
        using var probe = new StartupProbe();
        await using var viewModel = CreateViewModel();
        try
        {
            await probe.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.False(probe.RepositoryStartedOnDispatcher);
            var initialization = viewModel.InitializeAsync();
            var heartbeat = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(() => heartbeat.TrySetResult(Dispatcher.UIThread.CheckAccess()));
            Assert.True(await heartbeat.Task.WaitAsync(TestContext.Current.CancellationToken));
            Assert.False(initialization.IsCompleted);
            probe.Release.Set();
            await initialization;
            Assert.True(probe.BrokerConstructedOnDispatcher);
            Assert.NotEmpty(viewModel.TopLevelViews);
        }
        finally
        {
            probe.Release.Set();
        }
    }

    [AvaloniaFact]
    public async Task MainWindowViewModel_DisposedDuringRepositoryStartup_CancelsAndObservesLateCompletion()
    {
        using var probe = new StartupProbe();
        var viewModel = CreateViewModel();
        try
        {
            await probe.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.False(probe.RepositoryStartedOnDispatcher);
            var initialization = viewModel.InitializeAsync();
            var disposal = viewModel.DisposeAsync().AsTask();
            var concurrentDisposal = viewModel.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            Assert.False(concurrentDisposal.IsCompleted);
            probe.Release.Set();
            await disposal;
            await concurrentDisposal;
            await viewModel.DisposeAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => viewModel.InitializeAsync());
            Assert.False(probe.BrokerConstructedOnDispatcher);
            Assert.Empty(viewModel.TopLevelViews);
        }
        finally
        {
            probe.Release.Set();
            await viewModel.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task MainWindowViewModel_DisposedAfterRepositoryCreated_DisposesLateRepositoryExactlyOnce()
    {
        using var probe = new StartupProbe(gateCompletedRepository: true);
        var viewModel = CreateViewModel();
        try
        {
            await probe.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.False(probe.RepositoryStartedOnDispatcher);
            var disposal = viewModel.DisposeAsync().AsTask();
            var concurrentDisposal = viewModel.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            probe.Release.Set();
            await disposal;
            await concurrentDisposal;
            await viewModel.DisposeAsync();
            Assert.Equal(1, probe.RepositoryDisposals);
            Assert.Empty(viewModel.TopLevelViews);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => viewModel.InitializeAsync());
        }
        finally
        {
            probe.Release.Set();
            await viewModel.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task MainWindowViewModel_DisposedDuringFailingStartup_ObservesSameFailureAndCompletesCleanup()
    {
        var startupFailure = new InvalidOperationException("Controlled repository startup failure.");
        using var probe = new StartupProbe(failureAfterRelease: startupFailure);
        var viewModel = CreateViewModel();
        try
        {
            await probe.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.False(probe.RepositoryStartedOnDispatcher);
            var initialization = viewModel.InitializeAsync();
            var disposal = viewModel.DisposeAsync().AsTask();
            var concurrentDisposal = viewModel.DisposeAsync().AsTask();
            probe.Release.Set();
            var failure = await Assert.ThrowsAsync<AggregateException>(() => disposal);
            Assert.Same(startupFailure, Assert.Single(failure.InnerExceptions));
            Assert.Same(failure, await Assert.ThrowsAsync<AggregateException>(() => concurrentDisposal));
            Assert.Same(failure, await Assert.ThrowsAsync<AggregateException>(() => viewModel.DisposeAsync().AsTask()));
            Assert.Same(startupFailure, await Assert.ThrowsAsync<InvalidOperationException>(() => initialization));
            Assert.Empty(viewModel.TopLevelViews);
        }
        finally
        {
            probe.Release.Set();
            try { await viewModel.DisposeAsync(); }
            catch (AggregateException) { }
        }
    }

    private static MainWindowViewModel CreateViewModel() => new(
        new UnknownRepositorySource(),
        new WorkspacesConfiguration { SkipStartupWorkspace = true });

    private sealed class StartupProbe : IDisposable
    {
        private readonly ActivityListener listener;
        private int entered;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public bool RepositoryStartedOnDispatcher { get; private set; }
        public bool BrokerConstructedOnDispatcher { get; private set; }
        public int RepositoryDisposals { get; private set; }

        public StartupProbe(bool gateCompletedRepository = false, Exception? failureAfterRelease = null)
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            void Gate()
            {
                if (Interlocked.Exchange(ref this.entered, 1) != 0)
                    return;
                this.RepositoryStartedOnDispatcher = Dispatcher.UIThread.CheckAccess();
                this.Entered.TrySetResult();
                if (!this.RepositoryStartedOnDispatcher)
                {
                    // Activity callbacks are synchronous. Gate only the repository worker, never the dispatcher.
                    this.Release.Wait(cancellationToken);
                    if (failureAfterRelease is not null)
                        throw failureAfterRelease;
                }
            }
            this.listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == RepositoryStartupDiagnostics.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStarted = activity =>
                {
                    if (activity.OperationName == "broker-construction")
                        this.BrokerConstructedOnDispatcher = Dispatcher.UIThread.CheckAccess();
                    if (!gateCompletedRepository && activity.OperationName == "repository-schema-population")
                        Gate();
                },
                ActivityStopped = activity =>
                {
                    if (activity.OperationName == "repository-disposal")
                        this.RepositoryDisposals++;
                    if (gateCompletedRepository && activity.OperationName == "repository-initialization")
                        Gate();
                },
            };
            ActivitySource.AddActivityListener(this.listener);
        }

        public void Dispose()
        {
            this.Release.Set();
            this.listener.Dispose();
            this.Release.Dispose();
        }
    }
}
