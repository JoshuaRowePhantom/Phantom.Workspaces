using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Llm.Trust;

/// <summary>Resolves named trust profiles from the host's local workspace data layer.</summary>
internal sealed class DataAccessTrustProfileProvider : ITrustProfileProvider
{
    private readonly IDataAccessLayer dataAccessLayer;

    internal DataAccessTrustProfileProvider(IDataAccessLayer dataAccessLayer)
    {
        this.dataAccessLayer = dataAccessLayer
            ?? throw new ArgumentNullException(nameof(dataAccessLayer));
    }

    public async ValueTask<TrustProfile> ResolveAsync(
        string profileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        var entities = new Dictionary<string, TrustProfileEntity>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(profileName);
        while (pending.TryPop(out var current))
        {
            if (entities.ContainsKey(current))
                continue;

            var result = await this.dataAccessLayer.GetAsync(
                new GetRequest
                {
                    Entities =
                    [
                        new GetEntityRequest
                        {
                            EntityName = new EntityName("trust-profiles", current),
                        },
                    ],
                },
                cancellationToken).ConfigureAwait(false);
            var data = result.Batches
                .SelectMany(batch => batch.Entities)
                .Select(entity => entity.Data)
                .FirstOrDefault(value => value.HasValue);
            if (data is null)
            {
                throw new InvalidOperationException(
                    $"Trust profile '{current}' could not be resolved on this host.");
            }

            var entity = TrustProfileEntityReader.Read(data.Value);
            entities[current] = entity;
            foreach (var baseReference in entity.Bases)
                pending.Push(baseReference.ProfileName);
        }

        return await new DictionaryTrustProfileProvider(entities)
            .ResolveAsync(profileName, cancellationToken)
            .ConfigureAwait(false);
    }
}
