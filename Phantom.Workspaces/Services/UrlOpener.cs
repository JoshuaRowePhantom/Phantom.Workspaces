using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Services;

/// <summary>
/// Default <see cref="IUrlOpener"/> implementation (#1172). Routes http(s) URLs to an embedded
/// <see cref="WebViewModel"/> tab in the currently selected workspace pane (via
/// <see cref="IWorkspaceTabService"/>), and other schemes (mailto:, vscode:, file:, ...) to the
/// supplied external launcher. Embedded opens de-duplicate against the current pane so a repeat
/// click on the same URL activates the already-open tab instead of stacking a new one.
/// </summary>
public sealed class UrlOpener : IUrlOpener
{
    private readonly IWorkspaceTabService tabService;
    private readonly Func<string, Task> externalLauncher;

    /// <summary>
    /// Constructs the opener.
    /// </summary>
    /// <param name="tabService">
    /// The workspace tab service (typically <c>MainWindowViewModel</c>). Used to activate an
    /// existing web tab (same-URL, same-pane dedup) or open a new one.
    /// </param>
    /// <param name="externalLauncher">
    /// Callback used for the External branch. In production this wraps
    /// <c>TopLevel.Launcher.LaunchUriAsync</c> with a <c>Process.Start</c> shell-execute fallback;
    /// tests inject a recorder.
    /// </param>
    public UrlOpener(IWorkspaceTabService tabService, Func<string, Task> externalLauncher)
    {
        this.tabService = tabService ?? throw new ArgumentNullException(nameof(tabService));
        this.externalLauncher = externalLauncher ?? throw new ArgumentNullException(nameof(externalLauncher));
    }

    /// <summary>
    /// Convenience factory for production use: wraps <c>TopLevel.Launcher.LaunchUriAsync</c>
    /// (obtained via <paramref name="topLevelAccessor"/>) with a <c>Process.Start</c>
    /// shell-execute fallback.
    /// </summary>
    public static UrlOpener CreateDefault(
        IWorkspaceTabService tabService,
        Func<Avalonia.Controls.TopLevel?> topLevelAccessor)
    {
        return new UrlOpener(tabService, async url =>
        {
            var top = topLevelAccessor();
            if (top?.Launcher is { } launcher && Uri.TryCreate(url, UriKind.Absolute, out var launchUri))
            {
                await launcher.LaunchUriAsync(launchUri).ConfigureAwait(false);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch
            {
                // Best-effort: leave the failure to the user; nothing else we can do.
            }
        });
    }

    public async Task OpenAsync(OpenUrlRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var url = request.Url;
        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        var isHttp = Uri.TryCreate(url, UriKind.Absolute, out var uri)
                     && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        var goEmbedded = request.Preference switch
        {
            UrlOpenPreference.Embedded => isHttp,   // fall back to External for non-http(s)
            UrlOpenPreference.External => false,
            _ /* Auto */               => isHttp,
        };

        if (goEmbedded)
        {
            // Same-workspace, same-URL dedup: activate an already-open tab if there is one.
            if (await this.tabService.TryFocusExistingWebTabAsync(url).ConfigureAwait(true))
            {
                return;
            }

            var tab = new WebViewModel(url, this.tabService)
            {
                Id = $"web-{Guid.NewGuid():N}",
                Title = url,
            };
            await this.tabService.OpenTabAsync(tab, focus: true).ConfigureAwait(true);
            return;
        }

        await this.externalLauncher(url).ConfigureAwait(true);
    }
}
