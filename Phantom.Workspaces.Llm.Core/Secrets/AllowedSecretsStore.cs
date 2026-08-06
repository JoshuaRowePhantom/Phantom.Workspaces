using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phantom.Workspaces.Llm.Secrets;

/// <summary>
/// A JSON file-backed <see cref="IAllowedSecretsStore"/>. The in-memory map is loaded lazily on
/// first use and re-persisted (with <see cref="FileMode.Create"/>) on every <see cref="PutAsync"/>.
/// Only content-addressed hashes and value-free descriptors are ever written; no secret material
/// crosses into the file, by construction of <see cref="MemorizedSecret"/>.
/// </summary>
public sealed class AllowedSecretsStore : IAllowedSecretsStore
{
    // NOTE: These options intentionally mirror ConfigurationPersistenceService's serializer options
    // (camel-case, WhenWritingNull, indented, camel-case string enums). They are duplicated here
    // because AllowedSecretsStore lives in Llm.Core, which cannot reference the application project
    // where ConfigurationPersistenceService resides. Tracked for consolidation by a follow-up issue.
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly AllowedSecretsStoreConfiguration configuration;
    private readonly SemaphoreSlim gate = new(1, 1);

    private Dictionary<string, MemorizedSecret>? cache;

    /// <summary>Creates a store backed by the file described by <paramref name="configuration"/>.</summary>
    public AllowedSecretsStore(AllowedSecretsStoreConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        this.configuration = configuration;
    }

    /// <summary>The resolved absolute path of the backing JSON file.</summary>
    public string FilePath => this.configuration.Path ?? GetDefaultFilePath();

    /// <summary>
    /// Computes the default backing-file path: <c>allowed-secrets.json</c> next to the primary
    /// <c>config.json</c> under <c>%APPDATA%\Phantom.Workspaces\</c>.
    /// </summary>
    public static string GetDefaultFilePath()
    {
        var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(applicationData, "Phantom.Workspaces", "allowed-secrets.json");
    }

    /// <inheritdoc />
    public async Task<MemorizedSecret?> TryGetAsync(string hash, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(hash);

        await this.gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var map = await this.EnsureLoadedAsync(ct).ConfigureAwait(false);
            return map.TryGetValue(hash, out var record) ? record : null;
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task PutAsync(string hash, MemorizedSecret record, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(hash);
        ArgumentNullException.ThrowIfNull(record);

        await this.gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var map = await this.EnsureLoadedAsync(ct).ConfigureAwait(false);
            map[hash] = record;
            await this.PersistAsync(map, ct).ConfigureAwait(false);
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, MemorizedSecret>> LoadAllAsync(CancellationToken ct)
    {
        await this.gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var map = await this.EnsureLoadedAsync(ct).ConfigureAwait(false);
            return new Dictionary<string, MemorizedSecret>(map);
        }
        finally
        {
            this.gate.Release();
        }
    }

    private async Task<Dictionary<string, MemorizedSecret>> EnsureLoadedAsync(CancellationToken ct)
    {
        if (this.cache is not null)
        {
            return this.cache;
        }

        var path = this.FilePath;
        if (!File.Exists(path))
        {
            this.cache = new Dictionary<string, MemorizedSecret>(StringComparer.Ordinal);
            return this.cache;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);

        var loaded = await JsonSerializer
            .DeserializeAsync<Dictionary<string, MemorizedSecret>>(stream, SerializerOptions, ct)
            .ConfigureAwait(false);

        this.cache = loaded is null
            ? new Dictionary<string, MemorizedSecret>(StringComparer.Ordinal)
            : new Dictionary<string, MemorizedSecret>(loaded, StringComparer.Ordinal);
        return this.cache;
    }

    private async Task PersistAsync(Dictionary<string, MemorizedSecret> map, CancellationToken ct)
    {
        var path = this.FilePath;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            useAsync: true);

        await JsonSerializer.SerializeAsync(stream, map, SerializerOptions, ct).ConfigureAwait(false);
    }
}
