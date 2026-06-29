using System;
using Avalonia.Controls;

namespace Phantom.Workspaces.Gui.Shared.Controls;

/// <summary>
/// Creates the browser surface used by chat output. Defaults to a real
/// <see cref="ControllableWebViewControl"/>; headless test hosts replace <see cref="Create"/> with a
/// stub because a native WebView throws when attached under the Avalonia headless platform. The
/// returned control must implement <see cref="IControllableBrowser"/>.
/// </summary>
public static class ControllableBrowserFactory
{
    /// <summary>
    /// Factory for the browser control. The returned <see cref="Control"/> must also implement
    /// <see cref="IControllableBrowser"/>.
    /// </summary>
    public static Func<Control> Create { get; set; } = static () => new ControllableWebViewControl();
}
