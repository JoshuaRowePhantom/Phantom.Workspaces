# External Entity Card View Design

## Overview

When an `external` entity is displayed in the main-view entity list or the entity browser, its card should render only the entity's URLs as clickable links — not the full raw JSON fields. If the entity has exactly one URL and its key is `"default"`, the key label is suppressed and only the hyperlink is shown.

## Current Behaviour

External entities are rendered via the generic raw card view. The card displays every JSON property (`$schema`, `display-name`, `entity-id`, plus collapsible expanders for `entity-types`, `names`, `related-entity-ids`, `relationship-roles`, and `urls`). The URL value inside the `urls` expander is unclickable plain text.

## Desired Behaviour

The external entity card shows **only the URLs section**, rendered as a list of clickable hyperlinks:

- **Single URL, key is `"default"`**: show just the URL as a hyperlink with no label.
- **Single URL, key is not `"default"`**: show `<key>: <hyperlink>`.
- **Multiple URLs**: show one row per URL as `<key>: <hyperlink>`.

Clicking a URL hyperlink opens it via the existing `OpenExternalEntityShortcutHandler` / `WebViewModel` pipeline (or directly opens the system browser for that specific URL).

No other JSON fields (`$schema`, `entity-id`, `names`, etc.) are shown. The display name and entity-type badge (already rendered by the card header, outside the field list) remain unchanged.

## Implementation

### 1 — New `ExternalEntityCardViewModel`

A dedicated field-editor view model (or display-item view model) that exposes the URL list:

```csharp
public sealed record ExternalUrlViewModel(string Key, string Url, bool ShowKey);

public sealed class ExternalEntityCardViewModel : ViewModelBase
{
    public IReadOnlyList<ExternalUrlViewModel> Urls { get; }
}
```

`ShowKey = false` when `Urls.Count == 1 && Urls[0].Key == "default"`.

### 2 — Card view resolver

`EntityCardViewResolver.ResolveViewName` currently always returns `"raw"` (both branches of the ternary return `RawViewName`). Fix this so that when the entity has entity type `"external"` in its snapshot, the resolver returns `"external"` instead.

```csharp
public string ResolveViewName(SubscribedEntityViewModel entity, string? requestedViewName = null)
{
    if (!string.Equals(requestedViewName, RawViewName, StringComparison.Ordinal)
        && entity.IsEntityType("external"))
    {
        return "external";
    }
    return RawViewName;
}
```

### 3 — XAML data template

Add a `DataTemplate` for `ExternalEntityCardViewModel` (or bind from `EntityCardViewModel.CardViewName == "external"`) in `WorkspaceDataTemplates.axaml`:

```xml
<!-- Rendered when CardViewName == "external" -->
<DataTemplate DataType="vm:ExternalEntityCardViewModel">
    <ItemsControl ItemsSource="{Binding Urls}">
        <ItemsControl.ItemTemplate>
            <DataTemplate DataType="vm:ExternalUrlViewModel">
                <StackPanel Orientation="Horizontal" Spacing="4">
                    <!-- Label: shown only when ShowKey is true -->
                    <TextBlock Text="{Binding Key}"
                               IsVisible="{Binding ShowKey}"
                               Classes="muted workspace-field-label" />
                    <Button Content="{Binding Url}"
                            Classes="workspace-url-link"
                            Command="{Binding $parent[UserControl].DataContext.OpenUrlCommand}"
                            CommandParameter="{Binding Url}" />
                </StackPanel>
            </DataTemplate>
        </ItemsControl.ItemTemplate>
    </ItemsControl>
</DataTemplate>
```

The `OpenUrlCommand` on the enclosing card view model opens the URL via `OpenExternalEntityShortcutHandler` for the specific key (so the right browser tab is created / focused).

### 4 — `EntityCardViewModel` plumbing

`EntityCardViewModel` needs to populate an `ExternalEntityCardViewModel` when `CardViewName == "external"` rather than the generic `FieldEditors` list. One approach: add an `ExternalCard` property that is non-null only for external cards; the XAML switches between the raw field list and the external URL list based on this.

Alternatively, introduce a `ContentViewModel` property typed as `object?` that holds either `IReadOnlyCollection<EntityFieldEditorViewModel>` (raw) or `ExternalEntityCardViewModel` (external); the data template system dispatches automatically.

## Affected Files

| File | Change |
|---|---|
| `Phantom.Workspaces\ViewModels\EntityCardViewResolver.cs` | Return `"external"` for external entities |
| `Phantom.Workspaces\ViewModels\EntityCardViewModel.cs` | Populate `ExternalEntityCardViewModel` when card view is `"external"` |
| `Phantom.Workspaces\ViewModels\ExternalEntityCardViewModel.cs` | New file — URL list view model |
| `Phantom.Workspaces\Templates\WorkspaceDataTemplates.axaml` | New data template for `ExternalEntityCardViewModel` |
| `Phantom.Workspaces\Styles\WorkspaceStyles.axaml` (or equivalent) | Add `workspace-url-link` button style (underline, cursor pointer) |

## Tests

**`ExternalEntityCardViewModel_SingleDefaultUrl_SuppressesKeyLabel`**
- Build `ExternalEntityCardViewModel` from `{ "urls": { "default": "https://example.com" } }`
- Assert `Urls.Count == 1`, `ShowKey == false`

**`ExternalEntityCardViewModel_SingleNonDefaultUrl_ShowsKeyLabel`**
- Build from `{ "urls": { "docs": "https://example.com/docs" } }`
- Assert `Urls[0].ShowKey == true`, `Urls[0].Key == "docs"`

**`ExternalEntityCardViewModel_MultipleUrls_AllShowKeyLabels`**
- Build from `{ "urls": { "default": "...", "docs": "..." } }`
- Assert both rows have `ShowKey == true`

**`EntityCardViewResolver_ExternalEntity_ReturnsExternalViewName`**
- Assert `ResolveViewName(externalEntity)` returns `"external"`

**`EntityCardViewResolver_NonExternalEntity_ReturnsRaw`**
- Assert `ResolveViewName(workspaceEntity)` returns `"raw"`
