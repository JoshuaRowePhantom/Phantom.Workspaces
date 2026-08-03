using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Phantom.Workspaces.Tools;

/// <summary>
/// Default <see cref="IVsCodeTunnelStatusResolver"/>. Invokes <c>code tunnel status --output json</c>
/// (preferred) with a fallback to the legacy text form, and parses the tunnel name / URL /
/// connected state out of the captured stdout.
/// </summary>
public sealed class VsCodeTunnelStatusResolver : IVsCodeTunnelStatusResolver
{
    private readonly ILogger logger;
    private readonly Func<string, string, CancellationToken, Task<ProcessResult>> processRunner;

    public VsCodeTunnelStatusResolver(
        ILogger? logger = null,
        Func<string, string, CancellationToken, Task<ProcessResult>>? processRunner = null)
    {
        this.logger = logger ?? NullLogger.Instance;
        this.processRunner = processRunner ?? this.DefaultRunAsync;
    }

    public async Task<VsCodeTunnelStatus?> GetTunnelStatusAsync(string cliPath, CancellationToken cancellationToken)
    {
        ProcessResult jsonResult;
        try
        {
            jsonResult = await this.processRunner(cliPath, "tunnel status --output json", cancellationToken).ConfigureAwait(false);
        }
        catch (Win32Exception ex)
        {
            this.logger.LogWarning(ex, "VS Code CLI ('code') could not be launched at '{CliPath}'.", cliPath);
            return null;
        }

        if (TryParseJsonStatus(jsonResult.StandardOut, out var jsonStatus))
        {
            return jsonStatus;
        }

        ProcessResult textResult;
        try
        {
            textResult = await this.processRunner(cliPath, "tunnel status", cancellationToken).ConfigureAwait(false);
        }
        catch (Win32Exception ex)
        {
            this.logger.LogWarning(ex, "VS Code CLI ('code') could not be launched at '{CliPath}'.", cliPath);
            return null;
        }

        return TryParseTextStatus(textResult.StandardOut, textResult.ExitCode);
    }

    /// <summary>
    /// Attempts to parse <c>code tunnel status --output json</c> stdout. Returns false when the
    /// output does not describe a running tunnel (or cannot be parsed as JSON).
    /// </summary>
    internal static bool TryParseJsonStatus(string? stdout, out VsCodeTunnelStatus? status)
    {
        status = null;
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return false;
        }

        // The CLI may emit multiple JSON objects (one per line). Try each candidate.
        foreach (var line in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(trimmed);
                if (TryReadFromJsonElement(document.RootElement, out status))
                {
                    return true;
                }
            }
            catch (JsonException)
            {
            }
        }

        // Also try the whole document (may be pretty-printed JSON spanning multiple lines).
        try
        {
            using var document = JsonDocument.Parse(stdout);
            if (TryReadFromJsonElement(document.RootElement, out status))
            {
                return true;
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }

    private static bool TryReadFromJsonElement(JsonElement element, out VsCodeTunnelStatus? status)
    {
        status = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        // The CLI wraps the payload in a "tunnel" object on newer versions.
        var payload = element;
        if (element.TryGetProperty("tunnel", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object)
        {
            payload = wrapped;
        }

        string? tunnelName = ReadString(payload, "name")
            ?? ReadString(payload, "tunnel_name")
            ?? ReadString(payload, "tunnelName");
        string? tunnelUrl = ReadString(payload, "url")
            ?? ReadString(payload, "tunnel_url")
            ?? ReadString(payload, "tunnelUrl");
        bool? isConnected = ReadBool(payload, "connected")
            ?? ReadBool(payload, "isConnected")
            ?? ReadBool(payload, "is_connected");

        if (isConnected is null && payload.TryGetProperty("state", out var stateElement)
            && stateElement.ValueKind == JsonValueKind.String)
        {
            var stateValue = stateElement.GetString();
            if (!string.IsNullOrWhiteSpace(stateValue))
            {
                isConnected = stateValue!.Contains("connect", StringComparison.OrdinalIgnoreCase)
                    || stateValue.Contains("running", StringComparison.OrdinalIgnoreCase)
                    || stateValue.Contains("online", StringComparison.OrdinalIgnoreCase);
            }
        }

        if (string.IsNullOrWhiteSpace(tunnelName))
        {
            return false;
        }

        var effectiveUrl = string.IsNullOrWhiteSpace(tunnelUrl)
            ? $"https://vscode.dev/tunnel/{tunnelName}"
            : tunnelUrl!;
        status = new VsCodeTunnelStatus(tunnelName!, effectiveUrl, isConnected ?? true);
        return true;
    }

    private static string? ReadString(JsonElement obj, string propertyName)
    {
        if (obj.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    private static bool? ReadBool(JsonElement obj, string propertyName)
    {
        if (obj.TryGetProperty(propertyName, out var element))
        {
            return element.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => (bool?)null,
            };
        }

        return null;
    }

    /// <summary>
    /// Parses the legacy text form of <c>code tunnel status</c> stdout. Recognises the
    /// commonly-emitted <c>Connected to tunnel: &lt;name&gt;</c> and <c>tunnel name: &lt;name&gt;</c>
    /// forms, and returns <see langword="null"/> when the output indicates no tunnel.
    /// </summary>
    internal static VsCodeTunnelStatus? TryParseTextStatus(string? stdout, int exitCode)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return null;
        }

        if (LooksLikeNoTunnel(stdout))
        {
            return null;
        }

        var name = ExtractTunnelName(stdout);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        // Consider the tunnel connected when the CLI exited zero and the output mentions
        // "connected"/"online"/"running" (or does not mention an explicit disconnect state).
        var isConnected = exitCode == 0
            && !stdout.Contains("disconnected", StringComparison.OrdinalIgnoreCase);

        return new VsCodeTunnelStatus(name!, $"https://vscode.dev/tunnel/{name}", isConnected);
    }

    private static bool LooksLikeNoTunnel(string stdout)
    {
        return stdout.Contains("no tunnel", StringComparison.OrdinalIgnoreCase)
            || stdout.Contains("not started", StringComparison.OrdinalIgnoreCase)
            || stdout.Contains("tunnel not running", StringComparison.OrdinalIgnoreCase)
            || stdout.Contains("no active tunnel", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly Regex TunnelNameRegex = new(
        @"(?:connected to tunnel|tunnel name|tunnel)\s*[:=]\s*(?<name>[A-Za-z0-9._\-]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string? ExtractTunnelName(string stdout)
    {
        var match = TunnelNameRegex.Match(stdout);
        if (match.Success)
        {
            return match.Groups["name"].Value;
        }

        return null;
    }

    private async Task<ProcessResult> DefaultRunAsync(string cliPath, string arguments, CancellationToken cancellationToken)
    {
        var parameters = VsCodeCliLocator.BuildRunProcessParameters(cliPath, arguments);
        return await ProcessRunner.RunAndLogAsync(
            parameters,
            this.logger,
            operationDescription: $"vscode {arguments}",
            cancellationToken).ConfigureAwait(false);
    }
}
