using System;
using System.Collections.Generic;
using System.Text.Json;
using Phantom.Workspaces.Llm.Core.Manifest;
using Phantom.Workspaces.Llm.Trust;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// Covers the executor-resource resolver (issue #1436, per-component-executor-binding): each <c>id</c>
/// strategy resolves an <see cref="ExecutorResource"/> to the transport <b>connection-descriptor</b> that
/// <c>ITransportFactoryRegistry.ConnectToAsync</c> already dispatches on — no bespoke executor schema —
/// delegating to <see cref="ExecutionTargetResolver"/> for the local/profile shapes.
/// </summary>
public sealed class ExecutorResourceResolverTests
{
    private const string ProfileUuid = "11111111-2222-3333-4444-555555555555";

    private static ExecutorResource Resource(
        string id,
        string name = "worker",
        IReadOnlyDictionary<string, string?>? options = null,
        JsonElement? connectionDescriptor = null)
        => new()
        {
            Id = id,
            Name = name,
            Options = options ?? new Dictionary<string, string?>(StringComparer.Ordinal),
            ConnectionDescriptor = connectionDescriptor,
        };

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static TrustProfile TrustProfileWithTarget(JsonElement target)
        => TrustProfileComposer.Compose([new TrustProfileDefinition { DefaultExecutionTarget = target }]);

    [Fact]
    public void Resolve_LocalId_ReturnsLocalDescriptor()
    {
        var resolver = new ExecutorResourceResolver();

        var descriptor = resolver.Resolve(
            Resource(ExecutorResource.LocalStrategy),
            new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            trustProfile: null);

        Assert.Equal("local", descriptor.GetProperty("type").GetString());
    }

    [Fact]
    public void Resolve_ParameterUserComputerProfile_ReturnsImplicitTrustProfileDescriptor()
    {
        var resolver = new ExecutorResourceResolver();
        var selections = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["worker-executor"] = ExecutorParameterSelection.ForUserComputerProfile(ProfileUuid),
        };

        var descriptor = resolver.Resolve(
            Resource(
                ExecutorResource.ParameterStrategy,
                options: new Dictionary<string, string?>(StringComparer.Ordinal) { ["parameter"] = "worker-executor" }),
            selections,
            trustProfile: null);

        Assert.Equal("user-computer-profile", descriptor.GetProperty("type").GetString());
        Assert.Equal(ProfileUuid, descriptor.GetProperty("entity-id").GetString());
    }

    [Fact]
    public void Resolve_ParameterTrustProfile_ReturnsDefaultExecutionTarget()
    {
        var resolver = new ExecutorResourceResolver();
        var target = Parse($$"""{"type":"user-computer-profile","entity-id":"{{ProfileUuid}}"}""");
        var trustProfile = TrustProfileWithTarget(target);
        var selections = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["worker-executor"] = ExecutorParameterSelection.ForTrustProfile("defaults/trust-profiles/remote"),
        };

        var descriptor = resolver.Resolve(
            Resource(
                ExecutorResource.ParameterStrategy,
                options: new Dictionary<string, string?>(StringComparer.Ordinal) { ["parameter"] = "worker-executor" }),
            selections,
            trustProfile);

        Assert.Equal("user-computer-profile", descriptor.GetProperty("type").GetString());
        Assert.Equal(ProfileUuid, descriptor.GetProperty("entity-id").GetString());
    }

    [Fact]
    public void Resolve_ParameterSelection_TypedJsonElement_IsRead()
    {
        var resolver = new ExecutorResourceResolver();
        var resource = Resource(
            ExecutorResource.ParameterStrategy,
            options: new Dictionary<string, string?>(StringComparer.Ordinal) { ["parameter"] = "worker-executor" });

        // The user-computer-profile shape is read directly from the typed parameter-selections map.
        var ucpSelections = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["worker-executor"] = ExecutorParameterSelection.ForUserComputerProfile(ProfileUuid),
        };
        var ucpDescriptor = resolver.Resolve(resource, ucpSelections, trustProfile: null);
        Assert.Equal(ProfileUuid, ucpDescriptor.GetProperty("entity-id").GetString());

        // The trust-profile shape is likewise read from the same typed map.
        var target = Parse("""{"type":"local"}""");
        var trustSelections = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["worker-executor"] = ExecutorParameterSelection.ForTrustProfile("some/trust-profile"),
        };
        var trustDescriptor = resolver.Resolve(resource, trustSelections, TrustProfileWithTarget(target));
        Assert.Equal("local", trustDescriptor.GetProperty("type").GetString());

        // A malformed selection object (neither known discriminator) throws with a clear message.
        var malformed = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["worker-executor"] = Parse("""{"unknown-kind":"x"}"""),
        };
        var exception = Assert.Throws<InvalidOperationException>(
            () => resolver.Resolve(resource, malformed, trustProfile: null));
        Assert.Contains("could not be resolved", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_UserComputerProfileEntityId_ReturnsFixedUuidDescriptor()
    {
        var resolver = new ExecutorResourceResolver();

        var descriptor = resolver.Resolve(
            Resource(
                ExecutorResource.UserComputerProfileEntityStrategy,
                options: new Dictionary<string, string?>(StringComparer.Ordinal) { ["entity-id"] = ProfileUuid }),
            new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            trustProfile: null);

        Assert.Equal("user-computer-profile", descriptor.GetProperty("type").GetString());
        Assert.Equal(ProfileUuid, descriptor.GetProperty("entity-id").GetString());
    }

    [Fact]
    public void Resolve_TrustProfileId_ReturnsDefaultExecutionTargetDescriptor()
    {
        var resolver = new ExecutorResourceResolver();
        var target = Parse($$"""{"type":"user-computer-profile","entity-id":"{{ProfileUuid}}"}""");
        var trustProfile = TrustProfileWithTarget(target);

        var descriptor = resolver.Resolve(
            Resource(
                ExecutorResource.TrustProfileStrategy,
                options: new Dictionary<string, string?>(StringComparer.Ordinal) { ["trust-profile"] = "remote" }),
            new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            trustProfile);

        Assert.Equal("user-computer-profile", descriptor.GetProperty("type").GetString());
        Assert.Equal(ProfileUuid, descriptor.GetProperty("entity-id").GetString());
    }

    [Fact]
    public void Resolve_ConnectionDescriptorId_ReturnsInlineDescriptorVerbatim()
    {
        var resolver = new ExecutorResourceResolver();
        var inline = Parse("""{"type":"reverse-http","endpoint":"https://host.example/mcp/"}""");

        var descriptor = resolver.Resolve(
            Resource(ExecutorResource.ConnectionDescriptorStrategy, name: "container", connectionDescriptor: inline),
            new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            trustProfile: null);

        Assert.Equal("reverse-http", descriptor.GetProperty("type").GetString());
        Assert.Equal("https://host.example/mcp/", descriptor.GetProperty("endpoint").GetString());
    }

    [Fact]
    public void Resolve_UnknownId_ThrowsWithClearMessage()
    {
        var resolver = new ExecutorResourceResolver();

        var exception = Assert.Throws<InvalidOperationException>(() => resolver.Resolve(
            Resource("bogus-strategy", name: "mystery"),
            new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            trustProfile: null));

        Assert.Contains("bogus-strategy:mystery", exception.Message, StringComparison.Ordinal);
        Assert.Contains("could not be resolved", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ParameterMissing_ThrowsWithClearMessage()
    {
        var resolver = new ExecutorResourceResolver();

        var exception = Assert.Throws<InvalidOperationException>(() => resolver.Resolve(
            Resource(
                ExecutorResource.ParameterStrategy,
                options: new Dictionary<string, string?>(StringComparer.Ordinal) { ["parameter"] = "worker-executor" }),
            new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            trustProfile: null));

        Assert.Contains("could not be resolved", exception.Message, StringComparison.Ordinal);
    }
}
