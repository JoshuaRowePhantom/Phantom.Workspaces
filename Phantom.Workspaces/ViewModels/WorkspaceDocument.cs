using System.ComponentModel;
using Dock.Model.Mvvm.Controls;

namespace Phantom.Workspaces.ViewModels;

public class WorkspaceDocument : Document
{
    public WorkspaceDocument(WorkspaceTabViewModel tabViewModel)
    {
        this.TabViewModel = tabViewModel;
        this.Id = tabViewModel.Id;
        this.Title = TruncateTitle(tabViewModel.Title);
        this.CanClose = true;
        
        // Subscribe to title changes from the view model
        tabViewModel.PropertyChanged += OnTabViewModelPropertyChanged;
    }
    
    private void OnTabViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceTabViewModel.Title))
        {
            this.Title = TruncateTitle(this.TabViewModel.Title);
        }
        else if (e.PropertyName == nameof(WorkspaceTabViewModel.TabTooltip))
        {
            // Tooltip changes need to propagate through to the UI
            // Since Dock.Model.Document doesn't expose tooltips directly,
            // we rely on the TabViewModel binding in the style
        }
    }
    
    private static string TruncateTitle(string title)
    {
        if (title.Length > 20)
        {
            return title.Substring(0, 17) + "...";
        }
        return title;
    }

    public WorkspaceTabViewModel TabViewModel { get; }
}
