using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Authorization;

namespace Phantom.Workspaces.ViewModels;

public enum AuthorizationRowsPresentationMode { Bootstrap, Runtime }

public sealed class AuthorizationRequestRowsViewModel : IDisposable
{
    private readonly ObservableCollection<AuthorizationRequestRowViewModel> rows = new();
    private readonly Dictionary<UserAuthorizationRequestIdentifier, AuthorizationRequestRowViewModel> keyedRows = new();
    private readonly object snapshotGate = new();
    private IUserAuthorizationRegistry registry = null!;
    private AuthorizationRowsPresentationMode mode;
    private long queuedVersion;
    private long appliedVersion;
    private bool disposed;

    public AuthorizationRequestRowsViewModel() => this.Rows = new(this.rows);

    // Assign before Registry when supplying a test or host-specific dispatcher.
    public Action<Action> Dispatch { private get; init; } = action => Dispatcher.UIThread.Post(action);
    public required IUserAuthorizationRegistry Registry
    {
        private get => this.registry;
        init
        {
            this.registry = value;
            value.Changed += this.OnChanged;
            try
            {
                this.QueueSnapshot();
            }
            catch
            {
                this.disposed = true;
                value.Changed -= this.OnChanged;
                throw;
            }
        }
    }

    public ReadOnlyObservableCollection<AuthorizationRequestRowViewModel> Rows { get; }
    public AuthorizationRowsPresentationState Presentation { get; } = new();
    public event EventHandler? Changed;
    public int ActionableCount => this.keyedRows.Values.Count(row => row.CanAct);

    public void SetPresentationMode(AuthorizationRowsPresentationMode mode)
    {
        this.mode = mode;
        this.Project();
    }

    private void OnChanged(object? sender, UserAuthorizationChangedEventArgs arguments) => this.QueueSnapshot();

    private void QueueSnapshot()
    {
        lock (this.snapshotGate)
        {
            if (this.disposed) { return; }
            var snapshot = this.Registry.GetSnapshot().ToArray();
            var version = ++this.queuedVersion;
            this.Dispatch(() => this.ApplySnapshot(snapshot, version));
        }
    }

    private void ApplySnapshot(IReadOnlyList<UserAuthorizationSnapshot> snapshot, long version)
    {
        if (this.disposed || version <= this.appliedVersion) { return; }
        this.appliedVersion = version;
        var identifiers = snapshot.Select(value => value.Identifier).ToHashSet();
        foreach (var identifier in this.keyedRows.Keys.Where(identifier => !identifiers.Contains(identifier)).ToArray())
        {
            this.keyedRows[identifier].Deactivate();
            this.keyedRows.Remove(identifier);
        }
        foreach (var value in snapshot)
        {
            if (!this.keyedRows.TryGetValue(value.Identifier, out var row))
            {
                row = new AuthorizationRequestRowViewModel { Registry = this.Registry };
                this.keyedRows.Add(value.Identifier, row);
            }
            row.Apply(value);
        }
        this.Project();
    }

    private void Project()
    {
        if (this.disposed) { return; }
        var visible = this.keyedRows.Values.Where(row =>
            this.mode == AuthorizationRowsPresentationMode.Runtime || row.IsBootstrapBlocking).ToArray();
        foreach (var row in this.rows.Where(row => !visible.Contains(row)).ToArray()) { this.rows.Remove(row); }
        foreach (var row in visible.Where(row => !this.rows.Contains(row))) { this.rows.Add(row); }
        this.Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        lock (this.snapshotGate)
        {
            if (this.disposed) { return; }
            this.disposed = true;
            this.Registry.Changed -= this.OnChanged;
        }
        this.Dispatch(() =>
        {
            foreach (var row in this.keyedRows.Values) { row.Deactivate(); }
            this.keyedRows.Clear();
            this.rows.Clear();
            this.Presentation.Clear();
            this.Changed = null;
        });
    }
}
