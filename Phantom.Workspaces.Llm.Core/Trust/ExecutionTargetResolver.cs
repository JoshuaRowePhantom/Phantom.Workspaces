using System.Text.Json;
using System.Text.Json.Nodes;

namespace Phantom.Workspaces.Llm.Trust;

public sealed class ExecutionTargetResolver
{
    /// <summary>Descriptor <c>type</c> for the local, in-process transport.</summary>
    public const string LocalDescriptorType = "local";

    /// <summary>Descriptor <c>type</c> for a remote target resolved via user-computer profile.</summary>
    public const string RemoteDescriptorType = "user-computer-profile";

    public JsonElement Resolve(TrustProfile? trustProfile)
    {
        if (trustProfile?.DefaultExecutionTarget is { } target)
        {
            return target.Clone();
        }

        using var localDocument = JsonDocument.Parse("""{"type":"local"}""");
        return localDocument.RootElement.Clone();
    }

    /// <summary>Whether a connection descriptor can be built for the given target client instance.</summary>
    public bool CanResolve(string targetClientInstance)
        => !string.IsNullOrWhiteSpace(targetClientInstance);

    /// <summary>
    /// Builds the transport connection descriptor for a target client instance: the local machine
    /// (<c>"."</c>) resolves to a <c>local</c> descriptor; a remote target resolves to a
    /// <c>user-computer-profile</c> descriptor carrying the target client instance.
    /// </summary>
    public JsonElement ResolveDescriptor(string targetClientInstance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetClientInstance);

        JsonObject descriptor = IsLocal(targetClientInstance)
            ? new JsonObject { ["type"] = LocalDescriptorType }
            : new JsonObject
            {
                ["type"] = RemoteDescriptorType,
                ["entity-id"] = targetClientInstance,
            };

        return JsonSerializer.SerializeToElement(descriptor);
    }

    /// <summary>Whether the target client instance denotes the local machine (<c>"."</c>).</summary>
    public static bool IsLocal(string targetClientInstance)
        => string.Equals(targetClientInstance, TrustProfile.LocalClientInstance, StringComparison.Ordinal);
}
