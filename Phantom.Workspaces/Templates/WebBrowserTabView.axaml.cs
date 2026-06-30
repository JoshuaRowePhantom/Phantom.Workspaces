using Avalonia.Controls;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Templates;

public partial class WebBrowserTabView : UserControl
{
    public WebBrowserTabView()
    {
        this.InitializeComponent();
        this.DataContextChanged += OnDataContextChanged;
    }

    private WebViewModel? subscribedViewModel;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (this.subscribedViewModel != null)
        {
            this.subscribedViewModel.FocusUrlBarRequested -= OnFocusUrlBarRequested;
            this.subscribedViewModel = null;
        }

        if (this.DataContext is WebViewModel viewModel)
        {
            this.subscribedViewModel = viewModel;
            this.subscribedViewModel.FocusUrlBarRequested += OnFocusUrlBarRequested;
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
