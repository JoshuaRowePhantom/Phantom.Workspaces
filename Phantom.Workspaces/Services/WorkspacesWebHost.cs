using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Web.Server;
using Phantom.Workspaces.Services.Logging;
using Phantom.Workspaces.Transport;
using Phantom.Workspaces.Transport.Http;
using Phantom.Workspaces.Transport.ReverseHttp;
using Phantom.Workspaces.Web.Server;

namespace Phantom.Workspaces.Services;

/// <summary>
/// Manages the ASP.NET Core web server lifecycle for the Phantom.Workspaces GUI application.
/// When <see cref="RemoteHostingSettings.Enabled"/> is true, hosts the web data-access and
/// agent execution endpoints (including the transport reverse-relay hub) on the configured listen URL.
/// </summary>
public sealed class WorkspacesWebHost : IAsyncDisposable
{
    private readonly ReverseConnectionStatusRegistry statusRegistry;
    private readonly ReverseHttpServerTransportFactory reverseHttpServerTransportFactory;
    private readonly IReachabilityRouteStore? reachabilityRouteStore;
    private readonly EntityId? localProfileEntityId;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<WorkspacesWebHost> logger;
    private readonly bool ownsReverseHttpServerTransportFactory;
    private readonly SemaphoreSlim directRouteGate = new(1, 1);
    private WebApplication? application;
    private Task? runTask;
    private CancellationTokenSource? cancellationTokenSource;
    private HttpServerTransportFactory? httpServerTransportFactory;
    private ReachabilityRouteLease? directRouteLease;
    private bool httpServerTransportFactoryDisposed;

    public WorkspacesWebHost(ReverseConnectionStatusRegistry statusRegistry)
        : this(statusRegistry, null, null, null)
    {
    }

    public WorkspacesWebHost(
        ReverseConnectionStatusRegistry statusRegistry,
        ReverseHttpServerTransportFactory? reverseHttpServerTransportFactory,
        IReachabilityRouteStore? reachabilityRouteStore,
        EntityId? localProfileEntityId,
        TimeProvider? timeProvider = null,
        ILogger<WorkspacesWebHost>? logger = null)
    {
        this.statusRegistry = statusRegistry ?? throw new ArgumentNullException(nameof(statusRegistry));
        this.reverseHttpServerTransportFactory =
            reverseHttpServerTransportFactory ?? new ReverseHttpServerTransportFactory(statusRegistry);
        this.ownsReverseHttpServerTransportFactory = reverseHttpServerTransportFactory is null;
        this.reachabilityRouteStore = reachabilityRouteStore;
        this.localProfileEntityId = localProfileEntityId;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkspacesWebHost>.Instance;
    }

    /// <summary>The transport-layer connection-status registry fed by inbound reverse registrations.</summary>
    public ReverseConnectionStatusRegistry ConnectionStatusRegistry => this.statusRegistry;

    internal ReverseHttpServerTransportFactory ReverseHttpServerTransportFactory
        => this.reverseHttpServerTransportFactory;

    public Exception? LastReachabilityPublicationError { get; private set; }

    /// <summary>Whether the web server is currently running.</summary>
    public bool IsRunning => this.application is not null && this.runTask is not null;

    /// <summary>The listen URL the server is bound to (null if not running).</summary>
    public string? ListenUrl { get; private set; }

    public IReadOnlyList<string> ListenUrls { get; private set; } = [];

    /// <summary>Test-only: the running application's endpoint route patterns.</summary>
    internal IReadOnlyList<string> GetMappedRoutePatterns()
    {
        if (this.application is null)
        {
            return [];
        }

        return ((IEndpointRouteBuilder)this.application).DataSources
            .SelectMany(static ds => ds.Endpoints)
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Select(static e => e.RoutePattern.RawText ?? string.Empty)
            .ToArray();
    }

    /// <summary>Test-only: the currently mapped HttpServerTransportFactory (null when not running).</summary>
    internal HttpServerTransportFactory? HttpServerTransportFactory => this.httpServerTransportFactory;

    /// <summary>Test-only: true after StopAsync has disposed the HttpServerTransportFactory it created.</summary>
    internal bool HttpServerTransportFactoryWasDisposed => this.httpServerTransportFactoryDisposed;

    /// <summary>
    /// Starts the web server using the supplied configuration and data-access layer. Does nothing
    /// if hosting is not enabled or the server is already running.
    /// </summary>
    public async Task StartAsync(
        RemoteHostingSettings remoteHostingSettings,
        IDataAccessLayer dataAccessLayer,
        CancellationToken cancellationToken = default)
        => await this.StartAsync(remoteHostingSettings, dataAccessLayer, logDirectoryProvider: null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Starts the web server using the supplied configuration and data-access layer, registering the
    /// #1086 rolling file logging provider against the single <paramref name="logDirectoryProvider"/>
    /// directory (handed in from the config-resolved path — the host never computes its own). Does
    /// nothing if hosting is not enabled or the server is already running.
    /// </summary>
    public async Task StartAsync(
        RemoteHostingSettings remoteHostingSettings,
        IDataAccessLayer dataAccessLayer,
        ILogDirectoryProvider? logDirectoryProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remoteHostingSettings);
        ArgumentNullException.ThrowIfNull(dataAccessLayer);

        if (!remoteHostingSettings.Enabled || this.IsRunning)
        {
            return;
        }

        this.cancellationTokenSource = new CancellationTokenSource();
        var builder = WebApplication.CreateBuilder();

        if (logDirectoryProvider is not null)
        {
            builder.Logging.AddProvider(new RollingFileLoggerProvider(
                logDirectoryProvider.LogDirectory,
                LoggingBootstrap.DefaultRetention));
        }

        builder.Services.AddSingleton(dataAccessLayer);
        builder.WebHost.UseUrls(remoteHostingSettings.ListenUrls.ToArray());

        this.application = builder.Build();
        this.application.UseWebSockets();
        this.application.MapGet("/", () => $"Phantom.Workspaces ({typeof(WorkspacesWebHost).Namespace})");
        this.application.MapWebDataAccessEndpoints();
        this.application.MapAgentEndpoints();
        // #1209: expose the raw HTTP transport endpoint (/transport/connect) that reverse-HTTP
        // clients bootstrap against. Backed by a TransportRegistry that lists the reverse-HTTP
        // server factory so `reverse-register` and `reverse-http` channel-opens dispatch through
        // the same status registry. Mirrors Phantom.Workspaces.Web.Server/Program.cs.
        var transportRegistry = new TransportRegistry();
        transportRegistry.Register(this.reverseHttpServerTransportFactory);
        this.httpServerTransportFactory = new HttpServerTransportFactory(transportRegistry);
        this.httpServerTransportFactory.Map(this.application);
        this.httpServerTransportFactoryDisposed = false;

        this.application.MapTransportReverseEndpoints(this.reverseHttpServerTransportFactory, this.statusRegistry);

        var lifetime = this.application.Services.GetRequiredService<IHostApplicationLifetime>();
        this.runTask = this.application.RunAsync();
        var startedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationRegistration = cancellationToken.Register(
            () => startedTcs.TrySetCanceled(cancellationToken));
        using var startedRegistration = lifetime.ApplicationStarted.Register(
            () => startedTcs.TrySetResult());
        await startedTcs.Task.ConfigureAwait(false);

        var boundAddresses = this.application.Services
            .GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses;
        this.ListenUrls = boundAddresses is { Count: > 0 }
            ? boundAddresses.ToList()
            : remoteHostingSettings.ListenUrls.ToList();
        this.ListenUrl = this.ListenUrls.Count > 0
            ? this.ListenUrls[0]
            : remoteHostingSettings.PrimaryListenUrl;
        await this.SetPublishedEndpointAsync(this.ListenUrl, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetPublishedEndpointAsync(
        string? endpoint,
        CancellationToken cancellationToken = default)
    {
        await this.directRouteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.directRouteLease is not null)
            {
                await this.directRouteLease.DisposeAsync().ConfigureAwait(false);
                this.directRouteLease = null;
            }

            if (!this.IsRunning || string.IsNullOrWhiteSpace(endpoint))
            {
                return;
            }

            await this.StartDirectRoutePublicationAsync(endpoint, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.directRouteGate.Release();
        }
    }

    internal void ReportPublicationError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        this.OnPublicationStatusChanged(exception);
    }

    /// <summary>
    /// Stops the web server if it is running. Does nothing if the server is not running.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!this.IsRunning || this.application is null)
        {
            return;
        }

        await this.SetPublishedEndpointAsync(null, cancellationToken).ConfigureAwait(false);

        await this.application.StopAsync(cancellationToken).ConfigureAwait(false);
        if (this.runTask is not null)
        {
            try
            {
                await this.runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (this.httpServerTransportFactory is not null)
        {
            await this.httpServerTransportFactory.DisposeAsync().ConfigureAwait(false);
            this.httpServerTransportFactoryDisposed = true;
            this.httpServerTransportFactory = null;
        }

        this.application = null;
        this.runTask = null;
        this.ListenUrl = null;
        this.ListenUrls = [];
        this.cancellationTokenSource?.Dispose();
        this.cancellationTokenSource = null;
    }

    public async ValueTask DisposeAsync()
    {
        await this.StopAsync().ConfigureAwait(false);
        if (this.ownsReverseHttpServerTransportFactory)
        {
            await this.reverseHttpServerTransportFactory.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task StartDirectRoutePublicationAsync(
        string endpointUrl,
        CancellationToken cancellationToken)
    {
        if (this.reachabilityRouteStore is null
            || this.localProfileEntityId is null)
        {
            return;
        }

        string endpoint;
        try
        {
            endpoint = DataAccessReachabilityRouteStore.NormalizeEndpoint(endpointUrl);
        }
        catch (ArgumentException exception)
        {
            this.OnPublicationStatusChanged(exception);
            return;
        }

        this.directRouteLease = new ReachabilityRouteLease(
            this.reachabilityRouteStore,
            this.localProfileEntityId.Value,
            "direct-http",
            () => System.Text.Json.JsonSerializer.SerializeToElement(
                new Dictionary<string, object>
                {
                    ["type"] = "http",
                    ["url"] = endpoint,
                }),
            priority: 50,
            this.timeProvider,
            TimeSpan.FromMinutes(2),
            this.OnPublicationStatusChanged);
        await this.directRouteLease.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    private void OnPublicationStatusChanged(Exception? exception)
    {
        this.LastReachabilityPublicationError = exception;
        if (exception is not null)
        {
            this.logger.LogError(
                exception,
                "The direct HTTP listener is ready, but its reachability route could not be persisted.");
        }
    }
}
