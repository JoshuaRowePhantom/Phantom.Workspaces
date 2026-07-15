using System.Text.Json;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Transport;

public sealed class UserComputerProfileTransportFactory : ITransportFactory
{
    private const string DescriptorType = "user-computer-profile";
    private readonly IDataAccessLayer dataAccessLayer;
    private readonly WorkspaceEntitySession workspaceEntitySession;
    private readonly ITransportFactoryRegistry transportFactoryRegistry;

    public UserComputerProfileTransportFactory(
        IDataAccessLayer dataAccessLayer,
        WorkspaceEntitySession workspaceEntitySession,
        ITransportFactoryRegistry transportFactoryRegistry)
    {
        this.dataAccessLayer = dataAccessLayer ?? throw new ArgumentNullException(nameof(dataAccessLayer));
        this.workspaceEntitySession = workspaceEntitySession ?? throw new ArgumentNullException(nameof(workspaceEntitySession));
        this.transportFactoryRegistry = transportFactoryRegistry ?? throw new ArgumentNullException(nameof(transportFactoryRegistry));
    }

    public async Task<ITransport?> ConnectToAsync(JsonElement connectionDescriptor, CancellationToken ct = default)
    {
        if (!connectionDescriptor.TryGetProperty("type", out var type)
            || !string.Equals(type.GetString(), DescriptorType, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!connectionDescriptor.TryGetProperty("entity-id", out var entityIdProperty)
            || entityIdProperty.GetString() is not { Length: > 0 } entityIdText)
        {
            throw new TransportException("User computer profile descriptors must include entity-id.");
        }

        var entityId = new EntityId(entityIdText);
        var profileEntity = await this.GetRequiredProfileEntityAsync(entityId, ct).ConfigureAwait(false);
        JsonElement routedDescriptor;
        if (entityId == this.workspaceEntitySession.UserComputerProfileEntityId)
        {
            using var localDocument = JsonDocument.Parse("""{"type":"local"}""");
            routedDescriptor = localDocument.RootElement.Clone();
        }
        else
        {
            if (!profileEntity.TryGetProperty("connection-descriptor", out var entityConnectionDescriptor)
                || entityConnectionDescriptor.ValueKind != JsonValueKind.Object)
            {
                throw new TransportException(
                    $"User computer profile entity '{entityId}' does not contain a connection-descriptor object.");
            }

            routedDescriptor = entityConnectionDescriptor.Clone();
        }

        var transport = await this.transportFactoryRegistry.ConnectToAsync(routedDescriptor, ct).ConfigureAwait(false);
        if (connectionDescriptor.TryGetProperty("target", out var target) && target.ValueKind == JsonValueKind.Object)
        {
            return new TargetedTransport(transport, target.Clone());
        }

        return transport;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<JsonElement> GetRequiredProfileEntityAsync(EntityId entityId, CancellationToken ct)
    {
        var result = await this.dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities = [new GetEntityRequest { EntityId = entityId }],
                Timestamps = [null],
            },
            ct).ConfigureAwait(false);
        var entity = result.Batches
            .SelectMany(static batch => batch.Entities)
            .FirstOrDefault(snapshot => snapshot.EntityId == entityId);
        if (entity?.Data is not { } data)
        {
            throw new TransportException($"User computer profile entity '{entityId}' could not be resolved.");
        }

        return data.Clone();
    }

    private sealed class TargetedTransport : ITransport
    {
        private readonly ITransport inner;
        private readonly JsonElement targetDescriptor;

        public TargetedTransport(ITransport inner, JsonElement targetDescriptor)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            this.targetDescriptor = targetDescriptor.Clone();
        }

        public Task<IMessageChannel> ConnectToMessageChannelAsync(JsonElement request, CancellationToken ct = default)
            => this.inner.ConnectToMessageChannelAsync(this.targetDescriptor, ct);

        public Task<Stream> ConnectToStreamAsync(JsonElement request, CancellationToken ct = default)
            => this.inner.ConnectToStreamAsync(this.targetDescriptor, ct);

        public ValueTask DisposeAsync() => this.inner.DisposeAsync();
    }
}
