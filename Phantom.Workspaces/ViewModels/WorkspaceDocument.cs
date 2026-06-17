using Dock.Model.Mvvm.Controls;

namespace Phantom.Workspaces.ViewModels;

public class WorkspaceDocument : Document
{
    public WorkspaceDocument(WorkspaceTabViewModel tabViewModel)
    {
        this.TabViewModel = tabViewModel;
        this.Id = tabViewModel.Id;
        this.Title = tabViewModel.Title;
        this.CanClose = true;
    }

    public WorkspaceTabViewModel TabViewModel { get; }
}
