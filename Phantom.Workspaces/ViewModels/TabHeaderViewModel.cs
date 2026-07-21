using System.Collections.ObjectModel;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Base class for items displayed in a tab header before the title text.
/// </summary>
public abstract class TabHeaderItemViewModel : ViewModelBase { }

/// <summary>
/// A tab header item that shows a glyph icon (e.g. "🧠").
/// </summary>
public sealed class IconTabHeaderItemViewModel : TabHeaderItemViewModel
{
    /// <summary>The icon glyph string displayed in the tab strip.</summary>
    public required string Icon { get; init; }
}

/// <summary>
/// A tab header item that shows a favicon image, falling back to a globe glyph (🌐) when
/// <see cref="FaviconUri"/> is <see langword="null"/>.  Set <see cref="FaviconUri"/> to the
/// page's favicon URL once it has loaded.
/// </summary>
public sealed class FaviconTabHeaderItemViewModel : TabHeaderItemViewModel
{
    private string? faviconUri;

    public string? FaviconUri
    {
        get => this.faviconUri;
        set => this.SetProperty(ref this.faviconUri, value);
    }
}

/// <summary>
/// A tab header item that shows a pulsating brain icon while an agent is running.
/// Set <see cref="IsRunning"/> to true while the agent is actively processing.
/// </summary>
public sealed class AgentRunningIndicatorTabHeaderItemViewModel : TabHeaderItemViewModel
{
    private bool isRunning;

    public bool IsRunning
    {
        get => this.isRunning;
        set => this.SetProperty(ref this.isRunning, value);
    }
}

/// <summary>
/// A tab header item that shows a notification indicator. The indicator is always
/// present in the layout; its <see cref="HasUnread"/> property controls visibility
/// via an opacity converter so the tab width never changes.
/// </summary>
public sealed class NotificationIndicatorTabHeaderItemViewModel : TabHeaderItemViewModel
{
    private bool hasUnread;

    public bool HasUnread
    {
        get => this.hasUnread;
        set => this.SetProperty(ref this.hasUnread, value);
    }
}

/// <summary>
/// A tab header item that shows a unified status indicator (running / succeeded / failed / idle).
/// <see cref="Status"/> is owned and updated by <see cref="WorkspaceDocument"/>.
/// </summary>
public sealed class StatusTabHeaderItemViewModel : TabHeaderItemViewModel
{
    public StatusItem Status { get; } = new();
}

/// <summary>
/// Header model for workspace tabs. When set on <see cref="WorkspaceTabViewModel.TabHeader"/>,
/// overrides the plain-string title rendering in the tab strip.
/// <see cref="Items"/> holds icon/indicator elements rendered after <see cref="Title"/>.
/// </summary>
public class TabHeaderViewModel : ViewModelBase
{
    private string title = string.Empty;

    public required string Title
    {
        get => this.title;
        set => this.SetProperty(ref this.title, value);
    }

    public ObservableCollection<TabHeaderItemViewModel> Items { get; } = [];

    /// <summary>
    /// Creates a <see cref="TabHeaderViewModel"/> whose first item is an
    /// <see cref="IconTabHeaderItemViewModel"/> with the given <paramref name="icon"/>.
    /// </summary>
    public static TabHeaderViewModel WithIcon(string icon, string title)
    {
        var vm = new TabHeaderViewModel { Title = title };
        vm.Items.Add(new IconTabHeaderItemViewModel { Icon = icon });
        return vm;
    }
}
