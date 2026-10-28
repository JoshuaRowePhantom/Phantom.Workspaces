using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentSchema;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Filesystem selection carrying edit-store provider JSON, which is not an AgentSchema
/// connection. The kind-specific load bridge keeps SDK MCP/model connections untouched.
/// </summary>
public sealed class FilesystemTool : CustomTool
{
    private static readonly ConditionalWeakTable<Dictionary<string, object?>, ProviderInput> ProviderInputs = new();

    public FilesystemTool()
    {
        this.Kind = "filesystem";
    }

    public Dictionary<string, object?>? EditStoreConnection { get; set; }

    public string? EditStoreConnectionJson
        => this.EditStoreConnection is null ? null : JsonSerializer.Serialize(this.EditStoreConnection);

    internal static Dictionary<string, object?> PrepareInput(Dictionary<string, object?> data)
    {
        if (data.TryGetValue("kind", out var kind)
            && kind is string kindName && string.Equals(kindName, "filesystem", StringComparison.Ordinal)
            && data.TryGetValue("connection", out var connection))
        {
            var prepared = new Dictionary<string, object?>(data);
            prepared.Remove("connection");
            // Associate configuration with this exact load dictionary, not an injectable
            // synthetic JSON property. The SDK's polymorphic load may process it twice.
            ProviderInputs.Add(prepared, new ProviderInput((Dictionary<string, object?>?)connection));
            return prepared;
        }

        return data;
    }

    internal static FilesystemTool From(CustomTool source, Dictionary<string, object?> data)
        => new()
        {
            Kind = source.Kind,
            Name = source.Name,
            Description = source.Description,
            Bindings = source.Bindings,
            Options = source.Options,
            EditStoreConnection = ProviderInputs.TryGetValue(data, out var input)
                ? input.Connection
                : null,
        };

    public override Dictionary<string, object?> Save(SaveContext? context = null)
    {
        var saved = base.Save(context!);
        if (this.EditStoreConnection is not null)
        {
            saved["connection"] = this.EditStoreConnection;
        }

        return saved;
    }

    private sealed record ProviderInput(Dictionary<string, object?>? Connection);
}
