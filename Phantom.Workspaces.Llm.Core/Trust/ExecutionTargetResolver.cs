using System.Text.Json;

namespace Phantom.Workspaces.Llm.Trust;

public sealed class ExecutionTargetResolver
{
    public JsonElement Resolve(TrustProfile? trustProfile)
    {
        if (trustProfile?.DefaultExecutionTarget is { } target)
        {
            return target.Clone();
        }

        using var localDocument = JsonDocument.Parse("""{"type":"local"}""");
        return localDocument.RootElement.Clone();
    }
}
