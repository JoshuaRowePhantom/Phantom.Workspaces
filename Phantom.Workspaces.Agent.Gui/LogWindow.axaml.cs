using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace Phantom.Workspaces.Agent.Gui;

public partial class LogWindow : Window, INotifyPropertyChanged
{
    private readonly ObservableCollection<string> entries = [];
    private readonly IDisposable subscription;
    private long lastSequence;
    private bool closed;
    private bool isWordWrapEnabled = true;

    public LogWindow() : this(new ObservableLoggerFactory()) { }

    public LogWindow(ObservableLoggerFactory factory)
    {
        this.InitializeComponent();
        this.DataContext = this;
        this.LogItems.ItemsSource = this.entries;

        this.subscription = factory.Subscribe(
            entry => Dispatcher.UIThread.Post(() => this.Append(entry)),
            out var snapshot);
        this.Closed += (_, _) =>
        {
            this.closed = true;
            this.subscription.Dispose();
        };

        this.entries.CollectionChanged += this.OnEntriesChanged;

        foreach (var entry in snapshot)
            this.Append(entry);
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

    private void Append(ObservableLogEntry entry)
    {
        if (this.closed || entry.Sequence <= this.lastSequence)
            return;
        this.lastSequence = entry.Sequence;
        this.entries.Add(entry.Text);
        if (this.entries.Count > ObservableLoggerFactory.RecentEntryLimit)
            this.entries.RemoveAt(0);
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            Dispatcher.UIThread.Post(
                () => this.LogItems.ScrollIntoView(this.entries[^1]),
                DispatcherPriority.Background);
        }
    }

    private void OnClearClicked(object? sender, RoutedEventArgs e) => this.entries.Clear();

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => this.Close();
}
