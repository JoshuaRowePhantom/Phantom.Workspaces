using System.Text.Json;
using System.Text.Json.Serialization;
namespace Phantom.Workspaces.Llm.Interfaces.Access.Execution;

internal enum CredentialExecutorKind { StoredSecret, GitHub, Entra, OAuth, McpOAuth, DevTunnelIdentity, DevTunnelConnect, CliOwned }
internal enum ProviderProtocolKind { Credential, OAuth, Device, Sdk, Cli, TunnelIdentity, TunnelConnect }

internal sealed record ProviderAcquisitionKey
{
    public required LogicalCredentialIdentifier Configuration { get; init; }
    public required CredentialSourceIdentifier Source { get; init; }
    public CredentialAccountIdentifier? Account { get; init; }
    public required ProviderProtocolKind Protocol { get; init; }
}

[JsonConverter(typeof(SerializationGuard))]
internal sealed class ProviderInteractionPermit
{
    private readonly UserAuthorizationRequestIdentifier requestIdentifier;
    private readonly long revision;
    private readonly CredentialExecutorKind executor;
    private readonly Guid generation;
    private readonly ProviderAcquisitionKey key;
    private readonly Func<bool> isCurrent;
    private int consumed;
    private bool executionBound;

    internal ProviderInteractionPermit(
        UserAuthorizationRequestIdentifier requestIdentifier,
        long revision,
        CredentialExecutorKind executor,
        Guid generation,
        ProviderAcquisitionKey key,
        Func<bool> isCurrent)
    {
        this.requestIdentifier = requestIdentifier;
        this.revision = revision;
        this.executor = executor;
        this.generation = generation;
        this.key = key;
        this.isCurrent = isCurrent;
    }

    internal bool Authorizes(
        UserAuthorizationRequestIdentifier requestIdentifier,
        long revision,
        CredentialExecutorKind executor,
        Guid generation,
        ProviderAcquisitionKey key) =>
        this.requestIdentifier == requestIdentifier && this.revision == revision &&
        this.executor == executor && this.generation == generation && this.key == key &&
        this.isCurrent();

    internal bool TryConsume(IProviderAcquisitionOperation operation) =>
        this.Authorizes(operation.RequestIdentifier, operation.Revision,
            operation.ExecutorKind, operation.AcquisitionGeneration, operation.CompatibilityKey) &&
        Interlocked.CompareExchange(ref this.consumed, 1, 0) == 0;

    internal bool BelongsToGeneration(
        UserAuthorizationRequestIdentifier requestIdentifier,
        long revision,
        Guid generation) =>
        this.requestIdentifier == requestIdentifier && this.revision == revision &&
        this.generation == generation && this.isCurrent();

    internal bool AuthorizesProvider(
        CredentialExecutorKind executor,
        ProviderAcquisitionKey key) =>
        this.executionBound && this.executor == executor && this.key == key && this.isCurrent();

    internal ProviderInteractionPermit BindExecution(Func<bool> isActive)
    {
        var permit = new ProviderInteractionPermit(
            this.requestIdentifier, this.revision, this.executor, this.generation, this.key, isActive);
        permit.consumed = 1;
        permit.executionBound = true;
        return permit;
    }

    private sealed class SerializationGuard : JsonConverter<ProviderInteractionPermit>
    {
        public override ProviderInteractionPermit Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) => throw new NotSupportedException("Interaction permits cannot be serialized.");

        public override void Write(
            Utf8JsonWriter writer,
            ProviderInteractionPermit value,
            JsonSerializerOptions options) => throw new NotSupportedException("Interaction permits cannot be serialized.");
    }
}
