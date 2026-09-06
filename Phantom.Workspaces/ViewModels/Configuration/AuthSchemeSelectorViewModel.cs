using System;
using System.ComponentModel;
using Phantom.Workspaces.Configuration;

namespace Phantom.Workspaces.ViewModels.Configuration;

/// <summary>
/// Polymorphic auth-scheme selector (issue #1457) mirroring
/// <see cref="RepositoryConnectionSettingsViewModel"/>: it holds one sub-view-model per scheme and the
/// GUI binds to <see cref="ActiveScheme"/> (resolved by <see cref="Scheme"/>). Projects the chosen
/// scheme into a <see cref="RemoteAuthentication"/> for the dev-tunnel / web connection config.
/// </summary>
public sealed class AuthSchemeSelectorViewModel : ViewModelBase
{
    private string scheme;

    /// <summary>Creates a selector defaulting to the <c>github</c> scheme.</summary>
    public AuthSchemeSelectorViewModel()
        : this(null)
    {
    }

    /// <summary>
    /// Creates a selector initialized from an existing <see cref="RemoteAuthentication"/>. When
    /// <paramref name="authentication"/> is <see langword="null"/> (legacy <c>useGitHubAuthToken</c> or
    /// absent auth), the <c>github</c> scheme is selected.
    /// </summary>
    public AuthSchemeSelectorViewModel(RemoteAuthentication? authentication)
    {
        var resolved = authentication ?? new RemoteAuthentication(RemoteAuthentication.GithubScheme);
        var resolvedScheme = NormalizeScheme(resolved.Scheme);

        this.GitHub = new GitHubAuthSchemeViewModel();
        this.Anonymous = new AnonymousAuthSchemeViewModel();
        this.Entra = resolvedScheme == RemoteAuthentication.EntraScheme
            ? new EntraAuthSchemeViewModel(resolved)
            : new EntraAuthSchemeViewModel();
        this.OAuth = resolvedScheme == RemoteAuthentication.OAuthScheme
            ? new OAuthAuthSchemeViewModel(resolved)
            : new OAuthAuthSchemeViewModel();
        this.scheme = resolvedScheme;

        this.GitHub.PropertyChanged += this.OnActiveSchemeChanged;
        this.Anonymous.PropertyChanged += this.OnActiveSchemeChanged;
        this.Entra.PropertyChanged += this.OnActiveSchemeChanged;
        this.OAuth.PropertyChanged += this.OnActiveSchemeChanged;
    }

    /// <summary>The selectable auth schemes for binding.</summary>
    public static string[] AvailableSchemes { get; } =
    [
        RemoteAuthentication.GithubScheme,
        RemoteAuthentication.EntraScheme,
        RemoteAuthentication.OAuthScheme,
        RemoteAuthentication.AnonymousScheme,
    ];

    /// <summary>GitHub scheme settings.</summary>
    public GitHubAuthSchemeViewModel GitHub { get; }

    /// <summary>Entra scheme settings.</summary>
    public EntraAuthSchemeViewModel Entra { get; }

    /// <summary>OAuth scheme settings.</summary>
    public OAuthAuthSchemeViewModel OAuth { get; }

    /// <summary>Anonymous scheme settings.</summary>
    public AnonymousAuthSchemeViewModel Anonymous { get; }

    /// <summary>The selected auth scheme.</summary>
    public string Scheme
    {
        get => this.scheme;
        set
        {
            if (this.SetProperty(ref this.scheme, NormalizeScheme(value)))
            {
                this.RaisePropertyChanged(nameof(this.ActiveScheme));
                this.RaisePropertyChanged(nameof(this.IsValid));
                this.RaisePropertyChanged(nameof(this.ValidationMessage));
            }
        }
    }

    /// <summary>The sub-view-model for the currently selected scheme.</summary>
    public AuthSchemeViewModel ActiveScheme => this.scheme switch
    {
        RemoteAuthentication.EntraScheme => this.Entra,
        RemoteAuthentication.OAuthScheme => this.OAuth,
        RemoteAuthentication.AnonymousScheme => this.Anonymous,
        _ => this.GitHub,
    };

    /// <summary>Whether the active scheme's settings are complete and valid.</summary>
    public bool IsValid => this.ActiveScheme.IsValid;

    /// <summary>The active scheme's validation message, or <see langword="null"/> when valid.</summary>
    public string? ValidationMessage => this.ActiveScheme.ValidationMessage;

    /// <summary>Projects the active scheme into a <see cref="RemoteAuthentication"/>.</summary>
    public RemoteAuthentication ToRemoteAuthentication() => this.ActiveScheme.ToRemoteAuthentication();

    private static string NormalizeScheme(string? value)
        => (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            RemoteAuthentication.EntraScheme => RemoteAuthentication.EntraScheme,
            RemoteAuthentication.OAuthScheme => RemoteAuthentication.OAuthScheme,
            RemoteAuthentication.AnonymousScheme => RemoteAuthentication.AnonymousScheme,
            _ => RemoteAuthentication.GithubScheme,
        };

    private void OnActiveSchemeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AuthSchemeViewModel.IsValid)
            && ReferenceEquals(sender, this.ActiveScheme))
        {
            this.RaisePropertyChanged(nameof(this.IsValid));
            this.RaisePropertyChanged(nameof(this.ValidationMessage));
        }
    }
}
