using System;
using System.Windows.Input;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Workspace tab view model for displaying web content in an embedded browser.
/// </summary>
public class WebViewModel : WorkspaceTabViewModel
{
    private string addressBarUrl;
    private Uri? sourceUri;
    private bool canGoBack;
    private bool canGoForward;
    private bool isLoading;
    private string? errorMessage;

    public WebViewModel(string initialUrl)
    {
        this.addressBarUrl = initialUrl;
        this.sourceUri = Uri.TryCreate(initialUrl, UriKind.Absolute, out var uri) ? uri : null;

        this.NavigateCommand = new RelayCommand(_ => this.Navigate());
        this.GoBackCommand = new RelayCommand(_ => this.GoBack(), _ => this.CanGoBack);
        this.GoForwardCommand = new RelayCommand(_ => this.GoForward(), _ => this.CanGoForward);
        this.OpenInExternalBrowserCommand = new RelayCommand(_ => this.OpenInExternalBrowser());
    }

    public string AddressBarUrl
    {
        get => this.addressBarUrl;
        set => this.SetProperty(ref this.addressBarUrl, value);
    }

    public Uri? SourceUri
    {
        get => this.sourceUri;
        set => this.SetProperty(ref this.sourceUri, value);
    }

    public bool CanGoBack
    {
        get => this.canGoBack;
        set
        {
            if (this.SetProperty(ref this.canGoBack, value))
            {
                (this.GoBackCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanGoForward
    {
        get => this.canGoForward;
        set
        {
            if (this.SetProperty(ref this.canGoForward, value))
            {
                (this.GoForwardCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsLoading
    {
        get => this.isLoading;
        set => this.SetProperty(ref this.isLoading, value);
    }

    public string? ErrorMessage
    {
        get => this.errorMessage;
        set
        {
            this.SetProperty(ref this.errorMessage, value);
            this.RaisePropertyChanged(nameof(this.HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(this.ErrorMessage);

    public ICommand NavigateCommand { get; }
    public ICommand GoBackCommand { get; }
    public ICommand GoForwardCommand { get; }
    public ICommand OpenInExternalBrowserCommand { get; }

    private void Navigate()
    {
        if (Uri.TryCreate(this.AddressBarUrl, UriKind.Absolute, out var uri))
        {
            this.SourceUri = uri;
        }
    }

    private void GoBack()
    {
        // WebView control should handle this via binding
        this.RaiseNavigationRequested(NavigationDirection.Back);
    }

    private void GoForward()
    {
        // WebView control should handle this via binding
        this.RaiseNavigationRequested(NavigationDirection.Forward);
    }

    private void OpenInExternalBrowser()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = this.AddressBarUrl,
                UseShellExecute = true,
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            this.ErrorMessage = $"Failed to open browser: {ex.Message}";
        }
    }

    public event EventHandler<NavigationDirection>? NavigationRequested;

    private void RaiseNavigationRequested(NavigationDirection direction)
    {
        this.NavigationRequested?.Invoke(this, direction);
    }

    public void UpdateCurrentUrl(string url)
    {
        this.AddressBarUrl = url;
    }
}

public enum NavigationDirection
{
    Back,
    Forward
}
