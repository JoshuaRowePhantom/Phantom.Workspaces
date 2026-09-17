using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

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

    [Fact]
    public void ReleaseVersion_EntryPointsUseApplicationScopedPropertyAndKeepArchiveNames()
    {
        var applicationProject = MxcRepositoryTestSupport.Read(
            "Phantom.Workspaces", "Phantom.Workspaces.csproj");
        var directoryBuildProps = MxcRepositoryTestSupport.Read("Directory.Build.props");
        var release = MxcRepositoryTestSupport.Read(".github", "workflows", "release.yml");
        var validation = MxcRepositoryTestSupport.Read(
            ".github", "workflows", "publish-validation.yml");
        var installScript = MxcRepositoryTestSupport.Read("scripts", "test-install.ps1");

        Assert.Contains(
            "<Version Condition=\"'$(PhantomReleaseVersion)' != ''\">$(PhantomReleaseVersion)</Version>",
            applicationProject,
            StringComparison.Ordinal);
        Assert.DoesNotContain("PhantomReleaseVersion", directoryBuildProps, StringComparison.Ordinal);
        Assert.All(
            new[] { release, validation },
            caller =>
            {
                Assert.Contains("-p:PhantomReleaseVersion=$version", caller, StringComparison.Ordinal);
                Assert.DoesNotContain("-p:Version=", caller, StringComparison.Ordinal);
                Assert.Contains("Assert-PhantomReleaseVersion.ps1", caller, StringComparison.Ordinal);
                Assert.Contains("-ManagedOutputPath $buildArtifacts", caller, StringComparison.Ordinal);
                Assert.Contains(
                    "Remove-Item -LiteralPath $publishDir,$buildArtifacts",
                    caller,
                    StringComparison.Ordinal);
            });
        Assert.Contains(
            "-p:PhantomReleaseVersion=$version",
            installScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain("-p:Version=", installScript, StringComparison.Ordinal);
        Assert.Contains(
            "Phantom.Workspaces-$version-$rid.zip",
            release,
            StringComparison.Ordinal);
        Assert.Contains(
            "Phantom.Workspaces-$version-$rid.zip",
            validation,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReleaseVersion_ValidationSuffixEvaluatesOnlyOnApplicationEntryProject()
    {
        var property = "-p:PhantomReleaseVersion=0.0.0-validation";
        var appResult = await MxcRepositoryTestSupport.InvokeAsync(
            "dotnet",
            "msbuild",
            Path.Combine("Phantom.Workspaces", "Phantom.Workspaces.csproj"),
            "-nologo",
            "-getProperty:Version",
            property);
        var sdkResult = await MxcRepositoryTestSupport.InvokeAsync(
            "dotnet",
            "msbuild",
            Path.Combine(
                "microsoft",
                "mxc",
                "sdk",
                "dotnet",
                "Microsoft.Mxc.Sdk",
                "Microsoft.Mxc.Sdk.csproj"),
            "-nologo",
            "-getProperty:Version",
            property);

        Assert.Equal(0, appResult.ExitCode);
        Assert.Equal("0.0.0-validation", appResult.StandardOutput.Trim());
        Assert.Equal(0, sdkResult.ExitCode);
        Assert.Equal("0.8.0", sdkResult.StandardOutput.Trim());
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
    public void MxcSdkVersion_AppReleaseVersionOverride_LeavesSdkAssemblyVersionUnchanged()
    {
        var prerequisite = MxcRepositoryTestSupport.LoadCopilotWrapperPrerequisite();

        Assert.Equal("0.0.21", prerequisite.Manifest.AppReleaseVersion);
        Assert.Equal(
            prerequisite.Manifest.AppReleaseVersion,
            System.Reflection.AssemblyName
                .GetAssemblyName(prerequisite.AppAssembly)
                .Version!
                .ToString(3));
        Assert.Equal("0.0.21", prerequisite.Manifest.AppAssemblyVersion);
        Assert.Equal("0.0.21", prerequisite.Manifest.AppFileVersion);
        Assert.StartsWith(
            "0.0.21",
            prerequisite.Manifest.AppInformationalVersion,
            StringComparison.Ordinal);
        Assert.Equal("0.0.21", prerequisite.Manifest.AppExecutableFileVersion);
        Assert.StartsWith(
            "0.0.21",
            prerequisite.Manifest.AppExecutableProductVersion,
            StringComparison.Ordinal);
        Assert.Equal(
            "0.8.0",
            System.Reflection.AssemblyName
                .GetAssemblyName(prerequisite.MxcSdkAssembly)
                .Version!
                .ToString(3));
        Assert.Equal("0.8.0", prerequisite.Manifest.MxcSdkProjectVersion);
        Assert.Equal("0.8.0", prerequisite.Manifest.MxcSdkAssemblyVersion);
        Assert.NotEqual(
            prerequisite.Manifest.AppReleaseVersion,
            prerequisite.Manifest.MxcSdkAssemblyVersion);
        Assert.Equal("0.0.1", prerequisite.Manifest.CopilotWrapperAssemblyVersion);
        Assert.Equal("0.0.1", prerequisite.Manifest.LlmCoreAssemblyVersion);
        Assert.Equal(
            "0.0.1",
            System.Reflection.AssemblyName
                .GetAssemblyName(prerequisite.CopilotWrapperAssembly)
                .Version!
                .ToString(3));
        Assert.Equal(
            "0.0.1",
            System.Reflection.AssemblyName
                .GetAssemblyName(prerequisite.LlmCoreAssembly)
                .Version!
                .ToString(3));
        Assert.Equal(
            "Phantom.Workspaces-0.0.21-win-x64.zip",
            prerequisite.Manifest.ReleaseArchiveName);
    }

    [Fact]
    public async Task MxcSdkVersion_ProductPublishWithReleaseVersion_ValidatorPasses()
    {
        var prerequisite = MxcRepositoryTestSupport.LoadCopilotWrapperPrerequisite();
        var appResult = await MxcRepositoryTestSupport.InvokePowerShellAsync(
            "packaging", "validate", "Assert-PhantomReleaseVersion.ps1",
            "-ExpectedVersion", prerequisite.Manifest.AppReleaseVersion,
            "-ApplicationAssemblyPath", prerequisite.AppAssembly,
            "-ApplicationExecutablePath", prerequisite.AppAssembly);
        var mxcResult = await MxcRepositoryTestSupport.InvokePowerShellAsync(
            "packaging", "validate", "Assert-MxcSdkVersion.ps1",
            "-NativeLibraryPath", prerequisite.MxcFfi,
            "-ManagedOutputPath", prerequisite.PreparedDirectory);

        Assert.True(appResult.ExitCode == 0, appResult.StandardError);
        Assert.Contains(
            "application release version validated",
            appResult.StandardOutput,
            StringComparison.Ordinal);
        Assert.True(mxcResult.ExitCode == 0, mxcResult.StandardError);
        Assert.Contains(
            "native runtime version validated",
            mxcResult.StandardOutput,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task MxcSdkVersion_ManagedAndNativeUnits_Match()
    {
        var nativeLibrary = MxcRepositoryTestSupport.FindBuiltMxcFile("mxc_ffi.dll");
        var result = await MxcRepositoryTestSupport.InvokePowerShellAsync(
            "packaging", "validate", "Assert-MxcSdkVersion.ps1",
            "-NativeLibraryPath", nativeLibrary,
            "-ManagedOutputPath", MxcRepositoryTestSupport.BuiltMxcOutputDirectory);

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
            "-NativeLibraryPath", Path.Combine(output.Path, "missing.dll"),
            "-ManagedOutputPath", MxcRepositoryTestSupport.BuiltMxcOutputDirectory);

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

    [Fact]
    public async Task MxcSdkVersion_StaleWrongAssemblyAlongsideCurrent_Fails()
    {
        using var output = new MxcRepositoryTestSupport.TestDirectory();
        var staleDirectory = Path.Combine(output.Path, "stale");
        var currentDirectory = Path.Combine(output.Path, "current");
        Directory.CreateDirectory(staleDirectory);
        Directory.CreateDirectory(currentDirectory);
        var staleAssembly = Path.Combine(staleDirectory, "Microsoft.Mxc.Sdk.dll");
        var currentAssembly = Path.Combine(currentDirectory, "Microsoft.Mxc.Sdk.dll");
        File.Copy(typeof(MxcSdkVersionTests).Assembly.Location, staleAssembly);
        File.Copy(MxcRepositoryTestSupport.FindBuiltMxcFile("Microsoft.Mxc.Sdk.dll"), currentAssembly);
        File.SetLastWriteTimeUtc(staleAssembly, DateTime.UtcNow.AddDays(-1));
        File.SetLastWriteTimeUtc(currentAssembly, DateTime.UtcNow);

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
            "-NativeLibraryPath", nativeLibrary,
            "-ManagedOutputPath", MxcRepositoryTestSupport.BuiltMxcOutputDirectory);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(expectedError, result.StandardError, StringComparison.Ordinal);
    }
}

[Collection(MxcIntegrationCollection.Name)]
public sealed class MxcRuntimePayloadTests
{
    [Fact]
    public void MxcRuntimePayload_CommandConsumesTopLevelNativeBuild()
    {
        var prerequisite = MxcRepositoryTestSupport.LoadCopilotWrapperPrerequisite();
        var arguments = MxcRepositoryTestSupport.CreateMxcRuntimePayloadArguments(
            "payload",
            "isolated-artifacts",
            "empty-normal-output",
            prerequisite);

        Assert.Equal("msbuild", arguments[0]);
        Assert.Contains("--disable-build-servers", arguments);
        Assert.Contains("-m:1", arguments);
        Assert.Contains("/nodeReuse:false", arguments);
        Assert.Contains("-p:UseSharedCompilation=false", arguments);
        Assert.Contains("-p:UseArtifactsOutput=true", arguments);
        Assert.Contains("-p:ArtifactsPath=isolated-artifacts", arguments);
        Assert.Contains("-t:PublishMxcRuntimeLoose", arguments);
        Assert.Contains("-p:NoBuild=true", arguments);
        Assert.Contains("-p:UsePreparedCopilotPayloadForTests=true", arguments);
        Assert.Contains(
            $"-p:PreparedCopilotPayloadDirectory={prerequisite.PreparedDirectory}",
            arguments);
        Assert.DoesNotContain("publish", arguments);
    }

    [Fact]
    public async Task MxcRuntimePayload_RequiredNativeUnit_IsPresent()
    {
        var prerequisite = MxcRepositoryTestSupport.LoadCopilotWrapperPrerequisite();
        var processObserver = new RecordingProcessObserver();
        MxcRepositoryTestSupport.ProcessResult? publish = null;
        MxcRepositoryTestSupport.ProcessResult? validation = null;
        var resourcesReleasedBeforeCleanup = true;
        void ObserveResourcesReleased()
        {
            resourcesReleasedBeforeCleanup &=
                publish?.ActiveJobProcessesAfterCleanup == 0
                && validation?.ActiveJobProcessesAfterCleanup == 0
                && ReleasedCount(processObserver, ProcessRunnerWindowsResource.Process) == 2
                && ReleasedCount(processObserver, ProcessRunnerWindowsResource.Thread) == 2
                && ReleasedCount(processObserver, ProcessRunnerWindowsResource.Job) == 2
                && ReleasedCount(
                    processObserver,
                    ProcessRunnerWindowsResource.StandardOutputPipe) == 4
                && ReleasedCount(
                    processObserver,
                    ProcessRunnerWindowsResource.StandardErrorPipe) == 4;
        }

        await using var payload = new MxcRepositoryTestSupport.TestDirectory(
            ObserveResourcesReleased,
            message => Console.WriteLine($"MXC payload {message}"));
        await using var buildArtifacts =
            new MxcRepositoryTestSupport.TestDirectory(
                ObserveResourcesReleased,
                message => Console.WriteLine($"MXC build artifacts {message}"));
        await using var emptyNormalOutput =
            new MxcRepositoryTestSupport.TestDirectory(
                cleanupProgress: message => Console.WriteLine($"Empty normal output {message}"));
        publish = await MxcRepositoryTestSupport.InvokeAsync(
            "dotnet",
            new MxcRepositoryTestSupport.InvocationOptions
            {
                Timeout = TimeSpan.FromMinutes(10),
                Observer = processObserver,
            },
            MxcRepositoryTestSupport.CreateMxcRuntimePayloadArguments(
                payload.Path,
                buildArtifacts.Path,
                emptyNormalOutput.Path,
                prerequisite));
        Assert.True(
            publish.ExitCode == 0,
            $"Application publish failed.\nSTDOUT:\n{publish.StandardOutput}\nSTDERR:\n{publish.StandardError}");
        Assert.True(publish.DirectProcessInJob);
        Assert.Equal(0U, publish.ActiveJobProcessesAfterCleanup);

        validation = await MxcRepositoryTestSupport.InvokePowerShellAsync(
            Path.Combine(
                MxcRepositoryTestSupport.Root.FullName,
                "packaging",
                "validate",
                "Assert-MxcRuntimePayload.ps1"),
            new MxcRepositoryTestSupport.InvocationOptions
            {
                Timeout = TimeSpan.FromSeconds(30),
                Observer = processObserver,
            },
            "-PayloadDirectory", payload.Path,
            "-RuntimeIdentifier", "win-x64",
            "-ManagedOutputPath", prerequisite.PreparedDirectory);

        Assert.True(validation.ExitCode == 0, validation.StandardError);
        Assert.Contains(
            "runtime payload validation passed",
            validation.StandardOutput,
            StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(
            payload.Path,
            "runtimes",
            "win-x64",
            "native",
            "copilot.exe")));
        Assert.False(File.Exists(Path.Combine(
            payload.Path,
            "runtimes",
            "win-x64",
            "native",
            "phantom-copilot-wrapper.exe")));
        var nativeDirectory = Path.Combine(payload.Path, "runtimes", "win-x64", "native");
        Assert.Equal(
            MxcRepositoryTestSupport.ComputeSha256(
                prerequisite.MxcFfi),
            MxcRepositoryTestSupport.ComputeSha256(Path.Combine(nativeDirectory, "mxc_ffi.dll")));
        Assert.Equal(
            MxcRepositoryTestSupport.ComputeSha256(
                prerequisite.Plm),
            MxcRepositoryTestSupport.ComputeSha256(Path.Combine(nativeDirectory, "plm.exe")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(emptyNormalOutput.Path));

        await emptyNormalOutput.DisposeAsync();
        await buildArtifacts.DisposeAsync();
        await payload.DisposeAsync();
        Assert.True(
            resourcesReleasedBeforeCleanup,
            "Contained process, job, thread, or output-pipe resources remained at cleanup.");
    }

    [Fact]
    public async Task TestDirectory_CleanupProofWithOpenFile_RefusesPartialDeletion()
    {
        string? lockedPath = null;
        var directory = new MxcRepositoryTestSupport.TestDirectory(
            beforeCleanup: () =>
                MxcRepositoryTestSupport.AssertExclusivelyOpenable(
                    Assert.IsType<string>(lockedPath)));
        var firstPath = Path.Combine(directory.Path, "first.txt");
        lockedPath = Path.Combine(directory.Path, "locked.txt");
        File.WriteAllText(firstPath, "first");
        File.WriteAllText(lockedPath, "locked");

        using (File.Open(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => directory.DisposeAsync().AsTask());
            Assert.True(File.Exists(firstPath));
            Assert.True(File.Exists(lockedPath));
        }

        await directory.DisposeAsync();
        Assert.False(Directory.Exists(directory.Path));
    }

    [Fact]
    public async Task TestDirectory_CleanupProof_PrecedesEnumerationAndDeletion()
    {
        var proofRan = false;
        var cleanupStarted = false;
        var directory = new MxcRepositoryTestSupport.TestDirectory(
            beforeCleanup: () =>
            {
                Assert.False(cleanupStarted);
                proofRan = true;
            },
            cleanupProgress: _ =>
            {
                Assert.True(proofRan);
                cleanupStarted = true;
            });
        File.WriteAllText(Path.Combine(directory.Path, "content.txt"), "content");

        await directory.DisposeAsync();

        Assert.True(cleanupStarted);
        Assert.False(Directory.Exists(directory.Path));
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

        var launchId = Guid.NewGuid().ToString("N");
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
            launchId,
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
            line => line.StartsWith("EXITING_PARENT_READY:", StringComparison.Ordinal));
        var processIds = descendantLine.Split(':');
        Assert.Equal(launchId, processIds[1]);
        var parentId = int.Parse(
            processIds[2],
            System.Globalization.CultureInfo.InvariantCulture);
        var descendantId = int.Parse(
            processIds[3],
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.NotEqual(parentId, descendantId);
        Assert.False(MxcRepositoryTestSupport.IsProcessRunning(parentId));
        Assert.False(MxcRepositoryTestSupport.IsProcessRunning(descendantId));
    }

    [Fact]
    public async Task MxcRuntimePayload_UnsupportedRid_Fails()
    {
        using var payload = MxcRepositoryTestSupport.CreateMxcPayload();
        var result = await MxcRepositoryTestSupport.InvokePowerShellAsync(
            "packaging", "validate", "Assert-MxcRuntimePayload.ps1",
            "-PayloadDirectory", payload.Path,
            "-RuntimeIdentifier", "win-arm64",
            "-ManagedOutputPath", MxcRepositoryTestSupport.BuiltMxcOutputDirectory);

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
            "-RuntimeIdentifier", "win-x64",
            "-ManagedOutputPath", MxcRepositoryTestSupport.BuiltMxcOutputDirectory);

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
            "-RuntimeIdentifier", "win-x64",
            "-ManagedOutputPath", MxcRepositoryTestSupport.BuiltMxcOutputDirectory);

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
            "-RuntimeIdentifier", "win-x64",
            "-ManagedOutputPath", MxcRepositoryTestSupport.BuiltMxcOutputDirectory);

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

    private static int ReleasedCount(
        RecordingProcessObserver observer,
        ProcessRunnerWindowsResource resource) =>
        observer.Events.Count(
            processEvent => processEvent.Stage == ProcessRunnerWindowsStage.ReleaseResource
                && processEvent.Resource == resource
                && processEvent.Succeeded);

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
    public void NestedPublish_CommandRequiresPreparedReleaseWrapper()
    {
        var prerequisite = MxcRepositoryTestSupport.LoadCopilotWrapperPrerequisite();
        var arguments = MxcRepositoryTestSupport.CreateCopilotWrapperPublishArguments(
            "payload",
            "isolated-artifacts",
            prerequisite);

        Assert.Contains("--disable-build-servers", arguments);
        Assert.Contains("-m:1", arguments);
        Assert.Contains("/nodeReuse:false", arguments);
        Assert.Contains("-p:UseSharedCompilation=false", arguments);
        Assert.Contains(
            $"-p:PreparedCopilotPayloadDirectory={prerequisite.PreparedDirectory}",
            arguments);
        Assert.Contains("-p:PreparedCopilotPayloadConfiguration=Release", arguments);
        Assert.Contains("-p:PreparedCopilotPayloadRuntimeIdentifier=win-x64", arguments);
        Assert.Contains("-p:UsePreparedCopilotPayloadForTests=true", arguments);
        Assert.Contains("-p:NoBuild=true", arguments);
        Assert.DoesNotContain("-restore", arguments);
    }

    [Fact]
    public void PreparedWrapper_SourceToolchainAndPayloadProvenanceAreCurrent()
    {
        var prerequisite = MxcRepositoryTestSupport.LoadCopilotWrapperPrerequisite();

        Assert.Equal(5, prerequisite.Manifest.SchemaVersion);
        Assert.True(prerequisite.Manifest.ProductionPublishValidated);
        Assert.Equal("0.0.21", prerequisite.Manifest.AppReleaseVersion);
        Assert.Equal("0.0.21", prerequisite.Manifest.AppAssemblyVersion);
        Assert.Equal("0.8.0", prerequisite.Manifest.MxcSdkAssemblyVersion);
        Assert.Equal(
            "runtimes/win-x64/native/phantom-copilot-wrapper.exe",
            prerequisite.Manifest.ProductionPublishWrapperPath);
        Assert.Matches("^[0-9a-f]{64}$", prerequisite.Manifest.SourceFingerprint);
        Assert.Equal("Release", prerequisite.Manifest.Configuration);
        Assert.Equal("win-x64", prerequisite.Manifest.RuntimeIdentifier);
        Assert.Equal("x86_64-pc-windows-msvc", prerequisite.Manifest.NativeTarget);
        Assert.Equal("release", prerequisite.Manifest.NativeProfile);
        Assert.Equal(["mxc_ffi", "plm"], prerequisite.Manifest.NativePackages);
        Assert.Equal(["dotnetsdk"], prerequisite.Manifest.NativeFeatures);
        Assert.Equal("1.0.13", prerequisite.Manifest.CopilotSdkPackageVersion);
        Assert.False(string.IsNullOrWhiteSpace(
            prerequisite.Manifest.CopilotSdkPackageSha512));
        Assert.Equal("1.0.83", prerequisite.Manifest.CopilotCliVersion);
        Assert.Equal("win32-x64", prerequisite.Manifest.CopilotCliPlatform);
        Assert.EndsWith(
            "/v1.0.83/github-copilot-1.0.83-win32-x64.tgz",
            prerequisite.Manifest.CopilotCliDownloadUrl,
            StringComparison.Ordinal);
        Assert.EndsWith(
            "/v1.0.83/SHA256SUMS.txt",
            prerequisite.Manifest.CopilotCliChecksumsUrl,
            StringComparison.Ordinal);
        Assert.Matches(
            "^[0-9a-f]{64}$",
            prerequisite.Manifest.CopilotCliArchiveSha256);
        Assert.Matches(
            "^[0-9a-f]{64}$",
            prerequisite.Manifest.CopilotCliChecksumsSha256);
        Assert.StartsWith(
            Path.GetFileName(prerequisite.CacheDirectory),
            prerequisite.Manifest.CacheKey,
            StringComparison.Ordinal);
        prerequisite.AssertArtifactHashes();

        Assert.Contains(
            "/release_win-x64/",
            prerequisite.Manifest.ContainerRuntimeConfigGraphPath,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "/Phantom.Workspaces.Copilot.Cli.Wrapper/release_win-x64/",
            prerequisite.Manifest.CopilotCliGraphPath,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NestedPublish_ColdNativeBuildIsPreparedBeforeBlameMonitorsTestHost()
    {
        var testProject = MxcRepositoryTestSupport.Read(
            "Phantom.Workspaces.Install.Tests",
            "Phantom.Workspaces.Install.Tests.csproj");
        var preparationScript = MxcRepositoryTestSupport.Read(
            "scripts",
            "prepare-copilot-wrapper-test-prerequisite.ps1");

        Assert.Contains("BeforeTargets=\"VSTest\"", testProject, StringComparison.Ordinal);
        Assert.Contains(
            "prepare-copilot-wrapper-test-prerequisite.ps1",
            testProject,
            StringComparison.Ordinal);
        Assert.Contains("System.Threading.Mutex", preparationScript, StringComparison.Ordinal);
        Assert.Contains("Directory]::Move", preparationScript, StringComparison.Ordinal);
        Assert.Contains("cargo build", preparationScript, StringComparison.Ordinal);
        Assert.DoesNotContain("Start-Sleep", preparationScript, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NestedPublish_RidConsistentGraphProducesUniqueCompleteWrapperPayload()
    {
        var prerequisite = MxcRepositoryTestSupport.LoadCopilotWrapperPrerequisite();
        await using var payload = new MxcRepositoryTestSupport.TestDirectory(
            cleanupProgress: message => Console.WriteLine($"wrapper payload {message}"));
        await using var buildArtifacts = new MxcRepositoryTestSupport.TestDirectory(
            cleanupProgress: message => Console.WriteLine($"wrapper build artifacts {message}"));
        var publish = await MxcRepositoryTestSupport.InvokeAsync(
            "dotnet",
            MxcRepositoryTestSupport.CreateCopilotWrapperPublishArguments(
                payload.Path,
                buildArtifacts.Path,
                prerequisite));

        Assert.True(
            publish.ExitCode == 0,
            $"Nested wrapper publish failed.\nSTDOUT:\n{publish.StandardOutput}\nSTDERR:\n{publish.StandardError}");
        Assert.True(publish.DirectProcessInJob);
        Assert.Equal(0U, publish.ActiveJobProcessesAfterCleanup);

        var wrapperPublishDirectory = Directory.GetDirectories(
                buildArtifacts.Path,
                "cw",
                SearchOption.AllDirectories)
            .SelectMany(directory => Directory.GetDirectories(
                directory,
                "*",
                SearchOption.AllDirectories))
            .Single(directory => File.Exists(Path.Combine(
                directory,
                "phantom-copilot-wrapper.exe")));
        var wrapperRelativePaths = Directory.GetFiles(
                wrapperPublishDirectory,
                "*",
                SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(wrapperPublishDirectory, path))
            .ToArray();
        Assert.Contains("phantom-copilot-wrapper.exe", wrapperRelativePaths);
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
        Assert.Equal(
            MxcRepositoryTestSupport.ComputeSha256(prerequisite.WrapperExecutable),
            MxcRepositoryTestSupport.ComputeSha256(installedWrapper));
        var smoke = await MxcRepositoryTestSupport.InvokeAsync(installedWrapper);
        Assert.Equal(64, smoke.ExitCode);
        Assert.Contains("Invalid wrapper arguments.", smoke.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NestedPublish_CustomIntermediateWithSpaces_IsCanonicalAndInvocationUnique()
    {
        var prerequisite = MxcRepositoryTestSupport.LoadCopilotWrapperPrerequisite();
        await using var intermediateOwner = new MxcRepositoryTestSupport.TestDirectory();
        await using var firstPayload = new MxcRepositoryTestSupport.TestDirectory();
        await using var firstArtifacts = new MxcRepositoryTestSupport.TestDirectory();
        await using var secondPayload = new MxcRepositoryTestSupport.TestDirectory();
        await using var secondArtifacts = new MxcRepositoryTestSupport.TestDirectory();
        var customIntermediate = Path.Combine(
            intermediateOwner.Path,
            "unused",
            "..",
            "custom intermediate with spaces");
        var canonicalIntermediate = Path.GetFullPath(customIntermediate);

        var results = await Task.WhenAll(
            MxcRepositoryTestSupport.InvokeAsync(
                "dotnet",
                MxcRepositoryTestSupport.CreateCopilotWrapperPublishArguments(
                    firstPayload.Path,
                    firstArtifacts.Path,
                    prerequisite,
                    intermediateOutputPath: customIntermediate)),
            MxcRepositoryTestSupport.InvokeAsync(
                "dotnet",
                MxcRepositoryTestSupport.CreateCopilotWrapperPublishArguments(
                    secondPayload.Path,
                    secondArtifacts.Path,
                    prerequisite,
                    intermediateOutputPath: customIntermediate)));

        Assert.All(results, result => Assert.Equal(0, result.ExitCode));
        var resolvedDirectories = results
            .Select(result => MxcRepositoryTestSupport.ExtractWrapperPublishDirectory(
                result.StandardOutput))
            .ToArray();
        Assert.Equal(2, resolvedDirectories.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(resolvedDirectories, directory =>
        {
            Assert.True(Path.IsPathFullyQualified(directory));
            Assert.StartsWith(
                $"cw{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}win-x64{Path.DirectorySeparatorChar}",
                Path.GetRelativePath(canonicalIntermediate, directory),
                StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(directory, "phantom-copilot-wrapper.exe")));
        });
    }

    [Fact]
    public async Task NestedPublish_CustomBaseIntermediateWithSpaces_StaysUnderCanonicalBase()
    {
        var prerequisite = MxcRepositoryTestSupport.LoadCopilotWrapperPrerequisite();
        await using var intermediateOwner = new MxcRepositoryTestSupport.TestDirectory();
        await using var payload = new MxcRepositoryTestSupport.TestDirectory();
        await using var artifacts = new MxcRepositoryTestSupport.TestDirectory();
        var customBase = Path.Combine(intermediateOwner.Path, "base intermediate with spaces");
        var publish = await MxcRepositoryTestSupport.InvokeAsync(
            "dotnet",
            MxcRepositoryTestSupport.CreateCopilotWrapperPublishArguments(
                payload.Path,
                artifacts.Path,
                prerequisite,
                baseIntermediateOutputPath: customBase));

        Assert.Equal(0, publish.ExitCode);
        var resolved = MxcRepositoryTestSupport.ExtractWrapperPublishDirectory(
            publish.StandardOutput);
        Assert.StartsWith(
            Path.GetFullPath(customBase) + Path.DirectorySeparatorChar,
            resolved,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(resolved, "phantom-copilot-wrapper.exe")));
    }

    [Fact]
    public async Task NestedPublish_ReparseIntermediate_FailsBeforeWritingChildOutput()
    {
        var prerequisite = MxcRepositoryTestSupport.LoadCopilotWrapperPrerequisite();
        await using var intermediateOwner = new MxcRepositoryTestSupport.TestDirectory();
        await using var payload = new MxcRepositoryTestSupport.TestDirectory();
        await using var artifacts = new MxcRepositoryTestSupport.TestDirectory();
        var junctionTarget = Path.Combine(intermediateOwner.Path, "target");
        var junction = Path.Combine(intermediateOwner.Path, "junction");
        Directory.CreateDirectory(junctionTarget);
        var junctionCreation = await MxcRepositoryTestSupport.InvokeAsync(
            "cmd.exe",
            "/d",
            "/c",
            "mklink",
            "/J",
            junction,
            junctionTarget);
        Assert.Equal(0, junctionCreation.ExitCode);
        try
        {
            var publish = await MxcRepositoryTestSupport.InvokeAsync(
                "dotnet",
                MxcRepositoryTestSupport.CreateCopilotWrapperPublishArguments(
                    payload.Path,
                    artifacts.Path,
                    prerequisite,
                    intermediateOutputPath: junction));

            Assert.NotEqual(0, publish.ExitCode);
            Assert.Contains(
                "cannot traverse reparse point",
                publish.StandardOutput + publish.StandardError,
                StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(
                payload.Path,
                "runtimes",
                "win-x64",
                "native",
                "phantom-copilot-wrapper.exe")));
        }
        finally
        {
            Directory.Delete(junction);
        }
    }

    [Fact]
    public async Task NestedPublish_MissingChildOutput_FailsClosed()
    {
        var prerequisite = MxcRepositoryTestSupport.LoadCopilotWrapperPrerequisite();
        await using var payload = new MxcRepositoryTestSupport.TestDirectory();
        await using var artifacts = new MxcRepositoryTestSupport.TestDirectory();
        var result = await MxcRepositoryTestSupport.InvokeAsync(
            "dotnet",
            MxcRepositoryTestSupport.CreateCopilotWrapperPublishArguments(
                payload.Path,
                artifacts.Path,
                prerequisite,
                simulateMissingChildOutput: true));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "Copilot MXC wrapper was not published",
            result.StandardOutput + result.StandardError,
            StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(
            payload.Path,
            "runtimes",
            "win-x64",
            "native",
            "phantom-copilot-wrapper.exe")));
    }

    [Fact]
    public async Task NestedPublish_SharedPreparedWrapperProducesConcurrentIsolatedPayloads()
    {
        var prerequisite = MxcRepositoryTestSupport.LoadCopilotWrapperPrerequisite();
        await using var firstPayload = new MxcRepositoryTestSupport.TestDirectory();
        await using var firstArtifacts = new MxcRepositoryTestSupport.TestDirectory();
        await using var secondPayload = new MxcRepositoryTestSupport.TestDirectory();
        await using var secondArtifacts = new MxcRepositoryTestSupport.TestDirectory();

        var results = await Task.WhenAll(
            MxcRepositoryTestSupport.InvokeAsync(
                "dotnet",
                MxcRepositoryTestSupport.CreateCopilotWrapperPublishArguments(
                    firstPayload.Path,
                    firstArtifacts.Path,
                    prerequisite)),
            MxcRepositoryTestSupport.InvokeAsync(
                "dotnet",
                MxcRepositoryTestSupport.CreateCopilotWrapperPublishArguments(
                    secondPayload.Path,
                    secondArtifacts.Path,
                    prerequisite)));

        Assert.All(results, result =>
        {
            Assert.Equal(0, result.ExitCode);
            Assert.True(result.DirectProcessInJob);
            Assert.Equal(0U, result.ActiveJobProcessesAfterCleanup);
        });

        var relativeWrapperPath = Path.Combine(
            "runtimes",
            "win-x64",
            "native",
            "phantom-copilot-wrapper.exe");
        var firstWrapper = Path.Combine(firstPayload.Path, relativeWrapperPath);
        var secondWrapper = Path.Combine(secondPayload.Path, relativeWrapperPath);
        Assert.NotEqual(firstWrapper, secondWrapper);
        Assert.Equal(
            MxcRepositoryTestSupport.ComputeSha256(prerequisite.WrapperExecutable),
            MxcRepositoryTestSupport.ComputeSha256(firstWrapper));
        Assert.Equal(
            MxcRepositoryTestSupport.ComputeSha256(prerequisite.WrapperExecutable),
            MxcRepositoryTestSupport.ComputeSha256(secondWrapper));
    }
}

internal static class MxcRepositoryTestSupport
{
    private static readonly TimeSpan LongRunningProcessTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ValidatorProcessTimeout = TimeSpan.FromSeconds(30);

    internal static DirectoryInfo Root { get; } = FindRepositoryRoot();
    internal static string BuiltMxcOutputDirectory => Path.Combine(
        Root.FullName,
        "microsoft",
        "mxc",
        "sdk",
        "dotnet",
        "Microsoft.Mxc.Sdk",
        "bin",
        "Debug",
        "net8.0");

    internal static string Read(params string[] relativePath)
        => File.ReadAllText(Path.Combine([Root.FullName, .. relativePath]));

    internal static string[] CreateCopilotWrapperPublishArguments(
        string outputPath,
        string artifactsPath,
        CopilotWrapperPrerequisite prerequisite,
        string? intermediateOutputPath = null,
        string? baseIntermediateOutputPath = null,
        bool simulateMissingChildOutput = false) =>
    [
        "msbuild",
        "--disable-build-servers",
        Path.Combine("Phantom.Workspaces", "Phantom.Workspaces.csproj"),
        "-nologo",
        "-m:1",
        "-t:PublishCopilotWrapperLoose",
        "/nodeReuse:false",
        "-p:Configuration=Release",
        "-p:RuntimeIdentifier=win-x64",
        "-p:SelfContained=true",
        "-p:PublishReadyToRun=false",
        "-p:UseSharedCompilation=false",
        "-p:UseArtifactsOutput=true",
        $"-p:ArtifactsPath={artifactsPath}",
        .. intermediateOutputPath is null
            ? Array.Empty<string>()
            : new[] { $"-p:IntermediateOutputPath={intermediateOutputPath}{Path.DirectorySeparatorChar}" },
        .. baseIntermediateOutputPath is null
            ? Array.Empty<string>()
            : new[] { $"-p:BaseIntermediateOutputPath={baseIntermediateOutputPath}{Path.DirectorySeparatorChar}" },
        $"-p:PublishDir={outputPath}{Path.DirectorySeparatorChar}",
        .. simulateMissingChildOutput
            ? new[] { "-p:SimulateMissingCopilotWrapperChildOutputForTests=true" }
            : Array.Empty<string>(),
        .. CreatePreparedCopilotPayloadProperties(prerequisite),
    ];

    internal static string ExtractWrapperPublishDirectory(string output)
    {
        const string prefix = "Copilot wrapper child PublishDir: ";
        var line = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.Contains(prefix, StringComparison.Ordinal));
        return line[(line.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length)..].Trim();
    }

    internal static string[] CreateCopilotRuntimePayloadArguments(
        string outputPath,
        string artifactsPath,
        string normalOutputPath,
        CopilotWrapperPrerequisite prerequisite,
        string runtimeIdentifier = "win-x64",
        string? preparedDirectory = null,
        string? preparedFingerprint = null,
        bool usePreparedPayload = true,
        bool noBuild = true,
        string? preparedCopilotCliSha256 = null) =>
    [
        "msbuild",
        "--disable-build-servers",
        Path.Combine("Phantom.Workspaces", "Phantom.Workspaces.csproj"),
        "-nologo",
        "-m:1",
        "/nodeReuse:false",
        "-t:PublishCopilotRuntimeLoose;PublishMxcRuntimeLoose",
        $"-p:Configuration={prerequisite.Manifest.Configuration}",
        $"-p:RuntimeIdentifier={runtimeIdentifier}",
        "-p:UseSharedCompilation=false",
        "-p:UseArtifactsOutput=true",
        $"-p:ArtifactsPath={artifactsPath}",
        $"-p:OutDir={normalOutputPath}{Path.DirectorySeparatorChar}",
        $"-p:PublishDir={outputPath}{Path.DirectorySeparatorChar}",
        .. CreatePreparedCopilotPayloadProperties(
            prerequisite,
            preparedDirectory: preparedDirectory,
            preparedFingerprint: preparedFingerprint,
            usePreparedPayload: usePreparedPayload,
            noBuild: noBuild,
            preparedCopilotCliSha256: preparedCopilotCliSha256),
    ];

    internal static string[] CreateMxcRuntimePayloadArguments(
        string outputPath,
        string artifactsPath,
        string normalOutputPath,
        CopilotWrapperPrerequisite prerequisite) =>
    [
        "msbuild",
        "--disable-build-servers",
        Path.Combine("Phantom.Workspaces", "Phantom.Workspaces.csproj"),
        "-nologo",
        "-m:1",
        "/nodeReuse:false",
        "-t:PublishMxcRuntimeLoose",
        $"-p:Configuration={prerequisite.Manifest.Configuration}",
        "-p:RuntimeIdentifier=win-x64",
        "-p:UseSharedCompilation=false",
        "-p:UseArtifactsOutput=true",
        $"-p:ArtifactsPath={artifactsPath}",
        $"-p:OutDir={normalOutputPath}{Path.DirectorySeparatorChar}",
        $"-p:PublishDir={outputPath}{Path.DirectorySeparatorChar}",
        .. CreatePreparedCopilotPayloadProperties(prerequisite),
    ];

    private static string[] CreatePreparedCopilotPayloadProperties(
        CopilotWrapperPrerequisite prerequisite,
        string? preparedDirectory = null,
        string? preparedFingerprint = null,
        bool usePreparedPayload = true,
        bool noBuild = true,
        string? preparedCopilotCliSha256 = null) =>
    [
        $"-p:NoBuild={noBuild.ToString().ToLowerInvariant()}",
        $"-p:UsePreparedCopilotPayloadForTests={usePreparedPayload.ToString().ToLowerInvariant()}",
        $"-p:PreparedCopilotPayloadDirectory={preparedDirectory ?? prerequisite.PreparedDirectory}",
        $"-p:PreparedCopilotPayloadFingerprint={preparedFingerprint ?? prerequisite.Manifest.CacheKey}",
        $"-p:PreparedCopilotPayloadConfiguration={prerequisite.Manifest.Configuration}",
        $"-p:PreparedCopilotPayloadRuntimeIdentifier={prerequisite.Manifest.RuntimeIdentifier}",
        $"-p:PreparedCopilotPayloadManifestSha256={ComputeSha256(prerequisite.ManifestFile)}",
        $"-p:PreparedCopilotWrapperSha256={prerequisite.ArtifactSha256("prepared/phantom-copilot-wrapper.exe")}",
        $"-p:PreparedCopilotCliSha256={preparedCopilotCliSha256 ?? prerequisite.ArtifactSha256("prepared/copilot.exe")}",
        $"-p:PreparedCopilotRuntimeSha256={prerequisite.ArtifactSha256("prepared/copilot_runtime.dll")}",
        $"-p:PreparedCopilotLicenseSha256={prerequisite.ArtifactSha256("prepared/LICENSE.md")}",
        $"-p:PreparedMxcFfiSha256={prerequisite.ArtifactSha256("prepared/mxc_ffi.dll")}",
        $"-p:PreparedPlmSha256={prerequisite.ArtifactSha256("prepared/plm.exe")}",
        $"-p:PreparedMxcLicenseSha256={prerequisite.ArtifactSha256("prepared/MXC-LICENSE.md")}",
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

    internal static CopilotWrapperPrerequisite LoadCopilotWrapperPrerequisite()
    {
        var pathFile = Path.Combine(
            AppContext.BaseDirectory,
            "copilot-wrapper-prerequisite.path");
        Assert.True(
            File.Exists(pathFile),
            $"The pre-VSTest wrapper prerequisite path file is missing: '{pathFile}'.");
        var cacheDirectory = File.ReadAllText(pathFile).Trim();
        var manifestPath = Path.Combine(cacheDirectory, "prerequisite.json");
        Assert.True(
            File.Exists(manifestPath),
            $"The prepared wrapper prerequisite manifest is missing: '{manifestPath}'.");
        var manifest = JsonSerializer.Deserialize<CopilotWrapperPrerequisiteManifest>(
            File.ReadAllText(manifestPath));
        Assert.NotNull(manifest);
        Assert.Equal(5, manifest.SchemaVersion);
        Assert.Matches("^[0-9a-f]{64}$", manifest.CacheKey);
        Assert.Matches("^[0-9a-f]{64}$", manifest.SourceFingerprint);
        var expectedCacheRoot = Path.GetFullPath(Path.Combine(
            Root.FullName,
            "Phantom.Workspaces.Install.Tests",
            "obj",
            "mxcw"));
        Assert.Equal(
            expectedCacheRoot,
            Directory.GetParent(cacheDirectory)!.FullName,
            ignoreCase: true);
        Assert.Equal(
            manifest.CacheKey[..16],
            Path.GetFileName(Path.TrimEndingDirectorySeparator(cacheDirectory)));
        var fingerprintFile = Path.Combine(cacheDirectory, "prerequisite.fingerprint");
        Assert.True(
            File.Exists(fingerprintFile),
            $"The prepared wrapper prerequisite fingerprint is missing: '{fingerprintFile}'.");
        Assert.Equal(manifest.CacheKey, File.ReadAllText(fingerprintFile).Trim());

        string ArtifactPath(string relativePath)
        {
            var artifact = Assert.Single(
                manifest.Artifacts,
                artifact => string.Equals(
                    artifact.RelativePath,
                    relativePath,
                    StringComparison.Ordinal));
            return Path.Combine(
                cacheDirectory,
                artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        var prerequisite = new CopilotWrapperPrerequisite(
            cacheDirectory,
            Path.Combine(cacheDirectory, "prepared"),
            fingerprintFile,
            manifestPath,
            manifest,
            ArtifactPath("prepared/phantom-copilot-wrapper.exe"),
            ArtifactPath("prepared/copilot.exe"),
            ArtifactPath("prepared/copilot_runtime.dll"),
            ArtifactPath("prepared/LICENSE.md"),
            ArtifactPath("prepared/mxc_ffi.dll"),
            ArtifactPath("prepared/plm.exe"),
            ArtifactPath("prepared/MXC-LICENSE.md"),
            ArtifactPath("prepared/NativeMethods.g.cs"),
            ArtifactPath("prepared/Phantom.Workspaces.Containers.runtimeconfig.json"),
            ArtifactPath("prepared/Phantom.Workspaces.dll"),
            ArtifactPath("prepared/Microsoft.Mxc.Sdk.dll"),
            ArtifactPath("prepared/phantom-copilot-wrapper.dll"),
            ArtifactPath("prepared/Phantom.Workspaces.Llm.Core.dll"));
        prerequisite.AssertArtifactHashes();
        return prerequisite;
    }

    internal static string ComputeSha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

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
        var path = Path.Combine(BuiltMxcOutputDirectory, fileName);
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

    internal sealed record CopilotWrapperPrerequisite(
        string CacheDirectory,
        string PreparedDirectory,
        string FingerprintFile,
        string ManifestFile,
        CopilotWrapperPrerequisiteManifest Manifest,
        string WrapperExecutable,
        string CopilotExecutable,
        string CopilotRuntimeLibrary,
        string CopilotLicense,
        string MxcFfi,
        string Plm,
        string MxcLicense,
        string NativeBindings,
        string ContainerRuntimeConfig,
        string AppAssembly,
        string MxcSdkAssembly,
        string CopilotWrapperAssembly,
        string LlmCoreAssembly)
    {
        internal string ArtifactSha256(string relativePath) =>
            Assert.Single(
                Manifest.Artifacts,
                artifact => string.Equals(
                    artifact.RelativePath,
                    relativePath,
                    StringComparison.Ordinal)).Sha256;

        internal void AssertArtifactHashes()
        {
            var expectedArtifacts = new[]
            {
                "prepared/Microsoft.Mxc.Sdk.dll",
                "prepared/Phantom.Workspaces.Llm.Core.dll",
                "prepared/Phantom.Workspaces.dll",
                "prepared/phantom-copilot-wrapper.exe",
                "prepared/phantom-copilot-wrapper.dll",
                "prepared/copilot.exe",
                "prepared/copilot_runtime.dll",
                "prepared/LICENSE.md",
                "prepared/mxc_ffi.dll",
                "prepared/plm.exe",
                "prepared/MXC-LICENSE.md",
                "prepared/NativeMethods.g.cs",
                "prepared/Phantom.Workspaces.Containers.runtimeconfig.json",
            };
            Assert.Equal(
                expectedArtifacts,
                Manifest.Artifacts
                    .Select(artifact => artifact.RelativePath));
            Assert.Equal(Manifest.CacheKey, ComputeCacheKey());
            foreach (var artifact in Manifest.Artifacts)
            {
                Assert.Matches("^[0-9a-f]{64}$", artifact.Sha256);
                Assert.False(Path.IsPathFullyQualified(artifact.RelativePath));
                var path = Path.GetFullPath(Path.Combine(
                    CacheDirectory,
                    artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                Assert.True(
                    path.StartsWith(
                        Path.TrimEndingDirectorySeparator(CacheDirectory)
                            + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase),
                    $"Prepared artifact path escapes its cache: '{artifact.RelativePath}'.");
                Assert.True(
                    File.Exists(path),
                    $"Prepared wrapper artifact is missing: '{path}'.");
                Assert.Equal(artifact.Sha256, ComputeSha256(path));
            }
        }

        private string ComputeCacheKey()
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            void Add(string value)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(value);
                hasher.AppendData(BitConverter.GetBytes(bytes.Length));
                hasher.AppendData(bytes);
            }

            Add("copilot-wrapper-test-prerequisite-v5-cache");
            Add(Manifest.SourceFingerprint);
            Add(Manifest.CopilotSdkPackageVersion);
            Add(Manifest.CopilotSdkPackageSha512);
            Add(Manifest.CopilotCliVersion);
            Add(Manifest.CopilotCliPlatform);
            Add(Manifest.CopilotCliDownloadUrl);
            Add(Manifest.CopilotCliChecksumsUrl);
            Add(Manifest.CopilotCliArchiveSha256);
            Add(Manifest.CopilotCliChecksumsSha256);
            foreach (var artifact in Manifest.Artifacts)
            {
                Add(artifact.RelativePath);
                Add(artifact.Sha256);
            }

            return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        }
    }

    internal sealed record CopilotWrapperPrerequisiteManifest
    {
        public required int SchemaVersion { get; init; }
        public required string CacheKey { get; init; }
        public required string SourceFingerprint { get; init; }
        public required string AppReleaseVersion { get; init; }
        public required string AppAssemblyVersion { get; init; }
        public required string AppFileVersion { get; init; }
        public required string AppInformationalVersion { get; init; }
        public required string AppExecutableFileVersion { get; init; }
        public required string AppExecutableProductVersion { get; init; }
        public required string MxcSdkProjectVersion { get; init; }
        public required string MxcSdkAssemblyVersion { get; init; }
        public required string CopilotWrapperAssemblyVersion { get; init; }
        public required string LlmCoreAssemblyVersion { get; init; }
        public required string ReleaseArchiveName { get; init; }
        public required string Configuration { get; init; }
        public required string RuntimeIdentifier { get; init; }
        public required string NativeTarget { get; init; }
        public required string NativeProfile { get; init; }
        public required string[] NativePackages { get; init; }
        public required string[] NativeFeatures { get; init; }
        public required string CopilotSdkPackageVersion { get; init; }
        public required string CopilotSdkPackageSha512 { get; init; }
        public required string CopilotCliVersion { get; init; }
        public required string CopilotCliPlatform { get; init; }
        public required string CopilotCliDownloadUrl { get; init; }
        public required string CopilotCliChecksumsUrl { get; init; }
        public required string CopilotCliArchiveSha256 { get; init; }
        public required string CopilotCliChecksumsSha256 { get; init; }
        public required string CopilotCliGraphPath { get; init; }
        public required string ContainerRuntimeConfigGraphPath { get; init; }
        public required string RustcIdentity { get; init; }
        public required string CargoIdentity { get; init; }
        public required string DotNetIdentity { get; init; }
        public required bool ProductionPublishValidated { get; init; }
        public required string ProductionPublishWrapperPath { get; init; }
        public required CopilotWrapperPrerequisiteArtifact[] Artifacts { get; init; }
    }

    internal sealed record CopilotWrapperPrerequisiteArtifact
    {
        public required string RelativePath { get; init; }
        public required string Sha256 { get; init; }
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

    internal sealed class TestDirectory : IDisposable, IAsyncDisposable
    {
        private const int MaxCleanupConcurrency = 8;
        private readonly Action? beforeCleanup;
        private readonly Action<string>? cleanupProgress;
        private readonly SemaphoreSlim cleanupLock = new(1, 1);

        internal TestDirectory(
            Action? beforeCleanup = null,
            Action<string>? cleanupProgress = null)
        {
            this.beforeCleanup = beforeCleanup;
            this.cleanupProgress = cleanupProgress;
            Path = System.IO.Path.Combine(
                Root.FullName, "TestResults", $"mxc-integration-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

        public ValueTask DisposeAsync() => new(DisposeAsyncCore());

        private async Task DisposeAsyncCore()
        {
            await cleanupLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!Directory.Exists(Path))
                    return;

                beforeCleanup?.Invoke();
                await DeleteDirectoryAsync(
                    Path,
                    cleanupProgress,
                    CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                cleanupLock.Release();
            }
        }

        internal static async Task DeleteDirectoryAsync(
            string path,
            Action<string>? progress,
            CancellationToken cancellationToken)
        {
            var files = Directory.GetFiles(path, "*", SearchOption.AllDirectories);
            var directories = Directory.GetDirectories(path, "*", SearchOption.AllDirectories);
            progress?.Invoke(
                $"cleanup snapshot completed; enumeration handles closed"
                + $" ({files.Length} files, {directories.Length} directories)");

            var options = new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = MaxCleanupConcurrency,
            };
            await Parallel.ForEachAsync(
                files,
                options,
                static (file, _) =>
                {
                    File.Delete(file);
                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);
            progress?.Invoke($"deleted {files.Length} files");

            var directoryGroups = directories
                .GroupBy(directory => PathDepth(path, directory))
                .OrderByDescending(group => group.Key);
            foreach (var group in directoryGroups)
            {
                await Parallel.ForEachAsync(
                    group,
                    options,
                    static (directory, _) =>
                    {
                        Directory.Delete(directory);
                        return ValueTask.CompletedTask;
                    }).ConfigureAwait(false);
            }

            Directory.Delete(path);
            progress?.Invoke($"cleanup completed ({directories.Length + 1} directories)");
        }

        private static int PathDepth(string root, string path) =>
            System.IO.Path.GetRelativePath(root, path).Count(
                character => character == System.IO.Path.DirectorySeparatorChar
                    || character == System.IO.Path.AltDirectorySeparatorChar);
    }
}
