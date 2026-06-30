using System.ComponentModel;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public interface IAutoScrollViewModel : INotifyPropertyChanged
{
    bool AutoScrollEnabled { get; }
}
