using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Phantom.Workspaces.Controls;
using Phantom.Workspaces.Gui.Shared.Controls;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Templates;

public partial class WebBrowserTabView : UserControl
{
    private readonly Control webViewControl;

    public WebBrowserTabView()
    {
        this.InitializeComponent();

        // Use factory to create ConfiguredWebView (or headless stub in tests)
        this.webViewControl = ConfiguredWebViewFactory.Create();
        
        // Find the WebViewHost Border and set its child to the factory-created control
        var webViewHost = this.FindControl<Border>("WebViewHost");
        if (webViewHost != null)
        {
            webViewHost.Child = this.webViewControl;
        }

        // Leave Ctrl+F for the hosted page's in-page find rather than capturing it for the app's
        // global entity-find bar (issue #1255). The universal SharedStyles selector already enables
        // BrowserAcceleratorBehavior forwarding on the underlying AcceleratorAwareWebView.
        BrowserAcceleratorBehavior.SetNonCapturedAcceleratorKeys(this.webViewControl, new System.Collections.Generic.List<KeyGesture>
        {
            new(Key.F, KeyModifiers.Control),
        });

        this.DataContextChanged += OnDataContextChanged;
    }

    private WebViewModel? subscribedViewModel;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (this.subscribedViewModel != null)
        {
            this.subscribedViewModel.FocusUrlBarRequested -= OnFocusUrlBarRequested;
            this.subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            this.subscribedViewModel = null;
        }

        if (this.DataContext is WebViewModel viewModel)
        {
            this.subscribedViewModel = viewModel;
            this.subscribedViewModel.FocusUrlBarRequested += OnFocusUrlBarRequested;
            this.subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;

            // Set initial values on the web view control
            UpdateWebViewProperties();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Update web view properties when ViewModel properties change
        if (e.PropertyName == nameof(WebViewModel.SourceUri))
        {
            UpdateWebViewProperties();
        }
    }

    private void UpdateWebViewProperties()
    {
        if (this.subscribedViewModel == null || this.webViewControl == null)
        {
            return;
        }

        // Set ViewModel property if it exists
        var viewModelProperty = this.webViewControl.GetType().GetProperty("ViewModel");
        if (viewModelProperty != null && viewModelProperty.CanWrite)
        {
            viewModelProperty.SetValue(this.webViewControl, this.subscribedViewModel);
        }

        // Set Source property if it exists
        var sourceProperty = this.webViewControl.GetType().GetProperty("Source");
        if (sourceProperty != null && sourceProperty.CanWrite)
        {
            sourceProperty.SetValue(this.webViewControl, this.subscribedViewModel.SourceUri);
        }
    }

    private void OnFocusUrlBarRequested(object? sender, EventArgs e)
    {
        var addressBar = this.FindControl<TextBox>("AddressBar");
        if (addressBar != null)
        {
            addressBar.Focus();
            addressBar.SelectAll();
        }
    }
}
