using System;
using System.Text.RegularExpressions;
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
        // Persist legacy Token configs as Private — Token is retired; connect tokens are automatic.
#pragma warning disable CS0618 // Token is obsolete
        this.devTunnelAccessMode = devTunnel.AccessMode == DevTunnelAccessMode.Token
#pragma warning restore CS0618
            ? DevTunnelAccessMode.Private
            : devTunnel.AccessMode;
        this.tunnelName = devTunnel.TunnelName;
        this.userComputerProfileOverride = userComputerProfileOverride;
    }

    /// <summary>The selectable dev tunnel access modes for binding.</summary>
    public static DevTunnelAccessMode[] AvailableAccessModes { get; } =
    [
        DevTunnelAccessMode.Private,
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
    /// Helper text describing the "auto" tunnel-name discovery convention used by
    /// <see cref="Services.DevTunnel.DevTunnelNaming.IsAuto"/>. Rendered under the
    /// Dev tunnel name field in both the setup wizard and Settings dialog so both
    /// surfaces present the same explanation of the "auto" selector.
    /// </summary>
    public const string TunnelNameHelperText =
        "Name of the dev tunnel to host/connect by; the forwarded port is discovered automatically. When connecting, use \"auto\" to discover the single Workspaces tunnel without naming it.";

    /// <summary>
    /// Instance-property projection of <see cref="TunnelNameHelperText"/> so AXAML can bind to the
    /// single source of truth without repeating the literal string. Both the setup wizard and the
    /// Settings dialog helper TextBlock bind to this property.
    /// </summary>
    public string TunnelNameHelperTextValue => TunnelNameHelperText;

    /// <summary>
    /// Human-readable message naming which remote-access field is invalid when
    /// <see cref="IsValid"/> is <see langword="false"/>; <see langword="null"/> otherwise.
    /// </summary>
    public string? ValidationMessage =>
        this.HostingEnabled && !IsAcceptableListenUrl(this.ListenUrl)
            ? "Listen URL must be a valid absolute URL, or a wildcard binding such as http://*:5280 or http://+:5280, when hosting is enabled."
            : null;

    /// <summary>
    /// Regex matching the ASP.NET Core / Kestrel wildcard host forms that
    /// <see cref="Uri.TryCreate(string?, UriKind, out Uri?)"/> rejects: <c>http://*:port</c> and
    /// <c>http://+:port</c> (both bind all IPv4 + IPv6 via IPv6Any dual-mode). The port is optional
    /// and, when present, must be 1-5 digits to mirror what Kestrel's address binder accepts.
    /// </summary>
    private static readonly Regex WildcardListenUrlRegex = new(
        @"^(?<scheme>https?)://(?<host>\*|\+)(?::(?<port>\d{1,5}))?/?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Whether <paramref name="url"/> is an acceptable Kestrel Listen URL: either a normal absolute
    /// http/https URI (the historical rule) or one of the wildcard host forms <c>http://*:port</c> /
    /// <c>http://+:port</c> that Kestrel supports but <see cref="Uri.TryCreate(string?, UriKind, out Uri?)"/>
    /// rejects. The wildcard acceptance is scoped narrowly to the single-token <c>*</c> / <c>+</c> hosts;
    /// genuinely malformed strings are still rejected.
    /// </summary>
    private static bool IsAcceptableListenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            return true;
        }

        return WildcardListenUrlRegex.IsMatch(url);
    }

    /// <summary>
    /// Whether the current settings are valid. Derived from <see cref="ValidationMessage"/> so
    /// the gate and the message share a single predicate: settings are valid iff there is no
    /// validation message.
    /// </summary>
    public bool IsValid => this.ValidationMessage is null;

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
    };

    private void SetValidatedProperty<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (this.SetProperty(ref field, value, propertyName))
        {
            this.RaisePropertyChanged(nameof(this.IsValid));
            this.RaisePropertyChanged(nameof(this.ValidationMessage));
            this.RaisePropertyChanged(nameof(this.IsAnonymousAccessWarningVisible));
        }
    }
}
