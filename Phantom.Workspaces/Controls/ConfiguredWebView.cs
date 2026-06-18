using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Controls;

/// <summary>
/// A WebView control configured to use a persistent user data folder for cookies and cache.
/// Supports navigation commands and URL updates.
/// </summary>
public class ConfiguredWebView : NativeWebView
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
            System.Diagnostics.Debug.WriteLine($"WebView data folder: {userDataFolderPath}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to create WebView data folder: {ex.Message}");
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
                System.Diagnostics.Debug.WriteLine("Called PrepareWebViewStartup");
            }

            environmentConfigured = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to configure WebView environment: {ex.Message}");
        }
    }

    public WebViewModel? ViewModel
    {
        get => GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public ConfiguredWebView()
    {
        System.Diagnostics.Debug.WriteLine("[ConfiguredWebView] Constructor called");
        
        // Subscribe to EnvironmentRequested if the event exists
        this.Initialized += OnInitialized;
        
        // Listen for ViewModel changes
        this.PropertyChanged += OnPropertyChanged;
        
        // Subscribe to WebView navigation events directly
        System.Diagnostics.Debug.WriteLine("[ConfiguredWebView] Subscribing to NavigationStarted");
        this.NavigationStarted += OnWebViewNavigationStarted;
        
        System.Diagnostics.Debug.WriteLine("[ConfiguredWebView] Subscribing to NavigationCompleted");
        this.NavigationCompleted += OnWebViewNavigationCompleted;
        
        System.Diagnostics.Debug.WriteLine("[ConfiguredWebView] Subscribing to NewWindowRequested");
        this.NewWindowRequested += OnNewWindowRequested;
        
        System.Diagnostics.Debug.WriteLine("[ConfiguredWebView] Constructor completed");
    }

    private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // Handle ViewModel property changes (for subscribing to NavigationRequested)
        if (e.Property == ViewModelProperty)
        {
            if (e.OldValue is WebViewModel oldViewModel)
            {
                oldViewModel.NavigationRequested -= OnNavigationRequested;
            }

            if (e.NewValue is WebViewModel newViewModel)
            {
                newViewModel.NavigationRequested += OnNavigationRequested;
            }
        }
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
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Navigation failed: {ex.Message}");
        }
    }

    private void OnWebViewNavigationStarted(object? sender, EventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"[ConfiguredWebView] NavigationStarted event fired!");
        System.Diagnostics.Debug.WriteLine($"[ConfiguredWebView] Current Source: {this.Source}");
        System.Diagnostics.Debug.WriteLine($"[ConfiguredWebView] EventArgs type: {e?.GetType().FullName}");
        
        // Update the ViewModel with the URL as soon as navigation starts
        if (this.ViewModel != null && this.Source != null)
        {
            System.Diagnostics.Debug.WriteLine($"[ConfiguredWebView] Updating URL to: {this.Source}");
            this.ViewModel.UpdateCurrentUrl(this.Source.ToString());
        }
    }

    private void OnWebViewNavigationCompleted(object? sender, EventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"[ConfiguredWebView] NavigationCompleted: Source={this.Source}, CanGoBack={this.CanGoBack}, CanGoForward={this.CanGoForward}");
        
        // Update the ViewModel with the current URL after navigation completes
        if (this.ViewModel != null && this.Source != null)
        {
            this.ViewModel.UpdateCurrentUrl(this.Source.ToString());
            this.ViewModel.CanGoBack = this.CanGoBack;
            this.ViewModel.CanGoForward = this.CanGoForward;
            
            System.Diagnostics.Debug.WriteLine($"[ConfiguredWebView] Updated ViewModel: CanGoBack={this.ViewModel.CanGoBack}, CanGoForward={this.ViewModel.CanGoForward}");
            
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
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ConfiguredWebView] Failed to get document title: {ex.Message}");
        }
    }

    private void OnDocumentTitleChanged(object? sender, EventArgs e)
    {
        // This method is no longer needed - title is updated in OnWebViewNavigationCompleted
    }

    private void OnNewWindowRequested(object? sender, object? e)
    {
        System.Diagnostics.Debug.WriteLine($"[ConfiguredWebView] NewWindowRequested event fired! EventArgs type: {e?.GetType().FullName}");
        
        // Handle new window requests (e.g., Ctrl+click, window.open())
        if (this.ViewModel == null || e == null)
        {
            System.Diagnostics.Debug.WriteLine($"[ConfiguredWebView] ViewModel or EventArgs is null, skipping");
            return;
        }

        try
        {
            var argsType = e.GetType();
            
            // Log all properties
            System.Diagnostics.Debug.WriteLine($"[ConfiguredWebView] Event args properties:");
            foreach (var prop in argsType.GetProperties())
            {
                try
                {
                    var value = prop.GetValue(e);
                    System.Diagnostics.Debug.WriteLine($"  {prop.Name} ({prop.PropertyType.Name}): {value}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"  {prop.Name}: Error reading - {ex.Message}");
                }
            }
            
            // Try to get Request property (which is a Uri)
            var requestProperty = argsType.GetProperty("Request");
            if (requestProperty != null)
            {
                var url = requestProperty.GetValue(e);
                System.Diagnostics.Debug.WriteLine($"[ConfiguredWebView] Extracted Request: {url}");
                
                if (url != null)
                {
                    // Set Handled to true to prevent default behavior
                    var handledProperty = argsType.GetProperty("Handled");
                    if (handledProperty != null && handledProperty.CanWrite)
                    {
                        handledProperty.SetValue(e, true);
                        System.Diagnostics.Debug.WriteLine($"[ConfiguredWebView] Set Handled=true");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[ConfiguredWebView] WARNING: Handled property not found or not writable!");
                    }

                    // Convert to string
                    string urlString = url.ToString() ?? string.Empty;

                    // Notify the ViewModel to open a new tab
                    this.ViewModel.RaiseOpenNewWindow(urlString);
                    System.Diagnostics.Debug.WriteLine($"[ConfiguredWebView] Raised OpenNewWindow event: {urlString}");
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[ConfiguredWebView] WARNING: Request property not found on event args!");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ConfiguredWebView] Failed to handle NewWindowRequested: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[ConfiguredWebView] Stack trace: {ex.StackTrace}");
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
        System.Diagnostics.Debug.WriteLine("[ConfiguredWebView] Initialized event fired");
        
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
                    System.Diagnostics.Debug.WriteLine("Subscribed to EnvironmentRequested");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to set user data folder: {ex.Message}");
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
                System.Diagnostics.Debug.WriteLine($"Set UserDataFolder to: {userDataFolderPath}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to set UserDataFolder: {ex.Message}");
        }
    }
}
