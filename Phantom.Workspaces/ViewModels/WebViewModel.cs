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
    private string fullTitle = string.Empty;
    private string currentUrl = string.Empty;

    private readonly IWorkspaceTabService? tabService;

    public WebViewModel(string initialUrl, IWorkspaceTabService? tabService = null)
    {
        this.addressBarUrl = initialUrl;
        this.currentUrl = initialUrl;
        this.sourceUri = Uri.TryCreate(initialUrl, UriKind.Absolute, out var uri) ? uri : null;
        this.tabService = tabService;

        this.NavigateCommand = new RelayCommand(_ => this.Navigate());
        this.GoBackCommand = new RelayCommand(_ => this.GoBack(), _ => this.CanGoBack);
        this.GoForwardCommand = new RelayCommand(_ => this.GoForward(), _ => this.CanGoForward);
        this.OpenInExternalBrowserCommand = new RelayCommand(_ => this.OpenInExternalBrowser());
        
        UpdateTooltip();
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
            System.Diagnostics.Debug.WriteLine($"[WebViewModel] CanGoBack set to {value}");
            if (this.SetProperty(ref this.canGoBack, value))
            {
                (this.GoBackCommand as RelayCommand)?.RaiseCanExecuteChanged();
                System.Diagnostics.Debug.WriteLine($"[WebViewModel] Raised CanExecuteChanged for GoBackCommand");
            }
        }
    }

    public bool CanGoForward
    {
        get => this.canGoForward;
        set
        {
            System.Diagnostics.Debug.WriteLine($"[WebViewModel] CanGoForward set to {value}");
            if (this.SetProperty(ref this.canGoForward, value))
            {
                (this.GoForwardCommand as RelayCommand)?.RaiseCanExecuteChanged();
                System.Diagnostics.Debug.WriteLine($"[WebViewModel] Raised CanExecuteChanged for GoForwardCommand");
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
        System.Diagnostics.Debug.WriteLine($"[WebViewModel] GoBack called, CanGoBack={this.CanGoBack}");
        // WebView control should handle this via binding
        this.RaiseNavigationRequested(NavigationDirection.Back);
    }

    private void GoForward()
    {
        System.Diagnostics.Debug.WriteLine($"[WebViewModel] GoForward called, CanGoForward={this.CanGoForward}");
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

    public async void RaiseOpenNewWindow(string url)
    {
        System.Diagnostics.Debug.WriteLine($"[WebViewModel] RaiseOpenNewWindow: {url}");
        
        if (this.tabService == null)
        {
            System.Diagnostics.Debug.WriteLine($"[WebViewModel] No tab service available");
            return;
        }
        
        // Create a new WebViewModel for the requested URL
        var newTab = new WebViewModel(url, this.tabService)
        {
            Id = $"web-{Guid.NewGuid()}",
            Title = url, // Will be updated when page loads
            DockRegion = this.DockRegion, // Open in same region
        };

        System.Diagnostics.Debug.WriteLine($"[WebViewModel] Opening new tab via service: {newTab.Id}");
        await this.tabService.OpenTabAsync(newTab);
    }

    public void UpdateCurrentUrl(string url)
    {
        System.Diagnostics.Debug.WriteLine($"[WebViewModel] UpdateCurrentUrl called: {url}");
        this.AddressBarUrl = url;
        this.currentUrl = url;
        UpdateTooltip();
    }
    
    public void SetPageTitle(string pageTitle)
    {
        System.Diagnostics.Debug.WriteLine($"[WebViewModel] SetPageTitle called: {pageTitle}");
        this.fullTitle = pageTitle;
        this.Title = pageTitle; // Don't truncate here - let WorkspaceDocument handle it
        UpdateTooltip();
    }
    
    private void UpdateTooltip()
    {
        if (!string.IsNullOrEmpty(this.fullTitle))
        {
            this.TabTooltip = $"{this.fullTitle}\n{this.currentUrl}";
        }
        else
        {
            this.TabTooltip = this.currentUrl;
        }
    }
}

public enum NavigationDirection
{
    Back,
    Forward
}
