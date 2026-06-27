using Avalonia.Media;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class SlashCompletionItemViewModel : ViewModelBase
{
    private bool isSelected;

    public SlashCompletionItemViewModel(string completionText, string? label, string? description)
    {
        this.CompletionText = completionText;
        this.Label = label ?? completionText;
        this.Description = description;
    }

    public string CompletionText { get; }

    public string Label { get; }

    public string? Description { get; }

    public bool HasDescription => this.Description is not null;

    public bool IsSelected
    {
        get => this.isSelected;
        set
        {
            if (this.SetProperty(ref this.isSelected, value))
            {
                this.RaisePropertyChanged(nameof(this.LabelWeight));
            }
        }
    }

    public FontWeight LabelWeight => this.isSelected ? FontWeight.Bold : FontWeight.Normal;
}
