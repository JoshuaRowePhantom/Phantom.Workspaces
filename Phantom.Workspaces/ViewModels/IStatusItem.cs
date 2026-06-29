using System.ComponentModel;

namespace Phantom.Workspaces.ViewModels;

public enum RunningStatus { Idle, Running }

public enum ErrorStatus { None, Successful, Error }

public interface IStatusItem : INotifyPropertyChanged
{
    RunningStatus RunningStatus { get; }
    ErrorStatus ErrorStatus { get; }
}
