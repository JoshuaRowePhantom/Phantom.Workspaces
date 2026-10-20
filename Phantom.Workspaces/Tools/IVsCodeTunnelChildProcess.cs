using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Phantom.Workspaces.Tools;

public sealed record VsCodeTunnelProcessEvidence
{
    public required bool ConnectedSeen { get; init; }
    public required bool TokenErrorSeen { get; init; }
}

public sealed record VsCodeTunnelProcessCompletion
{
    public required int ExitCode { get; init; }
    public required bool ConnectedSeen { get; init; }
    public required bool TokenErrorSeen { get; init; }
    public required bool UnauthorizedHttp401Seen { get; init; }
}

public interface IVsCodeTunnelChildProcess : IDisposable
{
    bool HasExited { get; }
    VsCodeTunnelProcessEvidence Evidence { get; }
    Task<VsCodeTunnelProcessCompletion> WaitForExitAndDrainAsync(CancellationToken cancellationToken);
    void Kill();
}

public delegate IVsCodeTunnelChildProcess VsCodeTunnelProcessLauncher(
    string cliPath,
    string arguments);

internal sealed class ProcessBackedVsCodeTunnelChildProcess : IVsCodeTunnelChildProcess
{
    private readonly Process process;
    private readonly Task<VsCodeTunnelProcessCompletion> completion;
    private readonly EvidenceReducer reducer = new();

    public ProcessBackedVsCodeTunnelChildProcess(Process process)
    {
        this.process = process;
        var standardOutput = DrainAsync(process.StandardOutput, this.reducer.AcceptRecord);
        var standardError = DrainAsync(process.StandardError, this.reducer.AcceptRecord);
        this.completion = this.CompleteAsync(standardOutput, standardError);
    }

    public bool HasExited => this.process.HasExited;
    public VsCodeTunnelProcessEvidence Evidence => this.reducer.Evidence;

    public async Task<VsCodeTunnelProcessCompletion> WaitForExitAndDrainAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await this.completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            this.Kill();
            await this.completion.ConfigureAwait(false);
            throw;
        }
    }

    private async Task<VsCodeTunnelProcessCompletion> CompleteAsync(
        Task standardOutput,
        Task standardError)
    {
        await this.process.WaitForExitAsync().ConfigureAwait(false);
        await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
        return this.reducer.Complete(this.process.ExitCode);
    }

    internal static async Task DrainAsync(
        TextReader reader,
        Action<string> acceptRecord)
    {
        var buffer = new char[1024];
        var record = new StringBuilder();
        var oversized = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) != 0)
        {
            for (var index = 0; index < count; index++)
            {
                var character = buffer[index];
                if (character is '\r' or '\n')
                {
                    if (!oversized)
                    {
                        acceptRecord(record.ToString());
                    }

                    record.Clear();
                    oversized = false;
                }
                else if (record.Length < 16384 && !oversized)
                {
                    record.Append(character);
                }
                else
                {
                    record.Clear();
                    oversized = true;
                }
            }
        }

        if (!oversized && record.Length != 0)
        {
            acceptRecord(record.ToString());
        }

        Array.Clear(buffer);
        record.Clear();
    }

    public void Kill()
    {
        try
        {
            if (!this.process.HasExited)
            {
                this.process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public void Dispose()
    {
        this.process.Dispose();
    }

    internal sealed class EvidenceReducer
    {
        private const string MachinePrefix = "__VSCODE_CLI_STATUS__";
        private int flags;

        public VsCodeTunnelProcessEvidence Evidence => new()
        {
            ConnectedSeen = (Volatile.Read(ref this.flags) & 1) != 0,
            TokenErrorSeen = (Volatile.Read(ref this.flags) & 2) != 0,
        };

        public void AcceptRecord(string record)
        {
            if (record.StartsWith(MachinePrefix, StringComparison.Ordinal))
            {
                try
                {
                    using var document = JsonDocument.Parse(record.AsMemory(MachinePrefix.Length));
                    if (document.RootElement.ValueKind == JsonValueKind.Object
                        && document.RootElement.TryGetProperty("type", out var type)
                        && type.ValueKind == JsonValueKind.String)
                    {
                        var flag = type.GetString() switch { "connected" => 1, "tokenError" => 2, _ => 0 };
                        Interlocked.Or(ref this.flags, flag);
                    }
                }
                catch (JsonException) { }

                // Machine payload fields, including token-error messages, never become fallback evidence.
                return;
            }

            var normalized = Regex.Replace(record, @"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\))", string.Empty);
            normalized = new string(normalized.Where(character => !char.IsControl(character) || character == '\t').ToArray());
            normalized = Regex.Replace(normalized, @"\s+", " ");
            if (Regex.IsMatch(normalized, @"\bHTTP(?:/\d+(?:\.\d+)?)?\s+(?:(?:status(?:\s+code)?|response)(?:\s+client\s+error)?\s*[:=(]?\s*)?401\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                && Regex.IsMatch(normalized, @"\bUnauthorized\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                Interlocked.Or(ref this.flags, 4);
            }
        }

        public VsCodeTunnelProcessCompletion Complete(int exitCode) => new()
        {
            ExitCode = exitCode,
            ConnectedSeen = this.Evidence.ConnectedSeen,
            TokenErrorSeen = this.Evidence.TokenErrorSeen,
            UnauthorizedHttp401Seen = (Volatile.Read(ref this.flags) & 4) != 0,
        };
    }
}
