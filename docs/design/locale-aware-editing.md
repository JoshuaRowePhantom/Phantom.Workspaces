# Locale-aware editing in entity field editors

## Goal

Entity field editors should display and edit localized content consistently across localizable field types.

## Display behavior

1. If a field is not localized, render the single value directly.
2. If a field is localized, resolve the displayed value by locale in this order:
   1. exact `CultureInfo.CurrentUICulture.Name` match (for example `en-US`)
   2. neutral `CultureInfo.CurrentUICulture.TwoLetterISOLanguageName` match (for example `en`)
   3. `default`
   4. first available locale entry

## Edit behavior

1. Editing always targets the value currently shown by the locale resolution logic.
2. A `+` control is available in edit mode for localizable field editors.
3. Pressing `+` creates localized structure when needed:
   1. for non-localized values, migrate current content into `default`
   2. add a new locale entry (`new-locale`, then `new-locale-2`, etc.)
4. When more than one locale exists, an "Other locales" expander is shown in edit mode so additional locale values are visible and editable.

## Field coverage

- `local-string` fields use locale-aware editing with fallback display.
- `mime-attachment` fields use locale-aware editing for both single-value and localized map shapes.

## Notes

- Locale selection and fallback are UI concerns only in this layer; persistence format remains schema-driven entity JSON.
- The UI keeps localized editing asynchronous-safe by avoiding blocking calls in editor construction paths.
