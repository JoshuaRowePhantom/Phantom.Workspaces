using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Authorization;

namespace Phantom.Workspaces.ViewModels;

public enum AuthorizationRowsPresentationMode { Bootstrap, Runtime }

public sealed class AuthorizationPromptEventArgs : EventArgs
{
    public AccessPresentationOwnerIdentifier? PresentationOwner { get; init; }
    public Task<bool>? Presentation { get; set; }
}

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
    private bool projecting;
    private long presentationAvailabilityVersion;
    private readonly Dictionary<UserAuthorizationRequestIdentifier, long> presentedPrompts = new();
    private readonly HashSet<(UserAuthorizationRequestIdentifier Identifier, long Generation)> deliveringPrompts = new();

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
                lock (this.snapshotGate)
                {
                    this.disposed = true;
                    value.Changed -= this.OnChanged;
                }
                throw;
            }
        }
    }

    public ReadOnlyObservableCollection<AuthorizationRequestRowViewModel> Rows { get; }
    public AuthorizationRowsPresentationState Presentation { get; } = new();
    public event EventHandler? Changed;
    public event EventHandler<AuthorizationPromptEventArgs>? NewActionablePrompt;
    public int ActionableCount => this.keyedRows.Values.Count(row => row.CanAct);
    internal Task PresentationReady { get; private set; } = Task.CompletedTask;
    internal void NotifyPresentationOwnerAvailabilityChanged() => this.presentationAvailabilityVersion++;

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
            this.keyedRows[identifier].PropertyChanged -= this.OnRowPropertyChanged;
            this.keyedRows[identifier].Deactivate();
            this.keyedRows.Remove(identifier);
        }
        foreach (var value in snapshot)
        {
            if (!this.keyedRows.TryGetValue(value.Identifier, out var row))
            {
                row = new AuthorizationRequestRowViewModel { Registry = this.Registry };
                this.keyedRows.Add(value.Identifier, row);
                row.PropertyChanged += this.OnRowPropertyChanged;
            }
            this.projecting = true;
            try { row.Apply(value); }
            finally { this.projecting = false; }
        }
        this.Project();
    }

    private void Project()
    {
        if (this.disposed) { return; }
        var visible = this.keyedRows.Values.Where(row =>
            this.mode == AuthorizationRowsPresentationMode.Runtime || row.IsBootstrapBlocking).ToArray();
        this.Presentation.RetainFocusIfVisible(visible.Select(row => row.Identifier));
        foreach (var row in this.rows.Where(row => !visible.Contains(row)).ToArray()) { this.rows.Remove(row); }
        foreach (var row in visible.Where(row => !this.rows.Contains(row))) { this.rows.Add(row); }
        this.Changed?.Invoke(this, EventArgs.Empty);
        this.PresentFreshPrompts();
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs arguments)
    {
        if (!this.disposed && !this.projecting &&
            (string.IsNullOrEmpty(arguments.PropertyName) || arguments.PropertyName == nameof(AuthorizationRequestRowViewModel.CanAct)))
        {
            this.Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    internal void PresentFreshPrompts()
    {
        if (this.disposed || this.mode != AuthorizationRowsPresentationMode.Runtime || this.NewActionablePrompt is null) { return; }
        var fresh = this.keyedRows.Values.Where(row => row.CanAct &&
            (!this.presentedPrompts.TryGetValue(row.Identifier, out var previous) || previous < Math.Max(1, row.PromptGeneration)) &&
            !this.deliveringPrompts.Contains((row.Identifier, Math.Max(1, row.PromptGeneration))))
            .GroupBy(row => row.PresentationOwner).ToArray();
        foreach (var group in fresh)
        {
            var prompts = group.Select(row => (row.Identifier, Generation: Math.Max(1, row.PromptGeneration))).ToArray();
            foreach (var prompt in prompts) { this.deliveringPrompts.Add(prompt); }
            var arguments = new AuthorizationPromptEventArgs { PresentationOwner = group.Key };
            var availabilityVersion = this.presentationAvailabilityVersion;
            this.NewActionablePrompt.Invoke(this, arguments);
            var completion = this.CompletePresentationAsync(prompts, arguments.Presentation, availabilityVersion);
            this.PresentationReady = Task.WhenAll(this.PresentationReady, completion);
        }
    }

    private async Task CompletePresentationAsync(
        (UserAuthorizationRequestIdentifier Identifier, long Generation)[] prompts,
        Task<bool>? presentation,
        long availabilityVersion)
    {
        var delivered = false;
        try
        {
            if (presentation is not null && await presentation && !this.disposed)
            {
                delivered = true;
                foreach (var prompt in prompts)
                {
                    this.presentedPrompts[prompt.Identifier] = Math.Max(
                        this.presentedPrompts.GetValueOrDefault(prompt.Identifier), prompt.Generation);
                }
            }
        }
        finally
        {
            foreach (var prompt in prompts) { this.deliveringPrompts.Remove(prompt); }
        }
        // An owner may become available while an earlier show/refresh is still
        // in flight. Replay that availability edge, not an unbounded retry loop.
        if (!delivered && availabilityVersion != this.presentationAvailabilityVersion) { this.PresentFreshPrompts(); }
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
            foreach (var row in this.keyedRows.Values)
            {
                row.PropertyChanged -= this.OnRowPropertyChanged;
                row.Deactivate();
            }
            this.keyedRows.Clear();
            this.rows.Clear();
            this.Presentation.Clear();
            this.Changed = null;
            this.NewActionablePrompt = null;
        });
    }
}
