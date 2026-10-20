using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Authentication;
using Phantom.Workspaces.Llm.Auth;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Llm.Mcp;
using Phantom.Workspaces.Services.Mcp;

namespace Phantom.Workspaces.Services.Access;

internal sealed record OAuthProviderConfiguration
{
    public required OAuthProviderDescriptor Provider { get; init; }
    public required OAuthClientAuthentication ClientAuthentication { get; init; }
    public required IReadOnlyList<OAuthScope> Scopes { get; init; }
    public CredentialSourceIdentifier Source => OAuthCredentialAccessExecutor.CreateSourceIdentifier(
        this.Provider, this.ClientAuthentication, this.Scopes);
    public required CredentialManagerTokenCache Cache { get; init; }
    public required Func<Uri> CreateRedirectUri { get; init; }
    public Func<HttpMessageHandler> CreateHttpHandler { get; init; } =
        () => new SocketsHttpHandler { AllowAutoRedirect = false };
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

internal sealed class OAuthCredentialAccessExecutor :
    ICredentialAccessExecutor<OAuthCredentialAccessRequest, OAuthCredentialAccessResult>,
    IOAuthPrivateCredentialProvider,
    ICredentialCacheAdministration,
    IAsyncDisposable
{
    private readonly OAuthProviderRegistry registry;
    private readonly Dictionary<LogicalCredentialIdentifier, Provider> providers = [];
    private readonly ISystemBrowserLauncher browserLauncher;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object gate = new();
    private readonly HashSet<McpOAuthAttempt> attempts = [];
    private readonly Dictionary<Guid, Provider> listedCaches = [];
    private bool disposed;

    internal OAuthCredentialAccessExecutor(
        OAuthProviderRegistry registry,
        ISystemBrowserLauncher browserLauncher)
    {
        this.browserLauncher = browserLauncher;
        this.registry = registry;
    }

    public CredentialExecutorKind Kind => CredentialExecutorKind.OAuth;

    internal static CredentialReference DescribeCredential(
        OAuthProviderDescriptor provider,
        OAuthClientAuthentication clientAuthentication,
        IReadOnlyList<OAuthScope> scopes) => new()
    {
        LogicalIdentifier = CreateLogicalIdentifier(provider, clientAuthentication, scopes),
        Provider = CredentialProviderKind.OAuth,
        Kind = CredentialKind.AccessToken,
    };

    internal static CredentialSourceIdentifier CreateSourceIdentifier(
        OAuthProviderDescriptor provider,
        OAuthClientAuthentication clientAuthentication,
        IReadOnlyList<OAuthScope> scopes) =>
        CredentialIdentityFactory.CreateSourceIdentifier(
            CredentialProviderKind.OAuth,
            "oauth-provider:" + CreateLogicalIdentifier(provider, clientAuthentication, scopes).Digest);

    private static LogicalCredentialIdentifier CreateLogicalIdentifier(
        OAuthProviderDescriptor provider,
        OAuthClientAuthentication clientAuthentication,
        IReadOnlyList<OAuthScope> scopes) =>
        CredentialIdentityFactory.CreateLogicalIdentifier(new CredentialIdentityConfiguration
        {
            Provider = CredentialProviderKind.OAuth,
            Kind = CredentialKind.AccessToken,
            CredentialPartition = provider.Authority.AbsoluteUri,
            SourceCompatibility = "provider-private-oauth-v1",
            Authority = provider.Authority.AbsoluteUri,
            Audience = provider.Audience,
            ClientPartition = string.Join("\n",
                provider.AuthorizationEndpoint.AbsoluteUri,
                provider.TokenEndpoint.AbsoluteUri,
                provider.AccountEndpoint.AbsoluteUri,
                provider.AccountIdentifierClaim,
                EncodeClientAuthentication(clientAuthentication)),
            Scopes = scopes.Select(scope => scope.Value).ToArray(),
        });

    private static string EncodeClientAuthentication(OAuthClientAuthentication authentication) => authentication switch
    {
        PublicOAuthClient client => "public:" + Encode(client.Client?.Value ?? ""),
        ConfidentialOAuthClient client => "confidential:" + Encode(client.Client.Value) + Encode(client.ClientSecret.Name),
        _ => throw new InvalidOperationException("OAuth client authentication is unsupported."),
    };

    private static string Encode(string value) => value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value;

    public async ValueTask<CredentialInspection> InspectAsync(
        OAuthCredentialAccessRequest request,
        CancellationToken cancellationToken)
    {
        var provider = this.FindProvider(request);
        var metadata = provider is null ? null : await provider.InspectGenerationAsync(cancellationToken).ConfigureAwait(false);
        var accountMatches = request.Credential.AccountIdentifier is null || metadata?.AccountIdentifier is null ||
            request.Credential.AccountIdentifier == metadata.AccountIdentifier;
        return new CredentialInspection
        {
            InspectionIdentifier = Guid.NewGuid(),
            Credential = request.Credential with
            {
                AccountIdentifier = request.Credential.AccountIdentifier ?? metadata?.AccountIdentifier ?? provider?.LastKnownAccount,
            },
            Sources = provider is null || !accountMatches ? [] : request.Sources
                .Where(source => IsSource(provider, source))
                .Select(source => source with
                {
                    Usability = provider.IsUsable(metadata)
                        ? CredentialSourceUsability.Usable : CredentialSourceUsability.AcquisitionRequired,
                }).ToArray(),
            AcquisitionKeys = provider is null ? new Dictionary<CredentialSourceIdentifier, ProviderAcquisitionKey>() :
                new Dictionary<CredentialSourceIdentifier, ProviderAcquisitionKey>
                {
                    [provider.Configuration.Source] = provider.Key(
                        request.Credential.AccountIdentifier ?? metadata?.AccountIdentifier ?? provider.LastKnownAccount),
                },
            CachedGenerations = metadata is null ? new Dictionary<CredentialSourceIdentifier, CredentialGenerationInspection>() :
                new Dictionary<CredentialSourceIdentifier, CredentialGenerationInspection>
                {
                    [provider!.Configuration.Source] = metadata,
                },
        };
    }

    public async ValueTask<CredentialExecutionOutcome<OAuthCredentialAccessResult>> ExecuteAsync(
        OAuthCredentialAccessRequest request,
        CredentialInspection inspection,
        ReleaseAuthorization authorization,
        ProviderInteractionPermit? interactionPermit,
        ICredentialExecutionContext context,
        CancellationToken cancellationToken)
    {
        var provider = this.FindProvider(request);
        if (provider is null || context.PolicyRequest is not { } policyRequest ||
            inspection.SelectedSource != provider.Configuration.Source ||
            !inspection.Sources.Any(source => IsSource(provider, source) && source.Identifier == inspection.SelectedSource) ||
            policyRequest.SourceIdentifier != provider.Configuration.Source ||
            policyRequest.Credential.LogicalIdentifier != request.Credential.LogicalIdentifier ||
            policyRequest.RequestIdentifier != authorization.RequestIdentifier ||
            policyRequest.RequestGeneration != authorization.RequestGeneration)
        {
            return new CredentialExecutionDenied<OAuthCredentialAccessResult>();
        }

        var validation = await context.Policy.ValidateAsync(authorization.Identifier, policyRequest, cancellationToken).ConfigureAwait(false);
        if (validation != ReleaseAuthorizationValidation.Valid &&
            !(validation == ReleaseAuthorizationValidation.IdentityMismatch && policyRequest.Credential.AccountIdentifier is null))
        {
            return new CredentialExecutionDenied<OAuthCredentialAccessResult>();
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.lifetime.Token);
        try
        {
            var metadata = await provider.InspectGenerationAsync(cancellation.Token).ConfigureAwait(false);
            CredentialMaterialGenerationIdentifier generation;
            if (interactionPermit is not null)
            {
                var acquired = await context.ExecuteProviderAcquisitionAsync(
                    new Acquisition(this, request, provider, context, policyRequest.Credential.AccountIdentifier),
                    interactionPermit,
                    cancellation.Token).ConfigureAwait(false);
                if (acquired is not ProviderAcquisitionSucceeded succeeded)
                {
                    return AcquisitionFailure(acquired);
                }
                generation = succeeded.CommittedGeneration;
            }
            else if (provider.IsUsable(metadata))
            {
                generation = metadata!.Generation;
            }
            else
            {
                return new CredentialExecutionInteractionRequired<OAuthCredentialAccessResult>();
            }

            metadata = await provider.InspectGenerationAsync(cancellation.Token).ConfigureAwait(false);
            if (!provider.IsUsable(metadata) || metadata!.Generation != generation)
            {
                return new CredentialExecutionInteractionRequired<OAuthCredentialAccessResult>();
            }
            if ((request.Credential.AccountIdentifier is { } account && account != metadata.AccountIdentifier) ||
                (policyRequest.Credential.AccountIdentifier is { } approvedAccount && approvedAccount != metadata.AccountIdentifier))
            {
                return Failure(AccessErrorCode.IdentityMismatch);
            }

            var tokens = await provider.Configuration.Cache.GetGenerationTokensAsync(generation, cancellation.Token).ConfigureAwait(false);
            if (tokens is null || !await provider.IsCurrentGenerationAsync(generation, cancellation.Token).ConfigureAwait(false))
            {
                return new CredentialExecutionInteractionRequired<OAuthCredentialAccessResult>();
            }
            return new CredentialExecutionReady<OAuthCredentialAccessResult>
            {
                ResolvedIdentity = new ResolvedCredentialIdentity
                {
                    LogicalIdentifier = request.Credential.LogicalIdentifier,
                    AccountIdentifier = metadata.AccountIdentifier!,
                    SourceIdentifier = provider.Configuration.Source,
                    RequestIdentifier = policyRequest.RequestIdentifier,
                    RequestGeneration = policyRequest.RequestGeneration,
                },
                Materializer = new Materializer(tokens.AccessToken.ToCharArray(), metadata, authorization, context, provider),
            };
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            return Failure(AccessErrorCode.ProviderFailure);
        }
    }

    public async ValueTask<CredentialGenerationInspection?> InspectGenerationAsync(
        OAuthCredentialAccessRequest request,
        CancellationToken cancellationToken)
    {
        var provider = this.FindProvider(request);
        return provider is null ? null : await provider.InspectGenerationAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ProviderAcquisitionOutcome> AcquirePrivateAsync(
        OAuthCredentialAccessRequest request,
        ICredentialExecutionContext parentContext,
        ProviderInteractionPermit parentPermit,
        CancellationToken cancellationToken)
    {
        var provider = this.FindProvider(request);
        if (provider is null)
        {
            return new ProviderAcquisitionDenied();
        }
        try
        {
            var metadata = await provider.InspectGenerationAsync(cancellationToken).ConfigureAwait(false);
            var expectedAccount = request.Credential.AccountIdentifier ??
                parentContext.PolicyRequest?.Credential.AccountIdentifier ??
                metadata?.AccountIdentifier ?? provider.LastKnownAccount;
            return await parentContext.ExecutePrivateProviderDependencyAsync(
                new Acquisition(this, request, provider, parentContext, expectedAccount),
                parentPermit,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            return new ProviderAcquisitionFailed { ErrorCode = AccessErrorCode.ProviderFailure };
        }
    }

    public async ValueTask<CredentialGenerationInvalidationOutcome> InvalidateGenerationAsync(
        OAuthCredentialAccessRequest request,
        CredentialMaterialGenerationIdentifier generation,
        CancellationToken cancellationToken)
    {
        var provider = this.FindProvider(request);
        if (provider is null) { return CredentialGenerationInvalidationOutcome.Missing; }
        try
        {
            return await provider.InvalidateGenerationAsync(generation, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            throw new InvalidOperationException("OAuth generation invalidation failed.");
        }
    }

    public async ValueTask<OAuthGenerationUseOutcome<TResult>> UseGenerationAsync<TResult>(
        OAuthCredentialAccessRequest request,
        CredentialMaterialGenerationIdentifier generation,
        CredentialAccountIdentifier expectedAccount,
        ICredentialExecutionContext parentContext,
        ProviderInteractionPermit parentPermit,
        Func<string, CancellationToken, ValueTask<TResult>> operation,
        CancellationToken cancellationToken)
    {
        var provider = this.FindProvider(request);
        bool IsApproved() => parentContext.PolicyRequest is not null &&
            parentPermit.BelongsToGeneration(parentContext.RequestIdentifier, parentContext.Revision, parentContext.AcquisitionGeneration);
        if (provider is null || !IsApproved())
        {
            return new OAuthGenerationUseDenied<TResult>();
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.lifetime.Token);
        var metadata = await provider.InspectGenerationAsync(cancellation.Token).ConfigureAwait(false);
        if (!provider.IsUsable(metadata) || metadata!.Generation != generation ||
            metadata.AccountIdentifier != expectedAccount ||
            (request.Credential.AccountIdentifier is { } account && account != expectedAccount))
        {
            return new OAuthGenerationUseUnavailable<TResult>();
        }
        TokenContainer? tokens;
        try
        {
            tokens = await provider.Configuration.Cache.GetGenerationTokensAsync(generation, cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            return new OAuthGenerationUseUnavailable<TResult>();
        }
        if (tokens is null || !IsApproved() || !await provider.IsCurrentGenerationAsync(generation, cancellation.Token).ConfigureAwait(false))
        {
            return new OAuthGenerationUseUnavailable<TResult>();
        }

        var result = await operation(tokens.AccessToken, cancellation.Token).ConfigureAwait(false);
        var published = false;
        try
        {
            if (!IsApproved() || !await provider.IsCurrentGenerationAsync(generation, cancellation.Token).ConfigureAwait(false))
            {
                return new OAuthGenerationUseUnavailable<TResult>();
            }
            var outcome = new OAuthGenerationUseSucceeded<TResult> { Result = result };
            published = true;
            return outcome;
        }
        finally
        {
            if (!published)
            {
                if (result is IAsyncDisposable asynchronous) { await asynchronous.DisposeAsync().ConfigureAwait(false); }
                else if (result is IDisposable disposable) { disposable.Dispose(); }
            }
        }
    }

    public async ValueTask<IReadOnlyList<CredentialCacheDescriptor>> ListAsync(CancellationToken cancellationToken = default)
    {
        var descriptors = new List<CredentialCacheDescriptor>();
        foreach (var provider in this.SnapshotProviders(includeRegistered: true))
        {
            var metadata = await provider.InspectGenerationAsync(cancellationToken).ConfigureAwait(false);
            if (metadata is null) { continue; }
            lock (this.gate) { this.listedCaches[metadata.Generation.Value] = provider; }
            descriptors.Add(new CredentialCacheDescriptor
            {
                Identifier = new() { Value = metadata.Generation.Value },
                Generation = metadata.Generation,
                Provider = CredentialProviderKind.OAuth,
                Kind = CredentialKind.AccessToken,
                SourceIdentifier = provider.Configuration.Source,
                ExpiresOn = metadata.ExpiresOn,
            });
        }
        return descriptors;
    }

    public async ValueTask DeleteAsync(
        CredentialCacheIdentifier identifier,
        CancellationToken cancellationToken = default)
    {
        Provider? provider;
        lock (this.gate) { this.listedCaches.TryGetValue(identifier.Value, out provider); }
        if (provider is null)
        {
            throw new CredentialAdministrationException(CredentialAdministrationFailure.Unavailable);
        }
        CredentialGenerationInvalidationOutcome outcome;
        try
        {
            outcome = await provider.InvalidateGenerationAsync(new() { Value = identifier.Value }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            throw new CredentialAdministrationException(CredentialAdministrationFailure.Failed);
        }
        if (outcome != CredentialGenerationInvalidationOutcome.Invalidated)
        {
            throw new CredentialAdministrationException(outcome == CredentialGenerationInvalidationOutcome.NoLongerCurrent
                ? CredentialAdministrationFailure.NoLongerCurrent : CredentialAdministrationFailure.Unavailable);
        }
    }

    public async ValueTask DisposeAsync()
    {
        McpOAuthAttempt[] active;
        lock (this.gate)
        {
            if (this.disposed) { return; }
            this.disposed = true;
            active = this.attempts.ToArray();
        }
        await this.lifetime.CancelAsync().ConfigureAwait(false);
        foreach (var attempt in active) { await attempt.DisposeAsync().ConfigureAwait(false); }
        foreach (var provider in this.SnapshotProviders(includeRegistered: false)) { await provider.DisposeAsync().ConfigureAwait(false); }
    }

    private Provider? FindProvider(OAuthCredentialAccessRequest request)
    {
        var configuration = this.registry.FindConfiguration(request.Credential.LogicalIdentifier);
        if (configuration is null || configuration.Provider != request.Provider ||
            configuration.ClientAuthentication != request.ClientAuthentication ||
            !configuration.Scopes.Select(scope => scope.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .SequenceEqual(request.Scopes.Select(scope => scope.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)))
        {
            return null;
        }
        lock (this.gate)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            if (!this.providers.TryGetValue(request.Credential.LogicalIdentifier, out var provider))
            {
                this.providers.Add(request.Credential.LogicalIdentifier,
                    provider = new Provider(configuration, () => !Volatile.Read(ref this.disposed)));
            }
            return provider.LogicalIdentifier == request.Credential.LogicalIdentifier &&
                request.Credential.Provider == CredentialProviderKind.OAuth && request.Credential.Kind == CredentialKind.AccessToken &&
                request.Sources.Any(source => IsSource(provider, source)) ? provider : null;
        }
    }

    private Provider[] SnapshotProviders(bool includeRegistered)
    {
        var registered = includeRegistered ? this.registry.SnapshotConfigurations() : [];
        lock (this.gate)
        {
            if (!this.disposed)
            {
                foreach (var configuration in registered)
                {
                    var identifier = DescribeCredential(configuration.Provider, configuration.ClientAuthentication, configuration.Scopes).LogicalIdentifier;
                    if (!this.providers.ContainsKey(identifier))
                        this.providers.Add(identifier, new Provider(configuration, () => !Volatile.Read(ref this.disposed)));
                }
            }
            return this.providers.Values.ToArray();
        }
    }

    private static bool IsSource(Provider provider, CredentialSourceChoiceDescriptor source) =>
        source.Identifier == provider.Configuration.Source && source.Provider == CredentialProviderKind.OAuth &&
        source.Kind == CredentialSourceKind.Provider;

    private async ValueTask<ProviderAcquisitionOutcome> AcquireAsync(
        Acquisition operation,
        ProviderAcquisitionCommitContext commitContext,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!commitContext.InteractionPermit.AuthorizesProvider(this.Kind, operation.CompatibilityKey))
            {
                return new ProviderAcquisitionDenied();
            }
            if (operation.Request.ClientAuthentication is ConfidentialOAuthClient confidential)
            {
                var dependency = await operation.Context.AcquireDependencyAsync(
                    SecretRequest(confidential.ClientSecret, operation.Context), cancellationToken).ConfigureAwait(false);
                if (dependency is not AccessSucceeded<StoredSecretAccessResult> succeeded)
                {
                    return dependency is AccessFailed<StoredSecretAccessResult> failed
                        ? new ProviderAcquisitionFailed { ErrorCode = failed.ErrorCode } : new ProviderAcquisitionDenied();
                }
                await using var secret = succeeded.Result;
                var used = await secret.Lease.UseAsync(
                    (value, token) => this.AuthenticateAsync(operation, commitContext, value, token),
                    cancellationToken).ConfigureAwait(false);
                return used is SensitiveValueUseSucceeded<ProviderAcquisitionOutcome> success
                    ? success.Result : new ProviderAcquisitionDenied();
            }
            return await this.AuthenticateAsync(operation, commitContext, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            return new ProviderAcquisitionFailed { ErrorCode = AccessErrorCode.ProviderFailure };
        }
    }

    private async ValueTask<ProviderAcquisitionOutcome> AuthenticateAsync(
        Acquisition operation,
        ProviderAcquisitionCommitContext commitContext,
        string? clientSecret,
        CancellationToken cancellationToken)
    {
        var provider = operation.Provider;
        var configuration = provider.Configuration;
        var descriptor = configuration.Provider;
        var clientIdentifier = configuration.ClientAuthentication switch
        {
            PublicOAuthClient client => client.Client?.Value,
            ConfidentialOAuthClient client => client.Client.Value,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(clientIdentifier) ||
            new[] { descriptor.Authority, descriptor.AuthorizationEndpoint, descriptor.TokenEndpoint, descriptor.AccountEndpoint }
                .Any(uri => uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)))
        {
            return new ProviderAcquisitionFailed { ErrorCode = AccessErrorCode.UnsupportedRequest };
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.lifetime.Token);
        var commits = provider.Commits(operation.CompatibilityKey);
        var generation = await commits.BeginGenerationAsync(commitContext, cancellation.Token).ConfigureAwait(false);
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var redirect = configuration.CreateRedirectUri();
        var authorization = new UriBuilder(descriptor.AuthorizationEndpoint);
        var query = System.Web.HttpUtility.ParseQueryString(authorization.Query);
        query["response_type"] = "code";
        query["client_id"] = clientIdentifier;
        query["redirect_uri"] = redirect.AbsoluteUri;
        query["scope"] = string.Join(" ", configuration.Scopes.Select(scope => scope.Value).Distinct(StringComparer.Ordinal));
        query["resource"] = descriptor.Audience;
        query["state"] = state;
        query["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        query["code_challenge_method"] = "S256";
        authorization.Query = query.ToString();
        await using var attempt = new McpOAuthAttempt(this.browserLauncher, authorization.Uri, redirect, operation, this.RemoveAttempt);
        lock (this.gate)
        {
            if (this.disposed) { throw new OperationCanceledException(cancellation.Token); }
            this.attempts.Add(attempt);
        }
        await attempt.Ready.ConfigureAwait(false);
        var callback = await attempt.ExecuteAsync(commitContext.InteractionPermit, cancellation.Token).ConfigureAwait(false);
        var code = System.Web.HttpUtility.ParseQueryString(callback.Query)["code"]!;
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirect.AbsoluteUri,
            ["client_id"] = clientIdentifier,
            ["code_verifier"] = verifier,
            ["resource"] = descriptor.Audience,
        };
        if (clientSecret is not null) { form["client_secret"] = clientSecret; }
        using var httpClient = new HttpClient(configuration.CreateHttpHandler());
        using var request = new HttpRequestMessage(HttpMethod.Post, descriptor.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await httpClient.SendAsync(request, cancellation.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) { return new ProviderAcquisitionFailed { ErrorCode = AccessErrorCode.ProviderFailure }; }
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellation.Token).ConfigureAwait(false),
            cancellationToken: cancellation.Token).ConfigureAwait(false);
        var root = document.RootElement;
        if (!root.TryGetProperty("access_token", out var accessToken) || accessToken.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(accessToken.GetString()) ||
            !root.TryGetProperty("token_type", out var tokenType) || tokenType.ValueKind != JsonValueKind.String ||
            !string.Equals(tokenType.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            return new ProviderAcquisitionFailed { ErrorCode = AccessErrorCode.ProviderFailure };
        }
        int? expires = null;
        if (root.TryGetProperty("expires_in", out var expiry))
        {
            if (!expiry.TryGetInt64(out var seconds) || seconds <= 0 || seconds > int.MaxValue)
                return new ProviderAcquisitionFailed { ErrorCode = AccessErrorCode.ProviderFailure };
            expires = (int)seconds;
        }
        if (root.TryGetProperty("scope", out var granted))
        {
            if (granted.ValueKind != JsonValueKind.String ||
                !granted.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal)
                    .SetEquals(configuration.Scopes.Select(scope => scope.Value)))
            {
                return new ProviderAcquisitionFailed { ErrorCode = AccessErrorCode.ProviderFailure };
            }
        }
        var tokens = new TokenContainer
        {
            AccessToken = accessToken.GetString()!,
            TokenType = "Bearer",
            RefreshToken = root.TryGetProperty("refresh_token", out var refresh) && refresh.ValueKind == JsonValueKind.String
                ? refresh.GetString() : null,
            ExpiresIn = expires,
            ObtainedAt = configuration.TimeProvider.GetUtcNow(),
        };
        var account = await ResolveAccountAsync(configuration, tokens.AccessToken, cancellation.Token).ConfigureAwait(false);
        if (operation.ExpectedAccount is { } expected && account != expected)
        {
            return new ProviderAcquisitionFailed { ErrorCode = AccessErrorCode.IdentityMismatch };
        }
        try
        {
            await commits.StoreAndPublishAsync(tokens, generation, cancellation.Token).ConfigureAwait(false);
            await commits.WaitForCommitAsync(generation, cancellation.Token).ConfigureAwait(false);
        }
        catch (InvalidOperationException) when (generation.Outcome is ProviderAcquisitionFailed)
        {
            return generation.Outcome;
        }
        provider.BindAccount(generation.MaterialGeneration, account);
        return generation.Outcome!;
    }

    private void RemoveAttempt(McpOAuthAttempt attempt)
    {
        lock (this.gate) { this.attempts.Remove(attempt); }
    }

    private static async ValueTask<CredentialAccountIdentifier> ResolveAccountAsync(
        OAuthProviderConfiguration configuration,
        string token,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient(configuration.CreateHttpHandler());
        using var request = new HttpRequestMessage(HttpMethod.Get, configuration.Provider.AccountEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("Phantom.Workspaces");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) { throw new HttpRequestException("OAuth account evidence is unavailable."); }
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var claimName = configuration.Provider.AccountIdentifierClaim switch
        {
            OAuthAccountIdentifierClaim.Subject => "sub",
            OAuthAccountIdentifierClaim.Identifier => "id",
            _ => throw new InvalidOperationException("OAuth account evidence is unsupported."),
        };
        string? subject = null;
        if (document.RootElement.TryGetProperty(claimName, out var claim))
        {
            if (claim.ValueKind == JsonValueKind.String) { subject = claim.GetString(); }
            else if (claimName == "id" && claim.ValueKind == JsonValueKind.Number && claim.TryGetInt64(out var identifier))
                subject = identifier.ToString(CultureInfo.InvariantCulture);
        }
        if (string.IsNullOrWhiteSpace(subject)) { throw new HttpRequestException("OAuth account evidence is unavailable."); }
        return CredentialIdentityFactory.CreateAccountIdentifier(
            CredentialProviderKind.OAuth, configuration.Provider.Authority.AbsoluteUri + "\n" + subject);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool IsProviderFailure(Exception exception) => exception is HttpRequestException or IOException or
        JsonException or Win32Exception or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException;

    private static CredentialExecutionFailed<OAuthCredentialAccessResult> Failure(AccessErrorCode code) => new()
    {
        ErrorCode = code,
        CanRetry = true,
    };

    private static CredentialExecutionOutcome<OAuthCredentialAccessResult> AcquisitionFailure(ProviderAcquisitionOutcome outcome) =>
        outcome is ProviderAcquisitionDenied ? new CredentialExecutionDenied<OAuthCredentialAccessResult>() :
        Failure(outcome is ProviderAcquisitionFailed failed ? failed.ErrorCode : AccessErrorCode.ProviderFailure);

    private static StoredSecretAccessRequest SecretRequest(
        SecretReference reference,
        ICredentialExecutionContext context) => new()
    {
        Reference = reference,
        Credential = new CredentialReference
        {
            Provider = CredentialProviderKind.StoredSecret,
            Kind = CredentialKind.Secret,
            LogicalIdentifier = CredentialIdentityFactory.CreateLogicalIdentifier(new CredentialIdentityConfiguration
            {
                Provider = CredentialProviderKind.StoredSecret,
                Kind = CredentialKind.Secret,
                CredentialPartition = reference.Name,
                SourceCompatibility = "stored",
                Authority = "",
                Audience = "",
                ClientPartition = "",
                Scopes = [],
            }),
        },
        Requestor = context.Requestor,
        Use = context.Use,
        PresentationScope = context.PresentationScope,
        Sources = new[] { CredentialSourceKind.SecretStore, CredentialSourceKind.Environment }.Select(kind => new CredentialSourceChoiceDescriptor
        {
            Identifier = CredentialIdentityFactory.CreateSourceIdentifier(CredentialProviderKind.StoredSecret, $"{kind}:{reference.Name}"),
            Provider = CredentialProviderKind.StoredSecret,
            Kind = kind,
            Usability = CredentialSourceUsability.Missing,
            SafeDisplayName = kind == CredentialSourceKind.SecretStore ? "Stored client credential" : "Environment client credential",
        }).ToArray(),
    };

    private sealed class Provider(OAuthProviderConfiguration configuration, Func<bool> active) : ICredentialGenerationCache, IAsyncDisposable
    {
        private readonly object gate = new();
        private readonly Dictionary<ProviderAcquisitionKey, McpOAuthCacheCommitCoordinator> commits = [];
        private readonly Dictionary<CredentialMaterialGenerationIdentifier, CredentialAccountIdentifier> accounts = [];
        // Deleting material must not erase the principal pin of an already approved use.
        private CredentialAccountIdentifier? lastKnownAccount;
        internal OAuthProviderConfiguration Configuration { get; } = configuration;
        internal LogicalCredentialIdentifier LogicalIdentifier { get; } = DescribeCredential(
            configuration.Provider, configuration.ClientAuthentication, configuration.Scopes).LogicalIdentifier;
        internal CredentialAccountIdentifier? LastKnownAccount
        {
            get { lock (this.gate) { return this.lastKnownAccount; } }
        }
        internal ProviderAcquisitionKey Key(CredentialAccountIdentifier? account) => new()
        {
            Configuration = this.LogicalIdentifier,
            Source = this.Configuration.Source,
            Account = account,
            Protocol = ProviderProtocolKind.OAuth,
        };
        internal McpOAuthCacheCommitCoordinator Commits(ProviderAcquisitionKey key)
        {
            lock (this.gate)
            {
                if (!this.commits.TryGetValue(key, out var coordinator))
                {
                    this.commits[key] = coordinator = new McpOAuthCacheCommitCoordinator(
                        this.Configuration.Cache, key, CredentialExecutorKind.OAuth);
                }
                return coordinator;
            }
        }
        internal void BindAccount(CredentialMaterialGenerationIdentifier generation, CredentialAccountIdentifier account)
        {
            lock (this.gate)
            {
                this.accounts[generation] = account;
                this.lastKnownAccount = account;
            }
        }
        internal bool IsUsable(CredentialGenerationInspection? metadata) =>
            active() && metadata?.AccountIdentifier is not null &&
            (metadata.ExpiresOn is null || metadata.ExpiresOn > this.Configuration.TimeProvider.GetUtcNow());
        public async ValueTask<CredentialGenerationInspection?> InspectGenerationAsync(CancellationToken cancellationToken)
        {
            if (!active()) { return null; }
            var metadata = await ((ICredentialGenerationCache)this.Configuration.Cache).InspectGenerationAsync(cancellationToken).ConfigureAwait(false);
            if (metadata is null) { return null; }
            lock (this.gate) { return metadata with { AccountIdentifier = this.accounts.GetValueOrDefault(metadata.Generation) }; }
        }
        public async ValueTask<bool> IsCurrentGenerationAsync(CredentialMaterialGenerationIdentifier generation, CancellationToken cancellationToken)
        {
            var metadata = await this.InspectGenerationAsync(cancellationToken).ConfigureAwait(false);
            return this.IsUsable(metadata) && metadata!.Generation == generation &&
                await ((ICredentialGenerationCache)this.Configuration.Cache).IsCurrentGenerationAsync(generation, cancellationToken).ConfigureAwait(false);
        }
        public ValueTask<CredentialGenerationInvalidationOutcome> InvalidateGenerationAsync(
            CredentialMaterialGenerationIdentifier generation, CancellationToken cancellationToken) =>
            ((ICredentialGenerationCache)this.Configuration.Cache).InvalidateGenerationAsync(generation, cancellationToken);
        public async ValueTask DisposeAsync()
        {
            McpOAuthCacheCommitCoordinator[] coordinators;
            lock (this.gate) { coordinators = this.commits.Values.ToArray(); this.accounts.Clear(); }
            foreach (var coordinator in coordinators) { await coordinator.DisposeAsync().ConfigureAwait(false); }
        }
    }

    private sealed class Acquisition(
        OAuthCredentialAccessExecutor owner,
        OAuthCredentialAccessRequest request,
        Provider provider,
        ICredentialExecutionContext context,
        CredentialAccountIdentifier? expectedAccount) : IProviderAcquisitionOperation
    {
        internal OAuthCredentialAccessRequest Request { get; } = request;
        internal Provider Provider { get; } = provider;
        internal ICredentialExecutionContext Context { get; } = context;
        internal CredentialAccountIdentifier? ExpectedAccount { get; } = expectedAccount;
        public ProviderAcquisitionKey CompatibilityKey => this.Provider.Key(this.ExpectedAccount);
        public UserAuthorizationRequestIdentifier RequestIdentifier => this.Context.RequestIdentifier;
        public long Revision => this.Context.Revision;
        public CredentialExecutorKind ExecutorKind => CredentialExecutorKind.OAuth;
        public Guid AcquisitionGeneration => this.Context.AcquisitionGeneration;
        public ValueTask<ProviderAcquisitionOutcome> ExecuteAsync(
            ProviderAcquisitionCommitContext commitContext,
            CancellationToken cancellationToken) => owner.AcquireAsync(this, commitContext, cancellationToken);
    }

    private sealed class Materializer(
        char[] material,
        CredentialGenerationInspection metadata,
        ReleaseAuthorization authorization,
        ICredentialExecutionContext context,
        Provider provider) : IAccessResultMaterializer<OAuthCredentialAccessResult>
    {
        private bool disposed;
        public ValueTask<OAuthCredentialAccessResult> MaterializeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(this.disposed, this);
            return ValueTask.FromResult(new OAuthCredentialAccessResult
            {
                AccessToken = new SensitiveValueLease(material, new CredentialLeaseBinding
                {
                    AuthorizationIdentifier = authorization.Identifier,
                    Request = context.PolicyRequest!,
                    MaterialGeneration = metadata.Generation,
                    ExpiresOn = metadata.ExpiresOn,
                }, context.CreateLeaseContext(provider), provider.Configuration.TimeProvider),
                ExpiresOn = metadata.ExpiresOn,
            });
        }
        public ValueTask DisposeAsync()
        {
            this.disposed = true;
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(material.AsSpan()));
            return ValueTask.CompletedTask;
        }
    }
}
