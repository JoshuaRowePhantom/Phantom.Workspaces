using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using AgentSchema;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Secrets;

namespace Phantom.Workspaces.Llm;

internal sealed class AgentCredentialLeaseSet
{
    private readonly SensitiveValueLease? primary;
    private readonly ModelOptions? modelOptions;
    private readonly Dictionary<string, SensitiveValueLease> options = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<SensitiveValueLease> leases;

    internal AgentCredentialLeaseSet(
        SensitiveValueLease? primary,
        ModelOptions? modelOptions,
        ISecretPlaceholderResolver? resolver)
    {
        this.primary = primary;
        this.modelOptions = modelOptions;
        this.FindOptions(modelOptions?.AdditionalProperties, resolver);
        this.leases = (primary is null ? this.options.Values : this.options.Values.Prepend(primary))
            .Distinct<SensitiveValueLease>(ReferenceEqualityComparer.Instance).ToArray();
    }

    internal Task<TResult> UseAsync<TResult>(
        Func<string?, ModelOptions?, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        var material = new Dictionary<SensitiveValueLease, string>(ReferenceEqualityComparer.Instance);
        return UseAtAsync(0, cancellationToken);

        async Task<TResult> UseAtAsync(
            int index,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (index == this.leases.Count)
            {
                if (this.primary is not null && string.IsNullOrWhiteSpace(material[this.primary]))
                {
                    throw new AgentCredentialAccessException();
                }
                var resolvedOptions = this.options.Count == 0 ? this.modelOptions : new ModelOptions
                {
                    AdditionalProperties = (IDictionary<string, object>?)this.ResolveOptions(this.modelOptions?.AdditionalProperties, material),
                };
                return await operation(
                    this.primary is null ? null : material[this.primary],
                    resolvedOptions,
                    token).ConfigureAwait(false);
            }

            var lease = this.leases[index];
            var outcome = await lease.UseAsync(async (value, useCancellationToken) =>
            {
                material.Add(lease, value);
                try
                {
                    return await UseAtAsync(index + 1, useCancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    material.Remove(lease);
                }
            }, token).ConfigureAwait(false);
            return outcome switch
            {
                SensitiveValueUseSucceeded<TResult> succeeded => succeeded.Result,
                SensitiveValueUseAuthorizationRequired<TResult> required => throw new AgentCredentialAccessException(
                    CredentialLeaseRequiredActionKind.Authorization,
                    required.RequestIdentifier),
                SensitiveValueUseReacquisitionRequired<TResult> required => throw new AgentCredentialAccessException(
                    CredentialLeaseRequiredActionKind.Reacquisition,
                    required.RequestIdentifier),
                _ => throw new AgentCredentialAccessException(),
            };
        }
    }

    internal Task<TResult> GuardAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken) =>
        this.UseAsync((_, _, token) => operation(token), cancellationToken);

    internal Task GuardAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken) =>
        this.UseAsync(async (_, _, token) =>
        {
            await operation(token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    private void FindOptions(
        object? value,
        ISecretPlaceholderResolver? resolver)
    {
        if (value is string text)
        {
            foreach (Match match in Regex.Matches(text, @"\$\{SECRET:[^}]+\}"))
            {
                if (resolver is null || !resolver.TryResolve(match.Value, out var lease))
                {
                    throw new AgentCredentialAccessException();
                }

                this.options.TryAdd(match.Value, lease);
            }
        }
        else if (value is IDictionary<string, object> dictionary)
        {
            foreach (var item in dictionary.Values)
            {
                this.FindOptions(item, resolver);
            }
        }
        else if (value is IEnumerable<object> collection)
        {
            foreach (var item in collection)
            {
                this.FindOptions(item, resolver);
            }
        }
    }

    private object? ResolveOptions(
        object? value,
        IReadOnlyDictionary<SensitiveValueLease, string> material) => value switch
        {
            string text => Regex.Replace(text, @"\$\{SECRET:[^}]+\}", match => material[this.options[match.Value]]),
            IDictionary<string, object> dictionary => dictionary.ToDictionary(
                item => item.Key,
                item => this.ResolveOptions(item.Value, material)!,
                StringComparer.Ordinal),
            IEnumerable<object> collection => collection.Select(item => this.ResolveOptions(item, material)).ToArray(),
            _ => value,
        };
}

internal sealed class AgentCredentialAccessException : InvalidOperationException
{
    internal AgentCredentialAccessException()
        : base("Credential access is unavailable or was not authorized.")
    {
    }

    internal AgentCredentialAccessException(
        CredentialLeaseRequiredActionKind requiredAction,
        UserAuthorizationRequestIdentifier requestIdentifier)
        : base(requiredAction == CredentialLeaseRequiredActionKind.Authorization
            ? "Credential authorization is required."
            : "Credential reacquisition is required.")
    {
        this.RequiredAction = requiredAction;
        this.RequestIdentifier = requestIdentifier;
    }

    internal CredentialLeaseRequiredActionKind? RequiredAction { get; }
    internal UserAuthorizationRequestIdentifier? RequestIdentifier { get; }
}

internal sealed class CredentialBoundModelChatClient(
    IChatClient inner,
    AgentCredentialLeaseSet credentials,
    ICredentialAccessResult? owner,
    IAsyncDisposable? ownedResolver) : IChatClient, IAsyncDisposable
{
    private readonly object disposalGate = new();
    private Task? disposal;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        credentials.GuardAsync(token => inner.GetResponseAsync(messages, options, token), cancellationToken);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var enumerator = inner.GetStreamingResponseAsync(messages, options, streamCancellation.Token)
            .GetAsyncEnumerator(streamCancellation.Token);
        while (await credentials.GuardAsync(async token =>
        {
            using var registration = token.Register(streamCancellation.Cancel);
            return await enumerator.MoveNextAsync().ConfigureAwait(false);
        }, streamCancellation.Token).ConfigureAwait(false))
        {
            yield return enumerator.Current;
        }
    }

    public object? GetService(
        Type serviceType,
        object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is null && serviceType.IsInstanceOfType(this))
        {
            return this;
        }
        return serviceType == typeof(ChatClientMetadata) ? inner.GetService(serviceType, serviceKey) : null;
    }

    public void Dispose() => _ = this.BeginDisposal();
    public ValueTask DisposeAsync() => new(this.BeginDisposal());

    private Task BeginDisposal()
    {
        lock (this.disposalGate)
        {
            return this.disposal ??= this.DisposeCoreAsync();
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            if (inner is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                inner.Dispose();
            }
        }
        finally
        {
            try
            {
                if (owner is not null)
                {
                    await owner.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                if (ownedResolver is not null)
                {
                    await ownedResolver.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }
}
