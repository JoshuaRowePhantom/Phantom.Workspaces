using System.Linq;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm.Trust;

/// <summary>
/// Applies a trust profile's tool-call policy to a request's tools by wrapping each
/// <see cref="AIFunction"/> in a <see cref="TrustAuthorizingAIFunction"/>, so disallowed tool calls
/// are denied at invocation time. Non-function tools are left unchanged.
/// </summary>
public static class TrustToolAuthorization
{
    /// <summary>Wraps the function tools in <paramref name="chatOptions"/> to enforce the trust profile.</summary>
    public static void Apply(ChatOptions chatOptions, TrustProfile trustProfile)
    {
        ArgumentNullException.ThrowIfNull(chatOptions);
        ArgumentNullException.ThrowIfNull(trustProfile);

        if (chatOptions.Tools is null || chatOptions.Tools.Count == 0)
        {
            return;
        }

        var authorizer = new TrustToolCallAuthorizer(trustProfile);
        chatOptions.Tools = chatOptions.Tools
            .Select(tool => tool is AIFunction function
                ? new TrustAuthorizingAIFunction(function, authorizer)
                : tool)
            .ToList();
    }
}
