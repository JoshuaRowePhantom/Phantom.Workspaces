namespace Phantom.Workspaces.Llm.Secrets;

/// <summary>
/// A memorized grant that a secret may be used from a given <see cref="SecretSource"/> at a
/// remembered <see cref="SecretUseMemory"/>. Holds no secret value.
/// </summary>
public sealed record MemorizedSecret(SecretUseMemory Memory, SecretSource Source, DateTimeOffset GrantedAt);
