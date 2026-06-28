using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm.Shell;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Host-provided context for the workspace-gui toolset. Gives AI tools programmatic access
/// to the main window and shortcut system so they can open/close workspace panes, close tabs,
/// and invoke entity shortcuts on the UI thread.
/// </summary>
public sealed record WorkspaceGuiContext
{
    /// <summary>The main window view model; all UI operations must be dispatched on its scheduler.</summary>
    public required MainWindowViewModel MainWindowViewModel { get; init; }

    /// <summary>The shortcut manager used to resolve and invoke shortcuts.</summary>
    public required ShortcutManager ShortcutManager { get; init; }

    /// <summary>
    /// Optional factory for ephemeral shell sessions; injected in tests to avoid spawning a real PTY.
    /// When null, the <c>open_tab</c> tool uses <see cref="Phantom.Workspaces.Llm.Trust.LocalTrustedExecutor"/>.
    /// </summary>
    internal Func<string, IReadOnlyList<string>, string?, CancellationToken, Task<ITerminalSession>>? EphemeralShellSessionOpener { get; init; }
}
