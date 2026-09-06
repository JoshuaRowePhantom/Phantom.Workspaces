using System.Runtime.CompilerServices;
using Phantom.Workspaces.Configuration;

namespace Phantom.Workspaces.ViewModels.Configuration;

/// <summary>
/// Base class for a pluggable auth-scheme sub-view-model (issue #1457) representing a single
/// <see cref="RemoteAuthentication"/> scheme (<c>github</c> | <c>entra</c> | <c>oauth</c> |
/// <c>anonymous</c>). Mirrors the <see cref="RepositoryConnectionModeViewModel"/> polymorphic pattern:
/// the GUI binds to the concrete subtype selected by <see cref="AuthSchemeSelectorViewModel.ActiveScheme"/>.
/// </summary>
public abstract class AuthSchemeViewModel : ViewModelBase
{
    /// <summary>The <see cref="RemoteAuthentication.Scheme"/> value this sub-view-model represents.</summary>
    public abstract string Scheme { get; }

    /// <summary>Short human-readable description rendered under the scheme selector.</summary>
    public abstract string Description { get; }

    /// <summary>
    /// Whether the scheme's settings are complete and valid. Derived from
    /// <see cref="ValidationMessage"/> so the message and the gate share a single predicate.
    /// Subclasses only implement <see cref="ValidationMessage"/> — do NOT override this property.
    /// </summary>
    public bool IsValid => this.ValidationMessage is null;

    /// <summary>
    /// Human-readable message naming the missing/invalid field for this scheme when
    /// <see cref="IsValid"/> is <see langword="false"/>; <see langword="null"/> otherwise.
    /// </summary>
    public abstract string? ValidationMessage { get; }

    /// <summary>Projects the current fields into a <see cref="RemoteAuthentication"/>.</summary>
    public abstract RemoteAuthentication ToRemoteAuthentication();

    /// <summary>Sets a backing field and raises <see cref="IsValid"/>/<see cref="ValidationMessage"/> when it changes.</summary>
    protected void SetValidatedProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (this.SetProperty(ref field, value, propertyName))
        {
            this.RaisePropertyChanged(nameof(this.IsValid));
            this.RaisePropertyChanged(nameof(this.ValidationMessage));
        }
    }
}
