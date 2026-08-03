namespace Phantom.Workspaces.Tools;

/// <summary>
/// Full result of a <c>code tunnel status</c> invocation performed by
/// <see cref="IVsCodeTunnelStatusResolver"/>. <see cref="Status"/> is non-null iff the CLI
/// reported a running tunnel; <see cref="CliResult"/> is non-null iff the CLI was successfully
/// launched (its exit code / stdout / stderr are captured for logging and reporting).
/// </summary>
public sealed record VsCodeTunnelResolution(
    VsCodeTunnelStatus? Status,
    VsCodeCliResult? CliResult,
    string? CliLaunchError);
