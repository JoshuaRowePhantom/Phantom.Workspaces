using System.ComponentModel;
using Dock.Model.Mvvm.Controls;

namespace Phantom.Workspaces.ViewModels;

public class WorkspaceDocument : Document
{
    private bool hasUnreadNotification;
    private string baseTitle = string.Empty;

    public WorkspaceDocument(WorkspaceTabViewModel tabViewModel)
    {
        this.TabViewModel = tabViewModel;
        this.Id = tabViewModel.Id;
        this.baseTitle = ComputeBaseTitle(tabViewModel);
        this.Title = this.baseTitle;
        this.CanClose = true;
        
        tabViewModel.PropertyChanged += OnTabViewModelPropertyChanged;
    }
    
    private void OnTabViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkspaceTabViewModel.Title) or nameof(WorkspaceTabViewModel.TabHeader))
        {
            this.baseTitle = ComputeBaseTitle(this.TabViewModel);
            this.UpdateTitle();
        }
    }

    /// <summary>
    /// The header model for this tab. Falls back to a plain <see cref="TabHeaderViewModel"/>
    /// derived from the current title when <see cref="WorkspaceTabViewModel.TabHeader"/> is null.
    /// </summary>
    public TabHeaderViewModel EffectiveTabHeader =>
        this.TabViewModel.TabHeader ?? new TabHeaderViewModel { Title = this.TabViewModel.Title };

    public bool HasUnreadNotification
    {
        get => this.hasUnreadNotification;
        set
        {
            if (this.hasUnreadNotification == value) return;
            this.hasUnreadNotification = value;
            this.UpdateTitle();
        }
    }

    private void UpdateTitle()
    {
        this.Title = this.hasUnreadNotification ? "! " + this.baseTitle : this.baseTitle;
    }

    private static string ComputeBaseTitle(WorkspaceTabViewModel tabViewModel)
    {
        var raw = tabViewModel.TabHeader is IconTabHeaderViewModel icon
            ? $"{icon.Icon} {tabViewModel.Title}"
            : tabViewModel.Title;
        return TruncateTitle(raw);
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
