using System.Diagnostics;

namespace Phantom.Workspaces.Install.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MxcIntegrationCollection
{
    public const string Name = "MXC integration";
}

[Collection(MxcIntegrationCollection.Name)]
public sealed class BuildIntegrationTests
{
    [Fact]
    public async Task BuildSolution_MxcSourceDependency_BuildsThroughStandardEntryPoint()
    {
        using var output = new MxcRepositoryTestSupport.TestDirectory();
        var baseOutputPath = Path.Combine(
            output.Path, "$(MSBuildProjectName)", "bin") + Path.DirectorySeparatorChar;
        var result = await MxcRepositoryTestSupport.InvokeAsync(
            "dotnet",
            "build",
            "Phantom.Workspaces.slnx",
            "--no-restore",
            "--nologo",
            "/nodeReuse:false",
            $"-p:BaseOutputPath={baseOutputPath}");

        Assert.True(
            result.ExitCode == 0,
            $"Solution build failed.\nSTDOUT:\n{result.StandardOutput}\nSTDERR:\n{result.StandardError}");
        Assert.Contains("Microsoft.Mxc.Sdk", result.StandardOutput, StringComparison.Ordinal);
        Assert.NotEmpty(Directory.EnumerateFiles(
            output.Path, "mxc_ffi.dll", SearchOption.AllDirectories));
        Assert.NotEmpty(Directory.EnumerateFiles(
            output.Path, "plm.exe", SearchOption.AllDirectories));
    }
}

[Collection(MxcIntegrationCollection.Name)]
public sealed class ReleasePackagingTests
{
    [Fact]
    public void ReleaseArtifacts_CurrentMatrix_ContainsOnlyWinX64()
    {
        var applicationProject = MxcRepositoryTestSupport.Read("Phantom.Workspaces", "Phantom.Workspaces.csproj");
        var release = MxcRepositoryTestSupport.Read(".github", "workflows", "release.yml");
        var validation = MxcRepositoryTestSupport.Read(".github", "workflows", "publish-validation.yml");

        Assert.Contains("<RuntimeIdentifiers>win-x64</RuntimeIdentifiers>", applicationProject, StringComparison.Ordinal);
        Assert.DoesNotContain("win-arm64", release, StringComparison.Ordinal);
        Assert.DoesNotContain("win-arm64", validation, StringComparison.Ordinal);
    }
}

[Collection(MxcIntegrationCollection.Name)]
public sealed class MxcNativeUnitTests
{
    [Fact]
    public async Task MxcNativeUnit_WinX64Build_UpstreamTestsPass()
    {
        var publishValidation = MxcRepositoryTestSupport.Read(
            ".github", "workflows", "publish-validation.yml");
        Assert.Contains(
            "cargo test --manifest-path microsoft/mxc/src/Cargo.toml -p mxc_ffi --features dotnetsdk",
            publishValidation,
            StringComparison.Ordinal);

        var result = await MxcRepositoryTestSupport.InvokeAsync(
            "cargo",
            "test",
            "--manifest-path",
            "microsoft/mxc/src/Cargo.toml",
            "-p",
            "mxc_ffi",
            "--features",
            "dotnetsdk");

        Assert.True(
            result.ExitCode == 0,
            $"MXC upstream tests failed.\nSTDOUT:\n{result.StandardOutput}\nSTDERR:\n{result.StandardError}");
    }
}

[Collection(MxcIntegrationCollection.Name)]
public sealed class MxcSdkVersionTests
{
    [Fact]
    public void MxcSdkVersion_ManagedAndNativeUnits_Match()
    {
        var nativeLibrary = MxcRepositoryTestSupport.FindBuiltMxcFile("mxc_ffi.dll");
        var result = MxcRepositoryTestSupport.InvokePowerShell(
            "packaging", "validate", "Assert-MxcSdkVersion.ps1",
            "-NativeLibraryPath", nativeLibrary);

        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.Contains("native runtime version validated", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void MxcSdkVersion_MissingManagedAssembly_Fails()
    {
        using var output = new MxcRepositoryTestSupport.TestDirectory();
        var result = MxcRepositoryTestSupport.InvokePowerShell(
            "packaging", "validate", "Assert-MxcSdkVersion.ps1",
            "-NativeLibraryPath", MxcRepositoryTestSupport.FindBuiltMxcFile("mxc_ffi.dll"),
            "-ManagedOutputPath", output.Path);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Built Microsoft.Mxc.Sdk.dll not found", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void MxcSdkVersion_MissingNativeLibrary_Fails()
    {
        using var output = new MxcRepositoryTestSupport.TestDirectory();
        var result = MxcRepositoryTestSupport.InvokePowerShell(
            "packaging", "validate", "Assert-MxcSdkVersion.ps1",
            "-NativeLibraryPath", Path.Combine(output.Path, "missing.dll"));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("MXC native library not found", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void MxcSdkVersion_ManagedAssemblyVersionMismatch_Fails()
    {
        using var output = new MxcRepositoryTestSupport.TestDirectory();
        File.Copy(
            typeof(MxcSdkVersionTests).Assembly.Location,
            Path.Combine(output.Path, "Microsoft.Mxc.Sdk.dll"));
        var result = MxcRepositoryTestSupport.InvokePowerShell(
            "packaging", "validate", "Assert-MxcSdkVersion.ps1",
            "-NativeLibraryPath", MxcRepositoryTestSupport.FindBuiltMxcFile("mxc_ffi.dll"),
            "-ManagedOutputPath", output.Path);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("does not match project version", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "reported an empty version")]
    [InlineData("99.0.0", "does not match managed SDK version")]
    public void MxcSdkVersion_InvalidNativeVersion_Fails(string nativeVersion, string expectedError)
    {
        using var output = new MxcRepositoryTestSupport.TestDirectory();
        var nativeLibrary = MxcRepositoryTestSupport.BuildNativeVersionLibrary(output.Path, nativeVersion);
        var result = MxcRepositoryTestSupport.InvokePowerShell(
            "packaging", "validate", "Assert-MxcSdkVersion.ps1",
            "-NativeLibraryPath", nativeLibrary);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(expectedError, result.StandardError, StringComparison.Ordinal);
    }
}

[Collection(MxcIntegrationCollection.Name)]
public sealed class MxcRuntimePayloadTests
{
    [Fact]
    public void MxcRuntimePayload_PublishIsolatesBuildOutputsAndDisablesPersistentServers()
    {
        var arguments = MxcRepositoryTestSupport.CreatePublishArguments(
            "payload",
            "isolated-artifacts");

        Assert.Contains("--disable-build-servers", arguments);
        Assert.Contains("/nodeReuse:false", arguments);
        Assert.Contains("-p:UseSharedCompilation=false", arguments);
        Assert.Contains("-p:UseArtifactsOutput=true", arguments);
        Assert.Contains("-p:ArtifactsPath=isolated-artifacts", arguments);
    }

    [Fact]
    public async Task MxcRuntimePayload_RequiredNativeUnit_IsPresent()
    {
        using var payload = new MxcRepositoryTestSupport.TestDirectory();
        using var buildArtifacts = new MxcRepositoryTestSupport.TestDirectory();
        using var sharedOutputLock = MxcRepositoryTestSupport.LockSharedRuntimeConfig();
        var publish = await MxcRepositoryTestSupport.InvokeAsync(
            "dotnet",
            MxcRepositoryTestSupport.CreatePublishArguments(
                payload.Path,
                buildArtifacts.Path));
        Assert.True(
            publish.ExitCode == 0,
            $"Application publish failed.\nSTDOUT:\n{publish.StandardOutput}\nSTDERR:\n{publish.StandardError}");
        Assert.True(publish.DirectProcessInJob);
        Assert.Equal(0U, publish.ActiveJobProcessesAfterCleanup);

        var result = MxcRepositoryTestSupport.InvokePowerShell(
            "packaging", "validate", "Assert-MxcRuntimePayload.ps1",
            "-PayloadDirectory", payload.Path,
            "-RuntimeIdentifier", "win-x64");

        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.Contains("runtime payload validation passed", result.StandardOutput, StringComparison.Ordinal);

        var runtimeConfigs = Directory.GetFiles(
            buildArtifacts.Path,
            "*.runtimeconfig.json",
            SearchOption.AllDirectories);
        Assert.Contains(
            runtimeConfigs,
            path => Path.GetFileName(path).Equals(
                "Phantom.Workspaces.Containers.runtimeconfig.json",
                StringComparison.OrdinalIgnoreCase));
        Assert.All(runtimeConfigs, MxcRepositoryTestSupport.AssertExclusivelyOpenable);
        Assert.All(
            Directory.GetFiles(payload.Path, "*.runtimeconfig.json", SearchOption.TopDirectoryOnly),
            MxcRepositoryTestSupport.AssertExclusivelyOpenable);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task InvokeAsync_ExitedParentWithInheritedPipeWriter_CompletesWithDiagnostics(
        int _)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var descendantReadyName = $"Local\\MxcInvokeDescendantReady-{Guid.NewGuid():N}";
        var descendantReleaseName = $"Local\\MxcInvokeDescendantRelease-{Guid.NewGuid():N}";
        using var descendantReady = new EventWaitHandle(
            false,
            EventResetMode.ManualReset,
            descendantReadyName);
        using var descendantRelease = new EventWaitHandle(
            false,
            EventResetMode.ManualReset,
            descendantReleaseName);
        var probe = Path.Combine(
            AppContext.BaseDirectory,
            "Phantom.Workspaces.Test.WindowsProcessProbe.exe");

        var result = await MxcRepositoryTestSupport.InvokeAsync(
            probe,
            "--exiting-parent",
            descendantReadyName,
            "-",
            descendantReleaseName);

        Assert.Equal(23, result.ExitCode);
        Assert.Contains("parent-stdout", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("parent-stderr", result.StandardError, StringComparison.Ordinal);
        var descendantLine = Assert.Single(
            result.StandardOutput.Split(
                Environment.NewLine,
                StringSplitOptions.RemoveEmptyEntries),
            line => line.StartsWith("EXITING_PARENT_DESCENDANT:", StringComparison.Ordinal));
        var descendantId = int.Parse(
            descendantLine["EXITING_PARENT_DESCENDANT:".Length..],
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.False(MxcRepositoryTestSupport.IsProcessRunning(descendantId));
    }

    [Fact]
    public void MxcRuntimePayload_UnsupportedRid_Fails()
    {
        using var payload = MxcRepositoryTestSupport.CreateMxcPayload();
        var result = MxcRepositoryTestSupport.InvokePowerShell(
            "packaging", "validate", "Assert-MxcRuntimePayload.ps1",
            "-PayloadDirectory", payload.Path,
            "-RuntimeIdentifier", "win-arm64");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("supports only win-x64", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mxc_ffi.dll")]
    [InlineData("plm.exe")]
    [InlineData("MXC-LICENSE.md")]
    public void MxcRuntimePayload_MissingRequiredFile_Fails(string fileName)
    {
        using var payload = MxcRepositoryTestSupport.CreateMxcPayload();
        File.Delete(Path.Combine(payload.Path, "runtimes", "win-x64", "native", fileName));

        var result = MxcRepositoryTestSupport.InvokePowerShell(
            "packaging", "validate", "Assert-MxcRuntimePayload.ps1",
            "-PayloadDirectory", payload.Path,
            "-RuntimeIdentifier", "win-x64");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains($"expected '", result.StandardError, StringComparison.Ordinal);
        Assert.Contains(fileName, result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void MxcRuntimePayload_AlteredLicense_Fails()
    {
        using var payload = MxcRepositoryTestSupport.CreateMxcPayload();
        File.AppendAllText(
            Path.Combine(payload.Path, "runtimes", "win-x64", "native", "MXC-LICENSE.md"),
            $"{Environment.NewLine}altered");

        var result = MxcRepositoryTestSupport.InvokePowerShell(
            "packaging", "validate", "Assert-MxcRuntimePayload.ps1",
            "-PayloadDirectory", payload.Path,
            "-RuntimeIdentifier", "win-x64");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("unmodified upstream MIT license", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void MxcRuntimePayload_RequiredLicenseNotice_IsPresent()
    {
        using var payload = MxcRepositoryTestSupport.CreateMxcPayload();
        File.WriteAllText(
            Path.Combine(payload.Path, "runtimes", "win-x64", "native", "mxc.lic"),
            "not a valid MXC redistribution artifact");

        var result = MxcRepositoryTestSupport.InvokePowerShell(
            "packaging", "validate", "Assert-MxcRuntimePayload.ps1",
            "-PayloadDirectory", payload.Path,
            "-RuntimeIdentifier", "win-x64");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Unexpected mxc.lic", result.StandardError, StringComparison.Ordinal);
    }
}

[Collection(MxcIntegrationCollection.Name)]
public sealed class InstallScriptTests
{
    [Fact]
    public void Install_Arm64Architecture_ReportsUnsupportedArchitecture()
    {
        var script = Path.Combine(MxcRepositoryTestSupport.Root.FullName, "install.ps1");
        var result = MxcRepositoryTestSupport.Invoke(
            "pwsh",
            "-NoProfile",
            "-NonInteractive",
            "-Command",
            $". '{script}' -RuntimeIdentifier win-arm64; try {{ Resolve-RuntimeIdentifier }} catch {{ [Console]::Error.Write($_.Exception.Message); exit 42 }}");

        Assert.Equal(42, result.ExitCode);
        Assert.Contains("Microsoft MXC", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("win-x64", result.StandardError, StringComparison.Ordinal);
    }
}

[Collection(MxcIntegrationCollection.Name)]
public sealed class CopilotWrapperNestedPublishTests
{
    [Fact]
    public async Task NestedPublish_RidConsistentGraphProducesUniqueCompleteWrapperPayload()
    {
        using var payload = new MxcRepositoryTestSupport.TestDirectory();
        using var buildArtifacts = new MxcRepositoryTestSupport.TestDirectory();
        var publish = await MxcRepositoryTestSupport.InvokeAsync(
            "dotnet",
            "msbuild",
            Path.Combine("Phantom.Workspaces", "Phantom.Workspaces.csproj"),
            "-restore",
            "-nologo",
            "-t:PublishCopilotWrapperLoose",
            "/nodeReuse:false",
            "-p:Configuration=Release",
            "-p:RuntimeIdentifier=win-x64",
            "-p:SelfContained=true",
            "-p:PublishReadyToRun=false",
            "-p:UseSharedCompilation=false",
            "-p:UseArtifactsOutput=true",
            $"-p:ArtifactsPath={buildArtifacts.Path}",
            $"-p:PublishDir={payload.Path}{Path.DirectorySeparatorChar}");

        Assert.True(
            publish.ExitCode == 0,
            $"Nested wrapper publish failed.\nSTDOUT:\n{publish.StandardOutput}\nSTDERR:\n{publish.StandardError}");
        Assert.True(publish.DirectProcessInJob);
        Assert.Equal(0U, publish.ActiveJobProcessesAfterCleanup);

        var containerOutputDirectory = Path.Combine(
            buildArtifacts.Path,
            "bin",
            "Phantom.Workspaces.Containers");
        var containerRuntimeConfig = Assert.Single(
            Directory.GetFiles(
                containerOutputDirectory,
                "Phantom.Workspaces.Containers.runtimeconfig.json",
                SearchOption.AllDirectories));
        Assert.Contains(
            $"{Path.DirectorySeparatorChar}release_win-x64{Path.DirectorySeparatorChar}",
            containerRuntimeConfig,
            StringComparison.OrdinalIgnoreCase);

        var wrapperPublishDirectory = Path.Combine(
            buildArtifacts.Path,
            "obj",
            "Phantom.Workspaces",
            "release_win-x64",
            "copilot-wrapper");
        var wrapperRelativePaths = Directory.GetFiles(
                wrapperPublishDirectory,
                "*",
                SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(wrapperPublishDirectory, path))
            .ToArray();
        Assert.Equal(
            wrapperRelativePaths.Length,
            wrapperRelativePaths.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var installedWrapper = Path.Combine(
            payload.Path,
            "runtimes",
            "win-x64",
            "native",
            "phantom-copilot-wrapper.exe");
        Assert.True(new FileInfo(installedWrapper).Length > 0);
        var smoke = MxcRepositoryTestSupport.Invoke(installedWrapper);
        Assert.Equal(64, smoke.ExitCode);
        Assert.Contains("Invalid wrapper arguments.", smoke.StandardError, StringComparison.Ordinal);
    }
}

internal static class MxcRepositoryTestSupport
{
    internal static DirectoryInfo Root { get; } = FindRepositoryRoot();

    internal static string Read(params string[] relativePath)
        => File.ReadAllText(Path.Combine([Root.FullName, .. relativePath]));

    internal static string InvokeGit(params string[] arguments)
    {
        var result = Invoke("git", arguments);
        Assert.True(result.ExitCode == 0, result.StandardError);
        return result.StandardOutput.Trim();
    }

    internal static ProcessResult Invoke(string fileName, params string[] arguments)
    {
        var startInfo = CreateStartInfo(fileName, arguments);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, standardOutput, standardError);
    }

    internal static string[] CreatePublishArguments(string outputPath, string artifactsPath) =>
    [
        "publish",
        Path.Combine("Phantom.Workspaces", "Phantom.Workspaces.csproj"),
        "--nologo",
        "--disable-build-servers",
        "/nodeReuse:false",
        "-r",
        "win-x64",
        "-p:PublishReadyToRun=false",
        "-p:UseSharedCompilation=false",
        "-p:UseArtifactsOutput=true",
        $"-p:ArtifactsPath={artifactsPath}",
        "-o",
        outputPath,
    ];

    internal static async Task<ProcessResult> InvokeAsync(string fileName, params string[] arguments)
    {
        var startInfo = CreateStartInfo(fileName, arguments);
        if (!string.IsNullOrEmpty(startInfo.Arguments))
        {
            throw new InvalidOperationException(
                "Contained asynchronous commands must resolve to an executable, not a command script.");
        }

        var result = await ProcessRunner.RunProcessAsync(new RunProcessParameters(
            Command: startInfo.FileName,
            Arguments: startInfo.ArgumentList.ToArray(),
            KillOnClose: KillOnCloseAction.KillTree,
            WorkingDirectory: startInfo.WorkingDirectory,
            EnvironmentVariables: new Dictionary<string, string>
            {
                ["PATH"] = startInfo.Environment["PATH"] ?? string.Empty,
            }));
        return new ProcessResult(
            result.ExitCode,
            result.StandardOut,
            result.StandardError,
            result.DirectProcessInJob,
            result.ActiveJobProcessesBeforeCleanup,
            result.ActiveJobProcessesAfterCleanup);
    }

    internal static void AssertExclusivelyOpenable(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.True(stream.CanRead, $"Expected build output '{path}' to be exclusively openable.");
    }

    internal static IDisposable LockSharedRuntimeConfig()
    {
        var path = Path.Combine(
            Root.FullName,
            "Phantom.Workspaces.Containers",
            "bin",
            "Release",
            "net10.0",
            "win-x64",
            "Phantom.Workspaces.Containers.runtimeconfig.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new ExclusiveFileLease(path);
    }

    internal static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static ProcessStartInfo CreateStartInfo(string fileName, IEnumerable<string> arguments)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var cargoHome = Environment.GetEnvironmentVariable("CARGO_HOME");
        if (string.IsNullOrWhiteSpace(cargoHome))
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                cargoHome = Path.Combine(userProfile, ".cargo");
            }
        }

        var cargoBin = string.IsNullOrWhiteSpace(cargoHome)
            ? null
            : Path.Combine(cargoHome, "bin");
        if (cargoBin is not null && Directory.Exists(cargoBin))
        {
            path = string.IsNullOrEmpty(path)
                ? cargoBin
                : $"{cargoBin}{Path.PathSeparator}{path}";
        }

        var searchDirectories = path.Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var pathExtensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        var resolved = MxcExecutableResolver.Resolve(
            fileName,
            [.. arguments],
            searchDirectories,
            pathExtensions,
            OperatingSystem.IsWindows(),
            Environment.GetEnvironmentVariable("ComSpec")
                ?? Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            File.Exists);
        var startInfo = new ProcessStartInfo(resolved.FileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Root.FullName,
        };
        if (resolved.ArgumentString is not null)
        {
            startInfo.Arguments = resolved.ArgumentString;
        }
        else
        {
            foreach (var argument in resolved.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }
        startInfo.Environment["PATH"] = path;
        return startInfo;
    }

    internal static ProcessResult InvokePowerShell(params string[] arguments)
    {
        var scriptPath = Path.Combine([Root.FullName, .. arguments.TakeWhile(argument => !argument.StartsWith('-'))]);
        var scriptParts = arguments.TakeWhile(argument => !argument.StartsWith('-')).Count();
        return Invoke(
            "pwsh",
            [
                "-NoProfile",
                "-NonInteractive",
                "-File",
                scriptPath,
                .. arguments.Skip(scriptParts),
            ]);
    }

    internal static string FindBuiltMxcFile(string fileName)
    {
        var outputDirectory = Path.Combine(
            Root.FullName, "microsoft", "mxc", "sdk", "dotnet", "Microsoft.Mxc.Sdk", "bin", "Debug", "net8.0");
        var path = Path.Combine(outputDirectory, fileName);
        Assert.True(File.Exists(path), $"Expected the solution build to produce '{path}'.");
        return path;
    }

    internal static TestDirectory CreateMxcPayload()
    {
        var directory = new TestDirectory();
        var nativeDirectory = Path.Combine(directory.Path, "runtimes", "win-x64", "native");
        Directory.CreateDirectory(nativeDirectory);
        File.Copy(FindBuiltMxcFile("mxc_ffi.dll"), Path.Combine(nativeDirectory, "mxc_ffi.dll"));
        File.Copy(FindBuiltMxcFile("plm.exe"), Path.Combine(nativeDirectory, "plm.exe"));
        File.Copy(
            Path.Combine(Root.FullName, "microsoft", "mxc", "LICENSE.md"),
            Path.Combine(nativeDirectory, "MXC-LICENSE.md"));
        return directory;
    }

    internal static string BuildNativeVersionLibrary(string outputDirectory, string version)
    {
        var sourcePath = Path.Combine(outputDirectory, "version.rs");
        var libraryPath = Path.Combine(outputDirectory, "mxc_version_fixture.dll");
        File.WriteAllText(
            sourcePath,
            $$"""
            use std::ffi::c_char;

            static VERSION: &[u8] = b"{{version}}\0";

            #[unsafe(no_mangle)]
            pub extern "C" fn mxc_version() -> *const c_char {
                VERSION.as_ptr().cast()
            }
            """);
        var result = Invoke(
            "rustc",
            "--crate-type",
            "cdylib",
            "--edition",
            "2024",
            sourcePath,
            "-o",
            libraryPath);
        Assert.True(
            result.ExitCode == 0,
            $"Failed to build native-version fixture.\nSTDOUT:\n{result.StandardOutput}\nSTDERR:\n{result.StandardError}");
        return libraryPath;
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Phantom.Workspaces.slnx")))
            {
                return current;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    internal sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        bool? DirectProcessInJob = null,
        uint? ActiveJobProcessesBeforeCleanup = null,
        uint? ActiveJobProcessesAfterCleanup = null);

    private sealed class ExclusiveFileLease : IDisposable
    {
        private readonly string path;
        private readonly bool deleteOnDispose;
        private readonly FileStream stream;

        internal ExclusiveFileLease(string path)
        {
            this.path = path;
            deleteOnDispose = !File.Exists(path);
            stream = File.Open(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }

        public void Dispose()
        {
            stream.Dispose();
            if (deleteOnDispose)
                File.Delete(path);
        }
    }

    internal sealed class TestDirectory : IDisposable
    {
        internal TestDirectory()
        {
            Path = System.IO.Path.Combine(
                Root.FullName, "TestResults", $"mxc-integration-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
