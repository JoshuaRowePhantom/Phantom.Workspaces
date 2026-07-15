using System.Text.Json;
using System.Threading.Channels;

namespace Phantom.Workspaces.Transport;

/// <summary>
/// Bidirectional, async, JsonElement-typed communication channel.
/// </summary>
public interface IMessageChannel : IAsyncDisposable
{
    /// <summary>
    /// Writer for sending messages to the remote endpoint.
    /// </summary>
    ChannelWriter<JsonElement> Writer { get; }

    /// <summary>
    /// Reader for receiving messages from the remote endpoint.
    /// </summary>
    ChannelReader<JsonElement> Reader { get; }
}
