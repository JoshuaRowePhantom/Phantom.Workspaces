using System.Text.Json;
using Phantom.Workspaces.Testing;

namespace Phantom.Workspaces.Data.Tests;

public sealed class ValidatingEntitySeedFixtureTests
{
    [Fact]
    public async Task ValidatingEntitySeedFixture_SeedValidEntity_PersistsThroughValidatingPipeline()
    {
        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        var documents = CreateIdentityDocuments();

        var entityIds = await fixture.SeedManyValidAsync(documents);

        var profileId = entityIds[2];
        var result = await fixture.DataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities = [new GetEntityRequest { EntityId = profileId }],
                Timestamps = [null],
            });
        var profile = Assert.Single(Assert.Single(result.Batches).Entities);
        Assert.Equal(profileId, profile.EntityId);
        Assert.Equal("user-computer-profile", profile.Data!.Value.GetProperty("entity-types")[1].GetString());
    }

    [Fact]
    public async Task ValidatingEntitySeedFixture_SeedProfileWithConnectionDescriptor_IsRejected()
    {
        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        await fixture.SeedManyValidAsync(CreateIdentityDocuments()[..2]);
        using var document = JsonDocument.Parse(
            """
            {
              "entity-id": "10000000-0000-4000-8000-000000000003",
              "entity-types": ["entity", "user-computer-profile"],
              "computer-reference": ["computers", "name", "canonical"],
              "user-reference": ["users", "username", "canonical"],
              "connection-descriptor": { "type": "local" }
            }
            """);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.SeedValidEntityAsync(document.RootElement));

        Assert.Contains("unevaluated", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidatingEntitySeedFixture_SeedProfileMissingRequiredReferences_IsRejected()
    {
        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        using var document = JsonDocument.Parse(
            """
            {
              "entity-id": "10000000-0000-4000-8000-000000000003",
              "entity-types": ["entity", "user-computer-profile"]
            }
            """);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.SeedValidEntityAsync(document.RootElement));

        Assert.Contains("required", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidatingEntitySeedFixture_SeedRawForMalformedInputTest_BypassesValidator()
    {
        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        using var document = JsonDocument.Parse(
            """
            {
              "entity-id": "10000000-0000-4000-8000-000000000004",
              "entity-types": ["entity", "user-computer-profile"],
              "not-in-the-schema": true
            }
            """);

        await fixture.SeedRawEntityForMalformedInputTestAsync(document.RootElement);

        var result = await fixture.DataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityId = new EntityId("10000000-0000-4000-8000-000000000004"),
                    },
                ],
                Timestamps = [null],
            });
        Assert.Single(Assert.Single(result.Batches).Entities);
    }

    [Fact]
    public async Task ValidatingEntitySeedFixture_SeedManyValid_WithRelationships_AllValidate()
    {
        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        var documents = CreateIdentityDocuments()
            .Concat(
                ParseDocuments(
                    """
                    {
                      "entity-id": "10000000-0000-4000-8000-000000000004",
                      "entity-types": ["entity", "agent-session"],
                      "agent-session-id": "canonical-session"
                    }
                    """,
                    """
                    {
                      "entity-id": "10000000-0000-4000-8000-000000000005",
                      "entity-types": ["entity", "relationship", "default"],
                      "participants": {
                        "applied-to": "10000000-0000-4000-8000-000000000004",
                        "value": "10000000-0000-4000-8000-000000000003"
                      },
                      "note": "Canonical session is hosted by the canonical profile."
                    }
                    """))
            .ToArray();

        var entityIds = await fixture.SeedManyValidAsync(documents);

        Assert.Equal(5, entityIds.Count);
    }

    [Fact]
    public async Task ValidatingEntitySeedFixture_AgentSessionRuntimeFields_ValidateOnReplace()
    {
        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        var sessionId = new EntityId("10000000-0000-4000-8000-000000000006");
        await fixture.SeedValidEntityAsync(
            JsonDocument.Parse(
                $$"""
                {
                  "entity-id": "{{sessionId}}",
                  "entity-types": ["entity", "agent-session"],
                  "agent-session-id": "runtime-fields"
                }
                """).RootElement);
        var snapshot = Assert.Single(
            Assert.Single(
                (await fixture.DataAccessLayer.GetAsync(
                    new GetRequest
                    {
                        Entities = [new GetEntityRequest { EntityId = sessionId }],
                        Timestamps = [null],
                    })).Batches).Entities);
        var values = snapshot.Data!.Value.EnumerateObject().ToDictionary(
            property => property.Name,
            property => (object?)property.Value.Clone(),
            StringComparer.Ordinal);
        values["owning-profile-entity-id"] =
            "20000000-0000-4000-8000-000000000001";
        values["runtime-state"] = "running";
        values["runtime-epoch"] =
            "30000000-0000-4000-8000-000000000001";
        values["runtime-lease-expiry"] =
            "2026-09-23T12:00:00.0000000+00:00";

        var result = await fixture.DataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown { Text = "Persist runtime lease fields." },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = sessionId,
                        ConcurrencyTag = snapshot.ConcurrencyTag,
                        Data = JsonSerializer.SerializeToElement(values),
                        EntityChangeMode = EntityChangeMode.Replace,
                    },
                ],
            });

        Assert.Empty(Assert.Single(result.EntityResults).Errors);
    }

    [Fact]
    public async Task CanonicalFixtures_AllSeededEntityTypes_ValidateThroughProductionPipeline()
    {
        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        var documents = CreateIdentityDocuments()
            .Concat(
                ParseDocuments(
                    """
                    {
                      "entity-id": "20000000-0000-4000-8000-000000000001",
                      "entity-types": ["entity", "agent-session"],
                      "agent-session-id": "canonical-session"
                    }
                    """,
                    """
                    {
                      "entity-id": "20000000-0000-4000-8000-000000000002",
                      "entity-types": ["entity", "tool"],
                      "tool-type": "CanonicalTool"
                    }
                    """,
                    """
                    {
                      "entity-id": "20000000-0000-4000-8000-000000000003",
                      "entity-types": ["entity", "agent-definition"],
                      "definition": {
                        "kind": "prompt",
                        "name": "canonical-agent",
                        "model": { "id": "echo", "provider": "echo" },
                        "tools": []
                      }
                    }
                    """,
                    """
                    {
                      "entity-id": "20000000-0000-4000-8000-000000000004",
                      "entity-types": ["entity", "user-account"],
                      "provider": "https://github.com",
                      "user-name": "canonical"
                    }
                    """,
                    """
                    {
                      "entity-id": "20000000-0000-4000-8000-000000000005",
                      "entity-types": ["entity", "relationship", "default"],
                      "participants": {
                        "applied-to": "10000000-0000-4000-8000-000000000001",
                        "value": "10000000-0000-4000-8000-000000000002"
                      },
                      "note": "Canonical relationship."
                    }
                    """))
            .ToArray();

        var entityIds = await fixture.SeedManyValidAsync(documents);

        Assert.Equal(8, entityIds.Count);
    }

    private static JsonElement[] CreateIdentityDocuments()
        => ParseDocuments(
            """
            {
              "entity-id": "10000000-0000-4000-8000-000000000001",
              "entity-types": ["entity", "user"],
              "names": [["users", "username", "canonical"]]
            }
            """,
            """
            {
              "entity-id": "10000000-0000-4000-8000-000000000002",
              "entity-types": ["entity", "computer"],
              "names": [["computers", "name", "canonical"]]
            }
            """,
            """
            {
              "entity-id": "10000000-0000-4000-8000-000000000003",
              "entity-types": ["entity", "user-computer-profile"],
              "computer-reference": ["computers", "name", "canonical"],
              "user-reference": ["users", "username", "canonical"]
            }
            """);

    private static JsonElement[] ParseDocuments(params string[] json)
        => json.Select(static text => JsonDocument.Parse(text).RootElement.Clone()).ToArray();
}
