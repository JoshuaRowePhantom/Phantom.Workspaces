using System.Runtime.CompilerServices;
using Phantom.Workspaces.Configuration;

namespace Phantom.Workspaces.ViewModels.Configuration;

/// <summary>
/// Base class for a repository connection settings sub-view-model representing a single
/// <see cref="DataAccessMode"/>. The GUI binds to the concrete subtype.
/// </summary>
public abstract class RepositoryConnectionModeViewModel : ViewModelBase
{
    /// <summary>The data-access mode this sub-view-model represents.</summary>
    public abstract DataAccessMode Mode { get; }

    /// <summary>Whether the current settings are complete and valid for this mode.</summary>
    public abstract bool IsValid { get; }

    /// <summary>Projects the current settings into a <see cref="DataAccessConnectionProfile"/>.</summary>
    public abstract DataAccessConnectionProfile ToProfile();

    /// <summary>Sets a backing field and raises <see cref="IsValid"/> when it changes.</summary>
    protected void SetValidatedProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (this.SetProperty(ref field, value, propertyName))
        {
            this.RaisePropertyChanged(nameof(this.IsValid));
        }
    }
}
