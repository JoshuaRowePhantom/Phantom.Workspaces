using System.Diagnostics;
using System.Text;

namespace Phantom.Workspaces.Agent.Gui.Tests;

internal sealed class TestMcpServerProcess : IAsyncDisposable
{
    private readonly Process process;

    private TestMcpServerProcess(Process process, string boundUrl)
    {
        this.process = process;
        this.BoundUrl = boundUrl;
    }

    public string BoundUrl { get; }

    public static async Task<TestMcpServerProcess> StartAsync()
    {
        var executablePath = GetMcpExecutablePath();
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--mode");
        startInfo.ArgumentList.Add("http");

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start MCP test server process.");

        var stderrBuilder = new StringBuilder();
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is { Length: > 0 })
            {
                lock (stderrBuilder)
                {
                    stderrBuilder.AppendLine(eventArgs.Data);
                }
            }
        };
        process.BeginErrorReadLine();

        string? boundUrl;
        try
        {
            boundUrl = await ReadHttpUrlLineAsync(process, TimeSpan.FromSeconds(20));
        }
        catch (OperationCanceledException ex)
        {
            throw new TimeoutException($"Timed out waiting for MCP server URL. Stderr:{Environment.NewLine}{stderrBuilder}", ex);
        }

        if (string.IsNullOrWhiteSpace(boundUrl))
        {
            throw new InvalidOperationException($"MCP server did not emit a URL. Stderr:{Environment.NewLine}{stderrBuilder}");
        }

        if (!Uri.TryCreate(boundUrl, UriKind.Absolute, out var url) || !string.Equals(url.Scheme, "http", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"MCP server emitted invalid URL '{boundUrl}'. Stderr:{Environment.NewLine}{stderrBuilder}");
        }

        return new TestMcpServerProcess(process, boundUrl);
    }

    public async ValueTask DisposeAsync()
    {
        if (!this.process.HasExited)
        {
            this.process.Kill(entireProcessTree: true);
            await this.process.WaitForExitAsync();
        }

        this.process.Dispose();
    }

    private static async Task<string?> ReadHttpUrlLineAsync(Process process, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            var line = await process.StandardOutput.ReadLineAsync().WaitAsync(cts.Token);
            if (line is null)
            {
                return null;
            }

            if (Uri.TryCreate(line, UriKind.Absolute, out var uri)
                && string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase))
            {
                return line;
            }
        }

        return null;
    }

    internal static string GetMcpExecutablePathForTests() => GetMcpExecutablePath();

    private static string GetMcpExecutablePath()
    {
        var copiedExecutable = Path.Combine(AppContext.BaseDirectory, "Phantom.Workspaces.Llm.Test.Mcp.exe");
        if (File.Exists(copiedExecutable))
        {
            return copiedExecutable;
        }

        var repositoryRoot = FindRepositoryRoot();
        var fallbackExecutable = Path.Combine(
            repositoryRoot,
            "Phantom.Workspaces.Llm.Test.Mcp",
            "bin",
            "Debug",
            "net10.0",
            "Phantom.Workspaces.Llm.Test.Mcp.exe");
        if (File.Exists(fallbackExecutable))
        {
            return fallbackExecutable;
        }

        throw new InvalidOperationException($"""
            Could not locate Phantom.Workspaces.Llm.Test.Mcp.exe.
            Checked:
            - {copiedExecutable}
            - {fallbackExecutable}
            Base directory: {AppContext.BaseDirectory}
            Current directory: {Environment.CurrentDirectory}
            """);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Phantom.Workspaces.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test base directory.");
    }
}
