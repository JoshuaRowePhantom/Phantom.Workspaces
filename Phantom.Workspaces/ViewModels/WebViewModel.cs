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
    private readonly bool titleFixed;
    private readonly FaviconTabHeaderItemViewModel faviconItem;

    public WebViewModel(string initialUrl, IWorkspaceTabService? tabService = null, bool titleFixed = false)
    {
        this.titleFixed = titleFixed;
        this.addressBarUrl = initialUrl;
        this.currentUrl = initialUrl;
        this.sourceUri = Uri.TryCreate(initialUrl, UriKind.Absolute, out var uri) ? uri : null;
        this.tabService = tabService;

        this.HomeUrl = string.IsNullOrEmpty(initialUrl) ? null : initialUrl;

        this.NavigateCommand = new RelayCommand(_ => this.Navigate());
        this.GoBackCommand = new RelayCommand(_ => this.GoBack(), _ => this.CanGoBack);
        this.GoForwardCommand = new RelayCommand(_ => this.GoForward(), _ => this.CanGoForward);
        this.OpenInExternalBrowserCommand = new RelayCommand(_ => this.OpenInExternalBrowser());
        this.NavigateHomeCommand = new RelayCommand(_ => this.NavigateHome(), _ => this.HomeUrl != null);
        this.FocusUrlBarCommand = new RelayCommand(_ => this.RaiseFocusUrlBarRequested());

        this.faviconItem = new FaviconTabHeaderItemViewModel();
        this.TabHeader = new TabHeaderViewModel { Title = string.Empty };
        this.TabHeader.Items.Add(this.faviconItem);

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

    public string? HomeUrl { get; }

    public bool HasHomeUrl => this.HomeUrl != null;

    public string HomeUrlTooltip => this.HomeUrl is { Length: > 0 }
        ? $"Go to home page\n{this.HomeUrl}"
        : "Go to home page";

    public ICommand NavigateCommand { get; }
    public ICommand GoBackCommand { get; }
    public ICommand GoForwardCommand { get; }
    public ICommand OpenInExternalBrowserCommand { get; }
    public ICommand NavigateHomeCommand { get; }
    public ICommand FocusUrlBarCommand { get; }

    private void Navigate()
    {
        if (Uri.TryCreate(this.AddressBarUrl, UriKind.Absolute, out var uri))
        {
            this.SourceUri = uri;
        }
    }

    private void NavigateHome()
    {
        if (this.HomeUrl == null)
        {
            return;
        }

        this.AddressBarUrl = this.HomeUrl;
        if (Uri.TryCreate(this.HomeUrl, UriKind.Absolute, out var uri))
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
    public event EventHandler<int>? GoToTabAtIndexRequested;
    public event EventHandler<bool>? AltKeyStateChanged;
    public event EventHandler? FocusUrlBarRequested;

    private void RaiseNavigationRequested(NavigationDirection direction)
    {
        this.NavigationRequested?.Invoke(this, direction);
    }

    private void RaiseFocusUrlBarRequested()
    {
        this.FocusUrlBarRequested?.Invoke(this, EventArgs.Empty);
    }

    public void RaiseGoToTabAtIndex(int index)
    {
        this.GoToTabAtIndexRequested?.Invoke(this, index);
    }

    public void RaiseAltKeyStateChanged(bool isAltHeld)
    {
        this.AltKeyStateChanged?.Invoke(this, isAltHeld);
    }

    public void RaiseCloseTab()
    {
        this.tabService?.CloseTab(this);
    }

    public async void RaiseOpenNewWindow(string url)
    {
        if (this.tabService == null)
        {
            return;
        }
        
        // Create a new WebViewModel for the requested URL
        var newTab = new WebViewModel(url, this.tabService)
        {
            Id = $"web-{Guid.NewGuid()}",
            Title = url, // Will be updated when page loads
            DockRegion = this.DockRegion, // Open in same region
        };

        await this.tabService.OpenTabAsync(newTab, insertAfterTabId: this.Id);
    }

    public void UpdateCurrentUrl(string url)
    {
        this.AddressBarUrl = url;
        this.currentUrl = url;
        UpdateTooltip();
    }
    
    public void SetFaviconUri(string? uri)
    {
        this.faviconItem.FaviconUri = uri;
    }

    public void SetPageTitle(string pageTitle)
    {
        this.fullTitle = pageTitle;
        if (!this.titleFixed)
        {
            this.Title = pageTitle;
        }
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
