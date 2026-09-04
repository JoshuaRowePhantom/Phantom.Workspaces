using System.Diagnostics.CodeAnalysis;

namespace Phantom.Workspaces.Llm.Secrets;

/// <summary>Dictionary-backed per-materialization secret reference resolver.</summary>
public sealed class SecretPlaceholderResolver : ISecretPlaceholderResolver
{
    private readonly Dictionary<string, SecretRetriever> retrievers;

    public SecretPlaceholderResolver()
        : this([])
    {
    }

    private SecretPlaceholderResolver(Dictionary<string, SecretRetriever> retrievers)
    {
        this.retrievers = retrievers;
    }

    public static ISecretPlaceholderResolver Empty { get; } = new SecretPlaceholderResolver([]);

    public void Register(string placeholder, SecretRetriever retriever)
    {
        ArgumentException.ThrowIfNullOrEmpty(placeholder);
        ArgumentNullException.ThrowIfNull(retriever);
        this.retrievers[placeholder] = retriever;
    }

    public bool TryResolve(string placeholder, [NotNullWhen(true)] out SecretRetriever? retriever)
    {
        ArgumentNullException.ThrowIfNull(placeholder);
        return this.retrievers.TryGetValue(placeholder, out retriever);
    }
}
