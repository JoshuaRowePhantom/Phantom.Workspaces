using Phantom.Workspaces.ViewModels;
using System.Globalization;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Tests;

public sealed class EntityFieldEditorViewModelTests
{
    [PhantomAvaloniaFact]
    public void StringEditor_TogglesBetweenReadAndEditModes()
    {
        var editor = new StringFieldEditorViewModel("title", "Getting Started");

        Assert.True(editor.IsReadMode);
        Assert.False(editor.IsEditMode);

        editor.IsEditMode = true;
        Assert.False(editor.IsReadMode);
        Assert.True(editor.IsEditMode);

        editor.IsEditMode = false;
        Assert.True(editor.IsReadMode);
        Assert.False(editor.IsEditMode);
    }

    [PhantomAvaloniaFact]
    public void MimeAttachmentEditor_UpdatesMarkdownModeVisibilityForReadAndEdit()
    {
        var editor = new MarkdownMimeAttachmentFieldEditorViewModel(
            "content",
            "text/markdown",
            "# Heading",
            "documentation/getting-started.md");

        Assert.True(editor.ShowMarkdownReadMode);
        Assert.False(editor.ShowMarkdownEditMode);
        Assert.False(editor.ShowPlainTextReadMode);
        Assert.False(editor.ShowPlainTextEditMode);

        editor.IsEditMode = true;
        Assert.False(editor.ShowMarkdownReadMode);
        Assert.True(editor.ShowMarkdownEditMode);
        Assert.False(editor.ShowPlainTextReadMode);
        Assert.False(editor.ShowPlainTextEditMode);
    }

    [PhantomAvaloniaFact]
    public void MimeAttachmentClone_PreservesMarkdownSpecificEditorType()
    {
        var markdownEditor = new MarkdownMimeAttachmentFieldEditorViewModel(
            "content",
            "text/markdown",
            "# Heading",
            "documentation/getting-started.md");
        var plainEditor = new PlainMimeAttachmentFieldEditorViewModel(
            "content",
            "text/plain",
            "hello",
            null);

        var markdownClone = markdownEditor.Clone();
        var plainClone = plainEditor.Clone();

        Assert.IsType<MarkdownMimeAttachmentFieldEditorViewModel>(markdownClone);
        Assert.IsType<PlainMimeAttachmentFieldEditorViewModel>(plainClone);
    }

    [PhantomAvaloniaFact]
    public void NodeEditMode_PropagatesToNestedEditors()
    {
        var childEditor = new StringFieldEditorViewModel("text", "hello");
        var objectEditor = new ObjectFieldEditorViewModel("content", [childEditor]);
        var node = new EntityListNodeViewModel(
            "Documentation Note",
            "note",
            ["documentation", "getting-started"],
            "[\"documentation\",\"getting-started\"]",
            [objectEditor]);

        Assert.True(objectEditor.IsReadMode);
        Assert.True(childEditor.IsReadMode);

        node.Card.IsEditMode = true;
        Assert.True(objectEditor.IsEditMode);
        Assert.True(childEditor.IsEditMode);

        node.Card.IsEditMode = false;
        Assert.True(objectEditor.IsReadMode);
        Assert.True(childEditor.IsReadMode);
    }

    [PhantomAvaloniaFact]
    public void NodeDiscardEditMode_RevertsFieldValues()
    {
        var titleEditor = new StringFieldEditorViewModel("title", "Before");
        var node = new EntityListNodeViewModel(
            "Documentation Note",
            "note",
            ["documentation", "note"],
            "[\"documentation\",\"note\"]",
            [titleEditor]);

        node.Card.ToggleEditModeCommand.Execute(null);
        titleEditor.Value = "Changed";
        node.Card.DiscardEditModeCommand.Execute(null);

        var revertedEditor = Assert.Single(node.Card.FieldEditors, static editor => editor.FieldName == "title");
        var revertedStringEditor = Assert.IsType<StringFieldEditorViewModel>(revertedEditor);
        Assert.Equal("Before", revertedStringEditor.Value);
        Assert.False(node.Card.IsEditMode);
    }

    [PhantomAvaloniaFact]
    public void NodeSaveEditMode_PersistsFieldValuesInCurrentEditors()
    {
        var titleEditor = new StringFieldEditorViewModel("title", "Before");
        var node = new EntityListNodeViewModel(
            "Documentation Note",
            "note",
            ["documentation", "note"],
            "[\"documentation\",\"note\"]",
            [titleEditor]);

        node.Card.ToggleEditModeCommand.Execute(null);
        titleEditor.Value = "Changed";
        node.Card.SaveEditModeCommand.Execute(null);

        var savedEditor = Assert.Single(node.Card.FieldEditors, static editor => editor.FieldName == "title");
        var savedStringEditor = Assert.IsType<StringFieldEditorViewModel>(savedEditor);
        Assert.Equal("Changed", savedStringEditor.Value);
        Assert.False(node.Card.IsEditMode);
    }

    [PhantomAvaloniaFact]
    public void JsonSchemaEditor_PrettyPrintsAndFormatsMarkdownCodeBlock()
    {
        var editor = new JsonSchemaFieldEditorViewModel("schema", "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"}}}");

        Assert.Contains(Environment.NewLine + "  \"type\": \"object\"", editor.JsonText, StringComparison.Ordinal);
        Assert.StartsWith("```json", editor.MarkdownText, StringComparison.Ordinal);
        Assert.EndsWith("```", editor.MarkdownText, StringComparison.Ordinal);
    }

    [PhantomAvaloniaFact]
    public void LocalStringEditor_UsesCurrentLocaleAndFallsBackToDefault()
    {
        var localizedValues = new[]
        {
            new LocalizedTextValueViewModel("default", "Default value"),
            new LocalizedTextValueViewModel(CultureInfo.CurrentUICulture.Name, "Localized value"),
        };

        var editor = new LocalStringFieldEditorViewModel("title", localizedValues);

        Assert.Equal("Localized value", editor.Value);
    }

    [PhantomAvaloniaFact]
    public void LocalStringEditor_AddLocale_MigratesToDefaultLocale()
    {
        var editor = new LocalStringFieldEditorViewModel("title", "Simple value");

        editor.IsEditMode = true;
        editor.AddLocaleCommand.Execute(null);

        Assert.True(editor.IsLocalized);
        Assert.Contains(editor.OtherLocalizedValues, value => string.Equals(value.Locale, "new-locale", StringComparison.Ordinal));
        Assert.Equal("Simple value", editor.Value);
    }

    [PhantomAvaloniaFact]
    public void LocalizedMimeEditor_AddLocale_MigratesToDefaultLocale()
    {
        var editor = new LocalizedMimeAttachmentFieldEditorViewModel(
            "content",
            new MarkdownMimeAttachmentFieldEditorViewModel("content", "text/markdown", "# Heading", null));

        editor.IsEditMode = true;
        editor.AddLocaleCommand.Execute(null);

        Assert.True(editor.IsLocalized);
        var markdownEditor = Assert.IsType<MarkdownMimeAttachmentFieldEditorViewModel>(editor.ActiveEditor);
        Assert.Equal("# Heading", markdownEditor.TextContent);
        Assert.Contains(editor.OtherLocalizedValues, value => string.Equals(value.Locale, "new-locale", StringComparison.Ordinal));
    }

    [PhantomAvaloniaFact]
    public void LocalizedMimeEditor_UsesCurrentLocaleAndFallsBackToDefault()
    {
        var editor = new LocalizedMimeAttachmentFieldEditorViewModel(
            "content",
            [
                new LocalizedMimeAttachmentValueViewModel(
                    "default",
                    new MarkdownMimeAttachmentFieldEditorViewModel("content", "text/markdown", "Default markdown", null)),
                new LocalizedMimeAttachmentValueViewModel(
                    CultureInfo.CurrentUICulture.Name,
                    new MarkdownMimeAttachmentFieldEditorViewModel("content", "text/markdown", "Localized markdown", null)),
            ]);

        var activeEditor = Assert.IsType<MarkdownMimeAttachmentFieldEditorViewModel>(editor.ActiveEditor);
        Assert.Equal("Localized markdown", activeEditor.TextContent);
    }
}
