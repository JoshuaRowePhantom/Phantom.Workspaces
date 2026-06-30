using Avalonia.Markup.Xaml;
using Dock.Avalonia.Controls;
using Phantom.Workspaces.Templates;

namespace Phantom.Workspaces.Controls;

public partial class PhantomHostWindow : HostWindow
{
    public PhantomHostWindow()
    {
        AvaloniaXamlLoader.Load(this);
        AddDockDataTemplates();
    }

    private void AddDockDataTemplates()
    {
        foreach (var template in new DockDataTemplates())
        {
            this.DataTemplates.Add(template);
        }
    }
}
