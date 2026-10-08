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
        Directory.Delete(junction);
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
        LockFile = Path.Combine(projectDirectory, "obj", "mxcw.lock");
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

    internal Task<MxcRepositoryTestSupport.ProcessResult> PrepareAsync() =>
        MxcRepositoryTestSupport.InvokeAsync("dotnet",
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

    public void Dispose()
    {
        if (Directory.Exists(BaseDirectory))
            Directory.Delete(BaseDirectory, true);
        if (Directory.Exists(outputDirectory))
            Directory.Delete(outputDirectory, true);
    }
}
