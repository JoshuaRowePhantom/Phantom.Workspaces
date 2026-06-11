namespace Phantom.Workspaces.Tools;

public interface ICurrentExecutionContextProvider
{
    string ComputerName { get; }

    string UserName { get; }

    string OperatingSystemName { get; }

    string HomeDirectoryPath { get; }
}

public sealed class CurrentExecutionContextProvider : ICurrentExecutionContextProvider
{
    public string ComputerName => Environment.MachineName;

    public string UserName => Environment.UserName;

    public string OperatingSystemName =>
        OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsMacOS() ? "macos" :
        OperatingSystem.IsLinux() ? "linux" :
        "unknown";

    public string HomeDirectoryPath => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
