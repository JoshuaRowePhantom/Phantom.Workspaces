namespace Phantom.Workspaces.Llm.Mcp;

/// <summary>
/// Thrown when an MCP connection cannot be established because the session's trust profile
/// requires containment but the requested endpoint (for example HTTP/SSE) has no child process to
/// sandbox (issue #1477). Fails closed — no unconstrained fallback is attempted.
/// </summary>
public sealed class McpUnsupportedPolicyException : Exception
{
    public McpUnsupportedPolicyException(string message) : base(message)
    {
    }

    public McpUnsupportedPolicyException(string message, Exception inner) : base(message, inner)
    {
    }
}
