using System;
using System.ComponentModel;
using Phantom.Workspaces.Configuration;

namespace Phantom.Workspaces.ViewModels.Configuration;

/// <summary>
/// Editable view model for the repository data-access connection settings. Holds a sub-view-model
/// per connection type; the GUI binds to <see cref="ActiveSettings"/> (resolved by subtype).
/// </summary>
public sealed class RepositoryConnectionSettingsViewModel : ViewModelBase
{
    private DataAccessMode mode;

    /// <summary>Creates a view model with default settings.</summary>
    public RepositoryConnectionSettingsViewModel()
        : this(new DataAccessConnectionProfile())
    {
    }

    /// <summary>Creates a view model initialized from an existing profile.</summary>
    public RepositoryConnectionSettingsViewModel(DataAccessConnectionProfile profile)
        : this(profile, sharedRemoteAccess: null)
    {
    }

    /// <summary>
    /// Creates a view model initialized from an existing profile, optionally sharing the
    /// <see cref="DevTunnelWebSettingsViewModel.TunnelName"/> with the wizard/settings-level
    /// <see cref="RemoteAccessSettingsViewModel"/>. Used by the wizard so its DevTunnelWeb sub-view
    /// and the Remote-access section observe the same underlying tunnel name.
    /// </summary>
    public RepositoryConnectionSettingsViewModel(
        DataAccessConnectionProfile profile,
        RemoteAccessSettingsViewModel? sharedRemoteAccess)
    {
        ArgumentNullException.ThrowIfNull(profile);
        this.mode = profile.Mode;
        this.LocalMongoContainer = new LocalMongoContainerSettingsViewModel(profile);
        this.RemoteMongo = new RemoteMongoSettingsViewModel(profile);
        this.Web = new WebSettingsViewModel(profile);
        this.DevTunnelWeb = new DevTunnelWebSettingsViewModel(profile, sharedRemoteAccess);

        this.LocalMongoContainer.PropertyChanged += this.OnActiveSettingsChanged;
        this.RemoteMongo.PropertyChanged += this.OnActiveSettingsChanged;
        this.Web.PropertyChanged += this.OnActiveSettingsChanged;
        this.DevTunnelWeb.PropertyChanged += this.OnActiveSettingsChanged;
    }

    /// <summary>The selectable data-access modes for binding.</summary>
    public static DataAccessMode[] AvailableModes { get; } =
    [
        DataAccessMode.LocalMongoContainer,
        DataAccessMode.RemoteMongo,
        DataAccessMode.Web,
        DataAccessMode.DevTunnelWeb,
    ];

    /// <summary>Local MongoDB container settings.</summary>
    public LocalMongoContainerSettingsViewModel LocalMongoContainer { get; }

    /// <summary>Remote MongoDB settings.</summary>
    public RemoteMongoSettingsViewModel RemoteMongo { get; }

    /// <summary>Remote web endpoint settings.</summary>
    public WebSettingsViewModel Web { get; }

    /// <summary>Dev tunnel web endpoint settings.</summary>
    public DevTunnelWebSettingsViewModel DevTunnelWeb { get; }

    /// <summary>The selected data-access mode.</summary>
    public DataAccessMode Mode
    {
        get => this.mode;
        set
        {
            if (this.SetProperty(ref this.mode, value))
            {
                this.RaisePropertyChanged(nameof(this.ActiveSettings));
                this.RaisePropertyChanged(nameof(this.IsValid));
            }
        }
    }

    /// <summary>The sub-view-model for the currently selected mode.</summary>
    public RepositoryConnectionModeViewModel ActiveSettings => this.Mode switch
    {
        DataAccessMode.LocalMongoContainer => this.LocalMongoContainer,
        DataAccessMode.RemoteMongo => this.RemoteMongo,
        DataAccessMode.Web => this.Web,
        DataAccessMode.DevTunnelWeb => this.DevTunnelWeb,
        _ => this.LocalMongoContainer,
    };

    /// <summary>Whether the active connection settings are complete and valid.</summary>
    public bool IsValid => this.ActiveSettings.IsValid;

    /// <summary>Projects the active connection settings into a <see cref="DataAccessConnectionProfile"/>.</summary>
    public DataAccessConnectionProfile ToProfile() => this.ActiveSettings.ToProfile();

    private void OnActiveSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RepositoryConnectionModeViewModel.IsValid)
            && ReferenceEquals(sender, this.ActiveSettings))
        {
            this.RaisePropertyChanged(nameof(this.IsValid));
        }
    }
}
