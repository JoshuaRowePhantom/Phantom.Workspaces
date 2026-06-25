using System;

namespace Phantom.Workspaces.Gui.Styles.Controls;

/// <summary>
/// The bridge contract for a browser-hosted UI surface: load a static HTML shell, run startup scripts
/// once it is ready, and exchange messages with the page. <see cref="ControllableWebViewControl"/> is
/// the production implementation; headless test hosts substitute a no-op implementation through
/// <see cref="ControllableBrowserFactory"/> because a native WebView cannot attach under the Avalonia
/// headless platform.
/// </summary>
public interface IControllableBrowser
{
    /// <summary>The static HTML shell to load into the page.</summary>
    string? HtmlShell { get; set; }

    /// <summary>Raised once the shell has loaded and the startup scripts have run.</summary>
    event EventHandler? Ready;

    /// <summary>Raised when the page posts a message back to the host.</summary>
    event EventHandler<string>? JavaScriptMessageReceived;

    /// <summary>Registers a script to run immediately after the shell loads (in registration order).</summary>
    void AddStartupScript(string script);

    /// <summary>Delivers a message into the page, queueing until the shell is ready.</summary>
    void PostMessageToJavaScript(string message);
}
