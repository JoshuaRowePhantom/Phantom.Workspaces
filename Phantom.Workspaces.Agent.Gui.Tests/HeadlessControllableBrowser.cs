using System;
using Avalonia.Controls;
using Phantom.Workspaces.Gui.Styles.Controls;

namespace Phantom.Workspaces.Agent.Gui.Tests;

/// <summary>
/// A no-op <see cref="IControllableBrowser"/> for the Avalonia headless test harness, which cannot
/// host a native WebView (it throws on attach). Installed via <see cref="ControllableBrowserFactory"/>
/// so headless tests can construct the agent window. Real browser behaviour is covered by the
/// dedicated Win32 WebView integration test project.
/// </summary>
internal sealed class HeadlessControllableBrowser : Decorator, IControllableBrowser
{
    public string? HtmlShell { get; set; }

#pragma warning disable CS0067 // Events are part of the bridge contract; the stub never raises them.
    public event EventHandler? Ready;

    public event EventHandler<string>? JavaScriptMessageReceived;
#pragma warning restore CS0067

    public void AddStartupScript(string script)
    {
    }

    public void PostMessageToJavaScript(string message)
    {
    }
}
