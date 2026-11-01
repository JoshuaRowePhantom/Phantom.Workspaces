using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
            this.item.AttachedToVisualTree += this.OnAttachedToVisualTree;
            this.item.AddHandler(Control.ContextRequestedEvent, this.OnContextRequested, RoutingStrategies.Tunnel);
            this.item.AddHandler(InputElement.KeyDownEvent, this.OnKeyDown, RoutingStrategies.Tunnel);
            this.AttachController();
        }

        public void Dispose()
        {
            this.item.PropertyChanged -= this.OnItemPropertyChanged;
            this.item.DetachedFromVisualTree -= this.OnDetachedFromVisualTree;
            this.item.AttachedToVisualTree -= this.OnAttachedToVisualTree;
            this.item.RemoveHandler(Control.ContextRequestedEvent, this.OnContextRequested);
            this.item.RemoveHandler(InputElement.KeyDownEvent, this.OnKeyDown);
            this.DetachController();
        }

        private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs args)
            => this.DetachController();

        private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs args)
            => this.AttachController();

        private void OnContextRequested(object? sender, ContextRequestedEventArgs args)
        {
            if (args.Source is not Visual source) { return; }
            var origin = ResolveOrigin(source);
            if (ReferenceEquals(origin, this.item))
            {
                this.controller?.CaptureOrigin(this.item);
                if (ReferenceEquals(source, this.item) && this.menu is not null)
                {
                    // The theme attaches the stock menu to PART_TabBody, which is not
                    // keyboard-focusable. A keyboard request on the container still has
                    // an exact origin; open through Avalonia's supported owner overload.
                    this.OpenNativeMenu();
                    args.Handled = true;
                }
            }
        }

        private void OnKeyDown(object? sender, KeyEventArgs args)
        {
            if (args.Handled || this.menu is null ||
                !(args.Key == Key.Apps || args.Key == Key.F10 && args.KeyModifiers == KeyModifiers.Shift))
            {
                return;
            }
            if (args.Source is not Visual source ||
                !ReferenceEquals(ResolveOrigin(source), this.item))
            {
                return;
            }
            this.controller?.CaptureOrigin(this.item);
            this.OpenNativeMenu();
            args.Handled = true;
        }

        private static DocumentTabStripItem? ResolveOrigin(Visual source) =>
            source as DocumentTabStripItem ?? source.GetVisualAncestors().OfType<DocumentTabStripItem>().FirstOrDefault();

        private void OpenNativeMenu()
        {
            var owner = this.item.GetVisualDescendants().OfType<Control>()
                .SingleOrDefault(control => ReferenceEquals(control.ContextMenu, this.menu));
            if (owner is not null) { this.menu!.Open(owner); }
        }

        private void OnItemPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (args.Property == DocumentTabStripItem.DocumentContextMenuProperty)
            {
                this.AttachController();
            }
            else if (args.Property == StyledElement.DataContextProperty)
            {
                this.controller?.InvalidateOrigin(this.item);
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
        private DocumentTabStripItem? origin;

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
            this.InvalidateOrigin(item);
            this.registeredItems.Remove(item);
        }

        public void CaptureOrigin(DocumentTabStripItem item)
        {
            this.origin = this.registeredItems.Contains(item) ? item : null;
            if (this.origin is not null) { MenuFactory.Apply(this.menu, this.origin); }
            else { MenuFactory.Clear(this.menu); }
        }

        public void InvalidateOrigin(DocumentTabStripItem item)
        {
            if (ReferenceEquals(this.origin, item))
            {
                this.origin = null;
                MenuFactory.Clear(this.menu);
            }
        }

        private void OnOpened(object? sender, EventArgs args)
        {
            if (this.origin is not { } tabItem
                || !this.registeredItems.Contains(tabItem))
            {
                MenuFactory.Clear(this.menu);
                return;
            }

            MenuFactory.Apply(this.menu, tabItem);
        }

        private void OnClosed(object? sender, EventArgs args)
        {
            this.origin = null;
            MenuFactory.Clear(this.menu);
        }
    }
}
