using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Data.Tests;

// Issue #1403: agent-session entity authoring (names, display name, parameter-values, host-profile)
// was extracted out of the GUI AgentSessionShortcutContext into the data-layer
// AgentSessionEntityFactory. This guards that the extracted factory keeps the issue #1397 behavior:
// JSON-safe assembly (free text with quotes/backslashes round-trips) plus the human-readable local
// time + computer name in the display name, and that optional fields are set only when present.
public sealed class AgentSessionEntityFactoryTests
{
    private static readonly DateTimeOffset TestInstant = new(2026, 9, 2, 18, 42, 0, TimeSpan.Zero);

    [Fact]
    public void AgentSessionEntityFactory_CreateEntityData_EscapesFreeTextAndSetsOptionalFields()
    {
        var definitionId = new EntityId();
        var sessionName = new EntityName("tests", "agent-sessions", "session-1");

        var data = AgentSessionEntityFactory.CreateEntityData(
            agentDefinitionEntityId: definitionId,
            agentDisplayName: "Weird \"Agent\" \\ Name",
            agentSessionId: "abc123",
            agentSessionNames: new[] { sessionName },
            currentTime: TestInstant,
            computerName: "PC\"WITH\"QUOTES",
            parameterValues: new Dictionary<string, string> { ["topic"] = "quotes \"and\" slashes \\" },
            hostProfileEntityId: definitionId);

        // The document round-trips as valid JSON and preserves the special characters verbatim.
        var displayName = data.GetProperty("display-name").GetProperty("default").GetString();
        Assert.NotNull(displayName);
        Assert.Contains("Weird \"Agent\" \\ Name", displayName!);
        Assert.Contains("PC\"WITH\"QUOTES", displayName!);

        // #1397: the display name includes the human-readable local creation time.
        var expectedLocalTime = TestInstant.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);
        Assert.Contains(expectedLocalTime, displayName!);

        Assert.Equal(definitionId.ToString(), data.GetProperty("agent-source-entity-id").GetString());
        Assert.Equal("abc123", data.GetProperty("agent-session-id").GetString());
        Assert.Equal("quotes \"and\" slashes \\", data.GetProperty("parameter-values").GetProperty("topic").GetString());
        Assert.Equal(definitionId.ToString(), data.GetProperty("host-profile-entity-id").GetString());

        var entityTypes = data.GetProperty("entity-types");
        Assert.Equal("entity", entityTypes[0].GetString());
        Assert.Equal("agent-session", entityTypes[1].GetString());
    }

    [Fact]
    public void AgentSessionEntityFactory_CreateEntityData_OmitsOptionalFieldsWhenAbsent()
    {
        var data = AgentSessionEntityFactory.CreateEntityData(
            agentDefinitionEntityId: new EntityId(),
            agentDisplayName: "Plain",
            agentSessionId: "s1",
            agentSessionNames: new[] { new EntityName("tests", "agent-sessions", "session-2") },
            currentTime: TestInstant,
            computerName: "HOST");

        Assert.False(data.TryGetProperty("parameter-values", out _));
        Assert.False(data.TryGetProperty("host-profile-entity-id", out _));
    }

    [Fact]
    public void AgentSessionEntityFactory_CreateSessionSimpleName_EmbedsSanitizedComputerAndSessionId()
    {
        var simpleName = AgentSessionEntityFactory.CreateSessionSimpleName(
            agentSessionId: "sess42",
            currentTime: TestInstant,
            computerName: "JROWE-TEST-PC");

        Assert.Contains("2026-09-02-18-42-00", simpleName);
        Assert.Contains("jrowe-test-pc", simpleName);
        Assert.Contains("sess42", simpleName);
    }
}
