using System;
using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Resolves a GitHub authentication token from the environment, falling back to the GitHub CLI.
/// The token is taken from the predefined <c>GITHUB_TOKEN</c> environment variable when set;
/// otherwise it is obtained from <c>gh auth token</c>. This is the single place that resolves the
/// GitHub token (for example for GitHub model/MCP API keys and the dev tunnel
/// <c>X-Tunnel-Authorization</c> token), so the token source is never a user-configured field.
/// </summary>
public static class GitHubAuthTokenResolver
{
    /// <summary>The predefined environment variable that holds a GitHub token when present.</summary>
    public const string GitHubTokenEnvironmentVariable = "GITHUB_TOKEN";

    private static readonly TimeSpan GitHubCliTimeout = TimeSpan.FromMilliseconds(10_000);

    private static readonly RunProcessParameters GitHubCliParameters = OperatingSystem.IsWindows()
        // On Windows, UseShellExecute=false cannot execute .cmd scripts from PATH directly.
        // Route through cmd.exe so both gh.exe and gh.cmd variants are resolved correctly.
        ? new RunProcessParameters(
            Command: "cmd.exe",
            Arguments: ["/c", "gh", "auth", "token"],
            Timeout: GitHubCliTimeout)
        : new RunProcessParameters(
            Command: "gh",
            Arguments: ["auth", "token"],
            Timeout: GitHubCliTimeout);

    /// <summary>
    /// Asynchronously resolves the GitHub token from <c>GITHUB_TOKEN</c>, falling back to <c>gh auth token</c>.
    /// Returns <see langword="null"/> when neither source yields a token.
    /// </summary>
    public static async Task<string?> ResolveAsync(ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        var environmentValue = Environment.GetEnvironmentVariable(GitHubTokenEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return environmentValue;
        }

        return await ResolveFromCliAsync(logger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the GitHub token from <c>GITHUB_TOKEN</c>, falling back to <c>gh auth token</c>.
    /// Returns <see langword="null"/> when neither source yields a token.
    /// </summary>
    public static string? Resolve(ILogger? logger = null)
    {
        var environmentValue = Environment.GetEnvironmentVariable(GitHubTokenEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return environmentValue;
        }

        return ResolveFromCli(logger);
    }

    /// <summary>
    /// Asynchronously resolves the GitHub token from the GitHub CLI (<c>gh auth token</c>). Returns
    /// <see langword="null"/> when the CLI is unavailable or returns no token.
    /// When <paramref name="logger"/> is supplied, a Warning-level entry is emitted if
    /// <c>gh auth token</c> exits with a non-zero code.
    /// </summary>
    public static async Task<string?> ResolveFromCliAsync(ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        return await ResolveFromCliCoreAsync(logger ?? NullLogger.Instance, GitHubCliParameters, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the GitHub token from the GitHub CLI (<c>gh auth token</c>). Returns
    /// <see langword="null"/> when the CLI is unavailable or returns no token.
    /// When <paramref name="logger"/> is supplied, a Warning-level entry is emitted if
    /// <c>gh auth token</c> exits with a non-zero code.
    /// </summary>
    public static string? ResolveFromCli(ILogger? logger = null)
    {
        return ResolveFromCliCore(logger ?? NullLogger.Instance, GitHubCliParameters);
    }

    internal static async Task<string?> ResolveFromCliCoreAsync(
        ILogger logger, RunProcessParameters parameters, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await ProcessRunner.RunAndLogAsync(
                parameters,
                logger,
                operationDescription: "resolve GitHub auth token via CLI",
                cancellationToken: cancellationToken,
                successLogLevel: LogLevel.Debug).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                return null;
            }

            return result.StandardOut.Trim();
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    internal static string? ResolveFromCliCore(ILogger logger, RunProcessParameters parameters)
    {
        try
        {
            var result = ProcessRunner.RunAndLogAsync(
                parameters,
                logger,
                operationDescription: "resolve GitHub auth token via CLI",
                successLogLevel: LogLevel.Debug)
                .GetAwaiter().GetResult();

            if (result.ExitCode != 0)
            {
                return null;
            }

            return result.StandardOut.Trim();
        }
        catch (Win32Exception)
        {
            return null;
        }
    }
}
