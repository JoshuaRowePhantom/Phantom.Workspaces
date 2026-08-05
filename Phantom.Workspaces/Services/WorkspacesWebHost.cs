using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
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
    private WebApplication? application;
    private Task? runTask;
    private CancellationTokenSource? cancellationTokenSource;
    private HttpServerTransportFactory? httpServerTransportFactory;
    private bool httpServerTransportFactoryDisposed;

    public WorkspacesWebHost(ReverseConnectionStatusRegistry statusRegistry)
    {
        this.statusRegistry = statusRegistry ?? throw new ArgumentNullException(nameof(statusRegistry));
    }

    /// <summary>The transport-layer connection-status registry fed by inbound reverse registrations.</summary>
    public ReverseConnectionStatusRegistry ConnectionStatusRegistry => this.statusRegistry;

    /// <summary>Whether the web server is currently running.</summary>
    public bool IsRunning => this.application is not null && this.runTask is not null;

    /// <summary>The listen URL the server is bound to (null if not running).</summary>
    public string? ListenUrl { get; private set; }

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
        var builder = WebApplication.CreateBuilder(["--urls", remoteHostingSettings.ListenUrl]);

        if (logDirectoryProvider is not null)
        {
            builder.Logging.AddProvider(new RollingFileLoggerProvider(
                logDirectoryProvider.LogDirectory,
                LoggingBootstrap.DefaultRetention));
        }

        builder.Services.AddSingleton(dataAccessLayer);

        this.application = builder.Build();
        this.application.UseWebSockets();
        this.application.MapGet("/", () => $"Phantom.Workspaces ({typeof(WorkspacesWebHost).Namespace})");
        this.application.MapWebDataAccessEndpoints();
        this.application.MapAgentEndpoints();
        var serverTransportFactory = new ReverseHttpServerTransportFactory(this.statusRegistry);

        // #1209: expose the raw HTTP transport endpoint (/transport/connect) that reverse-HTTP
        // clients bootstrap against. Backed by a TransportRegistry that lists the reverse-HTTP
        // server factory so `reverse-register` and `reverse-http` channel-opens dispatch through
        // the same status registry. Mirrors Phantom.Workspaces.Web.Server/Program.cs.
        var transportRegistry = new TransportRegistry();
        transportRegistry.Register(serverTransportFactory);
        this.httpServerTransportFactory = new HttpServerTransportFactory(transportRegistry);
        this.httpServerTransportFactory.Map(this.application);
        this.httpServerTransportFactoryDisposed = false;

        this.application.MapTransportReverseEndpoints(serverTransportFactory, this.statusRegistry);

        this.ListenUrl = remoteHostingSettings.ListenUrl;
        this.runTask = this.application.RunAsync();
        await Task.Yield();
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
        this.cancellationTokenSource?.Dispose();
        this.cancellationTokenSource = null;
    }

    public async ValueTask DisposeAsync()
    {
        await this.StopAsync().ConfigureAwait(false);
    }
}
