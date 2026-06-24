using System.Security.Cryptography;
using System.Text;

namespace Phantom.Workspaces.Install;

/// <summary>
/// Computes the single-instance identity (mutex and activation-pipe names) from the active config
/// file path, or an explicit instance key. Keying on the config path means two instances pointed
/// at <em>different</em> config files coexist on one machine — essential for running/testing
/// multiple instances on a single computer — while two launches sharing a config file collapse to
/// one. Normalisation is case-insensitive and path-absolute so equivalent paths map to one key.
/// </summary>
public static class SingleInstanceKey
{
    /// <summary>Prefix for the per-config named mutex.</summary>
    public const string MutexPrefix = @"Local\Phantom.Workspaces.SingleInstance.";

    /// <summary>Prefix for the per-config activation pipe.</summary>
    public const string PipePrefix = "Phantom.Workspaces.Activation.";

    /// <summary>The basis used when no config path or explicit key is supplied (the default config).</summary>
    public const string DefaultBasis = "default";

    /// <summary>
    /// Computes the stable identity hash for <paramref name="configFilePath"/>, or
    /// <paramref name="explicitInstanceKey"/> when supplied. An absent config path maps to the
    /// default-config identity so the normal per-user launch is still single-instance.
    /// </summary>
    public static string Compute(string? configFilePath, string? explicitInstanceKey = null)
    {
        string basis;
        if (!string.IsNullOrWhiteSpace(explicitInstanceKey))
        {
            basis = explicitInstanceKey.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(configFilePath))
        {
            basis = Path.GetFullPath(configFilePath.Trim()).ToLowerInvariant();
        }
        else
        {
            basis = DefaultBasis;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(basis));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>The named-mutex name for the given config/instance.</summary>
    public static string MutexName(string? configFilePath, string? explicitInstanceKey = null)
        => MutexPrefix + Compute(configFilePath, explicitInstanceKey);

    /// <summary>The activation-pipe name for the given config/instance.</summary>
    public static string PipeName(string? configFilePath, string? explicitInstanceKey = null)
        => PipePrefix + Compute(configFilePath, explicitInstanceKey);
}
