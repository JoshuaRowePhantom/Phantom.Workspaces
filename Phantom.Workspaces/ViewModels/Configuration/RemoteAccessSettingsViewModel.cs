using System;
using Phantom.Workspaces.Configuration;

namespace Phantom.Workspaces.ViewModels.Configuration;

/// <summary>
/// Editable view model for remote-hosting and dev tunnel settings.
/// </summary>
public sealed class RemoteAccessSettingsViewModel : ViewModelBase
{
    private bool hostingEnabled;
    private string listenUrl;
    private bool acceptReverseExecution;
    private DevTunnelAccessMode devTunnelAccessMode;
    private string? devTunnelAccessTokenSource;
    private string? tunnelName;
    private string? userComputerProfileOverride;

    /// <summary>Creates a view model with default settings.</summary>
    public RemoteAccessSettingsViewModel()
        : this(new RemoteHostingSettings(), new DevTunnelConfiguration())
    {
    }

    /// <summary>Creates a view model initialized from existing settings.</summary>
    public RemoteAccessSettingsViewModel(
        RemoteHostingSettings remoteHosting,
        DevTunnelConfiguration devTunnel,
        string? userComputerProfileOverride = null)
    {
        ArgumentNullException.ThrowIfNull(remoteHosting);
        ArgumentNullException.ThrowIfNull(devTunnel);
        this.hostingEnabled = remoteHosting.Enabled;
        this.listenUrl = remoteHosting.ListenUrl;
        this.acceptReverseExecution = remoteHosting.AcceptReverseExecution;
        this.devTunnelAccessMode = devTunnel.AccessMode;
        this.devTunnelAccessTokenSource = devTunnel.AccessTokenSource;
        this.tunnelName = devTunnel.TunnelName;
        this.userComputerProfileOverride = userComputerProfileOverride;
    }

    /// <summary>The selectable dev tunnel access modes for binding.</summary>
    public static DevTunnelAccessMode[] AvailableAccessModes { get; } =
    [
        DevTunnelAccessMode.Private,
        DevTunnelAccessMode.Token,
        DevTunnelAccessMode.Anonymous,
    ];

    /// <summary>Whether this instance exposes the web data-access endpoint.</summary>
    public bool HostingEnabled
    {
        get => this.hostingEnabled;
        set => this.SetValidatedProperty(ref this.hostingEnabled, value);
    }

    /// <summary>The URL the web server binds to when hosting is enabled.</summary>
    public string ListenUrl
    {
        get => this.listenUrl;
        set => this.SetValidatedProperty(ref this.listenUrl, value);
    }

    /// <summary>
    /// Whether this instance accepts reverse-direction trusted execution from connected peers
    /// (any authenticated peer over the tunnel once enabled).
    /// </summary>
    public bool AcceptReverseExecution
    {
        get => this.acceptReverseExecution;
        set => this.SetValidatedProperty(ref this.acceptReverseExecution, value);
    }

    /// <summary>The dev tunnel access mode.</summary>
    public DevTunnelAccessMode DevTunnelAccessMode
    {
        get => this.devTunnelAccessMode;
        set => this.SetValidatedProperty(ref this.devTunnelAccessMode, value);
    }

    /// <summary>Source name for the dev tunnel access token (never the raw token).</summary>
    public string? DevTunnelAccessTokenSource
    {
        get => this.devTunnelAccessTokenSource;
        set => this.SetValidatedProperty(ref this.devTunnelAccessTokenSource, value);
    }

    /// <summary>Friendly tunnel name.</summary>
    public string? TunnelName
    {
        get => this.tunnelName;
        set => this.SetProperty(ref this.tunnelName, value);
    }

    /// <summary>
    /// Testing only: overrides the computer identity used when composing this instance's
    /// user-computer-profile, so a second instance can run on this machine with a distinct profile
    /// (and therefore distinct dev tunnel / MCP-server namespace / sessions). Leave blank for normal
    /// use.
    /// </summary>
    public string? UserComputerProfileOverride
    {
        get => this.userComputerProfileOverride;
        set => this.SetProperty(ref this.userComputerProfileOverride, value);
    }

    /// <summary>
    /// Whether anonymous tunnel access is selected, which should be warned in the UI.
    /// </summary>
    public bool IsAnonymousAccessWarningVisible => this.DevTunnelAccessMode == DevTunnelAccessMode.Anonymous;

    /// <summary>
    /// Whether the dev tunnel access-token-source field should be shown. Only Token mode uses a
    /// pre-shared token; Private (identity) and Anonymous modes do not.
    /// </summary>
    public bool IsAccessTokenSourceVisible => this.DevTunnelAccessMode == DevTunnelAccessMode.Token;

    /// <summary>Whether the current settings are valid.</summary>
    public bool IsValid
    {
        get
        {
            if (this.HostingEnabled && !Uri.TryCreate(this.ListenUrl, UriKind.Absolute, out _))
            {
                return false;
            }

            // Token-based tunnel access requires a token source (not the raw token).
            if (this.DevTunnelAccessMode == DevTunnelAccessMode.Token
                && string.IsNullOrWhiteSpace(this.DevTunnelAccessTokenSource))
            {
                return false;
            }

            return true;
        }
    }

    /// <summary>Projects the current settings into a <see cref="RemoteHostingSettings"/>.</summary>
    public RemoteHostingSettings ToRemoteHostingSettings() => new()
    {
        Enabled = this.HostingEnabled,
        ListenUrl = this.ListenUrl,
        AcceptReverseExecution = this.AcceptReverseExecution,
    };

    /// <summary>Projects the current settings into a <see cref="DevTunnelConfiguration"/>.</summary>
    public DevTunnelConfiguration ToDevTunnelConfiguration(DevTunnelConfiguration existing) => existing with
    {
        TunnelName = this.TunnelName,
        AccessMode = this.DevTunnelAccessMode,
        AccessTokenSource = this.DevTunnelAccessTokenSource,
    };

    private void SetValidatedProperty<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (this.SetProperty(ref field, value, propertyName))
        {
            this.RaisePropertyChanged(nameof(this.IsValid));
            this.RaisePropertyChanged(nameof(this.IsAnonymousAccessWarningVisible));
            this.RaisePropertyChanged(nameof(this.IsAccessTokenSourceVisible));
        }
    }
}
