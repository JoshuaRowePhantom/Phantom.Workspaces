using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm.Shell;

namespace Phantom.Workspaces.Llm.Trust;

/// <summary>
/// Handles a locally-dispatched stream session. The implementation owns <paramref name="hostEnd"/>
/// and must complete it (via <see cref="IStreamMessageChannel.DisposeAsync"/>) when the session ends.
/// </summary>
internal interface ILocalStreamHandler
{
    Task HandleAsync(
        JsonElement openPayload,
        IStreamMessageChannel hostEnd,
        CancellationToken ct);
}
