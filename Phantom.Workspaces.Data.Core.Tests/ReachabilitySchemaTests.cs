using System.Text.Json;
using Phantom.Workspaces.Testing;

namespace Phantom.Workspaces.Data.Tests;

public sealed class ReachabilitySchemaTests
{
    [Fact]
    public async Task UserComputerProfileSchema_ValidDirectHttpRoute_IsAccepted()
        => await AssertProfileAcceptedAsync(
            """
            "direct-http": {
              "descriptor": { "type": "http", "url": "https://machine.example/" },
              "owner-profile-entity-id": "10000000-0000-4000-8000-000000000003",
              "priority": 50,
              "last-confirmed": "2026-09-18T18:00:00Z",
              "expires-at": "2026-09-18T18:02:00Z"
            }
            """);

    [Fact]
    public async Task UserComputerProfileSchema_ValidReverseHttpRoute_IsAccepted()
        => await AssertProfileAcceptedAsync(
            """
            "reverse-http:20000000-0000-4000-8000-000000000001": {
              "descriptor": {
                "type": "reverse-http",
                "hub-urls": ["https://hub.example/reverse"],
                "entity-id": "10000000-0000-4000-8000-000000000003"
              },
              "owner-profile-entity-id": "10000000-0000-4000-8000-000000000003",
              "last-confirmed": "2026-09-18T18:00:00Z",
              "expires-at": "2026-09-18T18:02:00Z"
            }
            """);

    [Fact]
    public async Task UserComputerProfileSchema_ReachabilityWithZeroRoutes_IsAccepted()
        => await AssertProfileAcceptedAsync(string.Empty);

    [Fact]
    public async Task ReachableTransportDescriptorSchema_UnknownType_IsRejected()
        => await AssertDescriptorRejectedAsync("""{ "type": "quic" }""");

    [Fact]
    public async Task ReachableTransportDescriptorSchema_LocalTypePersisted_IsRejected()
        => await AssertDescriptorRejectedAsync("""{ "type": "local" }""");

    [Fact]
    public async Task ReachableTransportDescriptorSchema_HttpMissingUrl_IsRejected()
        => await AssertDescriptorRejectedAsync("""{ "type": "http" }""");

    [Theory]
    [InlineData("""{ "type": "reverse-http", "entity-id": "10000000-0000-4000-8000-000000000003" }""")]
    [InlineData("""{ "type": "reverse-http", "hub-urls": ["https://hub.example/"] }""")]
    public async Task ReachableTransportDescriptorSchema_ReverseHttpMissingHubUrlsOrEntityId_IsRejected(string descriptor)
        => await AssertDescriptorRejectedAsync(descriptor);

    [Theory]
    [InlineData("""{ "type": "http", "url": "https://machine.example/", "authorization": "secret" }""")]
    [InlineData("""{ "type": "reverse-http", "hub-urls": ["https://hub.example/"], "entity-id": "10000000-0000-4000-8000-000000000003", "headers": {} }""")]
    [InlineData("""{ "type": "http", "url": "https://machine.example/", "other": true }""")]
    public async Task ReachableTransportDescriptorSchema_ExtraPropertyOnAnyBranch_IsRejected(string descriptor)
        => await AssertDescriptorRejectedAsync(descriptor);

    [Theory]
    [InlineData("""{ "type": "http", "url": "https://user:pass@machine.example/" }""")]
    [InlineData("""{ "type": "reverse-http", "hub-urls": ["https://user:pass@hub.example/"], "entity-id": "10000000-0000-4000-8000-000000000003" }""")]
    public async Task ReachableTransportDescriptorSchema_CredentialBearingUrlUserinfo_IsRejected(string descriptor)
        => await AssertDescriptorRejectedAsync(descriptor);

    [Fact]
    public async Task ReachableTransportDescriptorSchema_DuplicateHubUrl_IsRejected()
        => await AssertDescriptorRejectedAsync(
            """
            {
              "type": "reverse-http",
              "hub-urls": ["https://hub.example/", "https://hub.example/"],
              "entity-id": "10000000-0000-4000-8000-000000000003"
            }
            """);

    [Fact]
    public async Task ReachableTransportDescriptorSchema_HubUrlsExceedsMax_IsRejected()
        => await AssertDescriptorRejectedAsync(
            """
            {
              "type": "reverse-http",
              "hub-urls": [
                "https://hub1.example/", "https://hub2.example/", "https://hub3.example/",
                "https://hub4.example/", "https://hub5.example/", "https://hub6.example/",
                "https://hub7.example/", "https://hub8.example/", "https://hub9.example/"
              ],
              "entity-id": "10000000-0000-4000-8000-000000000003"
            }
            """);

    [Theory]
    [InlineData("descriptor")]
    [InlineData("owner-profile-entity-id")]
    [InlineData("last-confirmed")]
    [InlineData("expires-at")]
    public async Task ReachabilityRouteSchema_MissingRequiredLifecycleField_IsRejected(string field)
    {
        var route = Parse(
            """
            {
              "descriptor": { "type": "http", "url": "https://machine.example/" },
              "owner-profile-entity-id": "10000000-0000-4000-8000-000000000003",
              "last-confirmed": "2026-09-18T18:00:00Z",
              "expires-at": "2026-09-18T18:02:00Z"
            }
            """);
        var properties = route.EnumerateObject()
            .Where(property => !string.Equals(property.Name, field, StringComparison.Ordinal))
            .ToDictionary(static property => property.Name, static property => (object)property.Value.Clone());

        await AssertDescriptorRejectedAsync(JsonSerializer.Serialize(properties), descriptorIsRoute: true);
    }

    [Fact]
    public async Task ReachabilityRouteSchema_ExtraProperty_IsRejected()
        => await AssertDescriptorRejectedAsync(
            """
            {
              "descriptor": { "type": "http", "url": "https://machine.example/" },
              "owner-profile-entity-id": "10000000-0000-4000-8000-000000000003",
              "last-confirmed": "2026-09-18T18:00:00Z",
              "expires-at": "2026-09-18T18:02:00Z",
              "secret": "no"
            }
            """,
            descriptorIsRoute: true);

    [Theory]
    [InlineData("last-confirmed")]
    [InlineData("expires-at")]
    public async Task ReachabilityRouteSchema_MalformedTimestamp_IsRejected(string field)
    {
        var lastConfirmed = field == "last-confirmed" ? "not-a-date" : "2026-09-18T18:00:00Z";
        var expiresAt = field == "expires-at" ? "not-a-date" : "2026-09-18T18:02:00Z";
        await AssertDescriptorRejectedAsync(
            $$"""
            {
              "descriptor": { "type": "http", "url": "https://machine.example/" },
              "owner-profile-entity-id": "10000000-0000-4000-8000-000000000003",
              "last-confirmed": "{{lastConfirmed}}",
              "expires-at": "{{expiresAt}}"
            }
            """,
            descriptorIsRoute: true);
    }

    [Fact]
    public async Task ReachabilityRouteSchema_MalformedOwnerUuid_IsRejected()
        => await AssertDescriptorRejectedAsync(
            """
            {
              "descriptor": { "type": "http", "url": "https://machine.example/" },
              "owner-profile-entity-id": "not-a-uuid",
              "last-confirmed": "2026-09-18T18:00:00Z",
              "expires-at": "2026-09-18T18:02:00Z"
            }
            """,
            descriptorIsRoute: true);

    [Theory]
    [InlineData(-1)]
    [InlineData(1001)]
    public async Task ReachabilityRouteSchema_PriorityOutOfRange_IsRejected(int priority)
        => await AssertDescriptorRejectedAsync(
            $$"""
            {
              "descriptor": { "type": "http", "url": "https://machine.example/" },
              "owner-profile-entity-id": "10000000-0000-4000-8000-000000000003",
              "priority": {{priority}},
              "last-confirmed": "2026-09-18T18:00:00Z",
              "expires-at": "2026-09-18T18:02:00Z"
            }
            """,
            descriptorIsRoute: true);

    [Theory]
    [InlineData("reverse-http:not-a-uuid")]
    [InlineData("")]
    [InlineData("direct-https")]
    public async Task UserComputerProfileSchema_MalformedRouteId_IsRejected(string routeId)
        => await AssertProfileRejectedAsync(
            $$"""
            "{{routeId}}": {
              "descriptor": { "type": "http", "url": "https://machine.example/" },
              "owner-profile-entity-id": "10000000-0000-4000-8000-000000000003",
              "last-confirmed": "2026-09-18T18:00:00Z",
              "expires-at": "2026-09-18T18:02:00Z"
            }
            """);

    [Fact]
    public async Task UserComputerProfileSchema_ReachabilityMissingRoutesKey_IsRejected()
        => await AssertProfileRejectedAsync(routeEntries: null);

    [Fact]
    public async Task UserComputerProfileSchema_RouteCountExceedsMaximum_IsRejected()
    {
        var routes = Enumerable.Range(1, 17)
            .Select(index =>
                $$"""
                "reverse-http:{{index:x8}}-0000-4000-8000-000000000000": {
                  "descriptor": {
                    "type": "reverse-http",
                    "hub-urls": ["https://hub{{index}}.example/"],
                    "entity-id": "10000000-0000-4000-8000-000000000003"
                  },
                  "owner-profile-entity-id": "10000000-0000-4000-8000-000000000003",
                  "last-confirmed": "2026-09-18T18:00:00Z",
                  "expires-at": "2026-09-18T18:02:00Z"
                }
                """);
        await AssertProfileRejectedAsync(string.Join(",", routes));
    }

    [Fact]
    public async Task ExecutorConnectionDescriptorSchema_Local_IsAccepted()
        => await AssertAgentSessionBindingAcceptedAsync("""{ "type": "local" }""");

    [Theory]
    [InlineData("""{ "type": "http", "url": "https://machine.example/" }""", true)]
    [InlineData("""{ "type": "reverse-http", "hub-urls": ["https://hub.example/"], "entity-id": "10000000-0000-4000-8000-000000000003" }""", true)]
    [InlineData("""{ "type": "local" }""", false)]
    [InlineData("""{ "type": "user-computer-profile", "entity-id": "10000000-0000-4000-8000-000000000003" }""", false)]
    public async Task ExecutorConnectionDescriptorSchema_UserComputerProfileWithForceRoute_IsAccepted(
        string forceRoute,
        bool accepted)
    {
        var binding =
            $$"""
            {
              "type": "user-computer-profile",
              "entity-id": "10000000-0000-4000-8000-000000000003",
              "force-route": {{forceRoute}}
            }
            """;
        if (accepted)
        {
            await AssertAgentSessionBindingAcceptedAsync(binding);
        }
        else
        {
            await AssertAgentSessionBindingRejectedAsync(binding);
        }
    }

    [Theory]
    [InlineData("""{ "type": "http", "url": "https://machine.example/" }""")]
    [InlineData("""{ "type": "reverse-http", "hub-urls": ["https://hub.example/"], "entity-id": "10000000-0000-4000-8000-000000000003" }""")]
    public async Task ExecutorConnectionDescriptorSchema_SharesHttpReverseHttpBranchesWithReachability(string descriptor)
    {
        await AssertDescriptorAcceptedAsync(descriptor);
        await AssertAgentSessionBindingAcceptedAsync(descriptor);
    }

    private static Task AssertDescriptorAcceptedAsync(string descriptor)
        => AssertProfileAcceptedAsync(BuildRoute("direct-http", descriptor));

    private static Task AssertDescriptorRejectedAsync(string descriptor, bool descriptorIsRoute = false)
        => AssertProfileRejectedAsync(
            descriptorIsRoute
                ? $$""" "direct-http": {{descriptor}} """
                : BuildRoute("direct-http", descriptor));

    private static string BuildRoute(string routeId, string descriptor)
        => $$"""
             "{{routeId}}": {
               "descriptor": {{descriptor}},
               "owner-profile-entity-id": "10000000-0000-4000-8000-000000000003",
               "last-confirmed": "2026-09-18T18:00:00Z",
               "expires-at": "2026-09-18T18:02:00Z"
             }
             """;

    private static async Task AssertProfileAcceptedAsync(string? routeEntries)
    {
        var fixture = await CreateFixtureWithIdentityAsync();
        await fixture.SeedValidEntityAsync(BuildProfile(routeEntries));
        var result = await fixture.DataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities = [new GetEntityRequest { EntityId = new EntityId("10000000-0000-4000-8000-000000000003") }],
                Timestamps = [null],
            });
        var profile = Assert.Single(Assert.Single(result.Batches).Entities);
        Assert.Equal(JsonValueKind.Object, profile.Data!.Value.GetProperty("reachability").GetProperty("routes").ValueKind);
    }

    private static async Task AssertProfileRejectedAsync(string? routeEntries)
    {
        var fixture = await CreateFixtureWithIdentityAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.SeedValidEntityAsync(BuildProfile(routeEntries)));
    }

    private static async Task AssertAgentSessionBindingAcceptedAsync(string binding)
    {
        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        await fixture.SeedValidEntityAsync(BuildAgentSession(binding));
    }

    private static async Task AssertAgentSessionBindingRejectedAsync(string binding)
    {
        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.SeedValidEntityAsync(BuildAgentSession(binding)));
    }

    private static async Task<ValidatingEntitySeedFixture> CreateFixtureWithIdentityAsync()
    {
        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        await fixture.SeedManyValidAsync(
            [
                Parse(
                    """
                    {
                      "entity-id": "10000000-0000-4000-8000-000000000001",
                      "entity-types": ["entity", "user"],
                      "names": [["users", "username", "reachability"]]
                    }
                    """),
                Parse(
                    """
                    {
                      "entity-id": "10000000-0000-4000-8000-000000000002",
                      "entity-types": ["entity", "computer"],
                      "names": [["computers", "name", "reachability"]]
                    }
                    """),
            ]);
        return fixture;
    }

    private static JsonElement BuildProfile(string? routeEntries)
    {
        var reachability = routeEntries is null
            ? """ "reachability": {} """
            : $$""" "reachability": { "routes": { {{routeEntries}} } } """;
        return Parse(
            $$"""
            {
              "entity-id": "10000000-0000-4000-8000-000000000003",
              "entity-types": ["entity", "user-computer-profile"],
              "computer-reference": ["computers", "name", "reachability"],
              "user-reference": ["users", "username", "reachability"],
              {{reachability}}
            }
            """);
    }

    private static JsonElement BuildAgentSession(string binding)
        => Parse(
            $$"""
            {
              "entity-id": "30000000-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-session"],
              "agent-session-id": "reachability-schema-test",
              "executor-bindings": {
                "session": {{binding}},
                "components": { "worker": {{binding}} }
              }
            }
            """);

    private static JsonElement Parse(string json)
        => JsonDocument.Parse(json).RootElement.Clone();
}
