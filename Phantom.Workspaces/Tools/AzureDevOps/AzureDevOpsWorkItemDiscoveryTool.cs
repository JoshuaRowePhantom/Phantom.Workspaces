using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Tools.AzureDevOps;

/// <summary>
/// A built-in scheduled tool that discovers Azure DevOps work items from
/// <c>azure-devops-project</c> participant entities and upserts each as an
/// <c>azure-devops-work-item</c> entity in the workspace store. Work items are keyed by the stable
/// name path <c>[&quot;azure-devops&quot;, org, project, &quot;work-items&quot;, id]</c>,
/// so re-running the tool updates rather than duplicates.
/// </summary>
public sealed class AzureDevOpsWorkItemDiscoveryTool : IWorkspaceTool
{
    /// <summary>Optional tool-entity property overriding the Personal Access Token.</summary>
    public const string PersonalAccessTokenProperty = "personal-access-token";

    /// <summary>The environment variable used for authentication when the property is absent.</summary>
    public const string PatEnvironmentVariable = "AZURE_DEVOPS_TOKEN";

    /// <summary>Optional tool-entity property limiting how many items to fetch per project.</summary>
    public const string MaxItemsProperty = "max-items";

    private const int DefaultMaxItems = 200;
    private const string ApiVersion = "7.0";

    private readonly ILogger<AzureDevOpsWorkItemDiscoveryTool> logger;
    private readonly Func<string, string, CancellationToken, Task<string>>? httpPoster;

    public AzureDevOpsWorkItemDiscoveryTool(
        Func<string, string, CancellationToken, Task<string>>? httpPoster = null,
        ILogger<AzureDevOpsWorkItemDiscoveryTool>? logger = null)
    {
        this.httpPoster = httpPoster;
        this.logger = logger ?? NullLogger<AzureDevOpsWorkItemDiscoveryTool>.Instance;
    }

    public string ToolType => "azure-devops-work-item-discovery";

    public async Task<WorkspaceToolExecutionResult> ExecuteAsync(WorkspaceToolExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var maxItems = ReadIntProperty(context.Tool.Data, MaxItemsProperty) ?? DefaultMaxItems;
        var pat = ReadStringProperty(context.Tool.Data, PersonalAccessTokenProperty)
            ?? Environment.GetEnvironmentVariable(PatEnvironmentVariable);

        var projectsScanned = 0;
        var entitiesUpserted = 0;

        foreach (var participant in context.Participants)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var projectInfo = TryExtractProjectInfo(participant);
            if (projectInfo is null)
            {
                continue;
            }

            var (org, project, projectUrl) = projectInfo.Value;
            projectsScanned++;

            this.logger.LogInformation("Scanning Azure DevOps work items for {Org}/{Project}", org, project);

            List<int> workItemIds;
            try
            {
                workItemIds = await this.FetchWorkItemIdsAsync(
                    projectUrl, project, maxItems, pat, context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "Failed to fetch work item IDs for {Org}/{Project}", org, project);
                continue;
            }

            if (workItemIds.Count == 0)
            {
                continue;
            }

            JsonElement workItems;
            try
            {
                workItems = await this.FetchWorkItemDetailsAsync(
                    projectUrl, workItemIds, pat, context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "Failed to fetch work item details for {Org}/{Project}", org, project);
                continue;
            }

            if (workItems.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in workItems.EnumerateArray())
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                if (!item.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id))
                {
                    continue;
                }

                await this.UpsertWorkItemAsync(
                    context.DataAccessLayer,
                    org, project, id, item,
                    context.CancellationToken).ConfigureAwait(false);
                entitiesUpserted++;
            }

            this.logger.LogInformation(
                "Finished scanning {Org}/{Project}: {Count} work item(s) upserted",
                org, project, entitiesUpserted);
        }

        var summary = $"Scanned {projectsScanned} project(s). Upserted {entitiesUpserted} work item(s).";
        return new WorkspaceToolExecutionResult { ResultContent = summary };
    }

    private async Task<List<int>> FetchWorkItemIdsAsync(
        string projectUrl,
        string project,
        int maxItems,
        string? pat,
        CancellationToken cancellationToken)
    {
        var wiqlUrl = $"{projectUrl.TrimEnd('/')}/_apis/wit/wiql?api-version={ApiVersion}";
        var wiqlBody = $$$"""{"query": "SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = '{{{project}}}' ORDER BY [System.ChangedDate] DESC"}""";

        var poster = this.httpPoster ?? ((url, body, ct) => DefaultHttpPostAsync(url, body, pat, ct));
        var responseJson = await poster(wiqlUrl, wiqlBody, cancellationToken).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(responseJson);
        var ids = new List<int>();

        if (!doc.RootElement.TryGetProperty("workItems", out var workItemsEl)
            || workItemsEl.ValueKind != JsonValueKind.Array)
        {
            return ids;
        }

        foreach (var item in workItemsEl.EnumerateArray())
        {
            if (item.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var id))
            {
                ids.Add(id);
                if (ids.Count >= maxItems)
                {
                    break;
                }
            }
        }

        return ids;
    }

    private async Task<JsonElement> FetchWorkItemDetailsAsync(
        string projectUrl,
        List<int> ids,
        string? pat,
        CancellationToken cancellationToken)
    {
        var batchUrl = $"{projectUrl.TrimEnd('/')}/_apis/wit/workitemsbatch?api-version={ApiVersion}";
        var idArray = string.Join(",", ids);
        var batchBody = $$$"""
            {
              "ids": [{{{idArray}}}],
              "fields": ["System.Id","System.Title","System.State","System.Tags"],
              "$expand": "relations"
            }
            """;

        var poster = this.httpPoster ?? ((url, body, ct) => DefaultHttpPostAsync(url, body, pat, ct));
        var responseJson = await poster(batchUrl, batchBody, cancellationToken).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(responseJson);
        if (doc.RootElement.TryGetProperty("value", out var valueEl))
        {
            return valueEl.Clone();
        }

        return JsonDocument.Parse("[]").RootElement.Clone();
    }

    private async Task UpsertWorkItemAsync(
        IDataAccessLayer dataAccessLayer,
        string org,
        string project,
        int id,
        JsonElement item,
        CancellationToken cancellationToken)
    {
        var fields = item.TryGetProperty("fields", out var fieldsEl) ? fieldsEl : default;

        var title = GetStringField(fields, "System.Title") ?? string.Empty;
        var stateValue = GetStringField(fields, "System.State") ?? string.Empty;
        var tagsValue = GetStringField(fields, "System.Tags") ?? string.Empty;
        var status = MapAdoStateToStatus(stateValue);
        var labels = ParseTags(tagsValue);

        var url = string.Empty;
        if (item.TryGetProperty("_links", out var links)
            && links.TryGetProperty("html", out var html)
            && html.TryGetProperty("href", out var href)
            && href.ValueKind == JsonValueKind.String)
        {
            url = href.GetString() ?? string.Empty;
        }

        var relatedCommits = new List<string>();
        if (item.TryGetProperty("relations", out var relations) && relations.ValueKind == JsonValueKind.Array)
        {
            foreach (var relation in relations.EnumerateArray())
            {
                if (!relation.TryGetProperty("rel", out var relType)
                    || relType.GetString() != "ArtifactLink")
                {
                    continue;
                }

                if (!relation.TryGetProperty("url", out var relationUrl)
                    || relationUrl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var sha = TryExtractCommitShaFromVstfsUri(relationUrl.GetString() ?? string.Empty);
                if (sha is not null)
                {
                    relatedCommits.Add(sha);
                }
            }
        }

        var entityName = new EntityName("azure-devops", org, project, "work-items", id.ToString());
        var entityData = BuildWorkItemJson(entityName, title, status, url, labels, relatedCommits, id.ToString(), project);

        await WorkspaceToolEntityUtilities.UpsertEntityByPrimaryNameAsync(
            dataAccessLayer,
            entityName,
            entityData,
            "Discover Azure DevOps work item.",
            cancellationToken).ConfigureAwait(false);
    }

    internal static string? TryExtractCommitShaFromVstfsUri(string vstfsUri)
    {
        // Format: vstfs:///Git/Commit/<project-id>%2F<repo-id>%2F<sha>
        //     or: vstfs:///Git/Commit/<project-id>/<repo-id>/<sha>
        const string prefix = "vstfs:///Git/Commit/";
        if (!vstfsUri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = vstfsUri[prefix.Length..]
            .Split(['/', '%'], StringSplitOptions.RemoveEmptyEntries);

        // URL-encoded form: parts are [project-id, 2F, repo-id, 2F, sha]
        // Raw form: parts are [project-id, repo-id, sha]
        var filtered = parts
            .Where(p => !p.Equals("2F", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return filtered.Length >= 3 ? filtered[^1] : null;
    }

    internal static string MapAdoStateToStatus(string state)
    {
        return state.ToLowerInvariant() switch
        {
            "active" or "in progress" or "committed" => "in-progress",
            "done" or "resolved" or "closed" or "completed" => "closed",
            _ => "open",
        };
    }

    internal static IReadOnlyList<string> ParseTags(string tags)
    {
        if (string.IsNullOrWhiteSpace(tags))
        {
            return [];
        }

        return tags
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(static t => t.Trim())
            .Where(static t => t.Length > 0)
            .ToList();
    }

    private static (string Org, string Project, string ProjectUrl)? TryExtractProjectInfo(EntitySnapshot participant)
    {
        var types = WorkspaceEntitySnapshotReader.GetEntityTypes(participant);
        if (!types.Contains("azure-devops-project", StringComparer.Ordinal))
        {
            return null;
        }

        if (participant.Data is not JsonElement data
            || !data.TryGetProperty("urls", out var urlsEl)
            || urlsEl.ValueKind != JsonValueKind.Object
            || !urlsEl.TryGetProperty("default", out var defaultUrl)
            || defaultUrl.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var url = defaultUrl.GetString() ?? string.Empty;
        return TryParseAzureDevOpsUrl(url);
    }

    internal static (string Org, string Project, string ProjectUrl)? TryParseAzureDevOpsUrl(string url)
    {
        // Expected: https://dev.azure.com/{org}/{project}
        if (!url.StartsWith("https://dev.azure.com/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var path = url["https://dev.azure.com/".Length..].TrimEnd('/');
        var slashIndex = path.IndexOf('/', StringComparison.Ordinal);
        if (slashIndex <= 0 || slashIndex == path.Length - 1)
        {
            return null;
        }

        var org = path[..slashIndex];
        var project = path[(slashIndex + 1)..];

        if (string.IsNullOrWhiteSpace(org) || string.IsNullOrWhiteSpace(project))
        {
            return null;
        }

        return (org, project, url.TrimEnd('/'));
    }

    private static JsonObject BuildWorkItemJson(
        EntityName entityName,
        string title,
        string status,
        string url,
        IReadOnlyList<string> labels,
        IReadOnlyList<string> relatedCommits,
        string workItemId,
        string project)
    {
        var namesArray = new JsonArray(
            new JsonArray(
                entityName.Components.Select(c => (JsonNode)c).ToArray()));

        var labelsArray = new JsonArray(
            labels.Select(l => (JsonNode)l).ToArray());

        var obj = new JsonObject
        {
            ["entity-types"] = new JsonArray("entity", "work-item", "azure-devops-work-item", "external"),
            ["names"] = namesArray,
            ["display-name"] = new JsonObject { ["default"] = title },
            ["title"] = title,
            ["status"] = status,
            ["labels"] = labelsArray,
            ["urls"] = new JsonObject { ["default"] = url },
            ["work-item-id"] = workItemId,
            ["project"] = project,
        };

        if (relatedCommits.Count > 0)
        {
            obj["related-commits"] = new JsonArray(
                relatedCommits.Select(c => (JsonNode)c).ToArray());
        }

        return obj;
    }

    private static string? GetStringField(JsonElement fields, string fieldName)
    {
        if (fields.ValueKind == JsonValueKind.Object
            && fields.TryGetProperty(fieldName, out var el)
            && el.ValueKind == JsonValueKind.String)
        {
            return el.GetString();
        }

        return null;
    }

    private static int? ReadIntProperty(JsonElement? toolEntity, string propertyName)
    {
        if (toolEntity is JsonElement toolEntityValue
            && toolEntityValue.ValueKind == JsonValueKind.Object
            && toolEntityValue.TryGetProperty(propertyName, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out var value))
        {
            return value;
        }

        return null;
    }

    private static string? ReadStringProperty(JsonElement? toolEntity, string propertyName)
    {
        if (toolEntity is JsonElement toolEntityValue
            && toolEntityValue.ValueKind == JsonValueKind.Object
            && toolEntityValue.TryGetProperty(propertyName, out var element)
            && element.ValueKind == JsonValueKind.String)
        {
            return element.GetString();
        }

        return null;
    }

    private static async Task<string> DefaultHttpPostAsync(
        string url,
        string body,
        string? pat,
        CancellationToken cancellationToken)
    {
        using var client = new System.Net.Http.HttpClient();

        if (!string.IsNullOrWhiteSpace(pat))
        {
            var encoded = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($":{pat}"));
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", encoded);
        }

        var content = new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json");
        var response = await client.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }
}
