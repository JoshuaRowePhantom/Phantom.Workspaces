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
    public async Task MxcSdkVersion_ManagedAndNativeUnits_Match()
    {
        var nativeLibrary = MxcRepositoryTestSupport.FindBuiltMxcFile("mxc_ffi.dll");
        var result = await MxcRepositoryTestSupport.InvokePowerShellAsync(
            "packaging", "validate", "Assert-MxcSdkVersion.ps1",
            "-NativeLibraryPath", nativeLibrary);

        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.Contains("native runtime version validated", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MxcSdkVersion_MissingManagedAssembly_Fails()
    {
        using var output = new MxcRepositoryTestSupport.TestDirectory();
        var result = await MxcRepositoryTestSupport.InvokePowerShellAsync(
            "packaging", "validate", "Assert-MxcSdkVersion.ps1",
            "-NativeLibraryPath", MxcRepositoryTestSupport.FindBuiltMxcFile("mxc_ffi.dll"),
            "-ManagedOutputPath", output.Path);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Built Microsoft.Mxc.Sdk.dll not found", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MxcSdkVersion_MissingNativeLibrary_Fails()
    {
        using var output = new MxcRepositoryTestSupport.TestDirectory();
        var result = await MxcRepositoryTestSupport.InvokePowerShellAsync(
            "packaging", "validate", "Assert-MxcSdkVersion.ps1",
            "-NativeLibraryPath", Path.Combine(output.Path, "missing.dll"));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("MXC native library not found", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MxcSdkVersion_ManagedAssemblyVersionMismatch_Fails()
    {
        using var output = new MxcRepositoryTestSupport.TestDirectory();
        File.Copy(
            typeof(MxcSdkVersionTests).Assembly.Location,
            Path.Combine(output.Path, "Microsoft.Mxc.Sdk.dll"));
        var result = await MxcRepositoryTestSupport.InvokePowerShellAsync(
            "packaging", "validate", "Assert-MxcSdkVersion.ps1",
            "-NativeLibraryPath", MxcRepositoryTestSupport.FindBuiltMxcFile("mxc_ffi.dll"),
            "-ManagedOutputPath", output.Path);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("does not match project version", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "reported an empty version")]
    [InlineData("99.0.0", "does not match managed SDK version")]
    public async Task MxcSdkVersion_InvalidNativeVersion_Fails(
        string nativeVersion,
        string expectedError)
    {
        using var output = new MxcRepositoryTestSupport.TestDirectory();
        var nativeLibrary = await MxcRepositoryTestSupport.BuildNativeVersionLibraryAsync(
            output.Path,
            nativeVersion);
        var result = await MxcRepositoryTestSupport.InvokePowerShellAsync(
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

        var result = await MxcRepositoryTestSupport.InvokePowerShellAsync(
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

    [Fact]
    public async Task InvokePowerShellAsync_StalledValidator_TimesOutAndCleansProcessTreeAndArtifacts()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var releaseName = $"Local\\MxcValidatorRelease-{Guid.NewGuid():N}";
        var childReadyName = $"Local\\MxcValidatorChildReady-{Guid.NewGuid():N}";
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, releaseName);
        using var childReady = new EventWaitHandle(false, EventResetMode.ManualReset, childReadyName);
        var time = new ManualTimeoutTimeProvider();
        var observer = new RecordingProcessObserver();
        var validatorReady = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        string payloadPath;
        string artifactsPath;
        int validatorProcessId;
        int childProcessId;

        try
        {
            using var fixture = new MxcRepositoryTestSupport.TestDirectory();
            using var payload = new MxcRepositoryTestSupport.TestDirectory();
            using var artifacts = new MxcRepositoryTestSupport.TestDirectory();
            payloadPath = payload.Path;
            artifactsPath = artifacts.Path;
            var scriptPath = Path.Combine(fixture.Path, "Assert-StalledMxcPayload.ps1");
            File.WriteAllText(
                scriptPath,
                """
                param(
                    [Parameter(Mandatory)][string] $PayloadDirectory,
                    [Parameter(Mandatory)][string] $ArtifactsDirectory,
                    [Parameter(Mandatory)][string] $ProbePath,
                    [Parameter(Mandatory)][string] $ChildReadyEvent,
                    [Parameter(Mandatory)][string] $ReleaseEvent
                )

                Set-Content -LiteralPath (Join-Path $PayloadDirectory 'validator.tmp') -Value 'payload'
                Set-Content -LiteralPath (Join-Path $ArtifactsDirectory 'validator.tmp') -Value 'artifacts'
                $childInfo = [Diagnostics.ProcessStartInfo]::new()
                $childInfo.FileName = $ProbePath
                $childInfo.UseShellExecute = $false
                $childInfo.CreateNoWindow = $true
                $childInfo.ArgumentList.Add('--child-wait-events')
                $childInfo.ArgumentList.Add($ChildReadyEvent)
                $childInfo.ArgumentList.Add($ReleaseEvent)
                $child = [Diagnostics.Process]::Start($childInfo)
                try {
                    $childReady = [Threading.EventWaitHandle]::OpenExisting($ChildReadyEvent)
                    try { $childReady.WaitOne() | Out-Null } finally { $childReady.Dispose() }
                    [Console]::Error.WriteLine('validator-stderr-before-stall')
                    [Console]::Out.WriteLine("VALIDATOR_READY:${PID}:$($child.Id)")
                    [Console]::Out.Flush()
                    $release = [Threading.EventWaitHandle]::OpenExisting($ReleaseEvent)
                    try { $release.WaitOne() | Out-Null } finally { $release.Dispose() }
                }
                finally {
                    $child.Dispose()
                }
                """);

            var operation = MxcRepositoryTestSupport.InvokePowerShellAsync(
                scriptPath,
                new MxcRepositoryTestSupport.InvocationOptions
                {
                    Timeout = TimeSpan.FromDays(1),
                    TimeProvider = time,
                    Observer = observer,
                    StandardOutputObserver = line =>
                    {
                        if (line.StartsWith("VALIDATOR_READY:", StringComparison.Ordinal))
                            validatorReady.TrySetResult(line);
                    },
                },
                "-PayloadDirectory", payload.Path,
                "-ArtifactsDirectory", artifacts.Path,
                "-ProbePath", Path.Combine(
                    AppContext.BaseDirectory,
                    "Phantom.Workspaces.Test.WindowsProcessProbe.exe"),
                "-ChildReadyEvent", childReadyName,
                "-ReleaseEvent", releaseName);

            await time.TimerArmed;
            var readiness = await Task.WhenAny(validatorReady.Task, operation);
            if (readiness == operation)
            {
                var prematureResult = await operation;
                Assert.Fail(
                    "The stalled validator exited before its readiness handshake."
                    + $"\nSTDOUT:\n{prematureResult.StandardOutput}"
                    + $"\nSTDERR:\n{prematureResult.StandardError}");
            }
            var readyLine = await validatorReady.Task;
            var processIds = readyLine.Split(':');
            validatorProcessId = int.Parse(
                processIds[1],
                System.Globalization.CultureInfo.InvariantCulture);
            childProcessId = int.Parse(
                processIds[2],
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(File.Exists(Path.Combine(payload.Path, "validator.tmp")));
            Assert.True(File.Exists(Path.Combine(artifacts.Path, "validator.tmp")));
            time.Expire();

            var exception = await Assert.ThrowsAsync<TimeoutException>(() => operation);
            Assert.Contains("validator-stderr-before-stall", exception.Message, StringComparison.Ordinal);
            Assert.Contains("VALIDATOR_READY:", exception.Message, StringComparison.Ordinal);
            Assert.False(MxcRepositoryTestSupport.IsProcessRunning(validatorProcessId));
            Assert.False(MxcRepositoryTestSupport.IsProcessRunning(childProcessId));
            Assert.Contains(
                observer.Events,
                processEvent => processEvent.Stage == ProcessRunnerWindowsStage.AssignJob
                    && processEvent.Succeeded);
            AssertReleased(observer, ProcessRunnerWindowsResource.Process, 1);
            AssertReleased(observer, ProcessRunnerWindowsResource.Thread, 1);
            AssertReleased(observer, ProcessRunnerWindowsResource.Job, 1);
            AssertReleased(observer, ProcessRunnerWindowsResource.StandardOutputPipe, 2);
            AssertReleased(observer, ProcessRunnerWindowsResource.StandardErrorPipe, 2);
        }
        finally
        {
            release.Set();
        }

        Assert.False(Directory.Exists(payloadPath));
        Assert.False(Directory.Exists(artifactsPath));
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
    public async Task MxcRuntimePayload_UnsupportedRid_Fails()
    {
        using var payload = MxcRepositoryTestSupport.CreateMxcPayload();
        var result = await MxcRepositoryTestSupport.InvokePowerShellAsync(
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
    public async Task MxcRuntimePayload_MissingRequiredFile_Fails(string fileName)
    {
        using var payload = MxcRepositoryTestSupport.CreateMxcPayload();
        File.Delete(Path.Combine(payload.Path, "runtimes", "win-x64", "native", fileName));

        var result = await MxcRepositoryTestSupport.InvokePowerShellAsync(
            "packaging", "validate", "Assert-MxcRuntimePayload.ps1",
            "-PayloadDirectory", payload.Path,
            "-RuntimeIdentifier", "win-x64");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains($"expected '", result.StandardError, StringComparison.Ordinal);
        Assert.Contains(fileName, result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MxcRuntimePayload_AlteredLicense_Fails()
    {
        using var payload = MxcRepositoryTestSupport.CreateMxcPayload();
        File.AppendAllText(
            Path.Combine(payload.Path, "runtimes", "win-x64", "native", "MXC-LICENSE.md"),
            $"{Environment.NewLine}altered");

        var result = await MxcRepositoryTestSupport.InvokePowerShellAsync(
            "packaging", "validate", "Assert-MxcRuntimePayload.ps1",
            "-PayloadDirectory", payload.Path,
            "-RuntimeIdentifier", "win-x64");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("unmodified upstream MIT license", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MxcRuntimePayload_RequiredLicenseNotice_IsPresent()
    {
        using var payload = MxcRepositoryTestSupport.CreateMxcPayload();
        File.WriteAllText(
            Path.Combine(payload.Path, "runtimes", "win-x64", "native", "mxc.lic"),
            "not a valid MXC redistribution artifact");

        var result = await MxcRepositoryTestSupport.InvokePowerShellAsync(
            "packaging", "validate", "Assert-MxcRuntimePayload.ps1",
            "-PayloadDirectory", payload.Path,
            "-RuntimeIdentifier", "win-x64");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Unexpected mxc.lic", result.StandardError, StringComparison.Ordinal);
    }

    private static void AssertReleased(
        RecordingProcessObserver observer,
        ProcessRunnerWindowsResource resource,
        int expectedCount)
    {
        var releases = observer.Events.Where(
            processEvent => processEvent.Stage == ProcessRunnerWindowsStage.ReleaseResource
                && processEvent.Resource == resource).ToArray();
        Assert.Equal(expectedCount, releases.Length);
        Assert.All(releases, processEvent => Assert.True(processEvent.Succeeded));
    }

    private sealed class RecordingProcessObserver : IProcessRunnerWindowsObserver
    {
        private readonly List<ProcessRunnerWindowsEvent> events = [];

        internal IReadOnlyList<ProcessRunnerWindowsEvent> Events
        {
            get
            {
                lock (events)
                    return events.ToArray();
            }
        }

        public void Observe(ProcessRunnerWindowsEvent processEvent)
        {
            lock (events)
                events.Add(processEvent);
        }
    }

    private sealed class ManualTimeoutTimeProvider : TimeProvider
    {
        private readonly TaskCompletionSource timerArmed = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private ManualTimer? timer;

        internal Task TimerArmed => timerArmed.Task;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            timer = new ManualTimer(callback, state);
            timerArmed.TrySetResult();
            return timer;
        }

        internal void Expire() =>
            (timer ?? throw new InvalidOperationException("The timeout timer was not armed.")).Fire();

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period) => !disposed;

            internal void Fire()
            {
                if (!disposed)
                    callback(state);
            }

            public void Dispose() => disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}

[Collection(MxcIntegrationCollection.Name)]
public sealed class InstallScriptTests
{
    [Fact]
    public async Task Install_Arm64Architecture_ReportsUnsupportedArchitecture()
    {
        var script = Path.Combine(MxcRepositoryTestSupport.Root.FullName, "install.ps1");
        var result = await MxcRepositoryTestSupport.InvokeAsync(
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
        var smoke = await MxcRepositoryTestSupport.InvokeAsync(installedWrapper);
        Assert.Equal(64, smoke.ExitCode);
        Assert.Contains("Invalid wrapper arguments.", smoke.StandardError, StringComparison.Ordinal);
    }
}

internal static class MxcRepositoryTestSupport
{
    private static readonly TimeSpan LongRunningProcessTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ValidatorProcessTimeout = TimeSpan.FromSeconds(30);

    internal static DirectoryInfo Root { get; } = FindRepositoryRoot();

    internal static string Read(params string[] relativePath)
        => File.ReadAllText(Path.Combine([Root.FullName, .. relativePath]));

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

    internal static Task<ProcessResult> InvokeAsync(string fileName, params string[] arguments) =>
        InvokeAsync(
            fileName,
            new InvocationOptions { Timeout = LongRunningProcessTimeout },
            arguments);

    internal static async Task<ProcessResult> InvokeAsync(
        string fileName,
        InvocationOptions options,
        params string[] arguments)
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
            Timeout: options.Timeout,
            EnvironmentVariables: new Dictionary<string, string>
            {
                ["PATH"] = startInfo.Environment["PATH"] ?? string.Empty,
            })
        {
            WindowsTestOptions = new()
            {
                TimeProvider = options.TimeProvider,
                Observer = options.Observer,
                StandardOutputObserver = options.StandardOutputObserver,
            },
        }, options.CancellationToken);
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

    internal static Task<ProcessResult> InvokePowerShellAsync(params string[] arguments)
    {
        var scriptPath = Path.Combine([Root.FullName, .. arguments.TakeWhile(argument => !argument.StartsWith('-'))]);
        var scriptParts = arguments.TakeWhile(argument => !argument.StartsWith('-')).Count();
        return InvokePowerShellAsync(
            scriptPath,
            new InvocationOptions { Timeout = ValidatorProcessTimeout },
            [.. arguments.Skip(scriptParts)]);
    }

    internal static Task<ProcessResult> InvokePowerShellAsync(
        string scriptPath,
        InvocationOptions options,
        params string[] arguments) =>
        InvokeAsync(
            "pwsh",
            options,
            [
                "-NoProfile",
                "-NonInteractive",
                "-File",
                scriptPath,
                .. arguments,
            ]);

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

    internal static async Task<string> BuildNativeVersionLibraryAsync(
        string outputDirectory,
        string version)
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
        var result = await InvokeAsync(
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

    internal sealed record InvocationOptions
    {
        internal required TimeSpan Timeout { get; init; }
        internal CancellationToken CancellationToken { get; init; }
        internal TimeProvider TimeProvider { get; init; } = TimeProvider.System;
        internal IProcessRunnerWindowsObserver? Observer { get; init; }
        internal Action<string>? StandardOutputObserver { get; init; }
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
