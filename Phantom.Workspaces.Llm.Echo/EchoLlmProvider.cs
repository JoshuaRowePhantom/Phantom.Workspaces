namespace Phantom.Workspaces.Llm.Echo;

/// <summary>
/// Test/play provider that returns the requested output exactly as provided.
/// </summary>
public sealed class EchoLlmProvider
{
    public string GetResponse(string requestedOutput)
    {
        return requestedOutput;
    }
}
