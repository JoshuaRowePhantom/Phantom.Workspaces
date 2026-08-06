using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Phantom.Workspaces.ViewModels;

public abstract class EntityFieldEditorViewModel
    : ViewModelBase
{
    private bool isEditMode;

    protected EntityFieldEditorViewModel(
        string fieldName,
        string typeName)
    {
        this.FieldName = fieldName;
        this.TypeName = typeName;
    }

    public string FieldName { get; }

    public string TypeName { get; }

    public bool IsEditMode
    {
        get => this.isEditMode;
        set
        {
            if (!this.SetProperty(ref this.isEditMode, value))
            {
                return;
            }

            this.RaisePropertyChanged(nameof(this.IsReadMode));
            this.OnEditModeChanged(value);
        }
    }

    public bool IsReadMode => !this.IsEditMode;

    /// <summary>
    /// Text of this editor's VALUE for search/highlight purposes (issue #1257). Property NAMES
    /// (<see cref="FieldName"/>) are intentionally excluded from the match set, so this returns only
    /// value text. Editors with no textual value return null.
    /// </summary>
    public virtual string? SearchableValueText => null;

    /// <summary>
    /// Invoked when <see cref="IsEditMode"/> changes. Override to cascade the edit mode to any child
    /// editors and to raise change notifications for mode-dependent presentation properties.
    /// </summary>
    protected virtual void OnEditModeChanged(
        bool isEditMode)
    {
    }

    public abstract EntityFieldEditorViewModel Clone();
}

public sealed class StringFieldEditorViewModel : EntityFieldEditorViewModel
{
    private string value;

    public StringFieldEditorViewModel(
        string fieldName,
        string value)
        : base(fieldName, "string")
    {
        this.value = value;
    }

    public string Value
    {
        get => this.value;
        set => this.SetProperty(ref this.value, value);
    }

    public override string? SearchableValueText => this.Value;

    public override EntityFieldEditorViewModel Clone()
    {
        return new StringFieldEditorViewModel(this.FieldName, this.Value);
    }
}

public sealed class BooleanToggleFieldEditorViewModel : EntityFieldEditorViewModel
{
    private bool value;

    public BooleanToggleFieldEditorViewModel(
        string fieldName,
        bool value)
        : base(fieldName, "boolean")
    {
        this.value = value;
    }

    public bool Value
    {
        get => this.value;
        set
        {
            if (this.SetProperty(ref this.value, value))
            {
                this.RaisePropertyChanged(nameof(this.DisplayValue));
            }
        }
    }

    /// <summary>Read-mode presentation of the boolean as lower-case JSON-style text.</summary>
    public string DisplayValue => this.value ? "true" : "false";

    public override string? SearchableValueText => this.DisplayValue;

    public override EntityFieldEditorViewModel Clone()
    {
        return new BooleanToggleFieldEditorViewModel(this.FieldName, this.Value);
    }
}

public sealed class LocalStringFieldEditorViewModel : EntityFieldEditorViewModel
{
    private readonly ObservableCollection<LocalizedTextValueViewModel> localizedValues = [];
    private bool isLocalized;
    private string unlocalizedValue = string.Empty;
    private LocalizedTextValueViewModel? activeLocalizedValue;

    public LocalStringFieldEditorViewModel(
        string fieldName,
        string value)
        : base(fieldName, "local-string")
    {
        this.unlocalizedValue = value;
        this.AddLocaleCommand = new RelayCommand(_ => this.AddLocale());
    }

    public LocalStringFieldEditorViewModel(
        string fieldName,
        IReadOnlyCollection<LocalizedTextValueViewModel> localizedValues)
        : base(fieldName, "local-string")
    {
        this.AddLocaleCommand = new RelayCommand(_ => this.AddLocale());
        this.isLocalized = true;
        foreach (var localizedValue in localizedValues)
        {
            this.localizedValues.Add(localizedValue);
            localizedValue.PropertyChanged += this.OnLocalizedValuePropertyChanged;
        }

        this.UpdateActiveLocalizedValue();
    }

    public string Value
    {
        get => this.isLocalized
            ? this.activeLocalizedValue?.Value ?? string.Empty
            : this.unlocalizedValue;
        set
        {
            if (!this.isLocalized)
            {
                this.SetProperty(ref this.unlocalizedValue, value);
                return;
            }

            if (this.activeLocalizedValue is null)
            {
                return;
            }

            this.activeLocalizedValue.Value = value;
            this.RaisePropertyChanged(nameof(this.Value));
        }
    }

    public bool IsLocalized => this.isLocalized;

    public override string? SearchableValueText => this.Value;

    public string? ActiveLocale => this.activeLocalizedValue?.Locale;

    public IReadOnlyCollection<LocalizedTextValueViewModel> OtherLocalizedValues =>
        this.localizedValues
            .Where(localizedValue => !ReferenceEquals(localizedValue, this.activeLocalizedValue))
            .ToArray();

    public bool HasMultipleLocales => this.isLocalized && this.localizedValues.Count > 1;

    public bool ShowOtherLocalesExpander => this.IsEditMode && this.HasMultipleLocales;

    public bool ShowAddLocaleButton => this.IsEditMode;

    public RelayCommand AddLocaleCommand { get; }

    protected override void OnEditModeChanged(
        bool isEditMode)
    {
        this.RaisePropertyChanged(nameof(this.ShowOtherLocalesExpander));
        this.RaisePropertyChanged(nameof(this.ShowAddLocaleButton));
    }

    public override EntityFieldEditorViewModel Clone()
    {
        if (!this.isLocalized)
        {
            return new LocalStringFieldEditorViewModel(this.FieldName, this.unlocalizedValue);
        }

        return new LocalStringFieldEditorViewModel(
            this.FieldName,
            this.localizedValues.Select(static localizedValue => localizedValue.Clone()).ToArray());
    }

    private void AddLocale()
    {
        if (!this.isLocalized)
        {
            var defaultValue = new LocalizedTextValueViewModel("default", this.unlocalizedValue);
            defaultValue.PropertyChanged += this.OnLocalizedValuePropertyChanged;
            this.localizedValues.Add(defaultValue);
            this.isLocalized = true;
            this.unlocalizedValue = string.Empty;
        }
        else
        {
            this.EnsureDefaultLocaleExists();
        }

        var sourceValue = this.activeLocalizedValue?.Value
                          ?? this.localizedValues.FirstOrDefault(static value =>
                              string.Equals(value.Locale, "default", StringComparison.Ordinal))?.Value
                          ?? string.Empty;
        var locale = CreateUniqueLocale(this.localizedValues.Select(static value => value.Locale));
        var newValue = new LocalizedTextValueViewModel(locale, sourceValue);
        newValue.PropertyChanged += this.OnLocalizedValuePropertyChanged;
        this.localizedValues.Add(newValue);
        this.UpdateActiveLocalizedValue();
    }

    private void EnsureDefaultLocaleExists()
    {
        if (this.localizedValues.Any(static value => string.Equals(value.Locale, "default", StringComparison.Ordinal)))
        {
            return;
        }

        var sourceValue = this.activeLocalizedValue?.Value ?? string.Empty;
        var defaultValue = new LocalizedTextValueViewModel("default", sourceValue);
        defaultValue.PropertyChanged += this.OnLocalizedValuePropertyChanged;
        this.localizedValues.Add(defaultValue);
    }

    private void OnLocalizedValuePropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(LocalizedTextValueViewModel.Locale), StringComparison.Ordinal)
            && !string.Equals(e.PropertyName, nameof(LocalizedTextValueViewModel.Value), StringComparison.Ordinal))
        {
            return;
        }

        this.UpdateActiveLocalizedValue();
        this.RaisePropertyChanged(nameof(this.Value));
    }

    private void UpdateActiveLocalizedValue()
    {
        if (!this.isLocalized)
        {
            this.activeLocalizedValue = null;
            this.RaisePropertyChanged(nameof(this.IsLocalized));
            this.RaisePropertyChanged(nameof(this.ActiveLocale));
            this.RaisePropertyChanged(nameof(this.OtherLocalizedValues));
            this.RaisePropertyChanged(nameof(this.HasMultipleLocales));
            this.RaisePropertyChanged(nameof(this.ShowOtherLocalesExpander));
            this.RaisePropertyChanged(nameof(this.Value));
            return;
        }

        this.activeLocalizedValue = SelectLocaleValue(this.localizedValues);
        this.RaisePropertyChanged(nameof(this.IsLocalized));
        this.RaisePropertyChanged(nameof(this.ActiveLocale));
        this.RaisePropertyChanged(nameof(this.OtherLocalizedValues));
        this.RaisePropertyChanged(nameof(this.HasMultipleLocales));
        this.RaisePropertyChanged(nameof(this.ShowOtherLocalesExpander));
        this.RaisePropertyChanged(nameof(this.Value));
    }

    private static LocalizedTextValueViewModel? SelectLocaleValue(
        IReadOnlyCollection<LocalizedTextValueViewModel> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var locale = CultureInfo.CurrentUICulture.Name;
        var match = values.FirstOrDefault(value => string.Equals(value.Locale, locale, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match;
        }

        var neutralLocale = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        match = values.FirstOrDefault(value => string.Equals(value.Locale, neutralLocale, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match;
        }

        match = values.FirstOrDefault(value => string.Equals(value.Locale, "default", StringComparison.OrdinalIgnoreCase));
        return match ?? values.First();
    }

    private static string CreateUniqueLocale(
        IEnumerable<string> locales)
    {
        var existing = new HashSet<string>(locales, StringComparer.OrdinalIgnoreCase);
        var candidate = "new-locale";
        var suffix = 2;
        while (existing.Contains(candidate))
        {
            candidate = $"new-locale-{suffix++}";
        }

        return candidate;
    }
}

public sealed class LocalizedTextValueViewModel : ViewModelBase
{
    private string locale;
    private string value;

    public LocalizedTextValueViewModel(
        string locale,
        string value)
    {
        this.locale = locale;
        this.value = value;
    }

    public string Locale
    {
        get => this.locale;
        set => this.SetProperty(ref this.locale, value);
    }

    public string Value
    {
        get => this.value;
        set => this.SetProperty(ref this.value, value);
    }

    public LocalizedTextValueViewModel Clone()
    {
        return new LocalizedTextValueViewModel(this.Locale, this.Value);
    }
}

public class MimeAttachmentFieldEditorViewModel : EntityFieldEditorViewModel
{
    private string mimeType;
    private string? textContent;
    private string? url;

    public MimeAttachmentFieldEditorViewModel(
        string fieldName,
        string mimeType,
        string? textContent,
        string? url,
        bool isInline = false)
        : base(fieldName, "mime-attachment")
    {
        this.mimeType = mimeType;
        this.textContent = textContent;
        this.url = url;
        this.IsInline = isInline;
    }

    /// <summary>
    /// When set, the field is rendered inline (e.g. a note's content): in read mode only the rendered
    /// markdown is shown, without the expander, mime-type, url, and content.text chrome.
    /// </summary>
    public bool IsInline { get; }

    public string MimeType
    {
        get => this.mimeType;
        set
        {
            if (!this.SetProperty(ref this.mimeType, value))
            {
                return;
            }

            this.RaisePropertyChanged(nameof(this.IsMarkdown));
        }
    }

    public string? TextContent
    {
        get => this.textContent;
        set => this.SetProperty(ref this.textContent, value);
    }

    public string? Url
    {
        get => this.url;
        set => this.SetProperty(ref this.url, value);
    }

    public bool IsMarkdown => this.MimeType.Equals("text/markdown", StringComparison.OrdinalIgnoreCase);

    public bool ShowMarkdownReadMode => this.IsReadMode && this.IsMarkdown;

    public bool ShowMarkdownEditMode => this.IsEditMode && this.IsMarkdown;

    public bool ShowPlainTextReadMode => this.IsReadMode && !this.IsMarkdown;

    public bool ShowPlainTextEditMode => this.IsEditMode && !this.IsMarkdown;

    /// <summary>The rendered markdown is shown on its own (no chrome) when inline and in read mode.</summary>
    public bool ShowInlineMarkdownReadMode => this.IsInline && this.IsReadMode && this.IsMarkdown;

    /// <summary>The expander chrome (mime-type, url, content.text rows) is shown unless rendering inline.</summary>
    public bool ShowChrome => !this.ShowInlineMarkdownReadMode;

    protected override void OnEditModeChanged(
        bool isEditMode)
    {
        this.RaisePropertyChanged(nameof(this.ShowMarkdownReadMode));
        this.RaisePropertyChanged(nameof(this.ShowMarkdownEditMode));
        this.RaisePropertyChanged(nameof(this.ShowPlainTextReadMode));
        this.RaisePropertyChanged(nameof(this.ShowPlainTextEditMode));
        this.RaisePropertyChanged(nameof(this.ShowInlineMarkdownReadMode));
        this.RaisePropertyChanged(nameof(this.ShowChrome));
    }

    public override EntityFieldEditorViewModel Clone()
    {
        return this.IsMarkdown
            ? new MarkdownMimeAttachmentFieldEditorViewModel(this.FieldName, this.MimeType, this.TextContent, this.Url, this.IsInline)
            : new PlainMimeAttachmentFieldEditorViewModel(this.FieldName, this.MimeType, this.TextContent, this.Url, this.IsInline);
    }
}

public sealed class MarkdownMimeAttachmentFieldEditorViewModel : MimeAttachmentFieldEditorViewModel
{
    public MarkdownMimeAttachmentFieldEditorViewModel(
        string fieldName,
        string mimeType,
        string? textContent,
        string? url,
        bool isInline = false)
        : base(fieldName, mimeType, textContent, url, isInline)
    {
    }
}

public sealed class PlainMimeAttachmentFieldEditorViewModel : MimeAttachmentFieldEditorViewModel
{
    public PlainMimeAttachmentFieldEditorViewModel(
        string fieldName,
        string mimeType,
        string? textContent,
        string? url,
        bool isInline = false)
        : base(fieldName, mimeType, textContent, url, isInline)
    {
    }
}

public sealed class LocalizedMimeAttachmentFieldEditorViewModel : EntityFieldEditorViewModel
{
    private readonly ObservableCollection<LocalizedMimeAttachmentValueViewModel> localizedValues = [];
    private bool isLocalized;
    private MimeAttachmentFieldEditorViewModel unlocalizedValue;
    private LocalizedMimeAttachmentValueViewModel? activeLocalizedValue;

    public LocalizedMimeAttachmentFieldEditorViewModel(
        string fieldName,
        MimeAttachmentFieldEditorViewModel value)
        : base(fieldName, "mime-attachment")
    {
        this.unlocalizedValue = value;
        this.AddLocaleCommand = new RelayCommand(_ => this.AddLocale());
    }

    public LocalizedMimeAttachmentFieldEditorViewModel(
        string fieldName,
        IReadOnlyCollection<LocalizedMimeAttachmentValueViewModel> localizedValues)
        : base(fieldName, "mime-attachment")
    {
        this.AddLocaleCommand = new RelayCommand(_ => this.AddLocale());
        this.isLocalized = true;
        this.unlocalizedValue = new PlainMimeAttachmentFieldEditorViewModel(fieldName, "application/octet-stream", null, null);
        foreach (var localizedValue in localizedValues)
        {
            this.localizedValues.Add(localizedValue);
            localizedValue.PropertyChanged += this.OnLocalizedValuePropertyChanged;
        }

        this.UpdateActiveLocalizedValue();
    }

    public bool IsLocalized => this.isLocalized;

    public string? ActiveLocale => this.activeLocalizedValue?.Locale;

    public MimeAttachmentFieldEditorViewModel ActiveEditor => this.isLocalized
        ? this.activeLocalizedValue?.Editor ?? this.unlocalizedValue
        : this.unlocalizedValue;

    public IReadOnlyCollection<LocalizedMimeAttachmentValueViewModel> OtherLocalizedValues =>
        this.localizedValues
            .Where(localizedValue => !ReferenceEquals(localizedValue, this.activeLocalizedValue))
            .ToArray();

    public bool HasMultipleLocales => this.isLocalized && this.localizedValues.Count > 1;

    public bool ShowOtherLocalesExpander => this.IsEditMode && this.HasMultipleLocales;

    public bool ShowAddLocaleButton => this.IsEditMode;

    /// <summary>In read mode an inline attachment renders only its active editor, without the field label.</summary>
    public bool ShowInlineReadMode => this.ActiveEditor.IsInline && this.IsReadMode;

    /// <summary>The labelled chrome (field name, add-locale button) is shown unless rendering inline.</summary>
    public bool ShowChrome => !this.ShowInlineReadMode;

    public RelayCommand AddLocaleCommand { get; }

    protected override void OnEditModeChanged(
        bool isEditMode)
    {
        this.unlocalizedValue.IsEditMode = isEditMode;
        foreach (var localizedValue in this.localizedValues)
        {
            localizedValue.Editor.IsEditMode = isEditMode;
        }

        this.RaisePropertyChanged(nameof(this.ShowOtherLocalesExpander));
        this.RaisePropertyChanged(nameof(this.ShowAddLocaleButton));
        this.RaisePropertyChanged(nameof(this.ShowInlineReadMode));
        this.RaisePropertyChanged(nameof(this.ShowChrome));
    }

    public override EntityFieldEditorViewModel Clone()
    {
        if (!this.isLocalized)
        {
            return new LocalizedMimeAttachmentFieldEditorViewModel(this.FieldName, (MimeAttachmentFieldEditorViewModel)this.unlocalizedValue.Clone());
        }

        return new LocalizedMimeAttachmentFieldEditorViewModel(
            this.FieldName,
            this.localizedValues.Select(static localizedValue => localizedValue.Clone()).ToArray());
    }

    private void AddLocale()
    {
        if (!this.isLocalized)
        {
            var defaultValue = new LocalizedMimeAttachmentValueViewModel("default", (MimeAttachmentFieldEditorViewModel)this.unlocalizedValue.Clone());
            defaultValue.PropertyChanged += this.OnLocalizedValuePropertyChanged;
            this.localizedValues.Add(defaultValue);
            this.isLocalized = true;
        }
        else
        {
            this.EnsureDefaultLocaleExists();
        }

        var sourceEditor = this.activeLocalizedValue?.Editor
                           ?? this.localizedValues.FirstOrDefault(static value =>
                               string.Equals(value.Locale, "default", StringComparison.Ordinal))?.Editor;
        var locale = CreateUniqueLocale(this.localizedValues.Select(static value => value.Locale));
        var newValue = new LocalizedMimeAttachmentValueViewModel(
            locale,
            sourceEditor is null
                ? new PlainMimeAttachmentFieldEditorViewModel(this.FieldName, "application/octet-stream", null, null)
                : (MimeAttachmentFieldEditorViewModel)sourceEditor.Clone());
        newValue.PropertyChanged += this.OnLocalizedValuePropertyChanged;
        this.localizedValues.Add(newValue);
        this.UpdateActiveLocalizedValue();
        newValue.Editor.IsEditMode = this.IsEditMode;
    }

    private void EnsureDefaultLocaleExists()
    {
        if (this.localizedValues.Any(static value => string.Equals(value.Locale, "default", StringComparison.Ordinal)))
        {
            return;
        }

        var sourceEditor = this.activeLocalizedValue?.Editor ?? this.unlocalizedValue;
        var defaultValue = new LocalizedMimeAttachmentValueViewModel("default", (MimeAttachmentFieldEditorViewModel)sourceEditor.Clone());
        defaultValue.PropertyChanged += this.OnLocalizedValuePropertyChanged;
        this.localizedValues.Add(defaultValue);
    }

    private void OnLocalizedValuePropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(LocalizedMimeAttachmentValueViewModel.Locale), StringComparison.Ordinal))
        {
            return;
        }

        this.UpdateActiveLocalizedValue();
    }

    private void UpdateActiveLocalizedValue()
    {
        if (!this.isLocalized)
        {
            this.activeLocalizedValue = null;
            this.RaisePropertyChanged(nameof(this.IsLocalized));
            this.RaisePropertyChanged(nameof(this.ActiveLocale));
            this.RaisePropertyChanged(nameof(this.ActiveEditor));
            this.RaisePropertyChanged(nameof(this.OtherLocalizedValues));
            this.RaisePropertyChanged(nameof(this.HasMultipleLocales));
            this.RaisePropertyChanged(nameof(this.ShowOtherLocalesExpander));
            this.RaisePropertyChanged(nameof(this.ShowInlineReadMode));
            this.RaisePropertyChanged(nameof(this.ShowChrome));
            return;
        }

        this.activeLocalizedValue = SelectLocaleValue(this.localizedValues);
        this.RaisePropertyChanged(nameof(this.IsLocalized));
        this.RaisePropertyChanged(nameof(this.ActiveLocale));
        this.RaisePropertyChanged(nameof(this.ActiveEditor));
        this.RaisePropertyChanged(nameof(this.OtherLocalizedValues));
        this.RaisePropertyChanged(nameof(this.HasMultipleLocales));
        this.RaisePropertyChanged(nameof(this.ShowOtherLocalesExpander));
        this.RaisePropertyChanged(nameof(this.ShowInlineReadMode));
        this.RaisePropertyChanged(nameof(this.ShowChrome));
    }

    private static LocalizedMimeAttachmentValueViewModel? SelectLocaleValue(
        IReadOnlyCollection<LocalizedMimeAttachmentValueViewModel> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var locale = CultureInfo.CurrentUICulture.Name;
        var match = values.FirstOrDefault(value => string.Equals(value.Locale, locale, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match;
        }

        var neutralLocale = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        match = values.FirstOrDefault(value => string.Equals(value.Locale, neutralLocale, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match;
        }

        match = values.FirstOrDefault(value => string.Equals(value.Locale, "default", StringComparison.OrdinalIgnoreCase));
        return match ?? values.First();
    }

    private static string CreateUniqueLocale(
        IEnumerable<string> locales)
    {
        var existing = new HashSet<string>(locales, StringComparer.OrdinalIgnoreCase);
        var candidate = "new-locale";
        var suffix = 2;
        while (existing.Contains(candidate))
        {
            candidate = $"new-locale-{suffix++}";
        }

        return candidate;
    }
}

public sealed class LocalizedMimeAttachmentValueViewModel : ViewModelBase
{
    private string locale;

    public LocalizedMimeAttachmentValueViewModel(
        string locale,
        MimeAttachmentFieldEditorViewModel editor)
    {
        this.locale = locale;
        this.Editor = editor;
    }

    public string Locale
    {
        get => this.locale;
        set => this.SetProperty(ref this.locale, value);
    }

    public MimeAttachmentFieldEditorViewModel Editor { get; }

    public LocalizedMimeAttachmentValueViewModel Clone()
    {
        return new LocalizedMimeAttachmentValueViewModel(
            this.Locale,
            (MimeAttachmentFieldEditorViewModel)this.Editor.Clone());
    }
}

public sealed class JsonSchemaFieldEditorViewModel : EntityFieldEditorViewModel
{
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    private string jsonText;

    public JsonSchemaFieldEditorViewModel(
        string fieldName,
        string jsonText)
        : base(fieldName, "json-schema")
    {
        this.jsonText = NormalizeJson(jsonText);
    }

    public string JsonText
    {
        get => this.jsonText;
        set
        {
            var normalizedJson = NormalizeJson(value);
            if (!this.SetProperty(ref this.jsonText, normalizedJson))
            {
                return;
            }

            this.RaisePropertyChanged(nameof(this.MarkdownText));
        }
    }

    public string MarkdownText => $"```json{Environment.NewLine}{this.JsonText}{Environment.NewLine}```";

    public override EntityFieldEditorViewModel Clone()
    {
        return new JsonSchemaFieldEditorViewModel(this.FieldName, this.JsonText);
    }

    private static string NormalizeJson(
        string jsonText)
    {
        if (string.IsNullOrWhiteSpace(jsonText))
        {
            return "{}";
        }

        try
        {
            using var document = JsonDocument.Parse(jsonText);
            return JsonSerializer.Serialize(document.RootElement, IndentedJsonOptions);
        }
        catch (JsonException)
        {
            return jsonText;
        }
    }
}

public sealed class ObjectFieldEditorViewModel : EntityFieldEditorViewModel
{
    public ObjectFieldEditorViewModel(
        string fieldName,
        IReadOnlyCollection<EntityFieldEditorViewModel> fields)
        : base(fieldName, "object")
    {
        this.Fields = fields;
    }

    public IReadOnlyCollection<EntityFieldEditorViewModel> Fields { get; }

    protected override void OnEditModeChanged(
        bool isEditMode)
    {
        foreach (var field in this.Fields)
        {
            field.IsEditMode = isEditMode;
        }
    }

    public override EntityFieldEditorViewModel Clone()
    {
        return new ObjectFieldEditorViewModel(
            this.FieldName,
            this.Fields.Select(static field => field.Clone()).ToArray());
    }
}

public sealed class ArrayFieldEditorViewModel : EntityFieldEditorViewModel
{
    public ArrayFieldEditorViewModel(
        string fieldName,
        IReadOnlyCollection<EntityFieldEditorViewModel> items)
        : base(fieldName, "array")
    {
        this.Items = items;
    }

    public IReadOnlyCollection<EntityFieldEditorViewModel> Items { get; }

    protected override void OnEditModeChanged(
        bool isEditMode)
    {
        foreach (var item in this.Items)
        {
            item.IsEditMode = isEditMode;
        }
    }

    public override EntityFieldEditorViewModel Clone()
    {
        return new ArrayFieldEditorViewModel(
            this.FieldName,
            this.Items.Select(static item => item.Clone()).ToArray());
    }
}
