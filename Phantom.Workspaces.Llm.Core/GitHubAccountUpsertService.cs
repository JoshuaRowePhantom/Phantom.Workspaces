using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Upserts a <c>user-account</c> entity into the workspace data store the first time a GitHub
/// token is seen. Uses <see cref="IGitHubIdentityResolver"/> to resolve the username from the
/// token, then writes (or no-ops if the entity already exists) a <c>user-account</c> entity named
/// <c>["users", "username", "&lt;username&gt;", "user-accounts", "github.com"]</c>.
/// </summary>
public sealed class GitHubAccountUpsertService : IGitHubAccountUpsertService
{
    private const string GitHubProviderUri = "https://github.com";
    private const string GitHubProviderHostname = "github.com";

    /// <summary>Entity type name for user-account entities.</summary>
    private const string UserAccountEntityType = "user-account";

    private readonly IDataAccessLayer dataAccessLayer;
    private readonly IGitHubIdentityResolver identityResolver;
    private readonly ILogger<GitHubAccountUpsertService> logger;

    /// <summary>
    /// Tracks tokens that have already been processed so each unique token triggers at most one
    /// DAL round-trip per process lifetime. Value is a <see cref="TaskCompletionSource"/> so
    /// concurrent callers with the same new token wait for the single in-flight upsert.
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> processedTokens = new();

    public GitHubAccountUpsertService(
        IDataAccessLayer dataAccessLayer,
        IGitHubIdentityResolver identityResolver,
        ILogger<GitHubAccountUpsertService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(dataAccessLayer);
        ArgumentNullException.ThrowIfNull(identityResolver);

        this.dataAccessLayer = dataAccessLayer;
        this.identityResolver = identityResolver;
        this.logger = logger ?? NullLogger<GitHubAccountUpsertService>.Instance;
    }

    /// <inheritdoc />
    public async Task UpsertForTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        // Fast path: token already processed in this process lifetime.
        if (this.processedTokens.ContainsKey(token))
        {
            return;
        }

        string? username = null;
        try
        {
            username = await this.identityResolver.GetUsernameAsync(token, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger.LogWarning(ex, "GitHub identity resolution threw unexpectedly; skipping user-account upsert.");
            // Mark token as processed to avoid retrying on every auth event.
            this.processedTokens.TryAdd(token, true);
            return;
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            // Mark as processed to avoid retrying.
            this.processedTokens.TryAdd(token, true);
            return;
        }

        try
        {
            await this.UpsertEntityAsync(username, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Failed to upsert user-account entity for GitHub user '{Username}'.", username);
        }
        finally
        {
            // Mark as processed regardless of DAL outcome to avoid repeated noisy failures.
            this.processedTokens.TryAdd(token, true);
        }
    }

    private async Task UpsertEntityAsync(string username, CancellationToken cancellationToken)
    {
        var entityName = new EntityName("users", "username", username, "user-accounts", GitHubProviderHostname);
        var entityId = DeterministicEntityId.Create("github-account-upsert", username, GitHubProviderHostname);

        // Fetch the current entity to honour its concurrency tag (avoids a conflict on re-upsert).
        var getResult = await this.dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityName = entityName,
                    },
                ],
            },
            cancellationToken).ConfigureAwait(false);

        EntitySnapshot? current = null;
        foreach (var batch in getResult.Batches)
        {
            foreach (var entity in batch.Entities)
            {
                current = entity;
            }
        }

        // If the entity already exists and already has the correct provider + user-name, skip.
        if (current is not null
            && EntityHasCorrectData(current.Data, GitHubProviderUri, username))
        {
            return;
        }

        var usedEntityId = current?.EntityId ?? entityId;
        var entityDataJson = $$"""
            {
              "entity-id": "{{usedEntityId}}",
              "entity-types": ["entity", "user-account"],
              "names": [{{BuildNamesJson(entityName)}}],
              "display-name": { "default": "{{EscapeJson(username)}} (GitHub)" },
              "provider": "{{GitHubProviderUri}}",
              "user-name": "{{EscapeJson(username)}}"
            }
            """;

        using var entityDataDocument = JsonDocument.Parse(entityDataJson);

        await this.dataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = $"Auto-created user-account for GitHub user '{username}' (issue #724).",
                    },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = usedEntityId,
                        ConcurrencyTag = current?.ConcurrencyTag,
                        EntityChangeMode = EntityChangeMode.Replace,
                        Data = entityDataDocument.RootElement.Clone(),
                    },
                ],
            },
            cancellationToken).ConfigureAwait(false);

        this.logger.LogInformation("Upserted user-account entity for GitHub user '{Username}'.", username);
    }

    private static bool EntityHasCorrectData(JsonElement? data, string expectedProvider, string expectedUserName)
    {
        if (data is not { } element)
        {
            return false;
        }

        if (!element.TryGetProperty("provider", out var providerEl)
            || !string.Equals(providerEl.GetString(), expectedProvider, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!element.TryGetProperty("user-name", out var userNameEl)
            || !string.Equals(userNameEl.GetString(), expectedUserName, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static string BuildNamesJson(EntityName name)
    {
        var jsonArrayInner = string.Join(", ", System.Linq.Enumerable.Select(name.Components, c => $"\"{EscapeJson(c)}\""));
        return $"[{jsonArrayInner}]";
    }

    private static string EscapeJson(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
