using Avalonia.Controls;

namespace Phantom.Workspaces.Controls;

/// <summary>
/// Factory for creating <see cref="ConfiguredWebView"/> instances.
/// In headless test environments, this can be swapped to return a no-op stub that does not
/// perform real network navigation.
/// </summary>
public static class ConfiguredWebViewFactory
{
    /// <summary>
    /// Factory function for creating the web view control.
    /// Default: returns a real <see cref="ConfiguredWebView"/>.
    /// Tests: can be set to return a headless stub.
    /// </summary>
    public static Func<Control> Create { get; set; } = static () => new ConfiguredWebView();
}
