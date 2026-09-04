using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Authentication;
using Phantom.Workspaces.Llm.Secrets;

namespace Phantom.Workspaces.Services.Mcp;

/// <summary>
/// Host implementation of the MCP SDK's interactive OAuth <see cref="AuthorizationRedirectDelegate"/>
/// (sub-item #1385). It gates the first interactive authorization of each MCP server behind a user
/// consent prompt (reusing <see cref="ISecretProvider"/>), opens the system browser at the
/// authorization URL, captures the authorization-code redirect on a loopback
/// <see cref="HttpListener"/>, and returns the captured redirect URI. The delegate registered into the
/// #1382 <c>McpOAuthOptions.RedirectDelegateProvider</c> seam adapts that URI into the authorization
/// <c>code</c> string the SDK expects.
/// </summary>
/// <remarks>
/// <para>
/// Consent is remembered per MCP server for the process session (the in-process
/// <see cref="consentedServers"/> fast-path) so silent token refreshes do not re-prompt the user.
/// The consent request also carries scope memories keyed on <c>McpOAuth:{server}</c>, so an accepted
/// non-<see cref="SecretUseScope.AlwaysAsk"/> scope is persisted by the consent provider
/// (<see cref="ISecretProvider"/>) through the same allowed-secrets store as the <c>${SECRET:}</c>
/// path and matched again on a fresh handler/process, suppressing re-prompting after restart. Only the
/// GUI/desktop host wires this handler; headless hosts keep the failing "interactive OAuth is not
/// configured" default from #1382.
/// </para>
/// <para>
/// A <b>single</b> shared loopback <see cref="HttpListener"/> is bound exactly once (issue #1425) and
/// kept alive for the process lifetime. Every MCP server reuses it — there is exactly one
/// <see cref="HttpListener.Start"/> per process, so overlapping/concurrent sign-ins never collide on
/// an identical prefix. A single accept loop demultiplexes each inbound callback to the correct pending
/// authorization using the OAuth <c>state</c> query parameter. When the authorization request already
/// carries a <c>state</c> (the SDK-generated common case), it is used as-is; when it omits one (RFC 6749
/// §4.1.1 makes <c>state</c> OPTIONAL — e.g. servers like IcM), a URL-safe random <c>state</c> is
/// synthesized and injected into the opened authorization URI, and the server echoes it back on the
/// redirect (§4.1.2). Because the listener is bound continuously
/// from first use, the port is never reserved-then-freed, removing the earlier TOCTOU window.
/// </para>
/// </remarks>
public sealed class McpOAuthRedirectHandler : IDisposable
{
    /// <summary>Overall time the loopback listener waits for the browser redirect before failing.</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private const string CloseWindowHtml =
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Sign-in complete</title></head>" +
        "<body><p>You can close this window and return to Phantom.Workspaces.</p></body></html>";

    private readonly ISystemBrowserLauncher browserLauncher;
    private readonly ISecretProvider consentProvider;
    private readonly AgentManifestSecretUseMemoryFactory memoryFactory;
    private readonly Func<string?>? sessionIdentityProvider;
    private readonly TimeSpan timeout;
    private readonly ILogger logger;
    private readonly object gate = new();
    private readonly HashSet<string> consentedServers = new(StringComparer.OrdinalIgnoreCase);

    // Shared loopback listener (issue #1425): bound once, held for the process lifetime, reused by every
    // server. `pending` maps each authorization's OAuth `state` to its waiter so the single accept loop
    // can route concurrent callbacks to the right sign-in.
    private readonly ConcurrentDictionary<string, PendingAuthorization> pending =
        new(StringComparer.Ordinal);
    private HttpListener? sharedListener;
    private Uri? boundRedirectUri;
    private int listenerStartCount;

    public McpOAuthRedirectHandler(
        ISystemBrowserLauncher browserLauncher,
        ISecretProvider consentProvider,
        ILogger<McpOAuthRedirectHandler>? logger = null,
        AgentManifestSecretUseMemoryFactory? memoryFactory = null,
        Func<string?>? sessionIdentityProvider = null)
        : this(browserLauncher, consentProvider, DefaultTimeout, logger, memoryFactory, sessionIdentityProvider)
    {
    }

    internal McpOAuthRedirectHandler(
        ISystemBrowserLauncher browserLauncher,
        ISecretProvider consentProvider,
        TimeSpan timeout,
        ILogger<McpOAuthRedirectHandler>? logger = null,
        AgentManifestSecretUseMemoryFactory? memoryFactory = null,
        Func<string?>? sessionIdentityProvider = null)
    {
        this.browserLauncher = browserLauncher ?? throw new ArgumentNullException(nameof(browserLauncher));
        this.consentProvider = consentProvider ?? throw new ArgumentNullException(nameof(consentProvider));
        this.memoryFactory = memoryFactory ?? new AgentManifestSecretUseMemoryFactory();
        this.sessionIdentityProvider = sessionIdentityProvider;
        this.timeout = timeout;
        this.logger = logger ?? (ILogger)NullLogger<McpOAuthRedirectHandler>.Instance;
    }

    /// <summary>Number of times the shared listener has been started. Exactly one per process.</summary>
    internal int ListenerStartCount
    {
        get { lock (this.gate) { return this.listenerStartCount; } }
    }

    /// <summary>True while the shared loopback listener is bound and listening.</summary>
    internal bool IsListenerBound
    {
        get { lock (this.gate) { return this.sharedListener is { IsListening: true }; } }
    }

    /// <summary>Count of in-flight authorizations still awaiting their loopback callback.</summary>
    internal int PendingCount => this.pending.Count;

    /// <summary>
    /// Factory matching the <c>Func&lt;string, AuthorizationRedirectDelegate&gt;</c> shape of
    /// <c>McpOAuthOptions.RedirectDelegateProvider</c>. The returned delegate runs the interactive flow
    /// and yields the authorization <c>code</c> the SDK consumes.
    /// </summary>
    public AuthorizationRedirectDelegate CreateRedirectDelegate(string serverName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        return async (authorizationUri, redirectUri, cancellationToken) =>
        {
            var captured = await this.HandleAsync(serverName, authorizationUri, redirectUri, cancellationToken)
                .ConfigureAwait(false);
            return GetQueryValue(captured, "code");
        };
    }

    /// <summary>
    /// Binds the single shared loopback <see cref="HttpListener"/> to an OS-assigned loopback port (if it
    /// is not already bound) and returns its <c>http://localhost:&lt;port&gt;/</c> redirect URI. Used by
    /// the composition root so <c>McpOAuthOptions.RedirectUri</c> is derived from the continuously-held
    /// listener rather than a reserve-then-free port (issue #1425).
    /// </summary>
    internal Uri EnsureListenerBound() => this.EnsureListener(preferredRedirectUri: null);

    /// <summary>
    /// Runs the interactive authorization flow and returns the full loopback redirect URI (its query
    /// carries <c>code</c>+<c>state</c>). Throws when consent is declined, when the redirect carries an
    /// <c>error</c>, or when the wait is cancelled or times out.
    /// </summary>
    public async Task<Uri> HandleAsync(
        string serverName,
        Uri authorizationUri,
        Uri redirectUri,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentNullException.ThrowIfNull(authorizationUri);
        ArgumentNullException.ThrowIfNull(redirectUri);

        await this.EnsureConsentAsync(serverName, cancellationToken).ConfigureAwait(false);

        // Reuse the single shared listener — never a second Start(). The first sign-in in the process
        // binds it; every subsequent server (concurrent or overlapping) demultiplexes on the same prefix.
        this.EnsureListener(redirectUri);

        // `state` uniquely identifies this authorization on the shared listener and must round-trip
        // through the browser redirect. When the SDK/authorization-server already supplied one, use it
        // as-is. When it is absent (RFC 6749 §4.1.1 makes `state` OPTIONAL, e.g. the IcM server), the
        // client synthesizes a URL-safe random `state` and injects it into the authorization request we
        // open; the server echoes it verbatim (§4.1.2), and the SDK — which never set `state` — only ever
        // sees the returned `code`. Register the waiter BEFORE opening the browser so a fast callback is
        // not lost.
        var state = GetQueryValue(authorizationUri, "state");
        var effectiveAuthorizationUri = authorizationUri;
        if (state is null)
        {
            state = GenerateState();
            effectiveAuthorizationUri = AppendQueryParameter(authorizationUri, "state", state);
        }

        var authorization = this.pending.GetOrAdd(
            state,
            _ => new PendingAuthorization(
                serverName,
                new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously)));
        var waiter = authorization.Completion;

        using var timeoutSource = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutSource.Token);
        if (this.timeout != Timeout.InfiniteTimeSpan)
        {
            timeoutSource.CancelAfter(this.timeout);
        }

        this.browserLauncher.Open(effectiveAuthorizationUri);

        using (linked.Token.Register(() => waiter.TrySetCanceled(linked.Token)))
        {
            try
            {
                return await waiter.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    this.logger.LogWarning(
                        "MCP OAuth sign-in for server '{ServerName}' was cancelled.", serverName);
                    throw new OperationCanceledException(
                        $"MCP OAuth sign-in for server '{serverName}' was cancelled.", cancellationToken);
                }

                this.logger.LogWarning(
                    "Timed out waiting for the OAuth redirect from MCP server '{ServerName}'.", serverName);
                throw new TimeoutException(
                    $"Timed out waiting for the OAuth redirect from MCP server '{serverName}'.");
            }
            finally
            {
                // A timed-out/cancelled/completed sign-in removes only its own entry; the shared listener
                // stays alive for the other servers still using it (issue #1425).
                this.pending.TryRemove(state, out _);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        HttpListener? listener;
        lock (this.gate)
        {
            listener = this.sharedListener;
            this.sharedListener = null;
        }

        if (listener is not null)
        {
            try
            {
                if (listener.IsListening)
                {
                    listener.Stop();
                }
            }
            catch (ObjectDisposedException)
            {
            }

            ((IDisposable)listener).Dispose();
        }

        foreach (var entry in this.pending.Values)
        {
            entry.Completion.TrySetCanceled();
        }
    }

    /// <summary>
    /// Idempotently binds the single shared loopback listener. When <paramref name="preferredRedirectUri"/>
    /// is supplied its loopback port is used; otherwise an OS-assigned ephemeral port is chosen and held.
    /// Exactly one <see cref="HttpListener.Start"/> occurs per process; later calls return the URI of the
    /// already-bound listener.
    /// </summary>
    private Uri EnsureListener(Uri? preferredRedirectUri)
    {
        lock (this.gate)
        {
            if (this.sharedListener is { IsListening: true })
            {
                return this.boundRedirectUri!;
            }

            var (listener, uri) = BindLoopbackListener(preferredRedirectUri);
            this.sharedListener = listener;
            this.boundRedirectUri = uri;
            this.listenerStartCount++;

            // Fire-and-forget accept loop: its first `GetContextAsync` yields immediately, so starting it
            // inside the lock does not block. It runs for the lifetime of the listener.
            _ = this.AcceptLoopAsync(listener);
            return uri;
        }
    }

    /// <summary>
    /// Creates and starts a loopback <see cref="HttpListener"/>. When a redirect URI is provided the
    /// listener binds to its port; otherwise a free ephemeral port is reserved and the listener is bound
    /// to it immediately (and held), so the port is never released between reservation and use.
    /// </summary>
    private static (HttpListener Listener, Uri RedirectUri) BindLoopbackListener(Uri? preferredRedirectUri)
    {
        if (preferredRedirectUri is not null)
        {
            var prefix = NormalizeLoopbackPrefix(preferredRedirectUri);
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            listener.Start();
            return (listener, new Uri(prefix));
        }

        const int maxAttempts = 16;
        for (var attempt = 1; ; attempt++)
        {
            var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            int port;
            try
            {
                port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            }
            finally
            {
                reservation.Stop();
            }

            var prefix = $"http://localhost:{port}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            try
            {
                listener.Start();
                return (listener, new Uri(prefix));
            }
            catch (HttpListenerException) when (attempt < maxAttempts)
            {
                // The reserved ephemeral port was taken in the narrow window before bind; try another.
                ((IDisposable)listener).Dispose();
            }
        }
    }

    /// <summary>
    /// Single accept loop for the shared listener. Writes the close page for every callback, then routes
    /// the result to the pending authorization matching the callback's OAuth <c>state</c>.
    /// </summary>
    private async Task AcceptLoopAsync(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (!listener.IsListening)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            var requestUri = context.Request.Url;

            try
            {
                await WriteClosePageAsync(context.Response).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best-effort close page; still route the result below.
            }

            if (requestUri is null)
            {
                continue;
            }

            var state = GetQueryValue(requestUri, "state");
            if (state is null || !this.pending.TryGetValue(state, out var authorization))
            {
                // Unknown/duplicate state: page already shown; drop it without faulting other waiters.
                continue;
            }

            var error = GetQueryValue(requestUri, "error");
            if (!string.IsNullOrEmpty(error))
            {
                var errorDescription = GetQueryValue(requestUri, "error_description");

                // Log ONLY the OAuth error/error_description codes. The full redirect URI must never be
                // logged: its query carries the authorization `code`/`state` (issue #1408).
                this.logger.LogError(
                    "MCP OAuth redirect for '{ServerName}' returned error {Error}.",
                    authorization.ServerName,
                    error);

                // Carry the decoded error/error_description into the thrown exception (via Data) so the
                // AgentChat catch can surface them as diagnostic detail items without re-parsing URIs.
                var oauthException = new InvalidOperationException(
                    $"MCP OAuth authorization failed for server '{authorization.ServerName}': {error}.");
                oauthException.Data["oauth_error"] = error;
                if (!string.IsNullOrEmpty(errorDescription))
                {
                    oauthException.Data["oauth_error_description"] = errorDescription;
                }

                authorization.Completion.TrySetException(oauthException);
            }
            else
            {
                authorization.Completion.TrySetResult(requestUri);
            }
        }
    }

    private async Task EnsureConsentAsync(string serverName, CancellationToken cancellationToken)
    {
        lock (this.gate)
        {
            if (this.consentedServers.Contains(serverName))
            {
                return;
            }
        }

        // Build the ordered scope memories for this OAuth consent so the dialog's scope ComboBox is
        // populated ("All Uses"/"Always Ask"/…). The lineage carries no manifest identity/content —
        // interactive OAuth has no manifest — and only a session identity when one is available.
        // Keyed on the OAuth secret name so an accepted, non-AlwaysAsk scope is persisted by the
        // consent provider (SecretProvider) and matched again on a fresh handler/process, suppressing
        // re-prompting after restart. The secret name/use string embed only the server name — never a
        // token, client secret, or authorization code (issue #1408 privacy).
        var secretName = $"McpOAuth:{serverName}";
        var useDisplayString = $"Interactive OAuth sign-in for MCP server '{serverName}'";
        var lineage = new AgentManifestSecretUseMemoryFactory.SecretUseLineage(
            ManifestIdentity: null,
            ManifestContentHash: null,
            SessionIdentity: this.sessionIdentityProvider?.Invoke());
        var memories = this.memoryFactory.Build(lineage, secretName, useDisplayString);

        // Interactive OAuth has no external credential source, so surface a single OAuth source rather
        // than leaving the source ComboBox blank.
        var oauthSource = new OAuthSecretSource();
        var request = new SecretRequest(
            SecretName: secretName,
            UseDisplayString: useDisplayString,
            Memories: memories,
            DefaultSecretSource: oauthSource,
            CandidateSecretSources: [oauthSource]);

        var result = await this.consentProvider
            .RequestSecretsAsync([request], cancellationToken)
            .ConfigureAwait(false);

        if (result is null)
        {
            this.logger.LogWarning(
                "User declined MCP OAuth sign-in for server '{ServerName}'.", serverName);
            throw new OperationCanceledException("User declined MCP OAuth sign-in.");
        }

        lock (this.gate)
        {
            this.consentedServers.Add(serverName);
        }
    }

    /// <summary>
    /// Derives the loopback listener prefix (<c>http://localhost:&lt;port&gt;/</c>) from
    /// <paramref name="redirectUri"/>. HttpListener requires the host/port authority form and a
    /// trailing slash. The host is <c>localhost</c> (not <c>127.0.0.1</c>) because Microsoft Entra
    /// performs port-agnostic loopback redirect matching only for <c>localhost</c> (issue #1426).
    /// </summary>
    internal static string NormalizeLoopbackPrefix(Uri redirectUri)
    {
        ArgumentNullException.ThrowIfNull(redirectUri);
        var builder = new UriBuilder("http", "localhost", redirectUri.Port);
        var prefix = builder.Uri.GetLeftPart(UriPartial.Authority);
        return prefix.EndsWith('/') ? prefix : prefix + "/";
    }

    private static async Task WriteClosePageAsync(HttpListenerResponse response)
    {
        var payload = Encoding.UTF8.GetBytes(CloseWindowHtml);
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = payload.Length;
        await response.OutputStream.WriteAsync(payload).ConfigureAwait(false);
        response.OutputStream.Close();
        response.Close();
    }

    /// <summary>
    /// Generates a cryptographically-random, URL-safe (base64url, unpadded) <c>state</c> value used as
    /// the shared-listener correlation key when the authorization request carries none (#1428).
    /// </summary>
    private static string GenerateState()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>
    /// Returns <paramref name="uri"/> with an additional <c>key=value</c> query parameter appended,
    /// URL-encoding both and preserving any existing query string (#1428).
    /// </summary>
    private static Uri AppendQueryParameter(Uri uri, string key, string value)
    {
        var encoded = $"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}";
        var builder = new UriBuilder(uri);
        var existing = builder.Query.TrimStart('?');
        builder.Query = string.IsNullOrEmpty(existing) ? encoded : existing + "&" + encoded;
        return builder.Uri;
    }

    private static string? GetQueryValue(Uri uri, string key)
    {
        var query = uri.Query;
        if (string.IsNullOrEmpty(query))
        {
            return null;
        }

        foreach (var segment in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = segment.IndexOf('=');
            var name = separatorIndex >= 0 ? segment[..separatorIndex] : segment;
            if (!string.Equals(Uri.UnescapeDataString(name), key, StringComparison.Ordinal))
            {
                continue;
            }

            var value = separatorIndex >= 0 ? segment[(separatorIndex + 1)..] : string.Empty;
            return Uri.UnescapeDataString(value);
        }

        return null;
    }

    /// <summary>A pending interactive authorization awaiting its loopback callback, keyed by OAuth state.</summary>
    private sealed record PendingAuthorization(string ServerName, TaskCompletionSource<Uri> Completion);
}
