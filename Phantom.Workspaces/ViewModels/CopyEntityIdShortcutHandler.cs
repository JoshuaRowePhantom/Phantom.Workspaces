using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;

namespace Phantom.Workspaces.ViewModels;

public sealed class CopyEntityIdShortcutHandler : ShortcutHandler
{
    // Copies the given text to the clipboard. Returns true on success, false when no clipboard
    // is available (e.g. no top-level window yet). Injected so tests can capture the payload —
    // Avalonia's IClipboard is not implementable by user code, so the seam is a delegate.
    private readonly Func<string, Task<bool>> copyTextAsync;

    public CopyEntityIdShortcutHandler()
        : this(DefaultCopyTextAsync)
    {
    }

    // Test seam.
    public CopyEntityIdShortcutHandler(
        Func<string, Task<bool>> copyTextAsync)
    {
        this.copyTextAsync = copyTextAsync
            ?? throw new ArgumentNullException(nameof(copyTextAsync));
    }

    public override ValueTask<bool> ShouldApplyTo(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
        // Every entity has an id — no entity-type gate.
        => ValueTask.FromResult(shortcut == Shortcut.CopyEntityId);

    public override async Task<bool> Handle(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        var fragment = $"\"entityid\":\"{entityViewModel.EntityId.Value:D}\"";
        return await this.copyTextAsync(fragment).ConfigureAwait(false);
    }

    private static async Task<bool> DefaultCopyTextAsync(string text)
    {
        var lifetime = Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime;
        var clipboard = lifetime?.MainWindow?.Clipboard;
        if (clipboard is null)
        {
            return false;
        }

        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(text));
        await clipboard.SetDataAsync(data).ConfigureAwait(false);
        return true;
    }
}
