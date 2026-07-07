using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Phantom.Workspaces.Gui.Shared.Utilities;

namespace Phantom.Workspaces.ViewModels;

public abstract class ViewModelBase : INotifyPropertyChanged, IAsyncDisposable
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected ViewModelLifetime Lifetime { get; } = new();

    protected bool SetProperty<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void RaisePropertyChanged(
        [CallerMemberName] string? propertyName = null)
    {
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public virtual ValueTask DisposeAsync()
    {
        return Lifetime.DisposeAsync();
    }
}
