using System.Runtime.InteropServices;
using GitHub.Copilot;
using Phantom.Workspaces.Llm.Trust;

namespace Phantom.Workspaces.Llm.Copilot;

internal interface ICopilotRuntimeConnectionFactory
{
    Task<CopilotRuntimeConnectionLease> CreateConnectionAsync(
        AgentExecutionTrustContext trustContext,
        string? cliPath,
        CancellationToken cancellationToken = default);
}

/// <summary>Owns one direct or wrapper Copilot runtime connection selection.</summary>
public sealed class CopilotRuntimeConnectionLease : IAsyncDisposable
{
    private CopilotLaunchPolicyLease? policyLease;

    internal CopilotRuntimeConnectionLease(
        RuntimeConnection connection,
        CopilotLaunchPolicyLease? policyLease,
        IReadOnlyList<TrustProfilePolicyDiagnostic>? diagnostics = null,
        string? executablePath = null,
        IReadOnlyList<string>? arguments = null)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        this.policyLease = policyLease;
        Diagnostics = diagnostics ?? [];
        ExecutablePath = executablePath;
        Arguments = arguments ?? [];
    }

    public RuntimeConnection Connection { get; }
    internal string? PolicyFilePath => this.policyLease?.Path;
    internal IReadOnlyList<TrustProfilePolicyDiagnostic> Diagnostics { get; }
    internal string? ExecutablePath { get; }
    internal IReadOnlyList<string> Arguments { get; }

    public ValueTask DisposeAsync()
    {
        var lease = Interlocked.Exchange(ref this.policyLease, null);
        return lease?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}

/// <summary>
/// Selects a direct or MXC-wrapper Copilot runtime connection from policy compiled on this host.
/// </summary>
public sealed class CopilotRuntimeConnectionFactory : ICopilotRuntimeConnectionFactory
{
    internal const string WrapperFileName = "phantom-copilot-wrapper.exe";
    private readonly ICopilotLaunchPolicyStore policyStore;
    private readonly string baseDirectory;
    private readonly string runtimeIdentifier;

    public CopilotRuntimeConnectionFactory()
        : this(
            new CopilotLaunchPolicyStore(),
            AppContext.BaseDirectory,
            RuntimeInformation.RuntimeIdentifier)
    {
    }

    internal CopilotRuntimeConnectionFactory(
        ICopilotLaunchPolicyStore policyStore,
        string baseDirectory,
        string runtimeIdentifier)
    {
        this.policyStore = policyStore ?? throw new ArgumentNullException(nameof(policyStore));
        this.baseDirectory = baseDirectory ?? throw new ArgumentNullException(nameof(baseDirectory));
        this.runtimeIdentifier = runtimeIdentifier ?? throw new ArgumentNullException(nameof(runtimeIdentifier));
    }

    public async Task<CopilotRuntimeConnectionLease> CreateConnectionAsync(
        AgentExecutionTrustContext trustContext,
        string? cliPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trustContext);
        var compilation = await trustContext
            .GetCompilationAsync(cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var realCliPath = ResolveRealCliPath(cliPath);
        if (!compilation.RequiresContainment)
        {
            return new CopilotRuntimeConnectionLease(
                RuntimeConnection.ForStdio(realCliPath),
                policyLease: null,
                compilation.Diagnostics,
                realCliPath);
        }

        if (compilation.Policy is null)
        {
            throw new InvalidOperationException(
                "The Copilot containment policy could not be compiled on the launch host.");
        }

        ValidateNormalExecutable(realCliPath, "Copilot CLI");
        var runtimeDirectory = Path.GetDirectoryName(realCliPath)!;
        var wrapperPath = Path.GetFullPath(Path.Combine(runtimeDirectory, WrapperFileName));
        ValidateNormalExecutable(wrapperPath, "Copilot wrapper");
        if (string.Equals(wrapperPath, realCliPath, PathComparison))
            throw new InvalidOperationException("The Copilot wrapper and CLI paths must be different.");

        var lease = this.policyStore.Create(compilation.Policy);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] arguments = ["--policy", lease.Path, "--copilot", realCliPath];
            return new CopilotRuntimeConnectionLease(
                RuntimeConnection.ForStdio(wrapperPath, arguments),
                lease,
                compilation.Diagnostics,
                wrapperPath,
                arguments);
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private string ResolveRealCliPath(string? cliPath)
    {
        var path = string.IsNullOrWhiteSpace(cliPath)
            ? Path.Combine(
                this.baseDirectory,
                "runtimes",
                this.runtimeIdentifier,
                "native",
                "copilot.exe")
            : cliPath;
        return Path.GetFullPath(path);
    }

    private static void ValidateNormalExecutable(string path, string description)
    {
        if (!Path.IsPathFullyQualified(path)
            || !File.Exists(path)
            || Directory.Exists(path)
            || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(Path.GetDirectoryName(path)!)
                & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"{description} path is missing or unsafe.");
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
