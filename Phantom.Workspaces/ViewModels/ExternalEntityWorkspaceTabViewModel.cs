using System;
using System.Windows.Input;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Services;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Workspace tab view model for displaying external entity URLs in an embedded browser.
/// </summary>
public class ExternalEntityWorkspaceTabViewModel : WorkspaceTabViewModel
{
    private bool isLoading;
    private string? currentUrl;
    private string? errorMessage;

    public ExternalEntityWorkspaceTabViewModel(
        SubscribedEntityViewModel entity,
        string urlKey,
        string url,
        IUrlOpener? urlOpener = null)
    {
        this.UrlKey = urlKey;
        this.Url = url;
        this.CurrentUrl = url;
        this.urlOpener = urlOpener;

        this.OpenInExternalBrowserCommand = new RelayCommand(
            _ => this.OpenInExternalBrowser());
    }

    private readonly IUrlOpener? urlOpener;

    public string UrlKey { get; }
    
    public string Url { get; }

    public Uri? SourceUri => Uri.TryCreate(this.Url, UriKind.Absolute, out var uri) ? uri : null;

    public bool IsLoading
    {
        get => this.isLoading;
        set => this.SetProperty(ref this.isLoading, value);
    }

    public string? CurrentUrl
    {
        get => this.currentUrl;
        set => this.SetProperty(ref this.currentUrl, value);
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

    public ICommand OpenInExternalBrowserCommand { get; }

    private void OpenInExternalBrowser()
    {
        // #1172: route through IUrlOpener when available.
        if (this.urlOpener is not null)
        {
            _ = this.urlOpener.OpenAsync(new OpenUrlRequest(this.Url)
            {
                Preference = UrlOpenPreference.External,
            });
            return;
        }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = this.Url,
                UseShellExecute = true,
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            this.ErrorMessage = $"Failed to open browser: {ex.Message}";
        }
    }
}
