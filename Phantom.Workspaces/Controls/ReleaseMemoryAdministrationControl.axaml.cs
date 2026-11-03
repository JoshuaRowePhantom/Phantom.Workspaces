using Avalonia.Controls;
using Avalonia.Interactivity;
using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Controls;

public partial class ReleaseMemoryAdministrationControl : UserControl
{
    public ReleaseMemoryAdministrationControl() => this.InitializeComponent();
    public Func<SafeWorkspaceEntityReference, Task>? NavigateEntityAsync { get; set; }

    private async void OpenOrigin(object? sender, RoutedEventArgs args)
    {
        if (sender is Button { CommandParameter: SafeWorkspaceEntityReference reference }
            && this.NavigateEntityAsync is { } navigate)
            await navigate(reference);
    }
}
