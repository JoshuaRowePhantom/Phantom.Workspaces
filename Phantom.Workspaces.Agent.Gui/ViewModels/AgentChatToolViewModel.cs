using System.Collections.ObjectModel;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class AgentChatToolViewModel : ViewModelBase
{
    private readonly Func<bool, Task> setEnabled;

    public AgentChatToolViewModel(
        string id,
        string name,
        string description,
        string kind,
        bool isEnabled,
        string? status,
        IReadOnlyList<AgentChatToolViewModel> children,
        Func<bool, Task> setEnabled)
    {
        this.Id = id;
        this.Name = name;
        this.Description = description;
        this.Kind = kind;
        this.IsEnabled = isEnabled;
        this.Status = status;
        this.setEnabled = setEnabled;
        this.Children = [.. children];
    }

    public string Id { get; }

    public string Name { get; }

    public string Description { get; }

    public string Kind { get; }

    public bool IsEnabled { get; }

    public string? Status { get; }

    public ObservableCollection<AgentChatToolViewModel> Children { get; }

    public Task SetEnabledAsync(bool enabled) => this.setEnabled(enabled);
}
