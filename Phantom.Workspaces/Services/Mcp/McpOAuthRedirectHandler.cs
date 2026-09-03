using System.Net;
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
/// Consent is remembered per MCP server for the process session (the in-process
/// <see cref="consentedServers"/> fast-path) so silent token refreshes do not re-prompt the user.
/// The consent request also carries scope memories keyed on <c>McpOAuth:{server}</c>, so an accepted
/// non-<see cref="SecretUseScope.AlwaysAsk"/> scope is persisted by the consent provider
/// (<see cref="ISecretProvider"/>) through the same allowed-secrets store as the <c>${SECRET:}</c>
/// path and matched again on a fresh handler/process, suppressing re-prompting after restart. Only the
/// GUI/desktop host wires this handler; headless hosts keep the failing "interactive OAuth is not
/// configured" default from #1382.
/// </remarks>
public sealed class McpOAuthRedirectHandler
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

        using var listener = new HttpListener();
        listener.Prefixes.Add(NormalizeLoopbackPrefix(redirectUri));
        listener.Start();
        try
        {
            this.browserLauncher.Open(authorizationUri);

            using var timeoutSource = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutSource.Token);
            if (this.timeout != Timeout.InfiniteTimeSpan)
            {
                timeoutSource.CancelAfter(this.timeout);
            }

            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(linked.Token).ConfigureAwait(false);
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

            var requestUri = context.Request.Url
                ?? throw new InvalidOperationException(
                    $"MCP OAuth redirect for server '{serverName}' did not include a request URI.");

            await WriteClosePageAsync(context.Response).ConfigureAwait(false);

            var error = GetQueryValue(requestUri, "error");
            if (!string.IsNullOrEmpty(error))
            {
                var errorDescription = GetQueryValue(requestUri, "error_description");

                // Log ONLY the OAuth error/error_description codes. The full redirect URI must never
                // be logged: its query carries the authorization `code`/`state` (issue #1408).
                this.logger.LogError(
                    "MCP OAuth redirect for '{ServerName}' returned error {Error}.", serverName, error);

                // Carry the decoded error/error_description into the thrown exception (via Data) so the
                // AgentChat catch can surface them as diagnostic detail items without re-parsing URIs.
                var oauthException = new InvalidOperationException(
                    $"MCP OAuth authorization failed for server '{serverName}': {error}.");
                oauthException.Data["oauth_error"] = error;
                if (!string.IsNullOrEmpty(errorDescription))
                {
                    oauthException.Data["oauth_error_description"] = errorDescription;
                }

                throw oauthException;
            }

            return requestUri;
        }
        finally
        {
            if (listener.IsListening)
            {
                listener.Stop();
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
    /// Derives the loopback listener prefix (<c>http://127.0.0.1:&lt;port&gt;/</c>) from
    /// <paramref name="redirectUri"/>. HttpListener requires the host/port authority form and a
    /// trailing slash.
    /// </summary>
    internal static string NormalizeLoopbackPrefix(Uri redirectUri)
    {
        ArgumentNullException.ThrowIfNull(redirectUri);
        var builder = new UriBuilder("http", "127.0.0.1", redirectUri.Port);
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
}
