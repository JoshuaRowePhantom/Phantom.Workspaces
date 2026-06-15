using System;

namespace Phantom.Workspaces.ViewModels.Configuration;

/// <summary>
/// A single selectable section in the settings dialog's master-detail layout: a <see cref="Title"/>
/// shown in the left-hand section list and a <see cref="Content"/> view model rendered (via data
/// templates) on the right.
/// </summary>
public sealed class SettingsSectionViewModel : ViewModelBase
{
    public SettingsSectionViewModel(string title, object content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(content);
        this.Title = title;
        this.Content = content;
    }

    /// <summary>The section label shown in the left-hand list.</summary>
    public string Title { get; }

    /// <summary>The section's content view model, rendered on the right.</summary>
    public object Content { get; }
}
