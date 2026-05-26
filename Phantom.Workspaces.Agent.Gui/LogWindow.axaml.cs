using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace Phantom.Workspaces.Agent.Gui;

public partial class LogWindow : Window, INotifyPropertyChanged
{
    private readonly ObservableLoggerFactory factory;
    private readonly ObservableCollection<string> entries = [];
    private bool isWordWrapEnabled = true;

    public LogWindow() : this(new ObservableLoggerFactory()) { }

    public LogWindow(ObservableLoggerFactory factory)
    {
        this.factory = factory;
        this.InitializeComponent();
        this.DataContext = this;
        this.LogItems.ItemsSource = this.entries;

        factory.EntryAdded += this.OnEntryAdded;
        this.Closed += (_, _) => factory.EntryAdded -= this.OnEntryAdded;

        this.entries.CollectionChanged += this.OnEntriesChanged;

        foreach (var entry in factory.Entries)
        {
            this.entries.Add(entry);
        }
    }

    public new event PropertyChangedEventHandler? PropertyChanged;

    public bool IsWordWrapEnabled
    {
        get => this.isWordWrapEnabled;
        set
        {
            if (this.isWordWrapEnabled == value)
            {
                return;
            }

            this.isWordWrapEnabled = value;
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.IsWordWrapEnabled)));
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.LogTextWrapping)));
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.LogHorizontalScrollBarVisibility)));
        }
    }

    public TextWrapping LogTextWrapping => this.IsWordWrapEnabled ? TextWrapping.Wrap : TextWrapping.NoWrap;

    public ScrollBarVisibility LogHorizontalScrollBarVisibility
        => this.IsWordWrapEnabled ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;

    private void OnEntryAdded(object? sender, string entry)
    {
        Dispatcher.UIThread.Post(() => this.entries.Add(entry));
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            Dispatcher.UIThread.Post(
                () => this.LogScrollViewer.Offset = new Vector(this.LogScrollViewer.Offset.X, double.MaxValue),
                DispatcherPriority.Background);
        }
    }

    private void OnClearClicked(object? sender, RoutedEventArgs e) => this.entries.Clear();

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => this.Close();
}
