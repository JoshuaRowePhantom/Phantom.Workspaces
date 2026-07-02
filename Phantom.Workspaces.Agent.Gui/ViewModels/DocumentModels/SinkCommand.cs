namespace Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;

/// <summary>
/// Captures a single <see cref="IChatOutputHtmlSink"/> call for deferred replay.
/// <list type="bullet">
///   <item><description><see cref="Location"/> is non-null → <see cref="IChatOutputHtmlSink.UpdateContent"/></description></item>
///   <item><description><see cref="Location"/> is null → <see cref="IChatOutputHtmlSink.RemoveContent"/></description></item>
/// </list>
/// </summary>
internal readonly record struct SinkCommand(
    string Path,
    ChatOutputUpdateLocation? Location,  // null = Remove
    string? Content);                    // null = Remove
