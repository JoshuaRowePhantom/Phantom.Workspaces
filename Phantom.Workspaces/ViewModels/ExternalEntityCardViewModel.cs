using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Phantom.Workspaces.ViewModels;

public sealed class ExternalUrlViewModel
{
    public ExternalUrlViewModel(string key, string url, bool showKey)
    {
        this.Key = key;
        this.Url = url;
        this.ShowKey = showKey;
        this.OpenCommand = new RelayCommand(_ =>
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch (Exception)
            {
                // Best-effort: if the URL cannot be opened, silently ignore.
            }
        });
    }

    public string Key { get; }

    public string Url { get; }

    public bool ShowKey { get; }

    public RelayCommand OpenCommand { get; }
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
    public static ExternalEntityCardViewModel Create(SubscribedEntityViewModel entity)
    {
        var urlMap = OpenExternalEntityShortcutHandler.ParseUrls(entity);
        bool suppressKey = urlMap.Count == 1 && urlMap.ContainsKey("default");
        var urls = urlMap
            .Select(kvp => new ExternalUrlViewModel(kvp.Key, kvp.Value, showKey: !suppressKey))
            .ToArray();
        return new ExternalEntityCardViewModel(urls);
    }
}
