using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Phantom.Workspaces.Services;

namespace Phantom.Workspaces.ViewModels;

public sealed class ExternalUrlViewModel : ViewModelBase
{
    private readonly Func<IUrlOpener?>? urlOpenerProvider;
    private string? errorMessage;

    public ExternalUrlViewModel(string key, string url, bool showKey, Func<IUrlOpener?>? urlOpenerProvider = null)
    {
        this.Key = key;
        this.Url = url;
        this.ShowKey = showKey;
        this.urlOpenerProvider = urlOpenerProvider;
        if (!IsSupportedUrl(url))
        {
            this.ErrorMessage = "Invalid or unsupported URL.";
        }

        this.OpenCommand = new AsyncRelayCommand(
            _ => this.OpenAsync(),
            _ => IsSupportedUrl(this.Url),
            allowConcurrentExecutions: false);
    }

    public string Key { get; }

    public string Url { get; }

    public bool ShowKey { get; }

    public AsyncRelayCommand OpenCommand { get; }

    public string? ErrorMessage
    {
        get => this.errorMessage;
        private set
        {
            if (this.SetProperty(ref this.errorMessage, value))
            {
                this.RaisePropertyChanged(nameof(this.HasError));
            }
        }
    }

    public bool HasError => this.ErrorMessage is not null;

    private static bool IsSupportedUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !uri.IsWellFormedOriginalString())
        {
            return false;
        }

        return uri.Scheme switch
        {
            "http" or "https" => !string.IsNullOrEmpty(uri.Host) && string.IsNullOrEmpty(uri.UserInfo),
            "mailto" or "tel" => !string.IsNullOrWhiteSpace(url[(uri.Scheme.Length + 1)..]),
            _ => false,
        };
    }

    private async Task OpenAsync()
    {
        try
        {
            var opener = this.urlOpenerProvider?.Invoke();
            if (opener is null)
            {
                this.ErrorMessage = "URL opener is unavailable.";
                return;
            }

            await opener.OpenAsync(new OpenUrlRequest(this.Url));
            this.ErrorMessage = null;
        }
        catch (Exception ex)
        {
            this.ErrorMessage = $"Failed to open URL: {ex.Message}";
        }
    }
}

public sealed class ExternalEntityCardViewModel : ViewModelBase
{
    public IReadOnlyList<ExternalUrlViewModel> Urls { get; }

    private ExternalEntityCardViewModel(IReadOnlyList<ExternalUrlViewModel> urls)
    {
        this.Urls = urls;
    }

    /// <summary>
    /// Builds an <see cref="ExternalEntityCardViewModel"/> from the URL map carried by an external entity.
    /// </summary>
    public static ExternalEntityCardViewModel Create(
        SubscribedEntityViewModel entity,
        Func<IUrlOpener?>? urlOpenerProvider = null)
    {
        var urlMap = OpenExternalEntityShortcutHandler.ParseUrls(entity);
        bool suppressKey = urlMap.Count == 1 && urlMap.ContainsKey("default");
        var urls = urlMap
            .Select(kvp => new ExternalUrlViewModel(kvp.Key, kvp.Value, showKey: !suppressKey, urlOpenerProvider))
            .ToArray();
        return new ExternalEntityCardViewModel(urls);
    }
}
