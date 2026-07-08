using System.Collections.Generic;

namespace Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;

/// <summary>
/// An <see cref="IChatOutputHtmlSink"/> implementation that records all sink calls as
/// <see cref="SinkCommand"/> values for deferred replay on the UI thread.
/// <para>
/// The sink is written entirely off the UI thread (during background chunk generation) and then read
/// on the UI thread (when replaying commands). This is safe because the list is written completely
/// before it is ever read — there is no concurrent access. No locking is needed.
/// </para>
/// </summary>
internal sealed class RecordingChatOutputHtmlSink : IChatOutputHtmlSink
{
    private readonly List<SinkCommand> commands = new();

    public IReadOnlyList<SinkCommand> Commands => this.commands;

    public void UpdateContent(string path, ChatOutputUpdateLocation location, string content)
        => this.commands.Add(new SinkCommand(path, location, content));

    public void RemoveContent(string path)
        => this.commands.Add(new SinkCommand(path, null, null));

    public void ScrollToBottom() { /* intentionally no-op */ }
}
