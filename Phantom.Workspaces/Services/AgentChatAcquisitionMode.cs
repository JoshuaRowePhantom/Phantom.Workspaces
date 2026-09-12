namespace Phantom.Workspaces.Services;

/// <summary>
/// Acquisition mode selector for <see cref="AcquireAgentChatRequest"/> (issue #1485).
/// </summary>
public enum AgentChatAcquisitionMode
{
    /// <summary>Local execution against the in-process engine.</summary>
    Local,
    /// <summary>Attach a viewer to an already-running remote session.</summary>
    AttachRemote,
    /// <summary>Attach if running, otherwise start a new remote session.</summary>
    StartOrAttachRemote,
}
