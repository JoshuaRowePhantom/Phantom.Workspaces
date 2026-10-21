using System.ComponentModel;
using System.Security.Cryptography;
using System.Threading.Channels;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Llm.Mcp;

namespace Phantom.Workspaces.Services.Mcp;

internal sealed class McpOAuthAttempt : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly OAuthCallbackBinding binding;
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
    private TaskCompletionSource? disposal;

    internal McpOAuthAttempt(
        ISystemBrowserLauncher browserLauncher,
        Uri authorizationUri,
        OAuthCallbackBinding binding,
        IProviderAcquisitionOperation operation,
        Action<McpOAuthAttempt>? onClosed = null)
    {
        var redirectUri = binding.RedirectUri;
        if (!binding.Authorizes(operation) || !redirectUri.IsLoopback || redirectUri.Scheme != Uri.UriSchemeHttp ||
            !string.IsNullOrEmpty(redirectUri.Query) || !string.IsNullOrEmpty(redirectUri.Fragment))
        {
            throw new InvalidOperationException("OAuth requires a local HTTP callback.");
        }

        this.browserLauncher = browserLauncher;
        this.binding = binding;
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
        this.acceptLoop = this.AcceptAsync();
    }

    public ValueTask Ready => ValueTask.CompletedTask;

    internal bool IsListenerBound
    {
        get
        {
            lock (this.gate) { if (this.closed) { return false; } }
            return this.binding.IsListening;
        }
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

            this.executing = true;
        }

        try
        {
            if (permit is null ||
                !this.binding.IsActive ||
                !permit.AuthorizesProvider(this.executorKind, this.acquisitionKey) ||
                !permit.Authorizes(this.requestIdentifier, this.revision, this.executorKind,
                    this.acquisitionGeneration, this.acquisitionKey))
            {
                throw new InvalidOperationException("OAuth interaction is not authorized.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                this.browserLauncher.Open(this.authorizationUri);
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                throw new InvalidOperationException("OAuth browser launch failed.");
            }
            return await this.completion.Task.ConfigureAwait(false);
        }
        finally
        {
            try { this.Close(); }
            finally { await this.binding.DisposeAsync().ConfigureAwait(false); }
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? leader = null;
        Task task;
        lock (this.gate)
        {
            if (this.disposal is null)
            {
                this.disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
                leader = this.disposal;
            }
            task = this.disposal.Task;
        }
        if (leader is not null) { _ = this.DisposeCoreAsync(leader); }
        return new ValueTask(task);
    }

    private async Task DisposeCoreAsync(TaskCompletionSource completion)
    {
        try
        {
            try
            {
                this.Close();
                await this.acceptLoop.ConfigureAwait(false);
            }
            finally { await this.binding.DisposeAsync().ConfigureAwait(false); }
            completion.TrySetResult();
        }
        catch (Exception exception) { completion.TrySetException(exception); }
    }

    private async Task AcceptAsync()
    {
        while (true)
        {
            OAuthCallbackRequest request;
            try
            {
                request = await this.binding.ReceiveAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ChannelClosedException or ObjectDisposedException)
            {
                this.Close();
                return;
            }

            var callback = request.Uri;
            var matched =
                callback.AbsolutePath == this.redirectUri.AbsolutePath &&
                QueryValue(callback, "state") == this.state;
            await request.CompleteAsync(matched).ConfigureAwait(false);

            if (!this.binding.IsActive)
            {
                this.Close();
                return;
            }
            var completed = false;
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

                    completed = true;
                }
            }
            if (completed) { this.Close(); return; }
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
        }
        this.binding.Close();
        this.onClosed?.Invoke(this);
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
