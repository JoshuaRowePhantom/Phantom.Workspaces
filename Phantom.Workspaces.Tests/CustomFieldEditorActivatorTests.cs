using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class CustomFieldEditorActivatorTests
{
    private static JsonElement StringValue(string value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return document.RootElement.Clone();
    }

    private static FieldEditorContext Context(string fieldEditorTypeName, JsonElement value, IEntityReferenceSearch? search = null)
    {
        var resolvedType = new ResolvedFieldType
        {
            TypeName = "string",
            FieldEditorTypeName = fieldEditorTypeName,
            EntityTypes = ["note"],
        };
        return new FieldEditorContext("field", value, resolvedType, search);
    }

    [Fact]
    public void TryCreate_RegisteredShortName_EntityReference_YieldsEntityReferenceEditor()
    {
        var activator = new CustomFieldEditorActivator();
        var created = activator.TryCreate("entity-reference", Context("entity-reference", StringValue("abc")), out var editor);

        Assert.True(created);
        Assert.IsType<EntityReferenceFieldEditorViewModel>(editor);
    }

    [Fact]
    public void TryCreate_RegisteredShortName_Markdown_YieldsMarkdownEditor()
    {
        var activator = new CustomFieldEditorActivator();
        var created = activator.TryCreate("markdown", Context("markdown", StringValue("hi")), out var editor);

        Assert.True(created);
        Assert.IsType<LocalizedMimeAttachmentFieldEditorViewModel>(editor);
    }

    [Fact]
    public void TryCreate_ShortNameMatchingIsCaseSensitive()
    {
        var activator = new CustomFieldEditorActivator();

        Assert.False(activator.TryCreate("Entity-Reference", Context("Entity-Reference", StringValue("abc")), out _));
    }

    [Fact]
    public void TryCreate_AssemblyQualifiedName_YieldsThatType()
    {
        var activator = new CustomFieldEditorActivator();
        var typeName = typeof(TestCustomFieldEditor).AssemblyQualifiedName!;

        var created = activator.TryCreate(typeName, Context(typeName, StringValue("v")), out var editor);

        Assert.True(created);
        Assert.IsType<TestCustomFieldEditor>(editor);
    }

    [Fact]
    public void TryCreate_UnknownShortNameOrType_FallsBack()
    {
        var activator = new CustomFieldEditorActivator();

        Assert.False(activator.TryCreate("not-a-real-editor", Context("not-a-real-editor", StringValue("v")), out var editor));
        Assert.Null(editor);
    }

    [Fact]
    public void Registry_ContainsAllWellKnownShortNames()
    {
        var activator = new CustomFieldEditorActivator();
        string[] expected = ["string", "local-string", "mime-attachment", "markdown", "json-schema", "entity-reference"];

        foreach (var name in expected)
        {
            Assert.Contains(name, activator.RegisteredShortNames);
        }
    }
}

public sealed class TestCustomFieldEditor : EntityFieldEditorViewModel
{
    public TestCustomFieldEditor(string fieldName)
        : base(fieldName, "test-custom")
    {
    }

    public override EntityFieldEditorViewModel Clone() => new TestCustomFieldEditor(this.FieldName);
}

public sealed class EntityReferenceFieldEditorViewModelTests
{
    private sealed class StubSearch : IEntityReferenceSearch
    {
        public string? LastSearchText { get; private set; }

        public IReadOnlyCollection<string>? LastEntityTypes { get; private set; }

        public Task<IReadOnlyList<EntityReferenceCandidate>> SearchAsync(
            string searchText,
            IReadOnlyCollection<string> entityTypes,
            CancellationToken cancellationToken = default)
        {
            this.LastSearchText = searchText;
            this.LastEntityTypes = entityTypes;
            IReadOnlyList<EntityReferenceCandidate> results =
            [
                new EntityReferenceCandidate("11111111-1111-1111-1111-111111111111", "Result One", "[\"tests\",\"one\"]"),
            ];
            return Task.FromResult(results);
        }

        public Task<EntityReferenceCandidate?> ResolveAsync(string entityId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<EntityReferenceCandidate?>(
                new EntityReferenceCandidate(entityId, "Resolved Name", "[\"tests\",\"resolved\"]"));
        }
    }

    [Fact]
    public async Task ResolveCurrentValueAsync_PopulatesDisplayNameAndTooltip()
    {
        var editor = new EntityReferenceFieldEditorViewModel("ref", "abc-id", ["note"], new StubSearch());

        await editor.ResolveCurrentValueAsync();

        Assert.Equal("Resolved Name", editor.ResolvedDisplayName);
        Assert.Contains("abc-id", editor.TooltipText);
        Assert.Contains("resolved", editor.TooltipText);
    }

    [Fact]
    public async Task SearchAsync_QueriesScopedToEntityTypes_AndPopulatesResults()
    {
        var search = new StubSearch();
        var editor = new EntityReferenceFieldEditorViewModel("ref", null, ["note", "task"], search);

        editor.SearchText = "hello";
        await editor.SearchAsync();

        Assert.Equal("hello", search.LastSearchText);
        Assert.Equal(new[] { "note", "task" }, search.LastEntityTypes);
        var result = Assert.Single(editor.Results);
        Assert.Equal("Result One", result.DisplayName);
        Assert.Equal("11111111-1111-1111-1111-111111111111", result.EntityId);
    }

    [Fact]
    public async Task SelectingResult_UpdatesValue()
    {
        var editor = new EntityReferenceFieldEditorViewModel("ref", null, ["note"], new StubSearch());
        editor.SearchText = "x";
        await editor.SearchAsync();

        editor.Results.Single().SelectCommand.Execute(null);

        Assert.Equal("11111111-1111-1111-1111-111111111111", editor.Value);
        Assert.True(editor.HasValue);
    }
}
