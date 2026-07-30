using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class FindViewModelTests
{
    private static EntityListNodeViewModel MakeStringNode(string displayName, string entityType = "entity")
    {
        var key = $"[\"{displayName}\"]";
        return new EntityListNodeViewModel(
            displayName: displayName,
            entityType: entityType,
            nameComponents: new[] { displayName },
            sortKey: key);
    }

    private static EntityListItemViewModel MakeItem(EntityListNodeViewModel node, int order, int level = 0, string? parentItemKey = null, IReadOnlyCollection<string>? childItemKeys = null, bool isExpanded = false)
    {
        return new EntityListItemViewModel(
            node,
            order: order,
            level: level,
            itemKey: node.SortKey,
            parentItemKey: parentItemKey,
            childItemKeys: childItemKeys,
            isExpanded: isExpanded);
    }

    private static EntityListViewModel MakeList(params (EntityListNodeViewModel node, string? parentKey)[] entries)
    {
        var list = new EntityListViewModel();
        var items = new List<EntityListItemViewModel>();
        for (int i = 0; i < entries.Length; i++)
        {
            items.Add(MakeItem(entries[i].node, order: i + 1, level: entries[i].parentKey is null ? 0 : 1, parentItemKey: entries[i].parentKey, isExpanded: true));
        }
        list.SetItems(items);
        return list;
    }

    private static SubscribedEntityViewModel CreateEntityWithJson(string entityType, string extraJsonFields = "")
    {
        var extras = string.IsNullOrEmpty(extraJsonFields) ? string.Empty : "," + extraJsonFields;
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "77777777-7777-7777-7777-777777777777",
              "entity-types": ["entity", "{{entityType}}"],
              "names": [["tests", "{{entityType}}"]],
              "display-name": { "default": "Test-{{entityType}}" }{{extras}}
            }
            """);
        var snapshot = new EntitySnapshot
        {
            EntityId = new EntityId("77777777-7777-7777-7777-777777777777"),
            ConcurrencyTag = new ConcurrencyTag("1"),
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, Guid.NewGuid().ToString()),
            Data = document.RootElement.Clone(),
            Relationships = Array.Empty<EntitySnapshot>(),
        };
        return new SubscribedEntityViewModel(snapshot, deleteEntityAsync: _ => Task.CompletedTask);
    }

    private static EntityListNodeViewModel MakeEntityNode(SubscribedEntityViewModel entity, string sortKey)
    {
        return new EntityListNodeViewModel(
            entity,
            nameComponents: new[] { sortKey },
            sortKey: $"[\"{sortKey}\"]");
    }

    // -------------------- FindViewModel tests --------------------

    [AvaloniaFact]
    public void FindViewModel_CtrlFWhenClosed_OpensFindBox()
    {
        var list = MakeList((MakeStringNode("apple"), null));
        var find = new FindViewModel(list);

        Assert.False(find.IsOpen);
        find.OpenCommand.Execute(null);
        Assert.True(find.IsOpen);
    }

    [AvaloniaFact]
    public void FindViewModel_EnterWithMatches_SelectsNextMatch()
    {
        var a = MakeStringNode("apple");
        var b = MakeStringNode("apricot");
        var list = MakeList((a, null), (b, null));
        var find = new FindViewModel(list);

        find.Open();
        find.Query = "ap";
        Assert.Equal(a.Card, find.CurrentCard);

        find.NextCommand.Execute(null);
        Assert.Equal(b.Card, find.CurrentCard);
    }

    [AvaloniaFact]
    public void FindViewModel_ShiftEnterWithMatches_SelectsPreviousMatch()
    {
        var a = MakeStringNode("apple");
        var b = MakeStringNode("apricot");
        var list = MakeList((a, null), (b, null));
        var find = new FindViewModel(list);

        find.Open();
        find.Query = "ap";
        find.NextCommand.Execute(null);
        Assert.Equal(b.Card, find.CurrentCard);

        find.PreviousCommand.Execute(null);
        Assert.Equal(a.Card, find.CurrentCard);
    }

    [AvaloniaFact]
    public void FindViewModel_TypingQuery_NavigatesToFirstMatch()
    {
        var a = MakeStringNode("banana");
        var b = MakeStringNode("apricot");
        var list = MakeList((a, null), (b, null));
        var find = new FindViewModel(list);

        find.Open();
        find.Query = "apri";
        Assert.Equal(b.Card, find.CurrentCard);
        Assert.True(b.Card.IsSelected);
    }

    [AvaloniaFact]
    public void FindViewModel_BackspaceShorteningQuery_RestoresCurrentEntity()
    {
        var a = MakeStringNode("aa");
        var b = MakeStringNode("ab");
        var list = MakeList((a, null), (b, null));
        var find = new FindViewModel(list);

        find.Open();
        find.Query = "a";
        Assert.Equal(a.Card, find.CurrentCard);
        find.NextCommand.Execute(null);
        Assert.Equal(b.Card, find.CurrentCard);

        // Narrow to only b.
        find.Query = "ab";
        Assert.Equal(b.Card, find.CurrentCard);

        // Shorten back so both match again — selection should be restored to previously-current (b).
        find.Query = "a";
        Assert.Equal(b.Card, find.CurrentCard);
    }

    [AvaloniaFact]
    public void FindViewModel_HideUnmatchedEnabled_KeepsGroupingParentsVisible()
    {
        var parent = MakeStringNode("group", entityType: "folder");
        var matching = MakeStringNode("matchme");
        var unrelated = MakeStringNode("unrelated");
        parent.SetChildren(new[] { matching, unrelated });

        var list = new EntityListViewModel();
        list.SetItems(new[]
        {
            new EntityListItemViewModel(parent, order: 1, level: 0, itemKey: parent.SortKey,
                childItemKeys: new[] { matching.SortKey, unrelated.SortKey }),
            new EntityListItemViewModel(matching, order: 2, level: 1, itemKey: matching.SortKey, parentItemKey: parent.SortKey),
            new EntityListItemViewModel(unrelated, order: 3, level: 1, itemKey: unrelated.SortKey, parentItemKey: parent.SortKey),
        });

        var find = new FindViewModel(list);
        find.Open();
        find.HideUnmatched = true;
        find.Query = "matchme";

        Assert.True(parent.IsAncestorOfMatch);
        Assert.True(parent.IsExpanded);
        Assert.Contains(matching, parent.VisibleChildren);
        Assert.DoesNotContain(unrelated, parent.VisibleChildren);
    }

    [AvaloniaFact]
    public void FindViewModel_QueryMatchingJsonValue_FindsEntityAndSwitchesToJsonView()
    {
        // display-name uses "Test-widget"; query "supersecret" only matches inside JSON values.
        var entity = CreateEntityWithJson("widget", "\"extra\": \"supersecret\"");
        var node = MakeEntityNode(entity, "w");
        var list = MakeList((node, null));
        var find = new FindViewModel(list);

        find.Open();
        find.Query = "supersecret";

        Assert.Equal(node.Card, find.CurrentCard);
        Assert.Equal(FindViewModel.MatchWhere.JsonOnly, find.CurrentMatch!.Value.Where);
        Assert.True(node.Card.IsJsonVisible);
    }

    [AvaloniaFact]
    public void FindViewModel_QueryMatchingJsonPropertyName_DoesNotMatch()
    {
        // "display-name" is a JSON property key that appears in the entity JSON. The matcher must
        // ignore keys.
        var entity = CreateEntityWithJson("widget");
        var node = MakeEntityNode(entity, "w");
        var list = MakeList((node, null));
        var find = new FindViewModel(list);

        find.Open();
        find.Query = "display-name";

        Assert.Empty(find.Matches);
        Assert.Null(find.CurrentCard);
    }

    [AvaloniaFact]
    public void FindViewModel_QueryMatchingCardText_MatchesWithoutSwitchingToJson()
    {
        var node = MakeStringNode("hello world", entityType: "note");
        var list = MakeList((node, null));
        var find = new FindViewModel(list);

        find.Open();
        find.Query = "hello";

        Assert.Equal(node.Card, find.CurrentCard);
        Assert.Equal(FindViewModel.MatchWhere.CardText, find.CurrentMatch!.Value.Where);
        Assert.False(node.Card.IsJsonVisible);
    }

    [AvaloniaFact]
    public void FindViewModel_NavigateAwayFromJsonMatch_RestoresCardView()
    {
        var jsonMatch = CreateEntityWithJson("widget", "\"payload\": \"quantum-token\"");
        var jsonNode = MakeEntityNode(jsonMatch, "j");
        var cardMatch = MakeStringNode("quantum-visible");
        var list = MakeList((jsonNode, null), (cardMatch, null));
        var find = new FindViewModel(list);

        find.Open();
        find.Query = "quantum";

        // First match is the JSON-only entity; JSON view opens.
        Assert.Equal(jsonNode.Card, find.CurrentCard);
        Assert.True(jsonNode.Card.IsJsonVisible);

        find.NextCommand.Execute(null);
        Assert.Equal(cardMatch.Card, find.CurrentCard);
        Assert.False(jsonNode.Card.IsJsonVisible);
    }

    [AvaloniaFact]
    public void FindViewModel_NextPastLastMatch_WrapsToFirst()
    {
        var a = MakeStringNode("apple");
        var b = MakeStringNode("apricot");
        var list = MakeList((a, null), (b, null));
        var find = new FindViewModel(list);

        find.Open();
        find.Query = "ap";
        find.NextCommand.Execute(null);
        Assert.Equal(b.Card, find.CurrentCard);
        find.NextCommand.Execute(null);
        Assert.Equal(a.Card, find.CurrentCard);
    }

    [AvaloniaFact]
    public void FindViewModel_PreviousBeforeFirstMatch_WrapsToLast()
    {
        var a = MakeStringNode("apple");
        var b = MakeStringNode("apricot");
        var list = MakeList((a, null), (b, null));
        var find = new FindViewModel(list);

        find.Open();
        find.Query = "ap";
        Assert.Equal(a.Card, find.CurrentCard);
        find.PreviousCommand.Execute(null);
        Assert.Equal(b.Card, find.CurrentCard);
    }

    [AvaloniaFact]
    public void FindViewModel_Escape_ClosesRestoresVisibilityAndKeepsFoundItemSelected()
    {
        var parent = MakeStringNode("group", entityType: "folder");
        var matching = MakeStringNode("matchme");
        var unrelated = MakeStringNode("unrelated");
        parent.SetChildren(new[] { matching, unrelated });

        var list = new EntityListViewModel();
        list.SetItems(new[]
        {
            new EntityListItemViewModel(parent, order: 1, level: 0, itemKey: parent.SortKey,
                childItemKeys: new[] { matching.SortKey, unrelated.SortKey }),
            new EntityListItemViewModel(matching, order: 2, level: 1, itemKey: matching.SortKey, parentItemKey: parent.SortKey),
            new EntityListItemViewModel(unrelated, order: 3, level: 1, itemKey: unrelated.SortKey, parentItemKey: parent.SortKey),
        });

        var find = new FindViewModel(list);
        find.Open();
        find.HideUnmatched = true;
        find.Query = "matchme";

        Assert.DoesNotContain(unrelated, parent.VisibleChildren);
        var found = find.CurrentCard;
        Assert.NotNull(found);

        find.CloseCommand.Execute(null);

        Assert.False(find.IsOpen);
        // Visibility restored: both children now visible again.
        Assert.Contains(matching, parent.VisibleChildren);
        Assert.Contains(unrelated, parent.VisibleChildren);
        // Selection kept on the found item.
        Assert.True(found!.IsSelected);
    }

    [AvaloniaFact]
    public void FindViewModel_ActivateMatch_BringsSelectedItemIntoView()
    {
        var node = MakeStringNode("targettext");
        var list = MakeList((node, null));
        EntityCardViewModel? broughtIntoView = null;
        var find = new FindViewModel(list, card => broughtIntoView = card);

        find.Open();
        find.Query = "target";

        Assert.Same(node.Card, broughtIntoView);
    }

    // -------------------- JsonValueMatcher tests --------------------

    [AvaloniaFact]
    public void JsonValueMatcher_QueryMatchingStringValue_ReturnsTrue()
    {
        using var doc = JsonDocument.Parse("{\"field\": \"the-answer\"}");
        Assert.True(JsonValueMatcher.MatchesJsonValues(doc.RootElement, "answer"));
    }

    [AvaloniaFact]
    public void JsonValueMatcher_QueryMatchingPropertyName_ReturnsFalse()
    {
        using var doc = JsonDocument.Parse("{\"display-name\": \"nothing\"}");
        Assert.False(JsonValueMatcher.MatchesJsonValues(doc.RootElement, "display-name"));
    }

    [AvaloniaFact]
    public void JsonValueMatcher_QueryMatchingNumberValue_ReturnsTrue()
    {
        using var doc = JsonDocument.Parse("{\"count\": 42}");
        Assert.True(JsonValueMatcher.MatchesJsonValues(doc.RootElement, "42"));
    }

    [AvaloniaFact]
    public void JsonValueMatcher_QueryMatchingBooleanValue_ReturnsTrue()
    {
        using var doc = JsonDocument.Parse("{\"enabled\": true, \"deleted\": false}");
        Assert.True(JsonValueMatcher.MatchesJsonValues(doc.RootElement, "true"));
        Assert.True(JsonValueMatcher.MatchesJsonValues(doc.RootElement, "false"));
    }

    [AvaloniaFact]
    public void JsonValueMatcher_QueryMatchingNestedArrayOrObjectValue_ReturnsTrue()
    {
        using var doc = JsonDocument.Parse("{\"outer\": {\"inner\": [\"nothing\", \"needle\"]}}");
        Assert.True(JsonValueMatcher.MatchesJsonValues(doc.RootElement, "needle"));
        // The property name "outer" appears only as a key, not as a value.
        Assert.False(JsonValueMatcher.MatchesJsonValues(doc.RootElement, "outer"));
    }
}
