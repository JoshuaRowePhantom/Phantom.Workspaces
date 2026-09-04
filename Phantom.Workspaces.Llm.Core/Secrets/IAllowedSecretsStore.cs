namespace Phantom.Workspaces.Llm.Secrets;

/// <summary>
/// A JSON-backed store mapping the content-addressed <c>hash</c> of an allowed secret use to the
/// remembered <see cref="MemorizedSecret"/> grant. The store never persists secret <em>values</em> —
/// only hashes, <see cref="SecretUseMemory"/> descriptors and <see cref="SecretSource"/> descriptors.
/// </summary>
public interface IAllowedSecretsStore
{
    /// <summary>
    /// Returns the remembered grant for <paramref name="hash"/>, or <see langword="null"/> when no
    /// grant is stored under that hash.
    /// </summary>
    Task<MemorizedSecret?> TryGetAsync(string hash, CancellationToken ct);

    /// <summary>Stores (creating or overwriting) the grant under <paramref name="hash"/>.</summary>
    Task PutAsync(string hash, MemorizedSecret record, CancellationToken ct);

    /// <summary>Deletes the grant under <paramref name="hash"/> when present.</summary>
    Task DeleteAsync(string hash, CancellationToken ct);

    /// <summary>Returns a snapshot of every stored hash → grant mapping.</summary>
    Task<IReadOnlyDictionary<string, MemorizedSecret>> LoadAllAsync(CancellationToken ct);
}
