using System;
using System.Collections.Generic;
using System.Text.Json;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Data.Tests;

/// <summary>
/// Covers the explicit session executor + <c>executor-bindings</c> persistence and resume
/// (issue #1437, per-component-executor-binding): bindings persist on the <c>agent-session</c> entity as
/// connection-descriptor <b>objects</b> under the <c>{ session, components }</c> shape (alongside the
/// unchanged <c>parameter-values</c> / <c>host-profile-entity-id</c>), the typed <c>executor</c> selection
/// round-trips in the sibling <c>parameter-selections</c> key (M7), resume rebuilds the topology from the
/// bindings, and a legacy host-profile-only session derives its session executor (M6).
/// </summary>
public sealed class AgentSessionExecutorBindingsTests
{
    private static readonly DateTimeOffset TestInstant = new(2026, 9, 2, 18, 42, 0, TimeSpan.Zero);
    private const string ProfileUuid = "11111111-2222-3333-4444-555555555555";

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static JsonElement CreateData(
        JsonElement? sessionExecutor = null,
        JsonElement? components = null,
        IReadOnlyDictionary<string, string>? parameterValues = null,
        EntityId? hostProfileEntityId = null,
        IReadOnlyDictionary<string, JsonElement>? parameterSelections = null)
        => AgentSessionEntityFactory.CreateEntityData(
            agentDefinitionEntityId: new EntityId(),
            agentDisplayName: "Executor Session",
            agentSessionId: "sess-1",
            agentSessionNames: new[] { new EntityName("tests", "agent-sessions", "session-1") },
            currentTime: TestInstant,
            computerName: "HOST",
            parameterValues: parameterValues,
            hostProfileEntityId: hostProfileEntityId,
            sessionExecutor: sessionExecutor,
            executorComponentBindings: components,
            parameterSelections: parameterSelections);

    [Fact]
    public void Persist_ExecutorBindings_RoundTrips()
    {
        var components = Parse($"{{\"worker\":{{\"type\":\"user-computer-profile\",\"entity-id\":\"{ProfileUuid}\"}}}}");

        var data = CreateData(sessionExecutor: Parse("""{"type":"local"}"""), components: components);

        var readBindings = AgentSessionExecutorBindings.ReadComponentBindings(data);
        Assert.True(readBindings.ContainsKey("worker"));
        Assert.Equal("user-computer-profile", readBindings["worker"].GetProperty("type").GetString());
        Assert.Equal(ProfileUuid, readBindings["worker"].GetProperty("entity-id").GetString());
    }

    [Fact]
    public void Persist_DescriptorObjects_RoundTrips()
    {
        var components = Parse($"{{\"worker\":{{\"type\":\"user-computer-profile\",\"entity-id\":\"{ProfileUuid}\"}}}}");

        var data = CreateData(components: components);

        // The persisted binding is a descriptor OBJECT, not a bare client-instance string.
        var bindingsRoot = data.GetProperty("executor-bindings");
        var componentValue = bindingsRoot.GetProperty("components").GetProperty("worker");
        Assert.Equal(JsonValueKind.Object, componentValue.ValueKind);
        Assert.Equal("user-computer-profile", componentValue.GetProperty("type").GetString());
    }

    [Fact]
    public void Persist_SessionAndComponentsKeys_RoundTrip()
    {
        var components = Parse("""{"worker":{"type":"local"}}""");
        var parameterValues = new Dictionary<string, string> { ["working-directory"] = "C:\\App" };
        var hostProfile = new EntityId();

        var data = CreateData(
            sessionExecutor: Parse($"{{\"type\":\"user-computer-profile\",\"entity-id\":\"{ProfileUuid}\"}}"),
            components: components,
            parameterValues: parameterValues,
            hostProfileEntityId: hostProfile);

        // The { session, components } root shape round-trips alongside parameter-values and
        // host-profile-entity-id (which are left untouched).
        var bindingsRoot = data.GetProperty("executor-bindings");
        Assert.Equal(ProfileUuid, bindingsRoot.GetProperty("session").GetProperty("entity-id").GetString());
        Assert.Equal("local", bindingsRoot.GetProperty("components").GetProperty("worker").GetProperty("type").GetString());
        Assert.Equal("C:\\App", data.GetProperty("parameter-values").GetProperty("working-directory").GetString());
        Assert.Equal(hostProfile.ToString(), data.GetProperty("host-profile-entity-id").GetString());
    }

    [Fact]
    public void Persist_ExecutorSelection_StoredInParameterSelections()
    {
        var parameterValues = new Dictionary<string, string> { ["working-directory"] = "C:\\App" };
        var parameterSelections = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["worker-executor"] = Parse($"{{\"user-computer-profile\":\"{ProfileUuid}\"}}"),
        };

        var data = CreateData(parameterValues: parameterValues, parameterSelections: parameterSelections);

        // The typed selection lives in the parameter-selections sibling key as a JSON OBJECT ...
        var readSelections = AgentSessionExecutorBindings.ReadParameterSelections(data);
        Assert.Equal(JsonValueKind.Object, readSelections["worker-executor"].ValueKind);
        Assert.Equal(ProfileUuid, readSelections["worker-executor"].GetProperty("user-computer-profile").GetString());

        // ... NOT as a JSON-encoded string in parameter-values, which stays string->string and unchanged.
        var parameterValuesElement = data.GetProperty("parameter-values");
        Assert.Equal("C:\\App", parameterValuesElement.GetProperty("working-directory").GetString());
        Assert.False(parameterValuesElement.TryGetProperty("worker-executor", out _));
    }

    [Fact]
    public void Resume_RebuildsTopologyFromBindings()
    {
        var components = Parse($"{{\"worker\":{{\"type\":\"user-computer-profile\",\"entity-id\":\"{ProfileUuid}\"}}}}");
        var data = CreateData(
            sessionExecutor: Parse("""{"type":"local"}"""),
            components: components);

        // On resume the session executor and each component re-bind to the same machines: the session is
        // local, and the component binds to the remote profile's client instance.
        var sessionExecutor = AgentSessionExecutorBindings.ReadSessionExecutor(data);
        Assert.Equal(
            AgentSessionExecutorBindings.LocalClientInstance,
            AgentSessionExecutorBindings.DeriveClientInstance(sessionExecutor));

        var readBindings = AgentSessionExecutorBindings.ReadComponentBindings(data);
        Assert.Equal(ProfileUuid, AgentSessionExecutorBindings.DeriveClientInstance(readBindings["worker"]));
    }

    [Fact]
    public void Resume_LegacyHostProfileOnly_DerivesSessionExecutor()
    {
        // A legacy session carries only host-profile-entity-id (no executor-bindings).
        var hostProfile = new EntityId();
        var data = CreateData(hostProfileEntityId: hostProfile);
        Assert.False(data.TryGetProperty("executor-bindings", out _));

        var sessionExecutor = AgentSessionExecutorBindings.ReadSessionExecutor(data);

        Assert.Equal("user-computer-profile", sessionExecutor.GetProperty("type").GetString());
        Assert.Equal(hostProfile.ToString(), sessionExecutor.GetProperty("entity-id").GetString());
        Assert.Equal(hostProfile.ToString(), AgentSessionExecutorBindings.DeriveClientInstance(sessionExecutor));
    }
}
