namespace Phantom.Workspaces.Tools;

public interface ICurrentExecutionContextProvider
{
    string ComputerName { get; }

    string UserName { get; }

    string OperatingSystemName { get; }

    string HomeDirectoryPath { get; }

    /// <summary>
    /// The computer name used when composing this instance's <c>user-computer-profile</c> entity
    /// name. Defaults to <see cref="ComputerName"/>; a testing override can set this to a distinct
    /// value so multiple instances on one machine resolve to different profiles. The real
    /// <see cref="ComputerName"/> (the <c>computers/hostname</c> computer entity) is unaffected.
    /// </summary>
    string EffectiveComputerName => this.ComputerName;
}

public sealed class CurrentExecutionContextProvider : ICurrentExecutionContextProvider
{
    private readonly string? userComputerProfileOverride;

    public CurrentExecutionContextProvider(string? userComputerProfileOverride = null)
    {
        this.userComputerProfileOverride = userComputerProfileOverride;
    }

    public string ComputerName => Environment.MachineName;

    public string UserName => Environment.UserName;

    public string OperatingSystemName =>
        OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsMacOS() ? "macos" :
        OperatingSystem.IsLinux() ? "linux" :
        "unknown";

    public string HomeDirectoryPath => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string EffectiveComputerName =>
        string.IsNullOrWhiteSpace(this.userComputerProfileOverride)
            ? this.ComputerName
            : this.userComputerProfileOverride;
}
