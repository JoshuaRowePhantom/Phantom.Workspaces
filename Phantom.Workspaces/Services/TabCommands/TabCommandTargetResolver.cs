using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Services.TabCommands;

/// <summary>
/// Side-effect-free resolver that maps a tab-command invocation origin onto the exact live
/// <see cref="TabCommandContext"/>. Explicit origins (a clicked/known tab) win; otherwise the
/// invoking top level's focused element is walked up to its owning document-tab container. This
/// is the shared invocation-origin seam: it introduces no global current-tab tracker and defers
/// real focus-ownership proof to the #1498 resolver when that lands. Resolution never mutates
/// selection or focus and never falls through to the active/selected tab when the supplied
/// origin is invalid.
/// </summary>
public static class TabCommandTargetResolver
{
    /// <summary>
    /// Attempts to resolve a keyboard/on-demand command target. When <paramref name="explicitOrigin"/>
    /// is supplied it is resolved to its registered document; otherwise the current focus
    /// (<paramref name="focusedElement"/>) is walked up to the owning document-tab container. A
    /// supplied-but-invalid origin rejects rather than retargeting another tab.
    /// </summary>
    public static bool TryResolveKeyboardTarget(
        MainWindowViewModel mainWindow,
        WorkspaceTabViewModel? explicitOrigin,
        Visual? focusedElement,
        out TabCommandContext? context)
    {
        ArgumentNullException.ThrowIfNull(mainWindow);

        context = null;

        if (explicitOrigin is not null)
        {
            return TabCommandContext.TryCreateForKeyboardOrigin(mainWindow, explicitOrigin, out context);
        }

        if (focusedElement is not null
            && TryResolveDocumentFromFocus(focusedElement, out var document)
            && document is not null)
        {
            return TabCommandContext.TryCreateForExplicitTarget(mainWindow, document, out context);
        }

        return false;
    }

    private static bool TryResolveDocumentFromFocus(Visual focusedElement, out WorkspaceDocument? document)
    {
        document = null;
        foreach (var ancestor in focusedElement.GetSelfAndVisualAncestors())
        {
            if (ancestor is DocumentTabStripItem { DataContext: WorkspaceDocument tabDocument })
            {
                document = tabDocument;
                return true;
            }

            if (ancestor is Control { DataContext: WorkspaceDocument control })
            {
                document = control;
                return true;
            }
        }

        return false;
    }
}
