namespace Phantom.Workspaces.Install.Tests;

public sealed partial class CopilotWrapperNestedPublishTests
{
    [Fact]
    public async Task CopilotWrapperPrerequisite_CleanAfterMultipleFingerprints_RemovesAllCacheEntriesAndPointers()
    {
        using var fixture = new CacheCleanFixture();
        fixture.AddEntries();
        var result = await fixture.CleanProjectAsync();
        Assert.Equal(0, result.ExitCode);
        fixture.AssertRemoved();
        Assert.True(File.Exists(fixture.OutsideFile));
    }

    [Fact]
    public async Task CopilotWrapperPrerequisite_RepeatedClean_IsIdempotent()
    {
        using var fixture = new CacheCleanFixture();
        fixture.AddEntries();
        Assert.Equal(0, (await fixture.CleanProjectAsync()).ExitCode);
        Assert.Equal(0, (await fixture.CleanProjectAsync()).ExitCode);
        fixture.AssertRemoved();
    }

    [Fact]
    public async Task CopilotWrapperPrerequisite_CustomBaseIntermediateWithSpaces_CleansOnlyCanonicalRoot()
    {
        using var fixture = new CacheCleanFixture();
        fixture.AddEntries();
        File.WriteAllText(fixture.PointerFile, fixture.OutsideFile);
        var result = await fixture.CleanProjectAsync();
        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        fixture.AssertRemoved();
        Assert.True(File.Exists(fixture.OutsideFile));
    }

    [Fact]
    public async Task CopilotWrapperPrerequisite_CleanWhileInUse_FailsWithoutPartialDeletion()
    {
        using var fixture = new CacheCleanFixture();
        fixture.AddEntries();
        using (File.Open(fixture.LockFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await fixture.CleanProjectAsync();
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("in use", result.StandardOutput + result.StandardError,
                StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(fixture.PointerFile));
            Assert.Equal(3, Directory.GetDirectories(fixture.CacheRoot).Length);
        }
        Assert.Equal(0, (await fixture.CleanProjectAsync()).ExitCode);
        fixture.AssertRemoved();
    }

    [Fact]
    public async Task CopilotWrapperPrerequisite_CleanWhileTestHostActive_PreservesPointerAndCache()
    {
        var pointer = Path.Combine(
            AppContext.BaseDirectory, "copilot-wrapper-prerequisite.path");
        var cache = File.ReadAllText(pointer).Trim();
        var result = await MxcRepositoryTestSupport.InvokeAsync(
            "dotnet", "msbuild",
            Path.Combine("Phantom.Workspaces.Install.Tests",
                "Phantom.Workspaces.Install.Tests.csproj"),
            "-target:CleanCopilotWrapperTestPrerequisite", "-nologo", "/nodeReuse:false");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("in use", result.StandardOutput + result.StandardError,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(cache, File.ReadAllText(pointer).Trim());
        Assert.True(File.Exists(Path.Combine(cache, "prerequisite.json")));
    }

    [Fact]
    public async Task CopilotWrapperPrerequisite_ReparsePoint_IsRejectedBeforeAnyDeletion()
    {
        using var fixture = new CacheCleanFixture();
        fixture.AddEntries();
        var junction = Path.Combine(fixture.CacheRoot, "cccccccccccccccc");
        var result = await MxcRepositoryTestSupport.InvokeAsync(
            "cmd", "/c", "mklink", "/J", junction, fixture.BaseDirectory);
        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        var clean = await fixture.CleanProjectAsync();
        Assert.NotEqual(0, clean.ExitCode);
        Assert.True(File.Exists(fixture.PointerFile));
        Assert.True(File.Exists(fixture.OutsideFile));
        fixture.AssertEntriesPresent();
        Directory.Delete(junction);
    }

    [Fact]
    public async Task CopilotWrapperPrerequisite_ReparseRoot_IsRejectedBeforeAnyDeletion()
    {
        using var fixture = new CacheCleanFixture();
        fixture.AddEntries();
        var target = fixture.CacheRoot + "-outside";
        Directory.Move(fixture.CacheRoot, target);
        var junction = await MxcRepositoryTestSupport.InvokeAsync(
            "cmd", "/c", "mklink", "/J", fixture.CacheRoot, target);
        Assert.Equal(0, junction.ExitCode);
        try
        {
            var result = await fixture.CleanProjectAsync();
            Assert.NotEqual(0, result.ExitCode);
            Assert.True(File.Exists(fixture.PointerFile));
            Assert.True(File.Exists(Path.Combine(target, "aaaaaaaaaaaaaaaa",
                "prepared", "phantom-copilot-wrapper.exe")));
            fixture.AssertEntriesPresent();
        }
        finally
        {
            Directory.Delete(fixture.CacheRoot);
            Directory.Move(target, fixture.CacheRoot);
        }
    }

    [Fact]
    public async Task CopilotWrapperPrerequisite_ReparseAncestor_IsRejectedBeforeAnyDeletion()
    {
        using var fixture = new CacheCleanFixture();
        fixture.AddEntries();
        var target = fixture.BaseDirectory + "-outside";
        Directory.Move(fixture.BaseDirectory, target);
        var junction = await MxcRepositoryTestSupport.InvokeAsync(
            "cmd", "/c", "mklink", "/J", fixture.BaseDirectory, target);
        Assert.Equal(0, junction.ExitCode);
        try
        {
            var result = await fixture.CleanProjectAsync();
            Assert.NotEqual(0, result.ExitCode);
            Assert.True(File.Exists(fixture.PointerFile));
            Assert.True(File.Exists(fixture.OutsideFile));
            fixture.AssertEntriesPresent();
        }
        finally
        {
            Directory.Delete(fixture.BaseDirectory);
            Directory.Move(target, fixture.BaseDirectory);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopilotWrapperPrerequisite_DirectoryPointer_IsRejectedBeforeAnyDeletion(
        bool copiedPointer)
    {
        using var fixture = new CacheCleanFixture();
        fixture.AddEntries();
        var pointer = copiedPointer ? fixture.CopiedPointerFile : fixture.PointerFile;
        File.Delete(pointer);
        Directory.CreateDirectory(pointer);
        File.WriteAllText(Path.Combine(pointer, "outside.txt"), "untouched");
        var result = await fixture.CleanProjectAsync();
        Assert.NotEqual(0, result.ExitCode);
        Assert.True(Directory.Exists(fixture.CacheRoot));
        Assert.True(File.Exists(Path.Combine(pointer, "outside.txt")));
        Assert.True(File.Exists(fixture.OutsideFile));
        fixture.AssertEntriesPresent();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopilotWrapperPrerequisite_ReparsePointer_IsRejectedBeforeAnyDeletion(
        bool copiedPointer)
    {
        using var fixture = new CacheCleanFixture();
        fixture.AddEntries();
        var pointer = copiedPointer ? fixture.CopiedPointerFile : fixture.PointerFile;
        var external = Path.Combine(fixture.BaseDirectory, "outside-pointer");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "untouched.txt"), "untouched");
        File.Delete(pointer);
        var junction = await MxcRepositoryTestSupport.InvokeAsync(
            "cmd", "/c", "mklink", "/J", pointer, external);
        Assert.Equal(0, junction.ExitCode);
        try
        {
            var result = await fixture.CleanProjectAsync();
            Assert.NotEqual(0, result.ExitCode);
            Assert.True(Directory.Exists(fixture.CacheRoot));
            Assert.True(File.Exists(Path.Combine(external, "untouched.txt")));
            Assert.True(File.Exists(fixture.OutsideFile));
            fixture.AssertEntriesPresent();
        }
        finally
        {
            Directory.Delete(pointer);
        }
    }

    [Fact]
    public async Task CopilotWrapperPrerequisite_UnexpectedRootEntry_IsRejectedBeforeAnyDeletion()
    {
        using var fixture = new CacheCleanFixture();
        fixture.AddEntries();
        var unrelated = Path.Combine(fixture.CacheRoot, "not-a-cache-entry.txt");
        File.WriteAllText(unrelated, "untouched");
        var result = await fixture.CleanProjectAsync();
        Assert.NotEqual(0, result.ExitCode);
        Assert.True(File.Exists(unrelated));
        Assert.True(File.Exists(fixture.PointerFile));
        Assert.True(File.Exists(fixture.OutsideFile));
        fixture.AssertEntriesPresent();
    }

    [Fact]
    public async Task CopilotWrapperPrerequisite_UnownedCopiedOutput_IsRejectedBeforeAnyDeletion()
    {
        using var fixture = new CacheCleanFixture();
        fixture.AddEntries();
        using var external = new MxcRepositoryTestSupport.TestDirectory();
        var result = await fixture.CleanWithOutDirAsync(external.Path);
        Assert.NotEqual(0, result.ExitCode);
        Assert.True(Directory.Exists(fixture.CacheRoot));
        Assert.True(File.Exists(fixture.PointerFile));
        Assert.True(File.Exists(fixture.OutsideFile));
        fixture.AssertEntriesPresent();
    }

    [Fact]
    public async Task CopilotWrapperPrerequisite_ExternalIntermediateRoot_IsRejected()
    {
        using var fixture = new CacheCleanFixture();
        fixture.AddEntries();
        var externalBase = Path.Combine(
            MxcRepositoryTestSupport.Root.FullName, "TestResults",
            $"external clean {Guid.NewGuid():N}");
        Directory.CreateDirectory(externalBase);
        try
        {
            var result = await MxcRepositoryTestSupport.InvokeAsync(
                "dotnet", "clean",
                Path.Combine("Phantom.Workspaces.Install.Tests",
                    "Phantom.Workspaces.Install.Tests.csproj"),
                "--nologo", "/nodeReuse:false",
                $"-p:BaseIntermediateOutputPath={externalBase}{Path.DirectorySeparatorChar}");
            Assert.NotEqual(0, result.ExitCode);
            Assert.True(File.Exists(fixture.PointerFile));
            Assert.True(File.Exists(fixture.OutsideFile));
            fixture.AssertEntriesPresent();
        }
        finally
        {
            Directory.Delete(externalBase, true);
        }
    }

    [Fact]
    public async Task CopilotWrapperPrerequisite_LockedCacheFile_FailsBeforePartialDeletion()
    {
        using var fixture = new CacheCleanFixture();
        fixture.AddEntries();
        var lockedFile = Path.Combine(fixture.CacheRoot, "bbbbbbbbbbbbbbbb",
            "prepared", "phantom-copilot-wrapper.exe");
        using (File.Open(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await fixture.CleanProjectAsync();
            Assert.NotEqual(0, result.ExitCode);
            Assert.True(Directory.Exists(fixture.CacheRoot));
            Assert.Equal(3, Directory.GetDirectories(fixture.CacheRoot).Length);
            Assert.True(File.Exists(fixture.PointerFile));
        }
        Assert.Equal(0, (await fixture.CleanProjectAsync()).ExitCode);
    }

    [Fact]
    [Trait("Category", "SlowLayout")]
    public async Task CopilotWrapperPrerequisite_CleanThenTest_RecreatesValidatedCache()
    {
        using var fixture = new CacheCleanFixture();
        fixture.AddEntries();
        Assert.Equal(0, (await fixture.CleanProjectAsync()).ExitCode);
        var result = await fixture.PrepareAsync();
        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        var cacheDirectory = File.ReadAllText(fixture.PointerFile).Trim();
        Assert.Equal(fixture.CacheRoot, Path.GetDirectoryName(cacheDirectory),
            ignoreCase: true);
        using var manifest = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(cacheDirectory, "prerequisite.json")));
        var fingerprint = manifest.RootElement.GetProperty("CacheKey").GetString();
        Assert.Matches("^[0-9a-f]{64}$", fingerprint);
        Assert.Equal(fingerprint, File.ReadAllText(
            Path.Combine(cacheDirectory, "prerequisite.fingerprint")).Trim());
        foreach (var artifact in manifest.RootElement.GetProperty("Artifacts").EnumerateArray())
        {
            var path = Path.Combine(cacheDirectory,
                artifact.GetProperty("RelativePath").GetString()!.Replace('/', '\\'));
            Assert.Equal(artifact.GetProperty("Sha256").GetString(),
                MxcRepositoryTestSupport.ComputeSha256(path));
        }
        Assert.Equal(cacheDirectory, File.ReadAllText(fixture.CopiedPointerFile).Trim());
        using (var prerequisite =
               MxcRepositoryTestSupport.LoadCopilotWrapperPrerequisite(
                   fixture.CopiedPointerFile))
        {
            await using var payload = new MxcRepositoryTestSupport.TestDirectory();
            await using var artifacts = new MxcRepositoryTestSupport.TestDirectory();
            var publish = await MxcRepositoryTestSupport.InvokeAsync(
                "dotnet", MxcRepositoryTestSupport.CreateCopilotWrapperPublishArguments(
                    payload.Path, artifacts.Path, prerequisite));
            Assert.True(publish.ExitCode == 0,
                "Custom prerequisite publish failed:" + Environment.NewLine +
                publish.StandardOutput + Environment.NewLine + publish.StandardError);
        }
        Assert.Equal(0, (await fixture.CleanProjectAsync()).ExitCode);
        fixture.AssertRemoved();
    }
}

public sealed partial class BuildIntegrationTests
{
    [Fact]
    public async Task CopilotWrapperPrerequisite_SolutionClean_RemovesInstallTestCache()
    {
        using var fixture = new CacheCleanFixture();
        fixture.AddEntries();
        var result = await fixture.CleanSolutionAsync();
        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        fixture.AssertRemoved();
    }
}

internal sealed class CacheCleanFixture : IDisposable
{
    private readonly string projectDirectory = Path.Combine(
        MxcRepositoryTestSupport.Root.FullName, "Phantom.Workspaces.Install.Tests");
    private readonly string name = "c s" + Guid.NewGuid().ToString("N")[..6];
    private readonly string outputDirectory;

    internal CacheCleanFixture()
    {
        BaseDirectory = Path.Combine(projectDirectory, "obj", name);
        outputDirectory = Path.Combine(projectDirectory, "bin", name);
        CacheRoot = Path.Combine(BaseDirectory, "mxcw");
        PointerFile = Path.Combine(BaseDirectory, "Debug", "net10.0",
            "copilot-wrapper-prerequisite.path");
        CopiedPointerFile = Path.Combine(outputDirectory, "Debug", "net10.0",
            "copilot-wrapper-prerequisite.path");
        OutsideFile = Path.Combine(BaseDirectory, "untouched.txt");
        LockFile = Path.Combine(BaseDirectory, "mxcw.lock");
    }

    internal string BaseDirectory { get; }
    internal string CacheRoot { get; }
    internal string PointerFile { get; }
    internal string CopiedPointerFile { get; }
    internal string OutsideFile { get; }
    internal string LockFile { get; }

    internal void AddEntries()
    {
        foreach (var name in new[]
        {
            "aaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbb", "dddddddddddddddd.staging-123"
        })
        {
            var entry = Path.Combine(CacheRoot, name, "prepared");
            Directory.CreateDirectory(entry);
            File.WriteAllText(Path.Combine(entry, "phantom-copilot-wrapper.exe"), "fixture");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(PointerFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(CopiedPointerFile)!);
        File.WriteAllText(PointerFile, Path.Combine(CacheRoot, "aaaaaaaaaaaaaaaa"));
        File.WriteAllText(CopiedPointerFile, Path.Combine(CacheRoot, "aaaaaaaaaaaaaaaa"));
        File.WriteAllText(OutsideFile, "untouched");
        File.WriteAllText(LockFile, string.Empty);
    }

    private string[] Arguments(string entryPoint) =>
    [
        "clean", entryPoint, "--nologo", "/nodeReuse:false",
        $"-p:BaseIntermediateOutputPath=obj\\{name}\\",
        $"-p:BaseOutputPath=bin\\{name}\\"
    ];

    internal Task<MxcRepositoryTestSupport.ProcessResult> CleanProjectAsync() =>
        MxcRepositoryTestSupport.InvokeAsync(
            "dotnet", Arguments(Path.Combine("Phantom.Workspaces.Install.Tests",
                "Phantom.Workspaces.Install.Tests.csproj")));

    internal Task<MxcRepositoryTestSupport.ProcessResult> CleanSolutionAsync() =>
        MxcRepositoryTestSupport.InvokeAsync(
            "dotnet", Arguments("Phantom.Workspaces.slnx"));

    internal Task<MxcRepositoryTestSupport.ProcessResult> CleanWithOutDirAsync(string outDir) =>
        MxcRepositoryTestSupport.InvokeAsync("dotnet",
            "clean", Path.Combine("Phantom.Workspaces.Install.Tests",
                "Phantom.Workspaces.Install.Tests.csproj"),
            "--nologo", "/nodeReuse:false",
            $"-p:BaseIntermediateOutputPath=obj\\{name}\\",
            $"-p:OutDir={outDir}{Path.DirectorySeparatorChar}");

    internal Task<MxcRepositoryTestSupport.ProcessResult> PrepareAsync() =>
        MxcRepositoryTestSupport.InvokeAsync("dotnet",
            new MxcRepositoryTestSupport.InvocationOptions
            {
                Timeout = TimeSpan.FromMinutes(25),
                StandardOutputObserver = line => Console.WriteLine(line)
            },
            [
                "msbuild",
                Path.Combine("Phantom.Workspaces.Install.Tests",
                    "Phantom.Workspaces.Install.Tests.csproj"),
                "-target:PrepareCopilotWrapperTestPrerequisite", "-nologo", "/nodeReuse:false",
                $"-p:BaseIntermediateOutputPath=obj\\{name}\\",
                $"-p:BaseOutputPath=bin\\{name}\\"
            ]);

    internal void AssertRemoved()
    {
        Assert.False(Directory.Exists(CacheRoot));
        Assert.False(File.Exists(PointerFile));
        Assert.False(File.Exists(CopiedPointerFile));
    }

    internal void AssertEntriesPresent()
    {
        foreach (var name in new[]
        {
            "aaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbb", "dddddddddddddddd.staging-123"
        })
        {
            Assert.True(File.Exists(Path.Combine(CacheRoot, name, "prepared",
                "phantom-copilot-wrapper.exe")));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(BaseDirectory))
            Directory.Delete(BaseDirectory, true);
        if (Directory.Exists(outputDirectory))
            Directory.Delete(outputDirectory, true);
    }
}
