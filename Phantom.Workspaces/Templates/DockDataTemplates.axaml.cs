using Avalonia.Controls.Templates;
using Avalonia.Markup.Xaml;

namespace Phantom.Workspaces.Templates;

public partial class DockDataTemplates : DataTemplates
{
    public DockDataTemplates()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
