using System.Globalization;
using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;

namespace Phantom.Workspaces.Tests;

public sealed class SubscribedEntityViewModelTests
{
    [Theory]
    [InlineData("agent-session", "fr-FR", "Renamed")]
    [InlineData("entity", "fr-FR", "Original canonical")]
    [InlineData("agent-session", "", "Renamed")]
    [InlineData("entity", "", "Renamed")]
    public async Task SaveDisplayNameAsync_PersistsLocaleAndChangesCanonicalOnlyForSessionsOrInvariantLocale(
        string entityType,
        string locale,
        string expectedCanonical)
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(locale);
            await using var repository = await EntityRepository.CreateAsync(new UnknownRepositorySource());
            repository.SetDataAccessLayerForTesting(new InMemoryDataAccessLayer());
            var broker = new EntityBroker(repository);
            var entityId = new EntityId(Guid.NewGuid());
            var data = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["entity-types"] = new[] { entityType },
                ["display-name"] = new Dictionary<string, string>
                {
                    ["default"] = "Original canonical",
                    ["fr-FR"] = "Original localized",
                    ["de-DE"] = "Unchanged translation",
                },
            });
            await broker.UpdateAsync(new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "Create rename target" } },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = entityId,
                        Data = data,
                        EntityChangeMode = EntityChangeMode.Replace,
                    },
                ],
            }, TestContext.Current.CancellationToken);
            var entity = Assert.Single(await broker.GetEntitiesAsync([entityId], TestContext.Current.CancellationToken));

            await entity.SaveDisplayNameAsync("Renamed");

            var loaded = await repository.DataAccessLayer.GetAsync(new GetRequest
            {
                Entities = [new GetEntityRequest { EntityId = entityId }],
                Timestamps = [null],
            }, TestContext.Current.CancellationToken);
            var persisted = Assert.Single(loaded.Batches.SelectMany(batch => batch.Entities));
            var names = persisted.Data!.Value.GetProperty("display-name");
            Assert.Equal(expectedCanonical, names.GetProperty("default").GetString());
            Assert.Equal(string.IsNullOrEmpty(locale) ? "Original localized" : "Renamed",
                names.GetProperty("fr-FR").GetString());
            Assert.Equal("Unchanged translation", names.GetProperty("de-DE").GetString());
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }
}
