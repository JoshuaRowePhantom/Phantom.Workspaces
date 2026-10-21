using System.Security;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Authentication;
using Phantom.Workspaces.Llm.Auth;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Llm.Secrets;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// Covers the persistent MCP OAuth token cache (#1384): the <see cref="ITokenCache"/> implementation
/// over <see cref="IPlatformSecretStore"/> (round-trip, per-server keying, overwrite, clear, refresh
/// / expiry preservation) and the provider-owned SDK cache factory. Tests use an in-memory
/// <see cref="IPlatformSecretStore"/> fake and never touch the real Windows Credential Manager.
/// </summary>
public sealed class CredentialManagerTokenCacheTests
{
    private const string ServerName = "github-mcp";
    private const string KeyPrefix = "mcp-oauth:";

    private static TokenContainer SampleTokens() => new()
    {
        TokenType = "Bearer",
        AccessToken = "access-token-value",
        RefreshToken = "refresh-token-value",
        ExpiresIn = 3600,
        Scope = "repo read:user",
        ObtainedAt = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero),
    };

    [Fact]
    public async Task TokenCache_StoreThenGet_RoundTripsTokenContainer()
    {
        var store = new FakePlatformSecretStore();
        var cache = new CredentialManagerTokenCache(store, KeyPrefix, ServerName);
        var tokens = SampleTokens();

        await cache.StoreTokensAsync(tokens, CancellationToken.None);
        var loaded = await cache.GetTokensAsync(CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(tokens.TokenType, loaded!.TokenType);
        Assert.Equal(tokens.AccessToken, loaded.AccessToken);
        Assert.Equal(tokens.Scope, loaded.Scope);
    }

    [Fact]
    public async Task TokenCache_GetWithNoStoredTokens_ReturnsNull()
    {
        var cache = new CredentialManagerTokenCache(new FakePlatformSecretStore(), KeyPrefix, ServerName);

        var loaded = await cache.GetTokensAsync(CancellationToken.None);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task TokenCache_StoresUnderPerServerKey()
    {
        var store = new FakePlatformSecretStore();
        var cache = new CredentialManagerTokenCache(store, KeyPrefix, ServerName);

        await cache.StoreTokensAsync(SampleTokens(), CancellationToken.None);

        var key = Assert.Single(store.Secrets.Keys);
        Assert.Equal(KeyPrefix + "v2:" + ServerName, key);
        Assert.Contains(ServerName, key);
    }

    [Fact]
    public async Task TokenCache_DifferentServers_DoNotShareTokens()
    {
        var store = new FakePlatformSecretStore();
        var cacheA = new CredentialManagerTokenCache(store, KeyPrefix, "server-a");
        var cacheB = new CredentialManagerTokenCache(store, KeyPrefix, "server-b");

        await cacheA.StoreTokensAsync(SampleTokens(), CancellationToken.None);

        var fromA = await cacheA.GetTokensAsync(CancellationToken.None);
        var fromB = await cacheB.GetTokensAsync(CancellationToken.None);

        Assert.NotNull(fromA);
        Assert.Null(fromB);
    }

    [Fact]
    public async Task TokenCache_OverwriteExistingTokens_ReplacesPreviousValue()
    {
        var store = new FakePlatformSecretStore();
        var cache = new CredentialManagerTokenCache(store, KeyPrefix, ServerName);

        await cache.StoreTokensAsync(SampleTokens(), CancellationToken.None);
        await cache.StoreTokensAsync(
            new TokenContainer { TokenType = "Bearer", AccessToken = "second-access-token", ObtainedAt = DateTimeOffset.UnixEpoch },
            CancellationToken.None);

        var loaded = await cache.GetTokensAsync(CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("second-access-token", loaded!.AccessToken);
        Assert.Single(store.Secrets);
    }

    [Fact]
    public async Task TokenCache_ClearTokens_RemovesStoredValue()
    {
        var store = new FakePlatformSecretStore();
        var cache = new CredentialManagerTokenCache(store, KeyPrefix, ServerName);
        await cache.StoreTokensAsync(SampleTokens(), CancellationToken.None);

        await cache.ClearAsync(CancellationToken.None);

        Assert.Empty(store.Secrets);
        Assert.Null(await cache.GetTokensAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TokenCache_SerializesRefreshAndExpiryFields()
    {
        var store = new FakePlatformSecretStore();
        var cache = new CredentialManagerTokenCache(store, KeyPrefix, ServerName);
        var tokens = SampleTokens();

        await cache.StoreTokensAsync(tokens, CancellationToken.None);
        var loaded = await cache.GetTokensAsync(CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(tokens.RefreshToken, loaded!.RefreshToken);
        Assert.Equal(tokens.ExpiresIn, loaded.ExpiresIn);
        Assert.Equal(tokens.ObtainedAt, loaded.ObtainedAt);
    }

    [Fact]
    public async Task TokenCache_LoadMiss_LogsInteractiveLoginRequired()
    {
        var logger = new CapturingLogger<CredentialManagerTokenCache>();
        var cache = new CredentialManagerTokenCache(new FakePlatformSecretStore(), KeyPrefix, ServerName, logger);

        var loaded = await cache.GetTokensAsync(CancellationToken.None);

        Assert.Null(loaded);
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Debug
            && entry.Message.Contains("interactive login", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains(ServerName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TokenCache_StoreAndHit_LogAtDebug_WithoutTokenValue()
    {
        var logger = new CapturingLogger<CredentialManagerTokenCache>();
        var cache = new CredentialManagerTokenCache(new FakePlatformSecretStore(), KeyPrefix, ServerName, logger);
        var tokens = SampleTokens();

        await cache.StoreTokensAsync(tokens, CancellationToken.None);
        var loaded = await cache.GetTokensAsync(CancellationToken.None);

        Assert.NotNull(loaded);

        // Store and cache-hit are both recorded at Debug.
        Assert.All(logger.Entries, entry => Assert.Equal(LogLevel.Debug, entry.Level));
        Assert.Contains(logger.Entries, entry =>
            entry.Message.Contains("Stored", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(logger.Entries, entry =>
            entry.Message.Contains("cache hit", StringComparison.OrdinalIgnoreCase));

        // #1408 redaction: no access/refresh token value ever appears in a log message.
        Assert.DoesNotContain(logger.Entries, entry =>
            entry.Message.Contains(tokens.AccessToken!, StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, entry =>
            entry.Message.Contains(tokens.RefreshToken!, StringComparison.Ordinal));
    }

    [Fact]
    public void TokenCacheFactory_OnUnsupportedPlatform_ReturnsNull()
    {
        var nullStoreProvider = CredentialManagerTokenCache.CreateProvider(new NullPlatformSecretStore(), KeyPrefix);
        var noStoreProvider = CredentialManagerTokenCache.CreateProvider(null, KeyPrefix);

        Assert.Null(nullStoreProvider(ServerName));
        Assert.Null(noStoreProvider(ServerName));
    }

    [Fact]
    public void TokenCacheFactory_CreatesProviderOwnedSdkCache()
    {
        var store = new FakePlatformSecretStore();
        var provider = CredentialManagerTokenCache.CreateProvider(store, KeyPrefix);
        var cache = provider(ServerName);

        Assert.NotNull(cache);
        Assert.IsType<CredentialManagerTokenCache>(cache);
    }

    [Fact]
    public async Task CredentialManagerTokenCache_UsesCallerSuppliedKeyPrefix_KeysUnderThatNamespace()
    {
        // #1454: the Credential Manager key prefix is supplied by the caller (MCP passes "mcp-oauth:",
        // dev-tunnel passes "devtunnel:"), not hard-coded. Tokens stored under a caller-supplied prefix
        // must key under exactly that namespace and never under the old "mcp-oauth:" default.
        const string devTunnelPrefix = "devtunnel:";
        var store = new FakePlatformSecretStore();
        var cache = new CredentialManagerTokenCache(store, devTunnelPrefix, ServerName);

        await cache.StoreTokensAsync(SampleTokens(), CancellationToken.None);

        var key = Assert.Single(store.Secrets.Keys);
        Assert.Equal(devTunnelPrefix + "v2:" + ServerName, key);
        Assert.StartsWith(devTunnelPrefix, key, StringComparison.Ordinal);
        Assert.DoesNotContain("mcp-oauth:", key, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mcp-oauth:")]
    [InlineData("devtunnel:")]
    public async Task CredentialManagerTokenCache_UnversionedBareTokens_AreNotReadMigratedOrDeleted(string prefix)
    {
        var store = new FakePlatformSecretStore();
        var obsoleteKey = prefix + ServerName;
        var currentKey = prefix + "v2:" + ServerName;
        using var bareTokens = new SecureString();
        foreach (var character in JsonSerializer.Serialize(SampleTokens()))
        {
            bareTokens.AppendChar(character);
        }
        bareTokens.MakeReadOnly();
        await store.WriteAsync(obsoleteKey, bareTokens, CancellationToken.None);
        var obsoleteEntry = store.Secrets[obsoleteKey];
        var cache = new CredentialManagerTokenCache(store, prefix, ServerName);
        var generations = (ICredentialGenerationCache)cache;

        Assert.Null(await cache.GetTokensAsync(CancellationToken.None));
        Assert.Null(await generations.InspectGenerationAsync(CancellationToken.None));
        Assert.Equal(new[] { currentKey }, store.ReadKeys);
        Assert.Same(obsoleteEntry, Assert.Single(store.Secrets).Value);

        await cache.StoreTokensAsync(new TokenContainer
        {
            TokenType = "Bearer",
            AccessToken = "versioned-access-token",
            ExpiresIn = 3600,
            ObtainedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);
        Assert.Equal(2, store.Secrets.Count);
        Assert.Contains(currentKey, store.Secrets.Keys);
        Assert.Same(obsoleteEntry, store.Secrets[obsoleteKey]);
        var metadata = (await generations.InspectGenerationAsync(CancellationToken.None))!;
        var freshCache = new CredentialManagerTokenCache(store, prefix, ServerName);
        Assert.Equal(metadata.Generation, (await ((ICredentialGenerationCache)freshCache)
            .InspectGenerationAsync(CancellationToken.None))!.Generation);
        Assert.Equal("versioned-access-token", (await freshCache.GetTokensAsync(CancellationToken.None))!.AccessToken);
        Assert.Equal("versioned-access-token", (await freshCache.GetGenerationTokensAsync(
            metadata.Generation, CancellationToken.None))!.AccessToken);

        Assert.Equal(CredentialGenerationInvalidationOutcome.Invalidated,
            await generations.InvalidateGenerationAsync(metadata.Generation, CancellationToken.None));
        await cache.ClearAsync(CancellationToken.None);
        Assert.Same(obsoleteEntry, Assert.Single(store.Secrets).Value);
        Assert.Null(await freshCache.GetTokensAsync(CancellationToken.None));
        Assert.DoesNotContain(obsoleteKey, store.ReadKeys);
        Assert.DoesNotContain(obsoleteKey, store.DeleteKeys);
    }

    private sealed class FakePlatformSecretStore : IPlatformSecretStore
    {
        public Dictionary<string, SecureString> Secrets { get; } = new(StringComparer.Ordinal);
        public List<string> ReadKeys { get; } = [];
        public List<string> DeleteKeys { get; } = [];

        public Task<SecureString?> ReadAsync(string name, CancellationToken ct)
        {
            this.ReadKeys.Add(name);
            return Task.FromResult(this.Secrets.TryGetValue(name, out var value) ? Copy(value) : null);
        }

        public Task WriteAsync(string name, SecureString value, CancellationToken ct)
        {
            this.Secrets[name] = Copy(value);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string name, CancellationToken ct)
        {
            this.DeleteKeys.Add(name);
            this.Secrets.Remove(name);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> EnumerateNamesAsync(string prefix, CancellationToken ct)
        {
            IReadOnlyList<string> names = this.Secrets.Keys
                .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
                .ToArray();
            return Task.FromResult(names);
        }

        private static SecureString Copy(SecureString value)
            => Phantom.Workspaces.Llm.Secrets.SecureStringMarshal.Use(value, ToSecureString);

        private static SecureString ToSecureString(string value)
        {
            var secure = new SecureString();
            foreach (var character in value)
            {
                secure.AppendChar(character);
            }

            secure.MakeReadOnly();
            return secure;
        }
    }
}
