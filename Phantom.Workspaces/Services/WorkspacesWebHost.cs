using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Web.Server;
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.Web.Server;

namespace Phantom.Workspaces.Services;

/// <summary>
/// Manages the ASP.NET Core web server lifecycle for the Phantom.Workspaces GUI application.
/// When <see cref="RemoteHostingSettings.Enabled"/> is true, hosts the web data-access and
/// agent execution endpoints (including reverse-execution) on the configured listen URL.
/// </summary>
public sealed class WorkspacesWebHost : IAsyncDisposable
{
    private readonly ReverseExecutionRegistry reverseExecutionRegistry;
    private WebApplication? application;
    private Task? runTask;
    private CancellationTokenSource? cancellationTokenSource;

    public WorkspacesWebHost(ReverseExecutionRegistry reverseExecutionRegistry)
    {
        this.reverseExecutionRegistry = reverseExecutionRegistry ?? throw new ArgumentNullException(nameof(reverseExecutionRegistry));
    }

    /// <summary>The reverse-execution registry (always available; inbound connections only work when hosting is enabled).</summary>
    public ReverseExecutionRegistry ReverseExecutionRegistry => this.reverseExecutionRegistry;

    /// <summary>Whether the web server is currently running.</summary>
    public bool IsRunning => this.application is not null && this.runTask is not null;

    /// <summary>The listen URL the server is bound to (null if not running).</summary>
    public string? ListenUrl { get; private set; }

    /// <summary>
    /// Starts the web server using the supplied configuration and data-access layer. Does nothing
    /// if hosting is not enabled or the server is already running.
    /// </summary>
    public async Task StartAsync(
        RemoteHostingSettings remoteHostingSettings,
        IDataAccessLayer dataAccessLayer,
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

        builder.Services.AddSingleton(dataAccessLayer);
        builder.Services.AddSingleton(this.reverseExecutionRegistry);

        this.application = builder.Build();
        this.application.UseWebSockets();
        this.application.MapGet("/", () => $"Phantom.Workspaces ({typeof(WorkspacesWebHost).Namespace})");
        this.application.MapWebDataAccessEndpoints();
        this.application.MapAgentEndpoints();
        this.application.MapReverseEndpoints(this.reverseExecutionRegistry);

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
