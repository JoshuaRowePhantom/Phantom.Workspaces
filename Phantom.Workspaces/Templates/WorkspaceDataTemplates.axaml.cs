using Avalonia.Controls.Templates;
using Avalonia.Markup.Xaml;

namespace Phantom.Workspaces.Templates;

public partial class WorkspaceDataTemplates : DataTemplates
{
    public WorkspaceDataTemplates()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
