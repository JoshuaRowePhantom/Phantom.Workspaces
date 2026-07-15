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
        this.CloseTabRequested += (_, _) => this.ViewModel?.RaiseCloseTab();
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
        // Update the ViewModel with the URL as soon as navigation starts
        if (this.ViewModel != null && this.Source != null)
        {
            this.ViewModel.UpdateCurrentUrl(this.Source.ToString());
        }
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
        // Update the ViewModel with the current URL after navigation completes
        if (this.ViewModel != null && this.Source != null)
        {
            this.ViewModel.UpdateCurrentUrl(this.Source.ToString());
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
        if (this.ViewModel == null || e == null)
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
                    // Set Handled to true to prevent default behavior
                    var handledProperty = argsType.GetProperty("Handled");
                    if (handledProperty != null && handledProperty.CanWrite)
                    {
                        handledProperty.SetValue(e, true);
                    }

                    // Convert to string
                    string urlString = url.ToString() ?? string.Empty;

                    // Notify the ViewModel to open a new tab
                    this.ViewModel.RaiseOpenNewWindow(urlString);
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
