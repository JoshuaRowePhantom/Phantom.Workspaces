using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phantom.Workspaces.Llm.Interfaces.Access;

/// <summary>A bounded presentation label, accepted only from explicit workspace/name fields.</summary>
public sealed record SafeApprovalLabel
{
    [JsonConstructor]
    public SafeApprovalLabel(string value)
    {
        if (!IsAllowed(value)) throw new JsonException("Invalid approval label.");
        this.Value = value;
    }

    public string Value { get; }
    public static SafeApprovalLabel? FromName(string? value) => IsAllowed(value) ? new(value!) : null;
    public override string ToString() => this.Value;

    private static bool IsAllowed(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 120 && value == value.Trim() &&
        value.All(character => char.IsLetterOrDigit(character) || " -_.()[]#".Contains(character)) &&
        !value.StartsWith("ghp_", StringComparison.OrdinalIgnoreCase) &&
        !value.StartsWith("gho_", StringComparison.OrdinalIgnoreCase) &&
        !value.StartsWith("github_pat_", StringComparison.OrdinalIgnoreCase) &&
        !value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
}

public enum SafeWorkspaceEntityKind { Manifest, Session }

/// <summary>A real workspace entity id, never a credential/account/source digest.</summary>
public sealed record SafeWorkspaceEntityReference
{
    [JsonConstructor]
    public SafeWorkspaceEntityReference(SafeWorkspaceEntityKind kind, Guid identifier, SafeApprovalLabel name)
    {
        if (!Enum.IsDefined(kind) || identifier == Guid.Empty || name is null)
            throw new JsonException("Invalid approval workspace reference.");
        this.Kind = kind;
        this.Identifier = identifier;
        this.Name = name;
    }

    public SafeWorkspaceEntityKind Kind { get; }
    public Guid Identifier { get; }
    public SafeApprovalLabel Name { get; }
    public static SafeWorkspaceEntityReference? FromEntity(SafeWorkspaceEntityKind kind, string? identifier, string? name) =>
        Guid.TryParse(identifier, out var id) && id != Guid.Empty && SafeApprovalLabel.FromName(name) is { } label
            ? new(kind, id, label) : null;
    public override string ToString() => $"{this.Name} ({this.Identifier.ToString("N")[..8]})";
}

/// <summary>Historical origin only; none of these fields participate in policy matching.</summary>
public sealed record SafeApprovalOrigin
{
    public SafeWorkspaceEntityReference? Manifest { get; init; }
    public SafeWorkspaceEntityReference? Session { get; init; }
    public SafeApprovalLabel? ManifestName { get; init; }
    public SafeApprovalLabel? Credential { get; init; }
    public SafeApprovalLabel? Requester { get; init; }

    public SafeApprovalOrigin Sanitize() => this with
    {
        Manifest = this.Manifest?.Kind == SafeWorkspaceEntityKind.Manifest ? this.Manifest : null,
        Session = this.Session?.Kind == SafeWorkspaceEntityKind.Session ? this.Session : null,
    };

    public override string ToString()
    {
        var origin = this.Sanitize();
        var names = new List<string>();
        if (origin.Manifest is { } manifest) names.Add($"manifest {manifest}");
        else if (origin.ManifestName is { } name) names.Add($"manifest {name} (entity reference unavailable)");
        if (origin.Session is { } session) names.Add($"session {session}");
        if (origin.Requester is { } requester) names.Add($"requester {requester}");
        var detail = names.Count > 0 ? $"Originally requested by: {string.Join(" / ", names)}"
            : "Older approval — manifest/session details were not recorded";
        if (origin.Manifest is null && origin.ManifestName is null && origin.Session is null && names.Count > 0)
            detail += " • Manifest/session details were not recorded";
        return origin.Credential is { } credential ? $"Credential: {credential} • {detail}" : detail;
    }
}
