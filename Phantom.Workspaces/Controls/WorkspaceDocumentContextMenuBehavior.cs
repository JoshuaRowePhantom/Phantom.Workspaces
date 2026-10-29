using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Phantom.Workspaces.Services.TabCommands;
using System.Runtime.CompilerServices;

namespace Phantom.Workspaces.Controls;

public static class WorkspaceDocumentContextMenuBehavior
{
    private static readonly ConditionalWeakTable<DocumentTabStripItem, ItemSubscription> ItemSubscriptions = [];
    private static readonly ConditionalWeakTable<ContextMenu, MenuController> MenuControllers = [];
    private static readonly WorkspaceDocumentMenuFactory MenuFactory = new();

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<DocumentTabStripItem, bool>(
            "IsEnabled",
            typeof(WorkspaceDocumentContextMenuBehavior));

    static WorkspaceDocumentContextMenuBehavior()
    {
        IsEnabledProperty.Changed.AddClassHandler<DocumentTabStripItem>(OnIsEnabledChanged);
    }

    public static bool GetIsEnabled(DocumentTabStripItem item) => item.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DocumentTabStripItem item, bool value) =>
        item.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DocumentTabStripItem item, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.GetNewValue<bool>())
        {
            ItemSubscriptions.GetValue(item, static key => new ItemSubscription(key));
            return;
        }

        if (ItemSubscriptions.TryGetValue(item, out var subscription))
        {
            subscription.Dispose();
            ItemSubscriptions.Remove(item);
        }
    }

    private sealed class ItemSubscription : IDisposable
    {
        private readonly DocumentTabStripItem item;
        private MenuController? controller;
        private ContextMenu? menu;

        public ItemSubscription(DocumentTabStripItem item)
        {
            this.item = item;
            this.item.PropertyChanged += this.OnItemPropertyChanged;
            this.item.DetachedFromVisualTree += this.OnDetachedFromVisualTree;
            this.AttachController();
        }

        public void Dispose()
        {
            this.item.PropertyChanged -= this.OnItemPropertyChanged;
            this.item.DetachedFromVisualTree -= this.OnDetachedFromVisualTree;
            this.DetachController();
        }

        private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs args)
            => this.Dispose();

        private void OnItemPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (args.Property == DocumentTabStripItem.DocumentContextMenuProperty)
            {
                this.AttachController();
            }
        }

        private void AttachController()
        {
            var nextMenu = this.item.DocumentContextMenu as ContextMenu;
            if (ReferenceEquals(this.menu, nextMenu))
            {
                return;
            }

            this.DetachController();
            this.menu = nextMenu;
            if (this.menu is null)
            {
                return;
            }

            this.controller = MenuControllers.GetValue(this.menu, static menu => new MenuController(menu));
            this.controller.Register(this.item);
        }

        private void DetachController()
        {
            if (this.controller is null || this.menu is null)
            {
                this.controller = null;
                this.menu = null;
                return;
            }

            this.controller.Unregister(this.item);
            if (this.controller.IsEmpty)
            {
                this.controller.Dispose();
                MenuControllers.Remove(this.menu);
            }

            this.controller = null;
            this.menu = null;
        }
    }

    private sealed class MenuController : IDisposable
    {
        private readonly ContextMenu menu;
        private readonly HashSet<DocumentTabStripItem> registeredItems = [];

        public MenuController(ContextMenu menu)
        {
            this.menu = menu;
            this.menu.Opened += this.OnOpened;
            this.menu.Closed += this.OnClosed;
        }

        public bool IsEmpty => this.registeredItems.Count == 0;

        public void Dispose()
        {
            this.menu.Opened -= this.OnOpened;
            this.menu.Closed -= this.OnClosed;
            MenuFactory.Clear(this.menu);
            this.registeredItems.Clear();
        }

        public void Register(DocumentTabStripItem item) => this.registeredItems.Add(item);

        public void Unregister(DocumentTabStripItem item)
        {
            this.registeredItems.Remove(item);
        }

        private void OnOpened(object? sender, EventArgs args)
        {
            if (this.menu.PlacementTarget is not DocumentTabStripItem tabItem
                || !this.registeredItems.Contains(tabItem))
            {
                MenuFactory.Clear(this.menu);
                return;
            }

            MenuFactory.Apply(this.menu, tabItem);
        }

        private void OnClosed(object? sender, EventArgs args) => MenuFactory.Clear(this.menu);
    }
}
