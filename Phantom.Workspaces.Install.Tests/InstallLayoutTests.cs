using Phantom.Workspaces.Install;

namespace Phantom.Workspaces.Install.Tests;

public sealed class InstallLayoutTests
{
    private static readonly DateTimeOffset FixedInstant = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private const string AppRoot = @"X:\sandbox\Phantom.Workspaces\app";

    [Fact]
    public void Bootstrap_CreatesVersionDirectoryRepointsCurrentAndWritesMetadata()
    {
        var fileSystem = new InMemoryFileSystem();
        var layout = new InstallLayout(fileSystem, AppRoot);
        var payload = SeedPayload(fileSystem, @"X:\downloads\extracted");

        layout.Bootstrap(payload, "0.1.0", FixedInstant);

        Assert.True(fileSystem.FileExists(layout.GetVersionExecutablePath("0.1.0")));
        Assert.Equal("0.1.0", layout.ResolveCurrentVersion());

        var metadata = layout.ReadInstallMetadata();
        Assert.NotNull(metadata);
        Assert.Equal("0.1.0", metadata!.Version);
        Assert.Equal(FixedInstant, metadata.InstalledAtUtc);
    }

    [Fact]
    public void Bootstrap_IsIdempotent()
    {
        var fileSystem = new InMemoryFileSystem();
        var layout = new InstallLayout(fileSystem, AppRoot);
        var payload = SeedPayload(fileSystem, @"X:\downloads\extracted");

        layout.Bootstrap(payload, "0.1.0", FixedInstant);
        layout.Bootstrap(payload, "0.1.0", FixedInstant);

        Assert.Equal("0.1.0", layout.ResolveCurrentVersion());
        Assert.Single(layout.GetInstalledVersions());
    }

    [Fact]
    public void RepointCurrent_SwitchesActiveVersion()
    {
        var fileSystem = new InMemoryFileSystem();
        var layout = new InstallLayout(fileSystem, AppRoot);
        fileSystem.CreateDirectory(layout.GetVersionDirectory("0.1.0"));
        fileSystem.CreateDirectory(layout.GetVersionDirectory("0.2.0"));

        layout.RepointCurrent("0.1.0");
        Assert.Equal("0.1.0", layout.ResolveCurrentVersion());

        layout.RepointCurrent("0.2.0");
        Assert.Equal("0.2.0", layout.ResolveCurrentVersion());
    }

    [Fact]
    public void RepointCurrent_MissingVersion_Throws()
    {
        var fileSystem = new InMemoryFileSystem();
        var layout = new InstallLayout(fileSystem, AppRoot);

        Assert.Throws<DirectoryNotFoundException>(() => layout.RepointCurrent("9.9.9"));
    }

    [Fact]
    public void ResolveCurrentVersion_WhenNoLink_ReturnsNull()
    {
        var fileSystem = new InMemoryFileSystem();
        var layout = new InstallLayout(fileSystem, AppRoot);

        Assert.Null(layout.ResolveCurrentVersion());
    }

    [Fact]
    public void PruneVersions_RetainsCurrentAndPreviousOnly()
    {
        var fileSystem = new InMemoryFileSystem();
        var layout = new InstallLayout(fileSystem, AppRoot);
        fileSystem.CreateDirectory(layout.GetVersionDirectory("0.1.0"));
        fileSystem.CreateDirectory(layout.GetVersionDirectory("0.2.0"));
        fileSystem.CreateDirectory(layout.GetVersionDirectory("0.3.0"));

        layout.PruneVersions(keepVersion: "0.3.0", alsoKeepVersion: "0.2.0");

        var remaining = layout.GetInstalledVersions().OrderBy(static name => name).ToArray();
        Assert.Equal(new[] { "0.2.0", "0.3.0" }, remaining);
    }

    [Fact]
    public void IsManagedExecutable_DistinguishesManagedFromUnmanaged()
    {
        var fileSystem = new InMemoryFileSystem();
        var layout = new InstallLayout(fileSystem, AppRoot);

        Assert.True(layout.IsManagedExecutable(layout.GetVersionExecutablePath("0.1.0")));
        Assert.False(layout.IsManagedExecutable(@"X:\downloads\extracted\Phantom.Workspaces.exe"));
    }

    [Fact]
    public void Bootstrap_InstalledPayload_PreservesRuntimeNestedPath()
    {
        var fileSystem = new InMemoryFileSystem();
        var layout = new InstallLayout(fileSystem, AppRoot);
        const string payloadDirectory = @"X:\downloads\extracted";
        const string rid = "win-x64";
        SeedPayload(fileSystem, payloadDirectory);
        var payloadRuntime = Path.Combine(payloadDirectory, "runtimes", rid, "native", "copilot.exe");
        fileSystem.WriteAllText(payloadRuntime, "copilot-bytes");

        layout.Bootstrap(payloadDirectory, "0.1.0", FixedInstant);

        var installedRuntime = Path.Combine(
            layout.GetVersionDirectory("0.1.0"), "runtimes", rid, "native", "copilot.exe");
        Assert.True(
            fileSystem.FileExists(installedRuntime),
            $"Expected installed nested runtime path to survive Bootstrap: {installedRuntime}");
    }

    private static string SeedPayload(InMemoryFileSystem fileSystem, string payloadDirectory)
    {
        fileSystem.CreateDirectory(payloadDirectory);
        fileSystem.WriteAllText(
            Path.Combine(payloadDirectory, InstallLayout.ApplicationExecutableName),
            "executable-bytes");
        return payloadDirectory;
    }
}
