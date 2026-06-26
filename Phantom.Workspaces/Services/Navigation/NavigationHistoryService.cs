using System;
using System.Collections.Generic;

namespace Phantom.Workspaces.Services.Navigation;

public sealed class NavigationHistoryService : INavigationHistoryService
{
    private const int MaxEntries = 200;

    private readonly List<NavigationEntry> entries = [];
    private int currentIndex = -1;

    public bool CanGoBack => this.currentIndex > 0;

    public bool CanGoForward => this.currentIndex < this.entries.Count - 1;

    public event EventHandler? CanNavigateChanged;

    public void Push(NavigationEntry entry)
    {
        // Deduplication: no-op if identical to current entry
        if (this.currentIndex >= 0 && this.entries[this.currentIndex] == entry)
        {
            return;
        }

        // Truncate forward history
        if (this.currentIndex < this.entries.Count - 1)
        {
            this.entries.RemoveRange(this.currentIndex + 1, this.entries.Count - this.currentIndex - 1);
        }

        this.entries.Add(entry);
        this.currentIndex = this.entries.Count - 1;

        // Enforce max depth by dropping oldest entries
        if (this.entries.Count > MaxEntries)
        {
            var excess = this.entries.Count - MaxEntries;
            this.entries.RemoveRange(0, excess);
            this.currentIndex -= excess;
        }

        this.CanNavigateChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool GoBack(out NavigationEntry? entry)
    {
        if (!this.CanGoBack)
        {
            entry = null;
            return false;
        }

        this.currentIndex--;
        entry = this.entries[this.currentIndex];
        this.CanNavigateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool GoForward(out NavigationEntry? entry)
    {
        if (!this.CanGoForward)
        {
            entry = null;
            return false;
        }

        this.currentIndex++;
        entry = this.entries[this.currentIndex];
        this.CanNavigateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
