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

    /// <summary>
    /// Whether the current settings are complete and valid for this mode. Derived from
    /// <see cref="ValidationMessage"/> so the message and the gate share a single predicate:
    /// a mode is valid iff it has no validation message. Subclasses only implement
    /// <see cref="ValidationMessage"/> — do NOT override this property.
    /// </summary>
    public bool IsValid => this.ValidationMessage is null;

    /// <summary>
    /// Short human-readable description of this mode for the setup wizard. Rendered under the
    /// mode selector so users can understand what each mode does before choosing.
    /// </summary>
    public abstract string Description { get; }

    /// <summary>
    /// Human-readable message naming the missing required field for this mode when
    /// <see cref="IsValid"/> is <see langword="false"/>; <see langword="null"/> otherwise.
    /// Used by both the wizard and Settings dialog to explain why saving is disabled.
    /// </summary>
    public abstract string? ValidationMessage { get; }

    /// <summary>Projects the current settings into a <see cref="DataAccessConnectionProfile"/>.</summary>
    public abstract DataAccessConnectionProfile ToProfile();

    /// <summary>Sets a backing field and raises <see cref="IsValid"/> when it changes.</summary>
    protected void SetValidatedProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (this.SetProperty(ref field, value, propertyName))
        {
            this.RaisePropertyChanged(nameof(this.IsValid));
            this.RaisePropertyChanged(nameof(this.ValidationMessage));
        }
    }
}
