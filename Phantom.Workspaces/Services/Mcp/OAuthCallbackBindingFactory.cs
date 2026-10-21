using System.Net;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;

namespace Phantom.Workspaces.Services.Mcp;

internal sealed class OAuthCallbackBindingFactory
{
    internal async ValueTask<OAuthCallbackBinding> BindAsync(
        IProviderAcquisitionOperation operation,
        ProviderInteractionPermit executionProof,
        CancellationToken cancellationToken)
    {
        bool IsAuthorized() => executionProof is not null &&
            operation.ExecutorKind is CredentialExecutorKind.OAuth or CredentialExecutorKind.McpOAuth &&
            operation.CompatibilityKey.Protocol == ProviderProtocolKind.OAuth &&
            executionProof.AuthorizesProvider(operation.ExecutorKind, operation.CompatibilityKey) &&
            executionProof.Authorizes(operation.RequestIdentifier, operation.Revision, operation.ExecutorKind,
                operation.AcquisitionGeneration, operation.CompatibilityKey);
        if (!IsAuthorized()) { throw new InvalidOperationException("OAuth callback binding is not authorized."); }
        var binding = await OAuthCallbackBinding.StartAsync(cancellationToken).ConfigureAwait(false);
        if (!IsAuthorized() || cancellationToken.IsCancellationRequested)
        {
            await binding.DisposeAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("OAuth callback binding is no longer authorized.");
        }
        return binding;
    }
}

internal sealed class OAuthCallbackBinding : IAsyncDisposable
{
    private readonly WebApplication application;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<OAuthCallbackRequest> requests = Channel.CreateBounded<OAuthCallbackRequest>(
        new BoundedChannelOptions(1) { AllowSynchronousContinuations = false, SingleReader = true });
    private readonly object gate = new();
    private Task stopping = Task.CompletedTask;
    private Task? disposal;
    private CancellationTokenRegistration cancellationRegistration;
    private bool closed;
    private Uri? redirectUri;

    private OAuthCallbackBinding(WebApplication application) => this.application = application;

    internal Uri RedirectUri => this.redirectUri ?? throw new InvalidOperationException("OAuth callback binding is not ready.");
    internal bool IsListening { get { lock (this.gate) { return !this.closed && this.redirectUri is not null; } } }

    internal static async ValueTask<OAuthCallbackBinding> StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0);
            options.Limits.MaxRequestBodySize = 0;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
        });
        var application = builder.Build();
        var binding = new OAuthCallbackBinding(application);
        application.Run(binding.HandleAsync);
        try
        {
            await application.StartAsync(cancellationToken).ConfigureAwait(false);
            var addresses = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
            var address = addresses?.SingleOrDefault() ?? throw new InvalidOperationException("OAuth callback binding is unavailable.");
            binding.redirectUri = new Uri(new Uri(address), "/oauth/callback");
            binding.cancellationRegistration = cancellationToken.Register(binding.Close);
            cancellationToken.ThrowIfCancellationRequested();
            return binding;
        }
        catch
        {
            await binding.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal ValueTask<OAuthCallbackRequest> ReceiveAsync() => this.requests.Reader.ReadAsync(this.lifetime.Token);

    internal void Close()
    {
        lock (this.gate)
        {
            if (this.closed) { return; }
            this.closed = true;
            this.lifetime.Cancel();
            this.requests.Writer.TryComplete();
            this.stopping = this.application.StopAsync();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (this.gate) { return new ValueTask(this.disposal ??= this.DisposeCoreAsync()); }
    }

    private async Task DisposeCoreAsync()
    {
        this.Close();
        try { await this.stopping.ConfigureAwait(false); }
        finally
        {
            await this.application.DisposeAsync().ConfigureAwait(false);
            await this.cancellationRegistration.DisposeAsync().ConfigureAwait(false);
            this.lifetime.Dispose();
        }
    }

    private async Task HandleAsync(HttpContext context)
    {
        if (context.Request.Method != HttpMethods.Get)
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }
        var callback = new UriBuilder(this.RedirectUri)
        {
            Path = context.Request.Path.Value,
            Query = context.Request.QueryString.Value,
        }.Uri;
        var request = new OAuthCallbackRequest(callback);
        if (!this.requests.Writer.TryWrite(request))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, this.lifetime.Token);
        try
        {
            context.Response.StatusCode = await request.Response.Task.WaitAsync(cancellation.Token).ConfigureAwait(false);
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync("<html><body>You may close this window.</body></html>", cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            context.Abort();
        }
        finally
        {
            request.Sent.TrySetResult();
        }
    }
}

internal sealed class OAuthCallbackRequest(Uri uri)
{
    internal Uri Uri { get; } = uri;
    internal TaskCompletionSource<int> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task CompleteAsync(bool matched)
    {
        this.Response.TrySetResult(matched ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
        return this.Sent.Task;
    }
}
