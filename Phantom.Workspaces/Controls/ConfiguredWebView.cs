using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Gui.Shared.Controls;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Controls;

/// <summary>
/// A WebView control configured to use a persistent user data folder for cookies and cache.
/// Supports navigation commands and URL updates.
/// </summary>
public class ConfiguredWebView : AcceleratorAwareWebView
{
    private static string? userDataFolderPath;
    private static bool environmentConfigured;
    private bool coreSourceChangedSubscribed;

    public static readonly StyledProperty<WebViewModel?> ViewModelProperty =
        AvaloniaProperty.Register<ConfiguredWebView, WebViewModel?>(nameof(ViewModel));

    static ConfiguredWebView()
    {
        // Get the WebView data folder path based on configuration location
        userDataFolderPath = ConfigurationPersistenceService.GetWebViewDataFolderPath();
        
        // Ensure the directory exists
        try
        {
            Directory.CreateDirectory(userDataFolderPath);
        }
        catch (Exception)
        {
        }

        // Try to configure the WebView environment globally before any instances are created
        TryConfigureEnvironment();
    }

    private static void TryConfigureEnvironment()
    {
        if (environmentConfigured || string.IsNullOrEmpty(userDataFolderPath))
        {
            return;
        }

        try
        {
            // Try to find PrepareWebViewStartup or similar static configuration methods
            var webViewType = typeof(NativeWebView);
            var prepareMethod = webViewType.GetMethod(
                "PrepareWebViewStartup",
                BindingFlags.Public | BindingFlags.Static);

            if (prepareMethod != null)
            {
                // Call PrepareWebViewStartup if it exists
                prepareMethod.Invoke(null, null);
            }

            environmentConfigured = true;
        }
        catch (Exception)
        {
        }
    }

    public WebViewModel? ViewModel
    {
        get => GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public ConfiguredWebView()
    {
        // Subscribe to EnvironmentRequested if the event exists
        this.Initialized += OnInitialized;
        
        // Listen for ViewModel changes
        this.PropertyChanged += OnPropertyChanged;
        
        // Subscribe to WebView navigation events directly
        this.NavigationStarted += OnWebViewNavigationStarted;
        this.NavigationCompleted += OnWebViewNavigationCompleted;
        this.NewWindowRequested += OnNewWindowRequested;

        this.WebMessageReceived += OnWebMessageReceived;

        // Forward accelerator events (from AcceleratorAwareWebView) to the bound ViewModel.
        this.AltKeyStateChanged += (_, held) => this.ViewModel?.RaiseAltKeyStateChanged(held);
        this.GoToTabAtIndexRequested += (_, idx) => this.ViewModel?.RaiseGoToTabAtIndex(idx);
        this.GoToWorkspacePaneAtIndexRequested += (_, idx) => this.ViewModel?.RaiseGoToWorkspacePaneAtIndex(idx);
        // #1310: Ctrl+W is handled exclusively by the top-level CloseActiveTabCommand
        // KeyBinding via BrowserAcceleratorBehavior. Subscribing to CloseTabRequested here
        // would double-close the tab.
    }

    private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // Handle ViewModel property changes (for subscribing to NavigationRequested)
        if (e.Property == ViewModelProperty)
        {
            if (e.OldValue is WebViewModel oldViewModel)
            {
                oldViewModel.NavigationRequested -= OnNavigationRequested;
                oldViewModel.FocusPrimaryControlRequested -= OnFocusPrimaryControlRequested;
            }

            if (e.NewValue is WebViewModel newViewModel)
            {
                newViewModel.NavigationRequested += OnNavigationRequested;
                newViewModel.FocusPrimaryControlRequested += OnFocusPrimaryControlRequested;
            }
        }
    }

    private void OnFocusPrimaryControlRequested(object? sender, EventArgs e)
    {
        this.Focus();
    }

    private void OnNavigationRequested(object? sender, NavigationDirection direction)
    {
        try
        {
            if (direction == NavigationDirection.Back && this.CanGoBack)
            {
                this.GoBack();
            }
            else if (direction == NavigationDirection.Forward && this.CanGoForward)
            {
                this.GoForward();
            }
        }
        catch (Exception)
        {
        }
    }

    private void OnWebViewNavigationStarted(object? sender, EventArgs e)
    {
        this.TrySubscribeCoreSourceChanged();
        this.UpdateViewModelCurrentUrl(e);
    }

    private void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        if (string.Equals(e.Body, "close-tab", StringComparison.Ordinal))
        {
            this.ViewModel?.RaiseCloseTab();
        }
    }

    private void OnWebViewNavigationCompleted(object? sender, EventArgs e)
    {
        this.TrySubscribeCoreSourceChanged();
        this.UpdateViewModelCurrentUrl(e);
        if (this.ViewModel != null)
        {
            this.ViewModel.CanGoBack = this.CanGoBack;
            this.ViewModel.CanGoForward = this.CanGoForward;
            
            // Update title by executing JavaScript to get document.title
            _ = UpdateTitleAsync();
        }
    }

    private async Task UpdateTitleAsync()
    {
        var viewModel = this.ViewModel;
        if (viewModel == null)
        {
            return;
        }

        try
        {
            var title = await this.InvokeScript("document.title");
            if (!string.IsNullOrEmpty(title) && viewModel == this.ViewModel)
            {
                viewModel.SetPageTitle(title);
            }
        }
        catch (Exception)
        {
        }
    }

    private void OnNewWindowRequested(object? sender, object? e)
    {
        // Handle new window requests (e.g., Ctrl+click, window.open())
        HandleNewWindowRequested(e, this.ViewModel);
    }

    /// <summary>
    /// Core handling for a <c>NewWindowRequested</c> event: sets the args' <c>Handled</c>
    /// flag so WebView2 does not spawn its own external OS window, then routes the requested
    /// URL to the bound <see cref="WebViewModel"/> so it opens as a new in-app tab.
    /// Extracted so the reflection-based args handling can be unit-tested without a live
    /// WebView2 control.
    /// </summary>
    internal static void HandleNewWindowRequested(object? e, WebViewModel? viewModel)
    {
        if (viewModel == null || e == null)
        {
            return;
        }

        try
        {
            var argsType = e.GetType();

            // Try to get Request property (which is a Uri)
            var requestProperty = argsType.GetProperty("Request");
            if (requestProperty != null)
            {
                var url = requestProperty.GetValue(e);

                if (url != null)
                {
                    // Set Handled to true to prevent default behavior (an external OS window).
                    var handledProperty = argsType.GetProperty("Handled");
                    if (handledProperty != null && handledProperty.CanWrite)
                    {
                        handledProperty.SetValue(e, true);
                    }

                    // Convert to string
                    string urlString = url.ToString() ?? string.Empty;

                    // Notify the ViewModel to open a new tab
                    viewModel.RaiseOpenNewWindow(urlString);
                }
            }
        }
        catch (Exception)
        {
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Update ViewModel navigation state when WebView state changes
        if (this.ViewModel != null)
        {
            if (change.Property.Name == nameof(this.CanGoBack))
            {
                this.ViewModel.CanGoBack = this.CanGoBack;
            }
            else if (change.Property.Name == nameof(this.CanGoForward))
            {
                this.ViewModel.CanGoForward = this.CanGoForward;
            }
            else if (change.Property.Name == nameof(this.Source))
            {
                if (this.Source != null)
                {
                    this.ViewModel.UpdateCurrentUrl(this.Source.ToString());
                }
            }
        }
    }

    private void OnInitialized(object? sender, EventArgs e)
    {
        // Try to set environment properties if they're available
        TrySetUserDataFolder();
        this.TrySubscribeCoreSourceChanged();
    }

    private void OnCoreSourceChanged(object? sender, object? e)
        => this.UpdateViewModelCurrentUrl(sender, e);

    private void UpdateViewModelCurrentUrl(params object?[] urlSources)
    {
        var url = TryGetUrlFromObjects(urlSources)
            ?? TryGetUrlFromObjects(GetReflectedPropertyValue(this, "CoreWebView2"), GetReflectedPropertyValue(this, "CoreWebView"))
            ?? this.Source?.ToString();

        if (!string.IsNullOrWhiteSpace(url))
        {
            this.ViewModel?.UpdateCurrentUrl(url);
        }
    }

    internal static string? TryGetUrlFromObjects(params object?[] sources)
    {
        foreach (var source in sources)
        {
            var url = TryGetUrlFromObject(source);
            if (!string.IsNullOrWhiteSpace(url))
            {
                return url;
            }
        }

        return null;
    }

    private static string? TryGetUrlFromObject(object? source)
    {
        if (source is null)
        {
            return null;
        }

        foreach (var propertyName in new[] { "Uri", "Url", "Source", "CurrentSource", "CurrentUri", "Location" })
        {
            var value = GetReflectedPropertyValue(source, propertyName);
            if (value is Uri uri)
            {
                return uri.ToString();
            }

            if (value is string { Length: > 0 } text)
            {
                return text;
            }
        }

        return null;
    }

    private void TrySubscribeCoreSourceChanged()
    {
        if (this.coreSourceChangedSubscribed)
        {
            return;
        }

        try
        {
            var core = GetReflectedPropertyValue(this, "CoreWebView2")
                ?? GetReflectedPropertyValue(this, "CoreWebView");
            var sourceChanged = core?.GetType().GetEvent("SourceChanged", BindingFlags.Public | BindingFlags.Instance);
            var handlerType = sourceChanged?.EventHandlerType;
            var handlerMethod = this.GetType().GetMethod(nameof(OnCoreSourceChanged), BindingFlags.NonPublic | BindingFlags.Instance);
            if (core != null && sourceChanged != null && handlerType != null && handlerMethod != null)
            {
                sourceChanged.AddEventHandler(core, Delegate.CreateDelegate(handlerType, this, handlerMethod));
                this.coreSourceChangedSubscribed = true;
            }
        }
        catch (Exception)
        {
        }
    }

    private static object? GetReflectedPropertyValue(object source, string propertyName)
    {
        try
        {
            return source.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(source);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void TrySetUserDataFolder()
    {
        if (string.IsNullOrEmpty(userDataFolderPath))
        {
            return;
        }

        try
        {
            // Try to find and subscribe to EnvironmentRequested event
            var eventInfo = this.GetType().GetEvent("EnvironmentRequested", 
                BindingFlags.Public | BindingFlags.Instance);
            
            if (eventInfo != null)
            {
                var handlerType = eventInfo.EventHandlerType;
                var handlerMethod = this.GetType().GetMethod(
                    nameof(OnEnvironmentRequested), 
                    BindingFlags.NonPublic | BindingFlags.Instance);
                
                if (handlerMethod != null && handlerType != null)
                {
                    var handler = Delegate.CreateDelegate(handlerType, this, handlerMethod);
                    eventInfo.AddEventHandler(this, handler);
                }
            }
        }
        catch (Exception)
        {
        }
    }

    private void OnEnvironmentRequested(object? sender, object? args)
    {
        // If this event fires, try to set the UserDataFolder on the args
        if (args == null || string.IsNullOrEmpty(userDataFolderPath))
        {
            return;
        }

        try
        {
            var argsType = args.GetType();
            var folderProperty = argsType.GetProperty("UserDataFolder", 
                BindingFlags.Public | BindingFlags.Instance);
            
            if (folderProperty != null && folderProperty.CanWrite)
            {
                folderProperty.SetValue(args, userDataFolderPath);
            }
        }
        catch (Exception)
        {
        }
    }
}
