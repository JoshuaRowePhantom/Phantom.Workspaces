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

    private static SubscribedEntityViewModel CreateEntityRaw(string entityId, string entityJson)
    {
        using var document = JsonDocument.Parse(entityJson);
        var snapshot = new EntitySnapshot
        {
            EntityId = new EntityId(entityId),
            ConcurrencyTag = new ConcurrencyTag("1"),
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, Guid.NewGuid().ToString()),
            Data = document.RootElement.Clone(),
            Relationships = Array.Empty<EntitySnapshot>(),
        };
        return new SubscribedEntityViewModel(snapshot, deleteEntityAsync: _ => Task.CompletedTask);
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

    // -------------------- #1200: display-name / JSON-value matching coverage --------------------

    [AvaloniaFact]
    public void FindViewModel_QueryMatchingSubstringOfDisplayName_FindsThatEntity()
    {
        var a = MakeStringNode("apple pie");
        var b = MakeStringNode("banana");
        var list = MakeList((a, null), (b, null));
        var find = new FindViewModel(list);

        find.Open();
        find.Query = "pie";

        Assert.Single(find.Matches);
        Assert.Equal(a.Card, find.CurrentCard);
    }

    [AvaloniaFact]
    public void FindViewModel_QueryCaseDiffersFromDisplayName_FindsEntityCaseInsensitively()
    {
        var a = MakeStringNode("Apple");
        var b = MakeStringNode("banana");
        var list = MakeList((a, null), (b, null));
        var find = new FindViewModel(list);

        find.Open();
        find.Query = "APPLE";
        Assert.Equal(a.Card, find.CurrentCard);

        find.Query = "BAN";
        Assert.Equal(b.Card, find.CurrentCard);
    }

    [AvaloniaFact]
    public void FindViewModel_EmptyQuery_ShowsAllEntitiesAndClearsFilter()
    {
        var a = MakeStringNode("apple");
        var b = MakeStringNode("banana");
        var list = MakeList((a, null), (b, null));
        var find = new FindViewModel(list);

        find.Open();
        find.HideUnmatched = true;
        find.Query = "apple";
        Assert.Single(find.Matches);

        find.Query = string.Empty;

        Assert.Empty(find.Matches);
        Assert.Null(find.CurrentCard);
        // ClearFindFilter runs — no node is in a hide-unmatched state, so every child is visible
        // and no match highlight remains.
        foreach (var item in list.Items)
        {
            Assert.False(item.Node.HideUnmatched);
            Assert.False(item.Node.MatchesFilter);
            Assert.False(item.Node.IsAncestorOfMatch);
        }
    }

    [AvaloniaFact]
    public void FindViewModel_QueryMatchesNoEntity_ProducesNoMatches()
    {
        var a = MakeStringNode("apple");
        var b = MakeStringNode("banana");
        var list = MakeList((a, null), (b, null));
        var find = new FindViewModel(list);

        find.Open();
        find.Query = "zzzzz-nothing-matches";

        Assert.Empty(find.Matches);
        Assert.Null(find.CurrentCard);
    }

    [AvaloniaFact]
    public void FindViewModel_EntityWithEmptyNamesArray_StillMatchesByEntityId()
    {
        // Regression for #1200: names[0] is [] so DisplayName falls back to EntityId. A query
        // containing a substring of the EntityId must still find the entity.
        var entityId = "abcdef01-2345-6789-abcd-ef0123456789";
        var entity = CreateEntityRaw(
            entityId,
            $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity"],
              "names": [[]]
            }
            """);
        var node = MakeEntityNode(entity, "z");
        var list = MakeList((node, null));
        var find = new FindViewModel(list);

        find.Open();
        // A distinctive substring of the entity id.
        find.Query = "abcdef01";

        Assert.Equal(node.Card, find.CurrentCard);
        Assert.Equal(FindViewModel.MatchWhere.CardText, find.CurrentMatch!.Value.Where);
        Assert.False(string.IsNullOrEmpty(node.Card.DisplayName));
    }

    [AvaloniaFact]
    public void FindViewModel_EntityWithEmptyDisplayNameButMatchingJsonValue_StillMatches()
    {
        // The display name resolves to EntityId (never ""), but the interesting text is in a
        // JSON leaf value — the entity is still found via JsonValueMatcher with JsonOnly.
        var entityId = "11112222-3333-4444-5555-666677778888";
        var entity = CreateEntityRaw(
            entityId,
            $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity"],
              "names": [[]],
              "content": { "default": "quirky-payload" }
            }
            """);
        var node = MakeEntityNode(entity, "z");
        var list = MakeList((node, null));
        var find = new FindViewModel(list);

        find.Open();
        find.Query = "quirky-payload";

        Assert.Equal(node.Card, find.CurrentCard);
        Assert.Equal(FindViewModel.MatchWhere.JsonOnly, find.CurrentMatch!.Value.Where);
    }

    [AvaloniaFact]
    public void FindViewModel_QueryMatchingJsonValueInNestedObject_FindsThatEntity()
    {
        var entity = CreateEntityWithJson(
            "note",
            "\"content\": { \"default\": { \"text\": \"deep-value\" } }");
        var node = MakeEntityNode(entity, "n");
        var list = MakeList((node, null));
        var find = new FindViewModel(list);

        find.Open();
        find.Query = "deep-value";

        Assert.Equal(node.Card, find.CurrentCard);
        Assert.Equal(FindViewModel.MatchWhere.JsonOnly, find.CurrentMatch!.Value.Where);
    }

    [AvaloniaFact]
    public void FindViewModel_QueryMatchingJsonValueInArray_FindsThatEntity()
    {
        var entity = CreateEntityWithJson(
            "note",
            "\"tags\": [\"alpha\", { \"label\": \"buried-tag\" }, \"gamma\"]");
        var node = MakeEntityNode(entity, "n");
        var list = MakeList((node, null));
        var find = new FindViewModel(list);

        find.Open();
        find.Query = "buried-tag";

        Assert.Equal(node.Card, find.CurrentCard);
        Assert.Equal(FindViewModel.MatchWhere.JsonOnly, find.CurrentMatch!.Value.Where);
    }

    [AvaloniaFact]
    public void FindViewModel_QueryMatchingUnrealizedVirtualizedItem_FindsThatEntity()
    {
        // The find filter walks the full source collection via EnumerateInOrder, not just the
        // items that a virtualizing panel happens to have realized. Prove that by constructing
        // a large list and matching an entity mid-way; no visual realization ever occurs in the
        // pure-view-model test, which is precisely the "unrealized" condition.
        var nodes = new List<EntityListNodeViewModel>();
        for (int i = 0; i < 200; i++)
        {
            nodes.Add(MakeStringNode($"item-{i:D3}"));
        }
        var target = MakeStringNode("needle-item");
        nodes.Add(target);
        for (int i = 200; i < 400; i++)
        {
            nodes.Add(MakeStringNode($"item-{i:D3}"));
        }

        var list = new EntityListViewModel();
        var items = new List<EntityListItemViewModel>();
        for (int i = 0; i < nodes.Count; i++)
        {
            items.Add(MakeItem(nodes[i], order: i + 1));
        }
        list.SetItems(items);

        var find = new FindViewModel(list);
        find.Open();
        find.Query = "needle-item";

        Assert.Single(find.Matches);
        Assert.Equal(target.Card, find.CurrentCard);
    }

    // -------------------- #1257: SearchQuery propagation to every card --------------------

    [AvaloniaFact]
    public void FindViewModel_QueryTyped_SetsSearchQueryOnEveryCardViewModel()
    {
        var a = MakeStringNode("apple");
        var b = MakeStringNode("banana");
        var list = MakeList((a, null), (b, null));
        var find = new FindViewModel(list);

        find.Open();
        find.Query = "app";

        Assert.Equal("app", a.Card.SearchQuery);
        Assert.Equal("app", b.Card.SearchQuery);
        Assert.True(a.Card.Matches);
        Assert.False(b.Card.Matches);
    }

    [AvaloniaFact]
    public void FindViewModel_ClearedQuery_ClearsSearchQueryOnEveryCardViewModel()
    {
        var a = MakeStringNode("apple");
        var b = MakeStringNode("banana");
        var list = MakeList((a, null), (b, null));
        var find = new FindViewModel(list);

        find.Open();
        find.Query = "app";
        Assert.Equal("app", a.Card.SearchQuery);

        find.Query = string.Empty;

        Assert.True(string.IsNullOrEmpty(a.Card.SearchQuery));
        Assert.True(string.IsNullOrEmpty(b.Card.SearchQuery));
        Assert.False(a.Card.Matches);
        Assert.False(b.Card.Matches);
    }

    // -------------------- #1199: FindViewModel-level open-idempotence --------------------

    [AvaloniaFact]
    public void FindViewModel_CtrlFWhenOpenWithQuery_LeavesQueryUnchanged()
    {
        // View-model-level assurance that Open() on an already-open bar does not mutate Query.
        // Select-all is a view concern and must not clear the model text.
        var a = MakeStringNode("apple");
        var list = MakeList((a, null));
        var find = new FindViewModel(list);

        find.Open();
        find.Query = "app";
        Assert.True(find.IsOpen);

        find.OpenCommand.Execute(null);

        Assert.True(find.IsOpen);
        Assert.Equal("app", find.Query);
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

    // -------------------- #1256: Generic-view find/hide-unmatched via ViewPopulationViewModel --------------------

    private static MainWindowViewModel CreateMainWindowViewModel() =>
        new MainWindowViewModel(new UnknownRepositorySource());

    private static ViewEntityViewModel CreateViewEntity(
        MainWindowViewModel mwvm,
        string displayName,
        string entityType = "entity",
        IReadOnlyCollection<EntityFieldEditorViewModel>? fieldEditors = null,
        bool isExpanded = true)
    {
        var entityId = Guid.NewGuid();
        var fieldEditorsJson = string.Empty;
        var snapshot = new EntitySnapshot
        {
            EntityId = new EntityId(entityId.ToString()),
            ConcurrencyTag = new ConcurrencyTag("1"),
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, Guid.NewGuid().ToString()),
            Data = JsonDocument.Parse($$"""{"display-name":{"default":"{{displayName}}"},"entity-types":["entity","{{entityType}}"],"names":[["{{displayName}}"]]}""").RootElement.Clone(),
            Relationships = Array.Empty<EntitySnapshot>(),
        };
        var entity = new SubscribedEntityViewModel(snapshot, deleteEntityAsync: _ => Task.CompletedTask);
        var vm = new ViewEntityViewModel(
            entity,
            mwvm,
            new ShortcutManager(),
            indentLevel: 0,
            isExpanded: isExpanded,
            fieldEditorFactory: fieldEditors is not null ? null : null);
        if (fieldEditors is not null)
        {
            vm.EntityCardNode.Card.SetFieldEditors(fieldEditors);
        }
        return vm;
    }

    private static ViewPopulationViewModel BuildPopulation(params ViewEntityViewModel[] roots)
    {
        var pop = new ViewPopulationViewModel();
        foreach (var root in roots)
        {
            pop.Entities.Add(root);
            pop.RootEntities.Add(root);
            AddDescendantsToEntities(pop, root);
        }
        return pop;
    }

    private static void AddDescendantsToEntities(ViewPopulationViewModel pop, ViewEntityViewModel parent)
    {
        foreach (var child in parent.Children)
        {
            pop.Entities.Add(child);
            AddDescendantsToEntities(pop, child);
        }
    }

    [AvaloniaFact]
    public void FindViewModel_QuerySet_FansOutSearchQueryToEveryCard()
    {
        var mwvm = CreateMainWindowViewModel();
        var a = CreateViewEntity(mwvm, "apple");
        var b = CreateViewEntity(mwvm, "banana");
        var pop = BuildPopulation(a, b);
        var find = new FindViewModel(pop);

        find.Open();
        find.Query = "ap";

        Assert.Equal("ap", a.EntityCardNode.Card.SearchQuery);
        Assert.Equal("ap", b.EntityCardNode.Card.SearchQuery);
    }

    [AvaloniaFact]
    public void FindViewModel_QueryCleared_ClearsSearchQueryOnEveryCard()
    {
        var mwvm = CreateMainWindowViewModel();
        var a = CreateViewEntity(mwvm, "apple");
        var b = CreateViewEntity(mwvm, "banana");
        var pop = BuildPopulation(a, b);
        var find = new FindViewModel(pop);

        find.Open();
        find.Query = "ap";
        Assert.Equal("ap", a.EntityCardNode.Card.SearchQuery);

        find.Query = string.Empty;

        Assert.Null(a.EntityCardNode.Card.SearchQuery);
        Assert.Null(b.EntityCardNode.Card.SearchQuery);
    }

    [AvaloniaFact]
    public void FindViewModel_HideUnmatchedEnabledWithQuery_HidesUnmatchedLeaf()
    {
        var mwvm = CreateMainWindowViewModel();
        var matching = CreateViewEntity(mwvm, "matchme");
        var unrelated = CreateViewEntity(mwvm, "unrelated");
        var pop = BuildPopulation(matching, unrelated);
        var find = new FindViewModel(pop);

        find.Open();
        find.HideUnmatched = true;
        find.Query = "matchme";

        Assert.False(unrelated.IsVisible);
    }

    [AvaloniaFact]
    public void FindViewModel_HideUnmatchedEnabledWithQuery_KeepsMatchingNodeVisible()
    {
        var mwvm = CreateMainWindowViewModel();
        var matching = CreateViewEntity(mwvm, "matchme");
        var unrelated = CreateViewEntity(mwvm, "unrelated");
        var pop = BuildPopulation(matching, unrelated);
        var find = new FindViewModel(pop);

        find.Open();
        find.HideUnmatched = true;
        find.Query = "matchme";

        Assert.True(matching.IsVisible);
    }

    [AvaloniaFact]
    public void FindViewModel_HideUnmatchedEnabledWithQuery_KeepsAncestorsOfMatchVisible()
    {
        var mwvm = CreateMainWindowViewModel();
        var parent = CreateViewEntity(mwvm, "group", entityType: "folder");
        var matching = CreateViewEntity(mwvm, "matchme");
        var unrelated = CreateViewEntity(mwvm, "unrelated");
        parent.AddChild(matching);
        parent.AddChild(unrelated);
        var pop = BuildPopulation(parent);
        var find = new FindViewModel(pop);

        find.Open();
        find.HideUnmatched = true;
        find.Query = "matchme";

        Assert.True(parent.IsVisible);
        Assert.True(matching.IsVisible);
        Assert.False(unrelated.IsVisible);
    }

    [AvaloniaFact]
    public void FindViewModel_HideUnmatchedEnabledWithQuery_MatchOnPropertyValueOnly_KeepsNodeVisible()
    {
        var mwvm = CreateMainWindowViewModel();
        var fields = new EntityFieldEditorViewModel[]
        {
            new StringFieldEditorViewModel("propname", "secretval")
        };
        var node = CreateViewEntity(mwvm, "noname", entityType: "generic", fieldEditors: fields);
        var pop = BuildPopulation(node);
        var find = new FindViewModel(pop);

        find.Open();
        find.HideUnmatched = true;
        find.Query = "secretval";

        Assert.True(node.EntityCardNode.Card.Matches);
        Assert.True(node.IsVisible);
    }

    [AvaloniaFact]
    public void FindViewModel_HideUnmatchedEnabledWithQuery_MatchOnPropertyNameOnly_HidesNode()
    {
        var mwvm = CreateMainWindowViewModel();
        var fields = new EntityFieldEditorViewModel[]
        {
            new StringFieldEditorViewModel("mypropname", "othervalue")
        };
        var node = CreateViewEntity(mwvm, "noname", entityType: "generic", fieldEditors: fields);
        var pop = BuildPopulation(node);
        var find = new FindViewModel(pop);

        find.Open();
        find.HideUnmatched = true;
        find.Query = "mypropname";

        Assert.False(node.EntityCardNode.Card.Matches);
        Assert.False(node.IsVisible);
    }

    [AvaloniaFact]
    public void FindViewModel_HideUnmatchedDisabled_KeepsAllVisibleEvenWithQuery()
    {
        var mwvm = CreateMainWindowViewModel();
        var a = CreateViewEntity(mwvm, "apple");
        var b = CreateViewEntity(mwvm, "banana");
        var pop = BuildPopulation(a, b);
        var find = new FindViewModel(pop);

        find.Open();
        find.HideUnmatched = false;
        find.Query = "apple";

        Assert.True(a.IsVisible);
        Assert.True(b.IsVisible);
    }

    [AvaloniaFact]
    public void FindViewModel_HideUnmatchedToggledOff_RestoresAllVisibility()
    {
        var mwvm = CreateMainWindowViewModel();
        var a = CreateViewEntity(mwvm, "apple");
        var b = CreateViewEntity(mwvm, "banana");
        var pop = BuildPopulation(a, b);
        var find = new FindViewModel(pop);

        find.Open();
        find.HideUnmatched = true;
        find.Query = "apple";
        Assert.False(b.IsVisible);

        find.HideUnmatched = false;

        Assert.True(a.IsVisible);
        Assert.True(b.IsVisible);
    }

    [AvaloniaFact]
    public void FindViewModel_HideUnmatchedEnabledThenQueryChanged_ReFilters()
    {
        var mwvm = CreateMainWindowViewModel();
        var a = CreateViewEntity(mwvm, "apple");
        var b = CreateViewEntity(mwvm, "banana");
        var pop = BuildPopulation(a, b);
        var find = new FindViewModel(pop);

        find.Open();
        find.HideUnmatched = true;
        find.Query = "apple";
        Assert.True(a.IsVisible);
        Assert.False(b.IsVisible);

        find.Query = "banana";
        Assert.False(a.IsVisible);
        Assert.True(b.IsVisible);
    }

    [AvaloniaFact]
    public void FindViewModel_HideUnmatchedEnabledWithQuery_AutoExpandsAncestorsOfMatch()
    {
        var mwvm = CreateMainWindowViewModel();
        var parent = CreateViewEntity(mwvm, "group", entityType: "folder", isExpanded: false);
        var matching = CreateViewEntity(mwvm, "matchme");
        parent.AddChild(matching);
        var pop = BuildPopulation(parent);
        var find = new FindViewModel(pop);

        find.Open();
        find.HideUnmatched = true;
        find.Query = "matchme";

        Assert.True(parent.IsExpanded);
    }

    [AvaloniaFact]
    public void FindViewModel_HideUnmatchedEnabledAndSelectedHidden_NormalizesSelection()
    {
        var mwvm = CreateMainWindowViewModel();
        var a = CreateViewEntity(mwvm, "apple");
        var b = CreateViewEntity(mwvm, "banana");
        var pop = BuildPopulation(a, b);
        b.EntityCardNode.Card.IsSelected = true;
        var find = new FindViewModel(pop);

        find.Open();
        find.HideUnmatched = true;
        find.Query = "apple";

        Assert.False(b.IsVisible);
        Assert.False(b.EntityCardNode.Card.IsSelected);
    }

    [AvaloniaFact]
    public void FindViewModel_CurrentViewPopulationSwapped_ReAppliesFindStateToNewPopulation()
    {
        var mwvm = CreateMainWindowViewModel();
        var a = CreateViewEntity(mwvm, "apple");
        var pop1 = BuildPopulation(a);
        var find = new FindViewModel(pop1);

        find.Open();
        find.HideUnmatched = true;
        find.Query = "cherry";

        // Now swap to a new population.
        var b = CreateViewEntity(mwvm, "cherry");
        var c = CreateViewEntity(mwvm, "grape");
        var pop2 = BuildPopulation(b, c);
        find.SetPopulation(pop2);

        Assert.True(b.IsVisible);
        Assert.False(c.IsVisible);
        Assert.Equal("cherry", b.EntityCardNode.Card.SearchQuery);
        Assert.Equal("cherry", c.EntityCardNode.Card.SearchQuery);
    }
}
