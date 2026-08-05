using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Services;

/// <summary>
/// Controls how <see cref="IUrlOpener"/> routes a URL between the embedded browser tab
/// pipeline and the external OS launcher. See #1172.
/// </summary>
public enum UrlOpenPreference
{
    /// <summary>
    /// http/https → embedded WebViewModel tab in the current workspace pane (with same-URL dedup);
    /// other schemes (mailto:, vscode:, file:, ...) → external launcher.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Force external OS launcher regardless of scheme. Used by explicit
    /// "Open in External Browser" affordances and crash-time paths.
    /// </summary>
    External = 1,

    /// <summary>
    /// Force embedded WebViewModel tab (with same-URL dedup); falls back to External
    /// if the scheme is not http(s).
    /// </summary>
    Embedded = 2,
}

/// <summary>
/// Request-style envelope for <see cref="IUrlOpener.OpenAsync"/>. Request records over
/// multi-parameter methods per repository preference.
/// </summary>
public sealed record OpenUrlRequest(string Url)
{
    public UrlOpenPreference Preference { get; init; } = UrlOpenPreference.Auto;
}

/// <summary>
/// Canonical URL-opening service (#1172). All in-app "open a URL" call sites route through
/// this seam so the app can decide embedded-vs-external routing centrally, dedup embedded
/// opens against an already-open tab in the current workspace pane, and remain testable.
/// </summary>
public interface IUrlOpener
{
    Task OpenAsync(OpenUrlRequest request, CancellationToken cancellationToken = default);
}
