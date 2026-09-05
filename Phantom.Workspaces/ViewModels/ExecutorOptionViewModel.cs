using System.Text.Json;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// A single selectable option in the Launchpad <c>executor</c> picker (issue #1440). An option is
/// either a <c>trust-profile</c> entity ("choose by trust policy") or a <c>user-computer-profile</c>
/// entity (which synthesizes an implicit trust profile). <see cref="Selection"/> is the disambiguated
/// value recorded in the session's typed <c>parameter-selections</c> map — <c>{"trust-profile":…}</c>
/// or <c>{"user-computer-profile":…}</c>.
/// </summary>
public sealed class ExecutorOptionViewModel
{
    /// <summary>The selection discriminator kind: <c>trust-profile</c> or <c>user-computer-profile</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>The human-readable label shown in the picker.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The disambiguated selection value recorded in <c>parameter-selections</c>.</summary>
    public required JsonElement Selection { get; init; }
}
