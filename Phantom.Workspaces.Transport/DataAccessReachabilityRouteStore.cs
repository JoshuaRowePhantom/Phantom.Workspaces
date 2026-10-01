using System.Globalization;
using System.Text.Json;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Transport;

public sealed class DataAccessReachabilityRouteStore : IReachabilityRouteStore
{
    private const int MaxConcurrencyAttempts = 8;
    private static readonly string[] CredentialMarkers =
    [
        "access_token",
        "access-token",
        "api_key",
        "api-key",
        "apikey",
        "authorization",
        "bearer",
        "credential",
        "password",
        "secret",
        "token",
    ];

    private readonly IDataAccessLayer dataAccessLayer;

    public DataAccessReachabilityRouteStore(IDataAccessLayer dataAccessLayer)
    {
        this.dataAccessLayer = dataAccessLayer ?? throw new ArgumentNullException(nameof(dataAccessLayer));
    }

    public async Task<IReadOnlyList<ReachabilityRoute>> GetRoutesAsync(
        EntityId profileEntityId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await this.GetRequiredSnapshotAsync(profileEntityId, cancellationToken).ConfigureAwait(false);
        return ReadRoutes(snapshot.Data);
    }

    public async Task UpsertRouteAsync(
        EntityId profileEntityId,
        ReachabilityRoute route,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        var normalized = NormalizeAndValidate(profileEntityId, route);

        for (var attempt = 0; attempt < MaxConcurrencyAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await this.GetRequiredSnapshotAsync(profileEntityId, cancellationToken).ConfigureAwait(false);
            var reachability = default(JsonElement);
            var routes = default(JsonElement);
            var hasReachability = snapshot.Data is JsonElement data
                && data.TryGetProperty("reachability", out reachability)
                && reachability.ValueKind == JsonValueKind.Object;
            var hasRoutes = hasReachability
                && reachability.TryGetProperty("routes", out routes)
                && routes.ValueKind == JsonValueKind.Object;
            var hasRoute = hasRoutes && routes.TryGetProperty(normalized.RouteId, out _);
            if (hasRoute)
            {
                var existing = ReadRoutes(snapshot.Data).FirstOrDefault(
                    item => string.Equals(item.RouteId, normalized.RouteId, StringComparison.Ordinal));
                if (existing is null || existing.OwnerProfileEntityId != profileEntityId)
                    throw new UnauthorizedAccessException("Reachability route owner mismatch.");
            }

            JsonElement patch;
            if (!hasReachability)
            {
                patch = JsonSerializer.SerializeToElement(
                    new object[]
                    {
                        new
                        {
                            op = "add",
                            path = "/reachability",
                            value = new
                            {
                                routes = new Dictionary<string, object>
                                {
                                    [normalized.RouteId] = ToPersistedValue(normalized),
                                },
                            },
                        },
                    });
            }
            else if (!hasRoutes)
            {
                patch = JsonSerializer.SerializeToElement(
                    new object[]
                    {
                        new
                        {
                            op = "add",
                            path = "/reachability/routes",
                            value = new Dictionary<string, object>
                            {
                                [normalized.RouteId] = ToPersistedValue(normalized),
                            },
                        },
                    });
            }
            else
            {
                patch = JsonSerializer.SerializeToElement(
                    new object[]
                    {
                        new
                        {
                            op = hasRoute ? "replace" : "add",
                            path = $"/reachability/routes/{EscapePointer(normalized.RouteId)}",
                            value = ToPersistedValue(normalized),
                        },
                    });
            }

            if (await this.TryApplyPatchAsync(profileEntityId, snapshot.ConcurrencyTag, patch, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }

        throw new ReachabilityRouteStoreException(
            $"Reachability route '{route.RouteId}' could not be persisted after concurrent updates.");
    }

    public async Task RemoveRouteAsync(
        EntityId profileEntityId,
        string routeId,
        EntityId ownerProfileEntityId,
        CancellationToken cancellationToken = default)
    {
        ValidateRouteId(routeId);
        for (var attempt = 0; attempt < MaxConcurrencyAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await this.GetRequiredSnapshotAsync(profileEntityId, cancellationToken).ConfigureAwait(false);
            var existing = ReadRoutes(snapshot.Data).FirstOrDefault(
                route => string.Equals(route.RouteId, routeId, StringComparison.Ordinal));
            if (existing is null)
            {
                return;
            }

            if (existing.OwnerProfileEntityId != ownerProfileEntityId)
            {
                throw new UnauthorizedAccessException(
                    $"Reachability route '{routeId}' is not owned by profile '{ownerProfileEntityId}'.");
            }

            var patch = JsonSerializer.SerializeToElement(
                new object[]
                {
                    new
                    {
                        op = "remove",
                        path = $"/reachability/routes/{EscapePointer(routeId)}",
                    },
                });
            if (await this.TryApplyPatchAsync(profileEntityId, snapshot.ConcurrencyTag, patch, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }

        throw new ReachabilityRouteStoreException(
            $"Reachability route '{routeId}' could not be removed after concurrent updates.");
    }

    public async Task ClearOwnedRoutesAsync(
        EntityId profileEntityId,
        EntityId ownerProfileEntityId,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < MaxConcurrencyAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await this.GetRequiredSnapshotAsync(profileEntityId, cancellationToken).ConfigureAwait(false);
            var ownedRouteIds = ReadRoutes(snapshot.Data)
                .Where(route => route.OwnerProfileEntityId == ownerProfileEntityId)
                .Select(static route => route.RouteId)
                .OrderBy(static routeId => routeId, StringComparer.Ordinal)
                .ToArray();
            if (ownedRouteIds.Length == 0)
            {
                return;
            }

            var patch = JsonSerializer.SerializeToElement(
                ownedRouteIds.Select(
                    routeId => new
                    {
                        op = "remove",
                        path = $"/reachability/routes/{EscapePointer(routeId)}",
                    }));
            if (await this.TryApplyPatchAsync(profileEntityId, snapshot.ConcurrencyTag, patch, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }

        throw new ReachabilityRouteStoreException(
            $"Owned reachability routes for profile '{profileEntityId}' could not be cleared after concurrent updates.");
    }

    public static string NormalizeEndpoint(string endpoint)
        => NormalizeEndpoint(endpoint, "endpoint");

    public static string NormalizeEndpoint(string endpoint, string field)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            foreach (var scheme in new[] { "https://", "http://" })
            {
                if (endpoint is not null && endpoint.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                {
                    var authority = endpoint.AsSpan(scheme.Length);
                    if (authority.IsEmpty || authority[0] is '/' or '?' or '#')
                        throw new RouteValidationException("endpoint.missing-host", field);
                }
            }
            throw new RouteValidationException("endpoint.nonabsolute", field);
        }
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            throw new RouteValidationException("endpoint.invalid-scheme", field);
        }
        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new RouteValidationException("endpoint.missing-host", field);
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new RouteValidationException("endpoint.userinfo", field);
        }

        var credentialSurface = $"{uri.Query}#{uri.Fragment}".ToLowerInvariant();
        if (CredentialMarkers.Any(credentialSurface.Contains))
        {
            throw new RouteValidationException("endpoint.credential-query-or-fragment", field);
        }

        return uri.AbsoluteUri;
    }

    private static ReachabilityRoute NormalizeAndValidate(
        EntityId profileEntityId,
        ReachabilityRoute route)
    {
        ValidateRouteId(route.RouteId);
        if (route.OwnerProfileEntityId != profileEntityId)
        {
            throw new UnauthorizedAccessException(
                $"Profile '{route.OwnerProfileEntityId}' cannot publish a route on profile '{profileEntityId}'.");
        }

        if (route.Priority is < 0 or > 1000)
        {
            throw new RouteValidationException("route.priority.out-of-range", "route.priority");
        }

        if (route.ExpiresAt <= route.LastConfirmed)
        {
            throw new RouteValidationException("route.expiry.not-after-confirmation", "route.expires-at");
        }

        if (route.Descriptor.ValueKind != JsonValueKind.Object)
        {
            throw new RouteValidationException("descriptor.shape.invalid", "descriptor");
        }
        if (!route.Descriptor.TryGetProperty("type", out var typeProperty)
            || typeProperty.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(typeProperty.GetString()))
            throw new RouteValidationException("descriptor.type.missing", "descriptor.type");
        var type = typeProperty.GetString()!;

        JsonElement descriptor;
        if (string.Equals(type, "http", StringComparison.Ordinal))
        {
            if (!string.Equals(route.RouteId, "direct-http", StringComparison.Ordinal))
                throw new RouteValidationException("descriptor.type.route-mismatch", "descriptor.type");
            if (!route.Descriptor.TryGetProperty("url", out var urlProperty)
                || urlProperty.ValueKind != JsonValueKind.String
                || urlProperty.GetString() is not { Length: > 0 } url)
            {
                throw new RouteValidationException("endpoint.missing", "descriptor.url");
            }

            descriptor = JsonSerializer.SerializeToElement(
                new Dictionary<string, object>
                {
                    ["type"] = "http",
                    ["url"] = NormalizeEndpoint(url, "descriptor.url"),
                });
        }
        else if (string.Equals(type, "reverse-http", StringComparison.Ordinal))
        {
            if (!route.RouteId.StartsWith("reverse-http:", StringComparison.Ordinal))
                throw new RouteValidationException("descriptor.type.route-mismatch", "descriptor.type");
            if (!route.Descriptor.TryGetProperty("entity-id", out var descriptorEntityId)
                || descriptorEntityId.ValueKind != JsonValueKind.String
                || !Guid.TryParse(descriptorEntityId.GetString(), out var descriptorEntityGuid))
                throw new RouteValidationException("descriptor.entity-id.missing-or-invalid", "descriptor.entity-id");
            if (new EntityId(descriptorEntityGuid) != profileEntityId)
                throw new RouteValidationException("descriptor.entity-id.target-mismatch", "descriptor.entity-id");
            if (!route.Descriptor.TryGetProperty("hub-urls", out var urls)
                || urls.ValueKind != JsonValueKind.Array)
                throw new RouteValidationException("descriptor.hub-urls.invalid-count", "descriptor.hub-urls");

            if (urls.GetArrayLength() is < 1 or > 8
                || urls.EnumerateArray().Any(static url => url.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(url.GetString())))
                throw new RouteValidationException("descriptor.hub-urls.invalid-count", "descriptor.hub-urls");
            var normalizedUrls = urls.EnumerateArray()
                .Select(static url => url.ValueKind == JsonValueKind.String ? url.GetString() : null)
                .Select(static url => NormalizeEndpoint(url!, "descriptor.hub-urls[]"))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (normalizedUrls.Length is < 1 or > 8)
            {
                throw new RouteValidationException("descriptor.hub-urls.invalid-count", "descriptor.hub-urls");
            }

            descriptor = JsonSerializer.SerializeToElement(
                new Dictionary<string, object>
                {
                    ["type"] = "reverse-http",
                    ["hub-urls"] = normalizedUrls,
                    ["entity-id"] = profileEntityId.ToString(),
                });
        }
        else
        {
            throw new RouteValidationException("descriptor.type.unsupported", "descriptor.type");
        }

        return route with { Descriptor = descriptor };
    }

    private static void ValidateRouteId(string routeId)
    {
        if (string.IsNullOrWhiteSpace(routeId))
            throw new RouteValidationException("route-id.missing", "route-id");
        if (string.Equals(routeId, "direct-http", StringComparison.Ordinal))
        {
            return;
        }

        const string prefix = "reverse-http:";
        if (routeId.StartsWith(prefix, StringComparison.Ordinal)
            && Guid.TryParse(routeId[prefix.Length..], out _))
        {
            return;
        }

        throw new RouteValidationException("route-id.invalid", "route-id");
    }

    private static object ToPersistedValue(ReachabilityRoute route)
        => new Dictionary<string, object>
        {
            ["descriptor"] = route.Descriptor,
            ["owner-profile-entity-id"] = route.OwnerProfileEntityId.ToString(),
            ["priority"] = route.Priority,
            ["last-confirmed"] = route.LastConfirmed.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["expires-at"] = route.ExpiresAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        };

    private static IReadOnlyList<ReachabilityRoute> ReadRoutes(JsonElement? data)
    {
        if (data is not JsonElement entity
            || !entity.TryGetProperty("reachability", out var reachability)
            || reachability.ValueKind != JsonValueKind.Object
            || !reachability.TryGetProperty("routes", out var routes)
            || routes.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var result = new List<ReachabilityRoute>();
        foreach (var property in routes.EnumerateObject())
        {
            var value = property.Value;
            if (value.ValueKind != JsonValueKind.Object
                || !value.TryGetProperty("descriptor", out var descriptor)
                || descriptor.ValueKind != JsonValueKind.Object
                || !value.TryGetProperty("owner-profile-entity-id", out var owner)
                || !Guid.TryParse(owner.GetString(), out var ownerId)
                || !value.TryGetProperty("last-confirmed", out var lastConfirmedProperty)
                || !DateTimeOffset.TryParse(
                    lastConfirmedProperty.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var lastConfirmed)
                || !value.TryGetProperty("expires-at", out var expiresAtProperty)
                || !DateTimeOffset.TryParse(
                    expiresAtProperty.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var expiresAt))
            {
                continue;
            }

            var priority = value.TryGetProperty("priority", out var priorityProperty)
                && priorityProperty.TryGetInt32(out var parsedPriority)
                    ? parsedPriority
                    : 100;
            result.Add(
                new ReachabilityRoute
                {
                    RouteId = property.Name,
                    Descriptor = descriptor.Clone(),
                    OwnerProfileEntityId = new EntityId(ownerId),
                    Priority = priority,
                    LastConfirmed = lastConfirmed,
                    ExpiresAt = expiresAt,
                });
        }

        return result;
    }

    private async Task<EntitySnapshot> GetRequiredSnapshotAsync(
        EntityId profileEntityId,
        CancellationToken cancellationToken)
    {
        GetResult result;
        try
        {
            result = await this.dataAccessLayer.GetAsync(
                new GetRequest
                {
                    Entities = [new GetEntityRequest { EntityId = profileEntityId }],
                    Timestamps = [null],
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new ReachabilityRouteStoreException(
                $"Reachability profile '{profileEntityId}' could not be read.",
                exception);
        }
        catch (IOException exception)
        {
            throw new ReachabilityRouteStoreException(
                $"Reachability profile '{profileEntityId}' could not be read.",
                exception);
        }
        catch (TimeoutException exception)
        {
            throw new ReachabilityRouteStoreException(
                $"Reachability profile '{profileEntityId}' could not be read.",
                exception);
        }
        var snapshot = result.Batches
            .SelectMany(static batch => batch.Entities)
            .FirstOrDefault(entity => entity.EntityId == profileEntityId);
        if (snapshot?.Data is null)
        {
            throw new ReachabilityRouteStoreException($"User computer profile entity '{profileEntityId}' could not be resolved.");
        }

        return snapshot;
    }

    private async Task<bool> TryApplyPatchAsync(
        EntityId profileEntityId,
        ConcurrencyTag? concurrencyTag,
        JsonElement patch,
        CancellationToken cancellationToken)
    {
        UpdateResult result;
        try
        {
            result = await this.dataAccessLayer.UpdateAsync(
                new UpdateRequest
                {
                    UpdateMetadata = new UpdateMetadata
                    {
                        Comment = new Markdown { Text = "Publish user-computer-profile reachability route." },
                    },
                    Changes =
                    [
                        new EntityChange
                        {
                            EntityId = profileEntityId,
                            ConcurrencyTag = concurrencyTag,
                            Data = patch,
                            EntityChangeMode = EntityChangeMode.JsonPatch,
                        },
                    ],
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new ReachabilityRouteStoreException(
                $"Reachability update for profile '{profileEntityId}' could not be sent.",
                exception);
        }
        catch (IOException exception)
        {
            throw new ReachabilityRouteStoreException(
                $"Reachability update for profile '{profileEntityId}' could not be sent.",
                exception);
        }
        catch (TimeoutException exception)
        {
            throw new ReachabilityRouteStoreException(
                $"Reachability update for profile '{profileEntityId}' timed out.",
                exception);
        }
        var entityResult = result.EntityResults.FirstOrDefault(
            item => item.RequestedEntityId == profileEntityId);
        if (entityResult is null)
        {
            throw new ReachabilityRouteStoreException($"Reachability update for profile '{profileEntityId}' returned no result.");
        }

        if (entityResult.UpdateState != UpdateState.Failed && entityResult.Errors.Count == 0)
        {
            return true;
        }

        if (entityResult.CurrentEntity is not null
            && entityResult.ConcurrencyMatchState == ConcurrencyMatchState.NotMatched)
        {
            return false;
        }

        throw new ReachabilityRouteStoreException(
            $"Reachability update for profile '{profileEntityId}' failed: "
            + string.Join("; ", entityResult.Errors.Select(static error => error.Message)));
    }

    private static string EscapePointer(string value)
        => value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}
