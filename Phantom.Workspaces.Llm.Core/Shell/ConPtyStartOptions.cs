namespace Phantom.Workspaces.Llm.Shell;

internal sealed record ConPtyStartOptions
{
    public required ShellOpenPayload Payload { get; init; }
    public required TimeSpan ShutdownTimeout { get; init; }
    public IWindowsProcessLaunchObserver? LaunchObserver { get; init; }
}

internal enum WindowsProcessLaunchStage
{
    CreateProcess,
    ConfigureJob,
    AssignJob,
    ResumeThread,
}

internal sealed record WindowsProcessLaunchEvent
{
    public required WindowsProcessLaunchStage Stage { get; init; }
    public required bool Succeeded { get; init; }
    public required int? Win32Error { get; init; }
}

internal interface IWindowsProcessLaunchObserver
{
    void Observe(WindowsProcessLaunchEvent launchEvent);
}
