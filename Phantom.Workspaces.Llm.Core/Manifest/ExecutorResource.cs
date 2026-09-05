using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Phantom.Workspaces.Llm.Core.Manifest;

/// <summary>
/// The parsed form of a manifest <c>resources[]</c> entry with <c>kind:"executor"</c>
/// (issue #1433, per-component-executor-binding).
/// </summary>
/// <remarks>
/// <para>
/// Reuse-first / no new execution schema: an executor resource <b>resolves to a transport
/// connection-descriptor</b> — the existing <c>type</c>-discriminated JSON already consumed by
/// <c>ITransportFactory.ConnectToAsync(JsonElement)</c> (<c>local</c>, <c>user-computer-profile</c>,
/// <c>http</c>, <c>reverse-http</c>, …). There is <b>no</b> parallel <c>ExecutorDescriptor</c>
/// type/record/schema. The <see cref="ConnectionDescriptor"/> escape hatch is what makes the model
/// open-endedly extensible with <b>no schema change</b> (a future container/k8s/WSL <c>type</c> can be
/// authored with no new manifest field).
/// </para>
/// <para>
/// AgentSchema does not know the <c>executor</c> resource discriminator and throws
/// <c>Unknown Resource discriminator value: executor</c> if it reaches deserialisation, so executor
/// resources are parsed here directly from the raw manifest JSON via
/// <see cref="ParseManifestResources(string)"/> rather than from <c>AgentManifest.Resources</c>.
/// The manifest loader strips executor entries before handing the JSON to AgentSchema.
/// </para>
/// </remarks>
public sealed record ExecutorResource
{
    /// <summary>The <c>kind</c> discriminator identifying an executor resource entry.</summary>
    public const string ResourceKind = "executor";

    /// <summary>Strategy <c>id</c>: resolves to <c>{"type":"local"}</c>.</summary>
    public const string LocalStrategy = "local";

    /// <summary>Strategy <c>id</c>: bind to a launch <c>executor</c> parameter (interactive selection).</summary>
    public const string ParameterStrategy = "parameter";

    /// <summary>Strategy <c>id</c>: fixed user-computer-profile entity-id.</summary>
    public const string UserComputerProfileEntityStrategy = "user-computer-profile-entity";

    /// <summary>Strategy <c>id</c>: fixed trust-profile.</summary>
    public const string TrustProfileStrategy = "trust-profile";

    /// <summary>Strategy <c>id</c>: raw inline connection-descriptor used verbatim (extension escape hatch).</summary>
    public const string ConnectionDescriptorStrategy = "connection-descriptor";

    /// <summary>The executor's name, referenced by <c>executor</c> fields on tools and models.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// The convenience resolution strategy: <see cref="LocalStrategy"/>, <see cref="ParameterStrategy"/>,
    /// <see cref="UserComputerProfileEntityStrategy"/>, <see cref="TrustProfileStrategy"/>, or
    /// <see cref="ConnectionDescriptorStrategy"/>.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The simple string inputs for the convenience strategies (the parameter name for
    /// <see cref="ParameterStrategy"/>; the fixed entity-id for
    /// <see cref="UserComputerProfileEntityStrategy"/>; the trust-profile name for
    /// <see cref="TrustProfileStrategy"/>).
    /// </summary>
    public IReadOnlyDictionary<string, string?> Options { get; init; }
        = new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>
    /// The inline connection-descriptor used verbatim by the <see cref="ConnectionDescriptorStrategy"/>
    /// strategy. No bespoke executor schema is introduced — this is the same shape the transport layer
    /// already consumes.
    /// </summary>
    public JsonElement? ConnectionDescriptor { get; init; }

    /// <summary>
    /// Parses every <c>kind:"executor"</c> entry from a manifest's <c>resources[]</c> array. Returns an
    /// empty list when the manifest declares no executor resources.
    /// </summary>
    public static IReadOnlyList<ExecutorResource> ParseManifestResources(string manifestJson)
    {
        ArgumentNullException.ThrowIfNull(manifestJson);

        using var document = JsonDocument.Parse(manifestJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("resources", out var resources)
            || resources.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<ExecutorResource>();
        foreach (var resource in resources.EnumerateArray())
        {
            if (IsExecutorResource(resource))
            {
                result.Add(FromResourceElement(resource));
            }
        }

        return result;
    }

    /// <summary>Whether the given <c>resources[]</c> element has <c>kind:"executor"</c>.</summary>
    public static bool IsExecutorResource(JsonElement resource)
        => resource.ValueKind == JsonValueKind.Object
            && resource.TryGetProperty("kind", out var kind)
            && kind.ValueKind == JsonValueKind.String
            && string.Equals(kind.GetString(), ResourceKind, StringComparison.Ordinal);

    /// <summary>Builds an <see cref="ExecutorResource"/> from a single <c>resources[]</c> element.</summary>
    public static ExecutorResource FromResourceElement(JsonElement resource)
    {
        if (!IsExecutorResource(resource))
        {
            throw new ArgumentException(
                "Resource element is not an executor resource (kind must be 'executor').",
                nameof(resource));
        }

        var name = resource.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Executor resource is missing a non-empty 'name'.", nameof(resource));
        }

        var id = resource.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException(
                $"Executor resource '{name}' is missing a non-empty 'id' strategy.",
                nameof(resource));
        }

        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (resource.TryGetProperty("options", out var optionsElement)
            && optionsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var option in optionsElement.EnumerateObject())
            {
                options[option.Name] = option.Value.ValueKind == JsonValueKind.Null
                    ? null
                    : option.Value.GetString() ?? option.Value.GetRawText();
            }
        }

        JsonElement? connectionDescriptor = null;
        if (resource.TryGetProperty("connection-descriptor", out var descriptorElement))
        {
            // Clone so the value survives disposal of the owning JsonDocument.
            connectionDescriptor = descriptorElement.Clone();
        }

        return new ExecutorResource
        {
            Name = name,
            Id = id,
            Options = options,
            ConnectionDescriptor = connectionDescriptor,
        };
    }

    /// <summary>
    /// Serialises this executor resource back into a <c>resources[]</c> JSON object, round-tripping the
    /// inline connection-descriptor verbatim.
    /// </summary>
    public JsonObject ToResourceNode()
    {
        var node = new JsonObject
        {
            ["kind"] = ResourceKind,
            ["id"] = this.Id,
            ["name"] = this.Name,
        };

        if (this.Options.Count > 0)
        {
            var options = new JsonObject();
            foreach (var option in this.Options)
            {
                options[option.Key] = option.Value is null ? null : JsonValue.Create(option.Value);
            }

            node["options"] = options;
        }

        if (this.ConnectionDescriptor is { } descriptor)
        {
            node["connection-descriptor"] = JsonNode.Parse(descriptor.GetRawText());
        }

        return node;
    }
}
