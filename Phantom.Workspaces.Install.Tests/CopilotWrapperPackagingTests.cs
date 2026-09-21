namespace Phantom.Workspaces.Install.Tests;

[Collection(MxcIntegrationCollection.Name)]
public sealed class CopilotWrapperPackagingTests
{
    [Fact]
    public void RuntimePayload_CommandUsesFocusedIsolatedNonReusableBuild()
    {
        var prerequisite = MxcRepositoryTestSupport.LoadCopilotWrapperPrerequisite();
        var arguments = MxcRepositoryTestSupport.CreateCopilotRuntimePayloadArguments(
            "payload",
            "isolated-artifacts",
            "empty-normal-output",
            prerequisite);

        Assert.Contains("--disable-build-servers", arguments);
        Assert.Contains("-m:1", arguments);
        Assert.Contains("/nodeReuse:false", arguments);
        Assert.Contains("-p:UseSharedCompilation=false", arguments);
        Assert.Contains("-p:UseArtifactsOutput=true", arguments);
        Assert.Contains("-p:ArtifactsPath=isolated-artifacts", arguments);
        Assert.Contains(
            $"-p:OutDir=empty-normal-output{Path.DirectorySeparatorChar}",
            arguments);
        Assert.Contains("-t:PublishCopilotRuntimeLoose;PublishMxcRuntimeLoose", arguments);
        Assert.Contains("-p:NoBuild=true", arguments);
        Assert.Contains("-p:UsePreparedCopilotPayloadForTests=true", arguments);
        Assert.Contains(
            $"-p:PreparedCopilotPayloadDirectory={prerequisite.PreparedDirectory}",
            arguments);
        Assert.Contains(
            $"-p:PreparedCopilotPayloadFingerprint={prerequisite.Manifest.CacheKey}",
            arguments);
        Assert.Contains(
            $"-p:PreparedCopilotCliSha256={prerequisite.ArtifactSha256("prepared/copilot.exe")}",
            arguments);
        Assert.DoesNotContain("-restore", arguments);
        Assert.DoesNotContain("publish", arguments);
    }

    [Fact]
    public async Task RuntimePayload_WinX64_IncludesWrapperCliMxcAndLicenses()
    {
        var prerequisite = MxcRepositoryTestSupport.LoadCopilotWrapperPrerequisite();
        await using var payload = new MxcRepositoryTestSupport.TestDirectory(
            cleanupProgress: message => Console.WriteLine($"Copilot payload {message}"));
        await using var buildArtifacts = new MxcRepositoryTestSupport.TestDirectory(
            cleanupProgress: message => Console.WriteLine($"Copilot build artifacts {message}"));
        await using var emptyNormalOutput = new MxcRepositoryTestSupport.TestDirectory(
            cleanupProgress: message => Console.WriteLine($"Empty normal output {message}"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(emptyNormalOutput.Path));

        var payloadAssembly = await MxcRepositoryTestSupport.InvokeAsync(
            "dotnet",
            MxcRepositoryTestSupport.CreateCopilotRuntimePayloadArguments(
                payload.Path,
                buildArtifacts.Path,
                emptyNormalOutput.Path,
                prerequisite));

        Assert.True(
            payloadAssembly.ExitCode == 0,
            $"Copilot payload assembly failed.\nSTDOUT:\n{payloadAssembly.StandardOutput}\nSTDERR:\n{payloadAssembly.StandardError}");
        Assert.True(payloadAssembly.DirectProcessInJob);
        Assert.Equal(0U, payloadAssembly.ActiveJobProcessesAfterCleanup);
        Assert.Empty(Directory.EnumerateFileSystemEntries(emptyNormalOutput.Path));

        var nativeDirectory = Path.Combine(payload.Path, "runtimes", "win-x64", "native");
        var expectedFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["phantom-copilot-wrapper.exe"] = prerequisite.WrapperExecutable,
            ["copilot.exe"] = prerequisite.CopilotExecutable,
            ["copilot_runtime.dll"] = prerequisite.CopilotRuntimeLibrary,
            ["mxc_ffi.dll"] = prerequisite.MxcFfi,
            ["plm.exe"] = prerequisite.Plm,
            ["LICENSE.md"] = prerequisite.CopilotLicense,
            ["MXC-LICENSE.md"] = prerequisite.MxcLicense,
        };
        foreach (var (fileName, preparedSource) in expectedFiles)
        {
            var file = new FileInfo(Path.Combine(nativeDirectory, fileName));
            Assert.True(file.Exists, $"Published payload is missing '{fileName}'.");
            Assert.True(file.Length > 0, $"Published payload contains an empty '{fileName}'.");
            Assert.Equal(await HashFileAsync(preparedSource), await HashFileAsync(file.FullName));
        }

        var validation = await MxcRepositoryTestSupport.InvokePowerShellAsync(
            "packaging",
            "validate",
            "Assert-CopilotRuntimePayload.ps1",
            "-PayloadDirectory", payload.Path,
            "-RuntimeIdentifier", "win-x64");
        Assert.Equal(0, validation.ExitCode);
        Assert.Contains(
            "Copilot runtime payload validation passed",
            validation.StandardOutput,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimePayload_GitHubFinalCommand_AcceptedNativeExitIsClean(
        bool useNativeErrorPreference)
    {
        var prerequisite = MxcRepositoryTestSupport.LoadCopilotWrapperPrerequisite();
        await using var payload = CreateValidatorPayload(prerequisite);

        var result = await InvokeValidatorAsGitHubStepAsync(
            payload.Path,
            useNativeErrorPreference);

        Assert.True(
            result.ExitCode == 0,
            $"Validator leaked its accepted native exit.\nSTDOUT:\n{result.StandardOutput}\nSTDERR:\n{result.StandardError}");
        Assert.Contains(
            "Copilot runtime payload validation passed",
            result.StandardOutput,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimePayload_GitHubFinalCommand_UnexpectedNativeExitFails()
    {
        var prerequisite = MxcRepositoryTestSupport.LoadCopilotWrapperPrerequisite();
        await using var payload = CreateValidatorPayload(prerequisite);
        File.Copy(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "whoami.exe"),
            Path.Combine(payload.Path, "runtimes", "win-x64", "native", "copilot.exe"),
            overwrite: true);

        var result = await InvokeValidatorAsGitHubStepAsync(
            payload.Path,
            useNativeErrorPreference: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "did not reach expected server-mode argument validation (exit 0)",
            result.StandardError + result.StandardOutput,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimePayload_GitHubFinalCommand_NativeLaunchFailureFails()
    {
        var prerequisite = MxcRepositoryTestSupport.LoadCopilotWrapperPrerequisite();
        await using var payload = CreateValidatorPayload(prerequisite);
        await File.WriteAllTextAsync(
            Path.Combine(payload.Path, "runtimes", "win-x64", "native", "copilot.exe"),
            "not a Windows executable");

        var result = await InvokeValidatorAsGitHubStepAsync(
            payload.Path,
            useNativeErrorPreference: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "copilot.exe could not be launched",
            result.StandardError + result.StandardOutput,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimePayload_GitHubFinalCommand_NonExecutablePeFailsBeforeLaunch()
    {
        var prerequisite = MxcRepositoryTestSupport.LoadCopilotWrapperPrerequisite();
        await using var payload = CreateValidatorPayload(prerequisite);
        var executable = Path.Combine(
            payload.Path,
            "runtimes",
            "win-x64",
            "native",
            "copilot.exe");
        ClearExecutableImageCharacteristic(executable);

        var result = await InvokeValidatorAsGitHubStepAsync(
            payload.Path,
            useNativeErrorPreference: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "copilot.exe could not be launched",
            result.StandardError + result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains(
            "invalid Windows executable image",
            result.StandardError + result.StandardOutput,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimePayload_GitHubFinalCommand_NativeLaunchFailure_CompletesRepeatedlyWithoutHang()
    {
        var prerequisite = MxcRepositoryTestSupport.LoadCopilotWrapperPrerequisite();
        var attempts = Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var payload = CreateValidatorPayload(prerequisite);
            await File.WriteAllTextAsync(
                Path.Combine(payload.Path, "runtimes", "win-x64", "native", "copilot.exe"),
                "not a Windows executable");
            return await InvokeValidatorAsGitHubStepAsync(
                payload.Path,
                useNativeErrorPreference: true);
        }).ToArray();

        var results = await Task.WhenAll(attempts);

        Assert.All(results, result =>
        {
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(
                "copilot.exe could not be launched",
                result.StandardError + result.StandardOutput,
                StringComparison.Ordinal);
            Assert.True(result.DirectProcessInJob);
            Assert.Equal(0U, result.ActiveJobProcessesAfterCleanup);
        });
    }

    [Fact]
    public void InvokeValidatorAsGitHubStep_UsesValidatorTimeout_NotLongRunningTimeout()
    {
        var options = MxcRepositoryTestSupport.CreateValidatorInvocationOptions();

        Assert.Equal(TimeSpan.FromSeconds(30), options.Timeout);
        Assert.True(options.Timeout < TimeSpan.FromSeconds(90));
    }

    [Fact]
    public async Task RuntimePayload_MissingPreparedFingerprint_FailsClosed()
    {
        var prerequisite = MxcRepositoryTestSupport.LoadCopilotWrapperPrerequisite();
        await using var payload = new MxcRepositoryTestSupport.TestDirectory();
        await using var buildArtifacts = new MxcRepositoryTestSupport.TestDirectory();
        await using var normalOutput = new MxcRepositoryTestSupport.TestDirectory();
        var fingerprint = RandomFingerprint();
        var preparedDirectory = CreateCanonicalPreparedDirectory(
            prerequisite,
            fingerprint);
        try
        {
            var result = await MxcRepositoryTestSupport.InvokeAsync(
                "dotnet",
                MxcRepositoryTestSupport.CreateCopilotRuntimePayloadArguments(
                    payload.Path,
                    buildArtifacts.Path,
                    normalOutput.Path,
                    prerequisite,
                    preparedDirectory: preparedDirectory,
                    preparedFingerprint: fingerprint));

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(
                "prepared Copilot payload fingerprint file does not exist",
                result.StandardError + result.StandardOutput,
                StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.EnumerateFileSystemEntries(payload.Path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(preparedDirectory)!, recursive: true);
        }
    }

    [Fact]
    public async Task RuntimePayload_WrongPreparedFingerprint_FailsClosed()
    {
        var prerequisite = MxcRepositoryTestSupport.LoadCopilotWrapperPrerequisite();
        await using var payload = new MxcRepositoryTestSupport.TestDirectory();
        await using var buildArtifacts = new MxcRepositoryTestSupport.TestDirectory();
        await using var normalOutput = new MxcRepositoryTestSupport.TestDirectory();
        var fingerprint = RandomFingerprint();
        var preparedDirectory = CreateCanonicalPreparedDirectory(
            prerequisite,
            fingerprint);
        File.WriteAllText(
            Path.Combine(Path.GetDirectoryName(preparedDirectory)!, "prerequisite.fingerprint"),
            new string('0', 64));
        try
        {
            var result = await MxcRepositoryTestSupport.InvokeAsync(
                "dotnet",
                MxcRepositoryTestSupport.CreateCopilotRuntimePayloadArguments(
                    payload.Path,
                    buildArtifacts.Path,
                    normalOutput.Path,
                    prerequisite,
                    preparedDirectory: preparedDirectory,
                    preparedFingerprint: fingerprint));

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(
                "prepared Copilot payload fingerprint does not match",
                result.StandardError + result.StandardOutput,
                StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.EnumerateFileSystemEntries(payload.Path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(preparedDirectory)!, recursive: true);
        }
    }

    [Fact]
    public async Task RuntimePayload_TamperedPreparedPath_FailsClosed()
    {
        var prerequisite = MxcRepositoryTestSupport.LoadCopilotWrapperPrerequisite();
        await using var payload = new MxcRepositoryTestSupport.TestDirectory();
        await using var buildArtifacts = new MxcRepositoryTestSupport.TestDirectory();
        await using var normalOutput = new MxcRepositoryTestSupport.TestDirectory();
        var nonCanonicalPath = Path.Combine(
            prerequisite.PreparedDirectory,
            "..",
            "prepared");

        var result = await MxcRepositoryTestSupport.InvokeAsync(
            "dotnet",
            MxcRepositoryTestSupport.CreateCopilotRuntimePayloadArguments(
                payload.Path,
                buildArtifacts.Path,
                normalOutput.Path,
                prerequisite,
                preparedDirectory: nonCanonicalPath));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "must be the canonical absolute 'prepared' directory",
            result.StandardError + result.StandardOutput,
            StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(payload.Path));
    }

    [Fact]
    public async Task RuntimePayload_TamperedPreparedHash_FailsClosed()
    {
        var prerequisite = MxcRepositoryTestSupport.LoadCopilotWrapperPrerequisite();
        await using var payload = new MxcRepositoryTestSupport.TestDirectory();
        await using var buildArtifacts = new MxcRepositoryTestSupport.TestDirectory();
        await using var normalOutput = new MxcRepositoryTestSupport.TestDirectory();

        var result = await MxcRepositoryTestSupport.InvokeAsync(
            "dotnet",
            MxcRepositoryTestSupport.CreateCopilotRuntimePayloadArguments(
                payload.Path,
                buildArtifacts.Path,
                normalOutput.Path,
                prerequisite,
                preparedCopilotCliSha256: new string('0', 64)));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "copilot.exe' does not match its provenance hash",
            result.StandardError + result.StandardOutput,
            StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(payload.Path));
    }

    [Fact]
    public async Task RuntimePayload_PreparedPropertiesWithoutTestOptIn_FailClosed()
    {
        var prerequisite = MxcRepositoryTestSupport.LoadCopilotWrapperPrerequisite();
        await using var payload = new MxcRepositoryTestSupport.TestDirectory();
        await using var buildArtifacts = new MxcRepositoryTestSupport.TestDirectory();
        await using var normalOutput = new MxcRepositoryTestSupport.TestDirectory();

        var result = await MxcRepositoryTestSupport.InvokeAsync(
            "dotnet",
            MxcRepositoryTestSupport.CreateCopilotRuntimePayloadArguments(
                payload.Path,
                buildArtifacts.Path,
                normalOutput.Path,
                prerequisite,
                usePreparedPayload: false));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "prepared Copilot payload properties require explicit test-only opt-in",
            result.StandardError + result.StandardOutput,
            StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(payload.Path));
    }

    [Fact]
    public async Task RuntimePayload_ProductionBuildRefusesPreparedOverride()
    {
        var prerequisite = MxcRepositoryTestSupport.LoadCopilotWrapperPrerequisite();
        await using var payload = new MxcRepositoryTestSupport.TestDirectory();
        await using var buildArtifacts = new MxcRepositoryTestSupport.TestDirectory();
        await using var normalOutput = new MxcRepositoryTestSupport.TestDirectory();

        var result = await MxcRepositoryTestSupport.InvokeAsync(
            "dotnet",
            MxcRepositoryTestSupport.CreateCopilotRuntimePayloadArguments(
                payload.Path,
                buildArtifacts.Path,
                normalOutput.Path,
                prerequisite,
                noBuild: false));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "may only be used by a build-free target invocation",
            result.StandardError + result.StandardOutput,
            StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(payload.Path));
    }

    [Fact]
    public async Task RuntimePayload_ConcurrentMatchingAndMismatchedRids_AreIsolated()
    {
        var prerequisite = MxcRepositoryTestSupport.LoadCopilotWrapperPrerequisite();
        await using var matchingPayload = new MxcRepositoryTestSupport.TestDirectory();
        await using var matchingArtifacts = new MxcRepositoryTestSupport.TestDirectory();
        await using var matchingNormalOutput = new MxcRepositoryTestSupport.TestDirectory();
        await using var mismatchedPayload = new MxcRepositoryTestSupport.TestDirectory();
        await using var mismatchedArtifacts = new MxcRepositoryTestSupport.TestDirectory();
        await using var mismatchedNormalOutput = new MxcRepositoryTestSupport.TestDirectory();

        var results = await Task.WhenAll(
            MxcRepositoryTestSupport.InvokeAsync(
                "dotnet",
                MxcRepositoryTestSupport.CreateCopilotRuntimePayloadArguments(
                    matchingPayload.Path,
                    matchingArtifacts.Path,
                    matchingNormalOutput.Path,
                    prerequisite)),
            MxcRepositoryTestSupport.InvokeAsync(
                "dotnet",
                MxcRepositoryTestSupport.CreateCopilotRuntimePayloadArguments(
                    mismatchedPayload.Path,
                    mismatchedArtifacts.Path,
                    mismatchedNormalOutput.Path,
                    prerequisite,
                    runtimeIdentifier: "win-arm64")));

        Assert.Equal(0, results[0].ExitCode);
        Assert.NotEqual(0, results[1].ExitCode);
        Assert.Contains(
            "prepared Copilot payload RID 'win-x64' does not match 'win-arm64'",
            results[1].StandardError + results[1].StandardOutput,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            await HashFileAsync(prerequisite.CopilotExecutable),
            await HashFileAsync(Path.Combine(
                matchingPayload.Path,
                "runtimes",
                "win-x64",
                "native",
                "copilot.exe")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(mismatchedPayload.Path));
    }

    private static async Task<byte[]> HashFileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return await System.Security.Cryptography.SHA256.HashDataAsync(stream);
    }

    private static MxcRepositoryTestSupport.TestDirectory CreateValidatorPayload(
        MxcRepositoryTestSupport.CopilotWrapperPrerequisite prerequisite)
    {
        var payload = new MxcRepositoryTestSupport.TestDirectory();
        var nativeDirectory = Path.Combine(payload.Path, "runtimes", "win-x64", "native");
        Directory.CreateDirectory(nativeDirectory);
        foreach (var (fileName, source) in new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["phantom-copilot-wrapper.exe"] = prerequisite.WrapperExecutable,
            ["copilot.exe"] = prerequisite.CopilotExecutable,
            ["copilot_runtime.dll"] = prerequisite.CopilotRuntimeLibrary,
            ["mxc_ffi.dll"] = prerequisite.MxcFfi,
            ["plm.exe"] = prerequisite.Plm,
            ["LICENSE.md"] = prerequisite.CopilotLicense,
            ["MXC-LICENSE.md"] = prerequisite.MxcLicense,
        })
        {
            File.Copy(source, Path.Combine(nativeDirectory, fileName));
        }

        return payload;
    }

    private static async Task<MxcRepositoryTestSupport.ProcessResult> InvokeValidatorAsGitHubStepAsync(
        string payloadDirectory,
        bool useNativeErrorPreference)
    {
        await using var scripts = new MxcRepositoryTestSupport.TestDirectory();
        var validator = Path.Combine(
            MxcRepositoryTestSupport.Root.FullName,
            "packaging",
            "validate",
            "Assert-CopilotRuntimePayload.ps1");
        var scriptPath = Path.Combine(scripts.Path, "github-step.ps1");
        await File.WriteAllTextAsync(
            scriptPath,
            $$"""
            $ErrorActionPreference = 'stop'
            $PSNativeCommandUseErrorActionPreference = ${{useNativeErrorPreference.ToString().ToLowerInvariant()}}
            & '{{EscapePowerShellLiteral(validator)}}' -PayloadDirectory '{{EscapePowerShellLiteral(payloadDirectory)}}' -RuntimeIdentifier 'win-x64'
            if ((Test-Path -LiteralPath variable:\LASTEXITCODE)) { exit $LASTEXITCODE }
            """);
        return await MxcRepositoryTestSupport.InvokeAsync(
            "pwsh",
            MxcRepositoryTestSupport.CreateValidatorInvocationOptions(),
            "-NoProfile",
            "-NonInteractive",
            "-File",
            scriptPath);
    }

    private static string EscapePowerShellLiteral(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    private static void ClearExecutableImageCharacteristic(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        stream.Position = 0x3c;
        var peHeaderOffset = reader.ReadInt32();
        stream.Position = peHeaderOffset + 22;
        var characteristics = reader.ReadUInt16();
        stream.Position -= sizeof(ushort);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)(characteristics & ~0x0002));
    }

    private static string RandomFingerprint() =>
        Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Guid.NewGuid().ToByteArray()))
            .ToLowerInvariant();

    private static string CreateCanonicalPreparedDirectory(
        MxcRepositoryTestSupport.CopilotWrapperPrerequisite prerequisite,
        string fingerprint)
    {
        var cacheRoot = Directory.GetParent(prerequisite.CacheDirectory)!.FullName;
        var cacheDirectory = Path.Combine(cacheRoot, fingerprint[..16]);
        Assert.False(Directory.Exists(cacheDirectory));
        var preparedDirectory = Path.Combine(cacheDirectory, "prepared");
        Directory.CreateDirectory(preparedDirectory);
        File.Copy(
            prerequisite.ManifestFile,
            Path.Combine(cacheDirectory, "prerequisite.json"));
        return preparedDirectory;
    }
}
