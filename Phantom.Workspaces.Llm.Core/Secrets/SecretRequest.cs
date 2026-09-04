namespace Phantom.Workspaces.Llm.Secrets;

/// <summary>
/// A request to obtain a single secret for a specific use. Holds no secret value.
/// </summary>
public sealed record SecretRequest(
    string SecretName,
    string UseDisplayString,
    IReadOnlyList<SecretUseMemory> Memories,
    SecretSource? DefaultSecretSource,
    IReadOnlyList<SecretSource> CandidateSecretSources);
