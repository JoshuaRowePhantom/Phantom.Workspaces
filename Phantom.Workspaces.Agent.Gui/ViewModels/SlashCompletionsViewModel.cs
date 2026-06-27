using System.Collections.Generic;
using System.Collections.ObjectModel;
using Phantom.Workspaces.Llm.SlashCommands;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class SlashCompletionsViewModel : ViewModelBase
{
    private bool isVisible;
    private int selectedIndex = -1;

    public ObservableCollection<SlashCompletionItemViewModel> Items { get; } = [];

    public bool IsVisible
    {
        get => this.isVisible;
        private set => this.SetProperty(ref this.isVisible, value);
    }

    public int SelectedIndex
    {
        get => this.selectedIndex;
        set
        {
            if (this.selectedIndex >= 0 && this.selectedIndex < this.Items.Count)
            {
                this.Items[this.selectedIndex].IsSelected = false;
            }

            this.selectedIndex = value;

            if (this.selectedIndex >= 0 && this.selectedIndex < this.Items.Count)
            {
                this.Items[this.selectedIndex].IsSelected = true;
            }

            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(this.SelectedItem));
        }
    }

    public SlashCompletionItemViewModel? SelectedItem =>
        this.selectedIndex >= 0 && this.selectedIndex < this.Items.Count
            ? this.Items[this.selectedIndex]
            : null;

    public void SelectNext()
    {
        if (this.Items.Count == 0)
        {
            return;
        }

        this.SelectedIndex = this.selectedIndex < this.Items.Count - 1
            ? this.selectedIndex + 1
            : 0;
    }

    public void SelectPrevious()
    {
        if (this.Items.Count == 0)
        {
            return;
        }

        this.SelectedIndex = this.selectedIndex > 0
            ? this.selectedIndex - 1
            : this.Items.Count - 1;
    }

    public void Dismiss()
    {
        this.SetItems([]);
    }

    /// <summary>
    /// Returns the <see cref="SlashCompletionItemViewModel.CompletionText"/> of the selected item
    /// (or <c>null</c> when nothing is selected) and then dismisses the popup.
    /// </summary>
    public string? Accept()
    {
        var text = this.SelectedItem?.CompletionText;
        this.Dismiss();
        return text;
    }

    public void SetItems(IReadOnlyList<SlashCommandCompletion> completions)
    {
        // Deselect current item before replacing the list.
        if (this.selectedIndex >= 0 && this.selectedIndex < this.Items.Count)
        {
            this.Items[this.selectedIndex].IsSelected = false;
        }

        this.selectedIndex = -1;
        this.RaisePropertyChanged(nameof(this.SelectedIndex));
        this.RaisePropertyChanged(nameof(this.SelectedItem));

        this.Items.Clear();
        foreach (var c in completions)
        {
            this.Items.Add(new SlashCompletionItemViewModel(c.CompletionText, c.Label, c.Description));
        }

        this.IsVisible = this.Items.Count > 0;
    }
}
