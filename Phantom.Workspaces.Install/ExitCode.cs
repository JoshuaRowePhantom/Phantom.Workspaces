namespace Phantom.Workspaces.Install;

/// <summary>
/// Well-defined process exit codes used by <c>--silent</c> install and by the updater,
/// which waits on the <c>--apply-update</c> process handle and reads the returned code.
/// </summary>
public enum ExitCode
{
    /// <summary>The operation completed successfully.</summary>
    Success = 0,

    /// <summary>An unclassified failure occurred.</summary>
    GeneralFailure = 1,

    /// <summary>The command line could not be parsed.</summary>
    BadArguments = 2,

    /// <summary>First-run bootstrap or an install-time IO operation failed.</summary>
    BootstrapFailure = 3,

    /// <summary>Applying an update failed; <c>current</c> was left untouched.</summary>
    UpdateApplyFailure = 4,
}
