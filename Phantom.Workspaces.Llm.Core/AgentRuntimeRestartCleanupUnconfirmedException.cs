namespace Phantom.Workspaces.Llm;

internal sealed class AgentRuntimeRestartCleanupUnconfirmedException(Exception innerException)
    : InvalidOperationException("Replacement startup or cleanup is unconfirmed. Restart is blocked.", innerException);
