using System.ComponentModel;
using Phantom.Workspaces.Agent.Gui.ViewModels;

namespace Phantom.Workspaces.Services;

internal interface IScrollLockLedHost : INotifyPropertyChanged
{
    IAutoScrollViewModel? ActiveAgentViewModel { get; }
}
