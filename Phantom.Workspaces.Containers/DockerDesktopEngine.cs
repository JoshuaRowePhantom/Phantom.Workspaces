namespace Phantom.Workspaces.Containers;

/// <summary>
/// Detects and launches Docker Desktop on Windows so callers can bring the container engine into
/// a usable state before shelling out to <c>docker</c> commands (issue #1299). Kept as a thin
/// abstraction so the readiness path in <c>MongoDbConnectionBroker</c> is unit-testable without
/// starting real processes.
/// </summary>
public interface IDockerDesktopLauncher
{
    /// <summary>
    /// The full path to the Docker Desktop executable if the install directory is present on
    /// disk, otherwise <see langword="null"/>. A non-null value guarantees the file existed at
    /// the time of the probe; it makes no claim about the engine being usable.
    /// </summary>
    string? InstalledExecutablePath { get; }

    /// <summary>Launches Docker Desktop. Throws if <see cref="InstalledExecutablePath"/> is null.</summary>
    void LaunchDockerDesktop();
}

/// <summary>
/// Default <see cref="IDockerDesktopLauncher"/>. Probes <c>%ProgramFiles%\Docker\Docker\Docker Desktop.exe</c>
/// and launches it with <see cref="System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)"/>
/// under <c>UseShellExecute=true</c> so the GUI detaches from any console-attached parent.
/// </summary>
public sealed class DockerDesktopLauncher : IDockerDesktopLauncher
{
    private readonly string? installedExecutablePath;

    public DockerDesktopLauncher()
    {
        this.installedExecutablePath = ProbeInstalledExecutablePath();
    }

    public string? InstalledExecutablePath => this.installedExecutablePath;

    public void LaunchDockerDesktop()
    {
        var path = this.installedExecutablePath
            ?? throw new InvalidOperationException(
                "Docker Desktop is not installed at the expected path (%ProgramFiles%\\Docker\\Docker\\Docker Desktop.exe).");

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        };
        System.Diagnostics.Process.Start(startInfo);
    }

    private static string? ProbeInstalledExecutablePath()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrEmpty(programFiles))
        {
            return null;
        }

        var candidate = Path.Combine(programFiles, "Docker", "Docker", "Docker Desktop.exe");
        return File.Exists(candidate) ? candidate : null;
    }
}

public abstract class DockerDesktopEngine : ContainerEngine
{
}
