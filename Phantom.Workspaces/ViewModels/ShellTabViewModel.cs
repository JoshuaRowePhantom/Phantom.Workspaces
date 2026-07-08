using System;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Gui.Shared.ViewModels;
using Phantom.Workspaces.Llm.Shell;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// A workspace tab bound to a live shell session. Wraps an <see cref="ITerminalSession"/> and
/// exposes a <see cref="TerminalSessionViewModel"/> for the terminal control to bind to. The tab
/// is opened ephemerally by <see cref="StartShellOnProfileShortcutHandler"/>; no entity or
/// relationship is created.
/// </summary>
public sealed class ShellTabViewModel : WorkspaceTabViewModel
{
    private readonly ITerminalSession session;

    /// <summary>Creates a shell tab wrapping <paramref name="session"/>.</summary>
    public ShellTabViewModel(ITerminalSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        this.session = session;
        this.TerminalSession = new TerminalSessionViewModel
        {
            Stream = session.Stream,
            ResizeCallback = (columns, rows, ct) => session.ResizeAsync(columns, rows, ct),
        };
    }

    /// <summary>The view model the terminal control binds to.</summary>
    public TerminalSessionViewModel TerminalSession { get; }

    public override async ValueTask DisposeAsync()
    {
        await this.session.DisposeAsync();
        await base.DisposeAsync();
    }
}
