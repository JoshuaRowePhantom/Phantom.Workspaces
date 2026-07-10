using System;
using Avalonia;
using Avalonia.Controls;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

/// <summary>
/// A no-op <see cref="ConfiguredWebView"/> stub for the Avalonia headless test harness.
/// Ignores Source property assignments and never fires navigation events, preventing tests from
/// making real outbound HTTP requests to example.com or other URLs.
/// </summary>
internal sealed class HeadlessConfiguredWebView : Decorator
{
    public static readonly StyledProperty<WebViewModel?> ViewModelProperty =
        AvaloniaProperty.Register<HeadlessConfiguredWebView, WebViewModel?>(nameof(ViewModel));

    public static readonly StyledProperty<Uri?> SourceProperty =
        AvaloniaProperty.Register<HeadlessConfiguredWebView, Uri?>(nameof(Source));

    public WebViewModel? ViewModel
    {
        get => GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public Uri? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }
}
