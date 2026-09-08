using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm.Trust;
using System.Security.Cryptography;
using System.Text;

namespace Phantom.Workspaces.Services;

internal sealed class DataAccessLayerTrustProfileResolver
    : IVersionedTrustProfileProvider, IRemoteTrustProfileResolver
{
    private const string TrustProfileEntityType = "llm-trust-profile";
    private readonly IDataAccessLayer dataAccessLayer;

    public DataAccessLayerTrustProfileResolver(IDataAccessLayer dataAccessLayer)
    {
        this.dataAccessLayer = dataAccessLayer ?? throw new ArgumentNullException(nameof(dataAccessLayer));
    }

    public async ValueTask<TrustProfile> ResolveAsync(
        string profileName,
        CancellationToken cancellationToken = default)
        => (await ResolveVersionedAsync(profileName, cancellationToken).ConfigureAwait(false)).Profile;

    public async ValueTask<VersionedTrustProfile> ResolveVersionedAsync(
        string profileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        var snapshots = await QueryProfilesAsync(cancellationToken).ConfigureAwait(false);
        var profiles = snapshots
            .Where(static snapshot => snapshot.Data is not null)
            .Select(static snapshot => (Snapshot: snapshot, Profile: TrustProfileEntityReader.Read(snapshot.Data!.Value)))
            .Where(static item => !string.IsNullOrWhiteSpace(item.Profile.Name))
            .ToDictionary(static item => item.Profile.Name!, static item => item, StringComparer.Ordinal);

        if (!profiles.TryGetValue(profileName, out var selected))
            throw new InvalidOperationException($"Trust profile '{profileName}' could not be resolved.");

        var provider = new DictionaryTrustProfileProvider(
            profiles.ToDictionary(static item => item.Key, static item => item.Value.Profile, StringComparer.Ordinal));
        var profile = await provider.ResolveAsync(profileName, cancellationToken).ConfigureAwait(false);
        var revision = BuildEffectiveRevision(profileName, profiles);
        return new VersionedTrustProfile(profile, revision);
    }

    async Task<RemoteTrustProfileResolution?> IRemoteTrustProfileResolver.ResolveAsync(
        string profileReference,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolved = await ResolveVersionedAsync(profileReference, cancellationToken).ConfigureAwait(false);
            return new RemoteTrustProfileResolution(resolved.Profile, resolved.Revision);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<QueryEntitySnapshot>> QueryProfilesAsync(CancellationToken cancellationToken)
    {
        var result = await dataAccessLayer.QueryAsync(
            new QueryRequest
            {
                Clauses =
                [
                    new TopLevelQueryClause
                    {
                        ClauseIdentifier = new QueryClauseIdentifier("trust-profiles"),
                        Clause = new EntityTypeQueryClause
                        {
                            EntityTypeNames = new EntityTypeNameSet([TrustProfileEntityType]),
                        },
                    },
                ],
            },
            cancellationToken).ConfigureAwait(false);
        return [.. result.Batches.SelectMany(static batch => batch.Entities)];
    }

    private static string BuildEffectiveRevision(
        string profileName,
        IReadOnlyDictionary<string, (QueryEntitySnapshot Snapshot, TrustProfileEntity Profile)> profiles)
    {
        var revisions = new SortedDictionary<string, string>(StringComparer.Ordinal);
        Visit(profileName);
        if (revisions.Count == 1)
            return revisions[profileName];

        var material = string.Join(
            "\n",
            revisions.Select(static item => $"{item.Key}\0{item.Value}"));
        return $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant()}";

        void Visit(string name)
        {
            if (revisions.ContainsKey(name))
                return;
            if (!profiles.TryGetValue(name, out var item))
                throw new InvalidOperationException($"Trust profile '{name}' could not be resolved.");

            revisions[name] = item.Snapshot.ConcurrencyTag?.Value
                ?? item.Snapshot.ModifiedTime.ChangeId;
            foreach (var baseReference in item.Profile.Bases)
                Visit(baseReference.ProfileName);
        }
    }
}
