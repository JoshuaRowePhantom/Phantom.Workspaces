# External Entity Browser View Design

## Overview

Design for displaying external entities with URLs in an embedded web browser within workspace tabs.

## Requirements

- Display external entities (schema: `JsonSchemas/external.json`) that contain one or more URLs
- Each URL should open as a separate tab within the workspace
- Use an embedded browser control suitable for Avalonia on .NET 10.0
- Integrate with existing workspace tab system
- Handle lifecycle and disposal properly

## External Entity Model

External entities follow the schema in `JsonSchemas/external.json`:

```json
{
  "entity-types": ["external"],
  "urls": {
    "default": "https://example.com",
    "docs": "https://example.com/docs",
    "api": "https://example.com/api"
  }
}
```

Each key in the `urls` object represents a named URL. The `default` key is treated as the primary URL.

## Browser Control Options for Avalonia/.NET 10

### Option 1: Avalonia.WebView (Recommended)
- **Package**: `Avalonia.WebView` 
- **Backing**: Uses platform-native WebView2 (Windows), WKWebView (macOS), WebKit (Linux)
- **Pros**: 
  - Platform-native performance
  - Well-maintained community package
  - Modern browser engine
  - Cross-platform
- **Cons**: 
  - Requires WebView2 runtime on Windows
  - Additional platform dependencies

### Option 2: CefSharp/CefGlue
- **Package**: `CefSharp.Avalonia` or `Xilium.CefGlue.Avalonia`
- **Backing**: Chromium Embedded Framework
- **Pros**:
  - Full Chromium browser
  - Consistent across platforms
- **Cons**:
  - Large distribution size (~100MB+)
  - Complex deployment
  - Licensing considerations

### Option 3: External Browser Launch
- **Implementation**: Open URLs in system default browser
- **Pros**:
  - No dependencies
  - Simple implementation
  - Respects user's default browser
- **Cons**:
  - Not embedded in UI
  - Worse user experience
  - Doesn't fit workspace paradigm

**Decision**: Use **Avalonia.WebView** for embedded browsing, with fallback to external browser launch if WebView initialization fails.

## View Model Architecture

### ExternalEntityWorkspaceTabViewModel

Represents a single URL from an external entity as a workspace tab.

```csharp
public class ExternalEntityWorkspaceTabViewModel : WorkspaceTabViewModel
{
    public ExternalEntityWorkspaceTabViewModel(
        EntitySnapshot entity,
        string urlKey,
        string url)
    {
        this.Entity = entity;
        this.UrlKey = urlKey;
        this.Url = url;
        this.Id = $"{entity.EntityId}_{urlKey}";
        this.Title = urlKey == "default" 
            ? entity.DisplayName 
            : $"{entity.DisplayName} - {urlKey}";
    }

    public EntitySnapshot Entity { get; }
    public string UrlKey { get; }
    public string Url { get; }
    
    // WebView state
    public bool IsLoading { get; set; }
    public string? CurrentUrl { get; set; }
    public string? ErrorMessage { get; set; }
    
    // Navigation commands
    public ICommand NavigateBackCommand { get; }
    public ICommand NavigateForwardCommand { get; }
    public ICommand ReloadCommand { get; }
    public ICommand OpenInExternalBrowserCommand { get; }
}
```

### View (XAML)

```xaml
<DataTemplate DataType="vm:ExternalEntityWorkspaceTabViewModel">
    <Grid RowDefinitions="Auto,*">
        <!-- Navigation toolbar -->
        <StackPanel Grid.Row="0" Orientation="Horizontal" Classes="browser-toolbar">
            <Button Command="{Binding NavigateBackCommand}" Content="←" />
            <Button Command="{Binding NavigateForwardCommand}" Content="→" />
            <Button Command="{Binding ReloadCommand}" Content="↻" />
            <TextBlock Text="{Binding CurrentUrl}" Classes="url-display" />
            <Button Command="{Binding OpenInExternalBrowserCommand}" Content="🌐" />
        </StackPanel>
        
        <!-- WebView control -->
        <webview:WebView 
            Grid.Row="1"
            Source="{Binding Url}"
            IsVisible="{Binding !HasError}"
            x:Name="BrowserControl" />
        
        <!-- Error state -->
        <Border Grid.Row="1" 
                IsVisible="{Binding HasError}"
                Classes="error-panel">
            <StackPanel>
                <TextBlock Text="Failed to load browser" />
                <TextBlock Text="{Binding ErrorMessage}" />
                <Button Command="{Binding OpenInExternalBrowserCommand}" 
                        Content="Open in external browser" />
            </StackPanel>
        </Border>
        
        <!-- Loading indicator -->
        <ProgressBar Grid.Row="1" 
                     IsIndeterminate="True"
                     IsVisible="{Binding IsLoading}" />
    </Grid>
</DataTemplate>
```

## Integration with Workspace System

### Creating Tabs from External Entities

When opening an external entity:

1. Parse the `urls` object from entity data
2. Create one `ExternalEntityWorkspaceTabViewModel` per URL
3. Add each tab to the current workspace via `MainWindowViewModel.OpenWorkspaceTab`
4. Activate the "default" URL tab if present, otherwise the first tab

```csharp
// In MainWindowViewModel or EntityOpenHandler
private void OpenExternalEntity(EntitySnapshot entity)
{
    if (!entity.Data.TryGetProperty("urls", out var urlsProperty))
        return;
    
    var urls = JsonSerializer.Deserialize<Dictionary<string, string>>(
        urlsProperty.GetRawText());
    
    if (urls == null || urls.Count == 0)
        return;
    
    // Create tabs for each URL
    var tabs = urls.Select(kvp => 
        new ExternalEntityWorkspaceTabViewModel(entity, kvp.Key, kvp.Value))
        .ToList();
    
    foreach (var tab in tabs)
    {
        this.OpenWorkspaceTab(tab);
    }
    
    // Activate the default tab
    var defaultTab = tabs.FirstOrDefault(t => t.UrlKey == "default") 
                     ?? tabs.First();
    // TODO: Activate defaultTab in dock
}
```

## Lifecycle and Disposal

### WebView Disposal
- `ExternalEntityWorkspaceTabViewModel` must implement `IDisposable`
- Dispose pattern:
  1. Unload WebView navigation handlers
  2. Call `WebView.Dispose()` if available
  3. Set WebView reference to null
- Call dispose when:
  - Tab is closed by user
  - Workspace is closed
  - Application shutdown

### Memory Management
- WebView controls can be memory-intensive
- Consider implementing lazy loading: create WebView only when tab is activated
- Option: Unload inactive WebView instances after timeout

```csharp
public void Dispose()
{
    if (this.webViewControl != null)
    {
        this.webViewControl.NavigationStarting -= OnNavigationStarting;
        this.webViewControl.NavigationCompleted -= OnNavigationCompleted;
        this.webViewControl.Dispose();
        this.webViewControl = null;
    }
}
```

## Open Questions

1. **WebView initialization failure handling**
   - Should we show inline error with "open externally" button?
   - Or silently open in external browser?
   - **Proposed**: Show error in tab with external browser button

2. **URL validation and security**
   - Should we validate URLs before loading (whitelist/blacklist)?
   - HTTPS-only enforcement?
   - **Proposed**: Load any URL, show security warning for HTTP

3. **Navigation scope**
   - Allow navigation to external domains from embedded browser?
   - Or lock to original domain?
   - **Proposed**: Allow navigation, update CurrentUrl property

4. **Tab title updates**
   - Should tab title update with page title?
   - Or stay fixed to entity name?
   - **Proposed**: Stay fixed for consistency

5. **Multiple URLs behavior**
   - Open all tabs immediately, or on-demand?
   - **Proposed**: Open "default" immediately, others on-demand via entity shortcut menu

6. **Platform-specific considerations**
   - Different behavior needed for Windows/Linux/macOS?
   - **Proposed**: Use Avalonia.WebView platform abstraction

## Test Tasks

### Unit Tests
- [ ] Parse external entity URLs dictionary
- [ ] Create correct number of tab view models from URLs
- [ ] Generate correct tab IDs and titles
- [ ] Handle missing "default" URL key
- [ ] Handle empty URLs dictionary
- [ ] Handle malformed URL values

### Integration Tests
- [ ] Open external entity creates tabs in current workspace
- [ ] "default" URL tab is activated first
- [ ] Closing tab disposes WebView properly
- [ ] Opening same external entity twice doesn't duplicate tabs
- [ ] Navigation back/forward commands work
- [ ] Reload command works
- [ ] Open in external browser command works

### Manual Tests
- [ ] WebView loads GitHub repository correctly
- [ ] Navigation within site works
- [ ] Error state displays when WebView unavailable
- [ ] External browser fallback works
- [ ] Memory usage is reasonable with multiple browser tabs
- [ ] Closing tabs releases memory
- [ ] Cross-platform behavior (Windows/Linux/macOS)

## Implementation Plan

1. **Phase 1: Core view model**
   - Create `ExternalEntityWorkspaceTabViewModel`
   - Implement URL parsing from external entity
   - Add to workspace tab system
   - Tests for view model logic

2. **Phase 2: WebView integration** 
   - Add Avalonia.WebView package dependency
   - Create XAML view with WebView control
   - Implement navigation commands
   - Handle WebView lifecycle and disposal

3. **Phase 3: Error handling**
   - Detect WebView initialization failure
   - Implement fallback to external browser
   - Add error UI states
   - Tests for error scenarios

4. **Phase 4: Polish**
   - Add navigation toolbar
   - Implement loading indicators
   - Add security warnings for HTTP
   - Performance testing and optimization

## Dependencies

- `Avalonia.WebView` (NuGet package)
- Platform-specific WebView runtimes:
  - Windows: WebView2 Runtime (auto-install or bundled)
  - Linux: WebKitGTK
  - macOS: Built-in WKWebView

## Notes

- External entities can be used for documentation links, API references, monitoring dashboards, etc.
- This design treats URLs as read-only views - no editing of the external entity from browser
- Consider adding "open all URLs" shortcut to external entity for convenience
