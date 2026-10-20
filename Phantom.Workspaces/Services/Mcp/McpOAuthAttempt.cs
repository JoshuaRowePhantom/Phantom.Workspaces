using System.ComponentModel;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Llm.Mcp;

namespace Phantom.Workspaces.Services.Mcp;

internal sealed class McpOAuthAttempt : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly HttpListener listener = new();
    private readonly ISystemBrowserLauncher browserLauncher;
    private readonly Uri authorizationUri;
    private readonly Uri redirectUri;
    private readonly string state;
    private readonly UserAuthorizationRequestIdentifier requestIdentifier;
    private readonly long revision;
    private readonly Guid acquisitionGeneration;
    private readonly ProviderAcquisitionKey acquisitionKey;
    private readonly CredentialExecutorKind executorKind;
    private readonly Action<McpOAuthAttempt>? onClosed;
    private readonly TaskCompletionSource<Uri> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task acceptLoop;
    private bool executing;
    private bool closed;

    internal McpOAuthAttempt(
        ISystemBrowserLauncher browserLauncher,
        Uri authorizationUri,
        Uri redirectUri,
        IProviderAcquisitionOperation operation,
        Action<McpOAuthAttempt>? onClosed = null)
    {
        if (!redirectUri.IsLoopback || redirectUri.Scheme != Uri.UriSchemeHttp ||
            !string.IsNullOrEmpty(redirectUri.Query) || !string.IsNullOrEmpty(redirectUri.Fragment))
        {
            throw new InvalidOperationException("OAuth requires a local HTTP callback.");
        }

        this.browserLauncher = browserLauncher;
        this.redirectUri = redirectUri;
        this.requestIdentifier = operation.RequestIdentifier;
        this.revision = operation.Revision;
        this.acquisitionGeneration = operation.AcquisitionGeneration;
        this.acquisitionKey = operation.CompatibilityKey;
        this.onClosed = onClosed;
        this.executorKind = operation.ExecutorKind;
        if (this.executorKind is not (CredentialExecutorKind.McpOAuth or CredentialExecutorKind.OAuth) ||
            this.acquisitionKey.Protocol != ProviderProtocolKind.OAuth)
        {
            throw new InvalidOperationException("OAuth attempt requires an OAuth acquisition.");
        }

        this.state = QueryValue(authorizationUri, "state") ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        this.authorizationUri = QueryValue(authorizationUri, "state") is null
            ? new UriBuilder(authorizationUri)
            {
                Query = authorizationUri.Query.TrimStart('?') +
                    (authorizationUri.Query.Length == 0 ? "" : "&") + "state=" + Uri.EscapeDataString(this.state),
            }.Uri
            : authorizationUri;
        this.listener.Prefixes.Add(redirectUri.GetLeftPart(UriPartial.Authority) + "/");
        try
        {
            this.listener.Start();
        }
        catch (HttpListenerException)
        {
            this.listener.Close();
            throw new InvalidOperationException("OAuth callback listener could not start.");
        }

        this.acceptLoop = this.AcceptAsync();
    }

    public ValueTask Ready => ValueTask.CompletedTask;

    internal bool IsListenerBound
    {
        get { lock (this.gate) { return !this.closed && this.listener.IsListening; } }
    }

    public async Task<Uri> ExecuteAsync(
        ProviderInteractionPermit permit,
        CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(this.Close);
        lock (this.gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (this.closed || this.executing)
            {
                throw new InvalidOperationException("OAuth attempt is no longer available.");
            }

            if (permit is null ||
                !permit.AuthorizesProvider(this.executorKind, this.acquisitionKey) ||
                !permit.Authorizes(this.requestIdentifier, this.revision, this.executorKind,
                    this.acquisitionGeneration, this.acquisitionKey))
            {
                this.Close();
                throw new InvalidOperationException("OAuth interaction is not authorized.");
            }

            this.executing = true;
            try
            {
                this.browserLauncher.Open(this.authorizationUri);
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                this.Close();
                throw new InvalidOperationException("OAuth browser launch failed.");
            }
        }

        try
        {
            return await this.completion.Task.ConfigureAwait(false);
        }
        finally
        {
            this.Close();
        }
    }

    public async ValueTask DisposeAsync()
    {
        this.Close();
        await this.acceptLoop.ConfigureAwait(false);
    }

    private async Task AcceptAsync()
    {
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await this.listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
            {
                this.Close();
                return;
            }

            var callback = context.Request.Url;
            var matched = callback is not null &&
                callback.AbsolutePath == this.redirectUri.AbsolutePath &&
                QueryValue(callback, "state") == this.state;
            try
            {
                context.Response.StatusCode = matched ? 200 : 400;
                context.Response.ContentType = "text/html; charset=utf-8";
                var payload = Encoding.UTF8.GetBytes("<html><body>You may close this window.</body></html>");
                context.Response.ContentLength64 = payload.Length;
                await context.Response.OutputStream.WriteAsync(payload).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpListenerException or IOException or ObjectDisposedException)
            {
                // A disconnected browser cannot change the private protocol result.
            }
            finally
            {
                context.Response.Close();
            }

            lock (this.gate)
            {
                if (!this.closed && this.executing && matched)
                {
                    if (QueryValue(callback!, "error") is not null || string.IsNullOrEmpty(QueryValue(callback!, "code")))
                    {
                        this.completion.TrySetException(new InvalidOperationException("OAuth authorization failed."));
                    }
                    else
                    {
                        this.completion.TrySetResult(callback!);
                    }

                    this.Close();
                    return;
                }
            }
        }
    }

    private void Close()
    {
        lock (this.gate)
        {
            if (this.closed)
            {
                return;
            }

            this.closed = true;
            this.completion.TrySetCanceled();
            this.listener.Close();
            this.onClosed?.Invoke(this);
        }
    }

    private static string? QueryValue(
        Uri uri,
        string name)
    {
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (Uri.UnescapeDataString(pair[0]) == name)
            {
                return pair.Length == 2 ? Uri.UnescapeDataString(pair[1].Replace("+", " ")) : "";
            }
        }

        return null;
    }
}
