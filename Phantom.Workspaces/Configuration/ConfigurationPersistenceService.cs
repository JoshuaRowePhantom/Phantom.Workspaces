using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Configuration;

/// <summary>
/// Reads and writes the persisted <see cref="WorkspacesConfiguration"/> document.
/// </summary>
/// <remarks>
/// The configuration is JSON-backed and stored in the user profile location unless an explicit
/// path is provided. Only secret <em>sources</em> (for example, environment variable names) are
/// persisted; raw secret values are never written, by construction of the model.
/// </remarks>
public sealed class ConfigurationPersistenceService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string defaultConfigurationPath;

    /// <summary>
    /// Creates a persistence service using the default user-profile configuration path.
    /// </summary>
    public ConfigurationPersistenceService()
        : this(GetDefaultConfigurationPath())
    {
    }

    /// <summary>
    /// Creates a persistence service using an explicit default configuration path.
    /// </summary>
    /// <param name="defaultConfigurationPath">The configuration file path to use when none is supplied per call.</param>
    public ConfigurationPersistenceService(string defaultConfigurationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultConfigurationPath);
        this.defaultConfigurationPath = defaultConfigurationPath;
    }

    /// <summary>The default configuration file path used when no explicit path is provided.</summary>
    public string DefaultConfigurationPath => this.defaultConfigurationPath;

    /// <summary>
    /// Computes the default configuration path under the user's application data directory.
    /// </summary>
    public static string GetDefaultConfigurationPath()
    {
        var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(applicationData, "Phantom.Workspaces", "config.json");
    }

    /// <summary>
    /// Determines whether a configuration file exists at the given (or default) path.
    /// </summary>
    public bool ConfigurationExists(string? path = null)
        => File.Exists(path ?? this.defaultConfigurationPath);

    /// <summary>
    /// Loads the configuration from the given (or default) path. When the file does not exist,
    /// a configuration populated with defaults is returned.
    /// </summary>
    public async Task<WorkspacesConfiguration> LoadAsync(
        string? path = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedPath = path ?? this.defaultConfigurationPath;
        if (!File.Exists(resolvedPath))
        {
            return new WorkspacesConfiguration();
        }

        await using var stream = new FileStream(
            resolvedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);

        var configuration = await JsonSerializer
            .DeserializeAsync<WorkspacesConfiguration>(stream, SerializerOptions, cancellationToken)
            .ConfigureAwait(false);

        return configuration ?? new WorkspacesConfiguration();
    }

    /// <summary>
    /// Saves the configuration to the given (or default) path, creating directories as needed.
    /// </summary>
    public async Task SaveAsync(
        WorkspacesConfiguration configuration,
        string? path = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var resolvedPath = path ?? this.defaultConfigurationPath;
        var directory = Path.GetDirectoryName(resolvedPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = new FileStream(
            resolvedPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            useAsync: true);

        await JsonSerializer
            .SerializeAsync(stream, configuration, SerializerOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Serializes the configuration to a JSON string using the canonical serializer options.
    /// </summary>
    public static string Serialize(WorkspacesConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return JsonSerializer.Serialize(configuration, SerializerOptions);
    }
}
