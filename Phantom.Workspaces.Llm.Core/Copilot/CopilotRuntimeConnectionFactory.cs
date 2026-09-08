using GitHub.Copilot;
using Phantom.Workspaces.Llm.Trust;
using System.Runtime.InteropServices;

namespace Phantom.Workspaces.Llm.Copilot;

internal interface ICopilotRuntimeConnectionFactory
{
    Task<CopilotRuntimeConnectionSelection> CreateAsync(
        CancellationToken cancellationToken = default);
}

internal sealed class CopilotRuntimeConnectionSelection : IAsyncDisposable
{
    private CopilotLaunchPolicyLease? policyLease;

    internal CopilotRuntimeConnectionSelection(
        RuntimeConnection? connection,
        CopilotLaunchPolicyLease? policyLease,
        IReadOnlyList<TrustProfilePolicyDiagnostic>? diagnostics = null,
        string? executablePath = null,
        IReadOnlyList<string>? arguments = null)
    {
        Connection = connection;
        this.policyLease = policyLease;
        Diagnostics = diagnostics ?? [];
        ExecutablePath = executablePath;
        Arguments = arguments ?? [];
    }

    internal RuntimeConnection? Connection { get; }
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

internal sealed class CopilotRuntimeConnectionFactory : ICopilotRuntimeConnectionFactory
{
    internal const string WrapperFileName = "phantom-copilot-wrapper.exe";
    private readonly string? cliPath;
    private readonly TrustProfile? effectiveTrustProfile;
    private readonly ITrustProfileProcessPolicyCompiler compiler;
    private readonly CopilotLaunchPolicyStore policyStore;
    private readonly string baseDirectory;
    private readonly string runtimeIdentifier;

    internal CopilotRuntimeConnectionFactory(
        string? cliPath,
        TrustProfile? effectiveTrustProfile)
        : this(
            cliPath,
            effectiveTrustProfile,
            new MxcTrustProfilePolicyCompiler(),
            new CopilotLaunchPolicyStore(),
            AppContext.BaseDirectory,
            RuntimeInformation.RuntimeIdentifier)
    {
    }

    internal CopilotRuntimeConnectionFactory(
        string? cliPath,
        TrustProfile? effectiveTrustProfile,
        ITrustProfileProcessPolicyCompiler compiler,
        CopilotLaunchPolicyStore policyStore,
        string baseDirectory,
        string runtimeIdentifier)
    {
        this.cliPath = cliPath;
        this.effectiveTrustProfile = effectiveTrustProfile;
        this.compiler = compiler;
        this.policyStore = policyStore;
        this.baseDirectory = baseDirectory;
        this.runtimeIdentifier = runtimeIdentifier;
    }

    public Task<CopilotRuntimeConnectionSelection> CreateAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var compilation = this.effectiveTrustProfile is null
            ? new TrustProfileProcessPolicyCompilation(false, null, [])
            : this.compiler.Compile(this.effectiveTrustProfile);

        var realCliPath = ResolveRealCliPath();
        if (!compilation.RequiresContainment)
        {
            var direct = string.IsNullOrWhiteSpace(this.cliPath)
                ? null
                : RuntimeConnection.ForStdio(realCliPath);
            return Task.FromResult(new CopilotRuntimeConnectionSelection(
                direct,
                policyLease: null,
                compilation.Diagnostics,
                executablePath: direct is null ? null : realCliPath));
        }

        if (compilation.Policy is null)
        {
            var errors = string.Join(
                "; ",
                compilation.Diagnostics.Select(diagnostic => diagnostic.Message));
            throw new InvalidOperationException(
                $"The Copilot containment policy could not be compiled: {errors}");
        }

        ValidateNormalExecutable(realCliPath, "Copilot CLI");
        var runtimeDirectory = System.IO.Path.GetDirectoryName(realCliPath)!;
        var wrapperPath = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(runtimeDirectory, WrapperFileName));
        ValidateNormalExecutable(wrapperPath, "Copilot wrapper");
        if (string.Equals(wrapperPath, realCliPath, PathComparison))
            throw new InvalidOperationException("The Copilot wrapper and CLI paths must be different.");

        var lease = this.policyStore.Create(compilation.Policy);
        try
        {
            string[] arguments = ["--policy", lease.Path, "--copilot", realCliPath];
            var connection = RuntimeConnection.ForStdio(wrapperPath, arguments);
            return Task.FromResult(new CopilotRuntimeConnectionSelection(
                connection,
                lease,
                compilation.Diagnostics,
                wrapperPath,
                arguments));
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private string ResolveRealCliPath()
    {
        var path = string.IsNullOrWhiteSpace(this.cliPath)
            ? System.IO.Path.Combine(
                this.baseDirectory,
                "runtimes",
                this.runtimeIdentifier,
                "native",
                "copilot.exe")
            : this.cliPath;
        return System.IO.Path.GetFullPath(path);
    }

    private static void ValidateNormalExecutable(string path, string description)
    {
        if (!System.IO.Path.IsPathFullyQualified(path)
            || !File.Exists(path)
            || Directory.Exists(path)
            || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(System.IO.Path.GetDirectoryName(path)!)
                & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"{description} path is missing or unsafe: '{path}'.");
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
