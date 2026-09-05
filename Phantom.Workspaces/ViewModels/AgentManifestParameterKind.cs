namespace Phantom.Workspaces.ViewModels;

public enum AgentManifestParameterKind
{
    Text,
    Directory,

    /// <summary>
    /// The <c>executor</c> launch parameter (issue #1440, per-component-executor-binding): a combined
    /// picker over both <c>trust-profile</c> and <c>user-computer-profile</c> entities whose selection is
    /// recorded as a disambiguated value in the session's typed <c>parameter-selections</c> map. Renamed
    /// from the earlier user-computer-profile-only <c>UserComputerProfile</c> framing.
    /// </summary>
    Executor,
}
