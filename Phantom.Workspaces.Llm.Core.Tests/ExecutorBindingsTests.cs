using System;
using System.Collections.Generic;
using System.Text.Json;
using Phantom.Workspaces.Llm.Core.Manifest;
using Phantom.Workspaces.Llm.Core.Transport;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// Covers <see cref="ExecutorBindings"/> (issue #1436, per-component-executor-binding): component
/// resolution inherits the explicit session executor when unset, unknown names throw, and the bindings
/// round-trip through <see cref="ExecutorBindings.ToPersistableMap"/> as connection-descriptor
/// <b>objects</b> (not bare strings).
/// </summary>
public sealed class ExecutorBindingsTests
{
    private const string ProfileUuid = "11111111-2222-3333-4444-555555555555";

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static ExecutorBindings BindingsWith(params (string Name, string Descriptor)[] entries)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            map[entry.Name] = Parse(entry.Descriptor);
        }

        return new ExecutorBindings { Bindings = map };
    }

    [Fact]
    public void ResolveComponent_UnsetExecutor_InheritsSessionExecutor()
    {
        var bindings = new ExecutorBindings
        {
            SessionExecutor = Parse($$"""{"type":"user-computer-profile","entity-id":"{{ProfileUuid}}"}"""),
        };

        var unset = bindings.ResolveComponent(null);
        var empty = bindings.ResolveComponent(string.Empty);

        Assert.Equal(ProfileUuid, unset.GetProperty("entity-id").GetString());
        Assert.Equal(ProfileUuid, empty.GetProperty("entity-id").GetString());
    }

    [Fact]
    public void ResolveComponent_UnknownName_Throws()
    {
        var bindings = BindingsWith(("worker", """{"type":"local"}"""));

        var exception = Assert.Throws<InvalidOperationException>(() => bindings.ResolveComponent("missing"));
        Assert.Contains("missing", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveComponent_BoundName_ReturnsConnectionDescriptor()
    {
        var bindings = BindingsWith(
            ("worker", $$"""{"type":"user-computer-profile","entity-id":"{{ProfileUuid}}"}"""));

        var descriptor = bindings.ResolveComponent("worker");

        Assert.Equal("user-computer-profile", descriptor.GetProperty("type").GetString());
        Assert.Equal(ProfileUuid, descriptor.GetProperty("entity-id").GetString());
    }

    [Fact]
    public void ToTopology_LocalSession_ResolvesLocally()
    {
        var bindings = new ExecutorBindings { SessionExecutor = ExecutorBindings.LocalDescriptor() };

        var topology = bindings.ToTopology();

        Assert.True(topology.ResolvesLocally(ExecutorTarget.AgentExecutor));
        Assert.True(topology.ResolvesLocally(ExecutorTarget.GuiLocal));
        Assert.True(topology.ResolvesLocally(ExecutorTarget.HostingInstance));
        Assert.True(topology.IsSingleMachine);
    }

    [Fact]
    public void ToTopology_RemoteSession_RoutesAgentToRemoteButGuiLocal()
    {
        var bindings = new ExecutorBindings
        {
            SessionExecutor = Parse($$"""{"type":"user-computer-profile","entity-id":"{{ProfileUuid}}"}"""),
        };

        var topology = bindings.ToTopology();

        Assert.False(topology.ResolvesLocally(ExecutorTarget.AgentExecutor));
        Assert.False(topology.ResolvesLocally(ExecutorTarget.HostingInstance));
        Assert.True(topology.ResolvesLocally(ExecutorTarget.GuiLocal));
        Assert.Equal(ProfileUuid, topology.Resolve(ExecutorTarget.AgentExecutor));
    }

    [Fact]
    public void ToPersistableMap_DescriptorObjects_RoundTrips()
    {
        var bindings = BindingsWith(
            ("local-worker", """{"type":"local"}"""),
            ("remote-worker", $$"""{"type":"user-computer-profile","entity-id":"{{ProfileUuid}}"}"""));

        var persisted = bindings.ToPersistableMap();

        // Persisted as descriptor OBJECTS, not bare strings.
        Assert.Equal(JsonValueKind.Object, persisted.GetProperty("local-worker").ValueKind);
        Assert.Equal(JsonValueKind.Object, persisted.GetProperty("remote-worker").ValueKind);

        var restored = ExecutorBindings.FromPersistableMap(persisted);
        Assert.Equal("local", restored.Bindings["local-worker"].GetProperty("type").GetString());
        Assert.Equal("user-computer-profile", restored.Bindings["remote-worker"].GetProperty("type").GetString());
        Assert.Equal(ProfileUuid, restored.Bindings["remote-worker"].GetProperty("entity-id").GetString());
    }

    [Fact]
    public void FromPersistableMap_BareStringBinding_ReadAsUserComputerProfileDescriptor()
    {
        // Back-compat: a legacy bare client-instance string is read as a descriptor object.
        var legacy = Parse($$"""{"worker":"{{ProfileUuid}}","local":"."}""");

        var restored = ExecutorBindings.FromPersistableMap(legacy);

        Assert.Equal("user-computer-profile", restored.Bindings["worker"].GetProperty("type").GetString());
        Assert.Equal(ProfileUuid, restored.Bindings["worker"].GetProperty("entity-id").GetString());
        Assert.Equal("local", restored.Bindings["local"].GetProperty("type").GetString());
    }
}
