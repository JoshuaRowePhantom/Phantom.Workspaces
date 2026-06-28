using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Tools.GitHub;

/// <summary>
/// A built-in scheduled tool that discovers GitHub issues from <c>git-repository</c> participant
/// entities and upserts each as a <c>git-work-item</c> entity in the workspace store. Issues are
/// keyed by the stable name path <c>[&quot;github&quot;, owner, repo, &quot;work-items&quot;, number]</c>,
/// so re-running the tool updates rather than duplicates.
/// </summary>
public sealed class GitHubWorkItemDiscoveryTool : IWorkspaceTool
{
    /// <summary>Optional tool-entity property limiting how many issues to fetch per repository.</summary>
    public const string MaxItemsProperty = "max-items";

    private const int DefaultMaxItems = 200;

    private readonly ILogger<GitHubWorkItemDiscoveryTool> logger;
    private readonly Func<string, CancellationToken, Task<string>>? issueListRunner;
    private readonly Func<string, int, CancellationToken, Task<string>>? timelineRunner;

    public GitHubWorkItemDiscoveryTool(
        Func<string, CancellationToken, Task<string>>? issueListRunner = null,
        Func<string, int, CancellationToken, Task<string>>? timelineRunner = null,
        ILogger<GitHubWorkItemDiscoveryTool>? logger = null)
    {
        this.issueListRunner = issueListRunner;
        this.timelineRunner = timelineRunner;
        this.logger = logger ?? NullLogger<GitHubWorkItemDiscoveryTool>.Instance;
    }

    public string ToolType => "github-work-item-discovery";

    public async Task<WorkspaceToolExecutionResult> ExecuteAsync(WorkspaceToolExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var maxItems = ReadIntProperty(context.Tool.Data, MaxItemsProperty) ?? DefaultMaxItems;
        var repositoriesScanned = 0;
        var entitiesCreatedOrUpdated = 0;

        foreach (var participant in context.Participants)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var ownerRepo = TryExtractGitHubOwnerRepo(participant);
            if (ownerRepo is null)
            {
                continue;
            }

            var (owner, repo) = ownerRepo.Value;
            repositoriesScanned++;

            this.logger.LogInformation("Scanning GitHub issues for {Owner}/{Repo}", owner, repo);

            string issuesJson;
            try
            {
                issuesJson = await this.FetchIssuesAsync(owner, repo, maxItems, context.CancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "Failed to fetch issues for {Owner}/{Repo}", owner, repo);
                continue;
            }

            JsonElement issuesArray;
            try
            {
                using var doc = JsonDocument.Parse(issuesJson);
                issuesArray = doc.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                this.logger.LogWarning(ex, "Failed to parse issues JSON for {Owner}/{Repo}", owner, repo);
                continue;
            }

            if (issuesArray.ValueKind != JsonValueKind.Array)
            {
                this.logger.LogWarning("Issues response for {Owner}/{Repo} is not a JSON array", owner, repo);
                continue;
            }

            foreach (var issue in issuesArray.EnumerateArray())
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                if (issue.TryGetProperty("pull_request", out _))
                {
                    continue;
                }

                if (!issue.TryGetProperty("number", out var numberElement)
                    || numberElement.ValueKind != JsonValueKind.Number
                    || !numberElement.TryGetInt32(out var number))
                {
                    continue;
                }

                var workItemEntity = await this.UpsertWorkItemAsync(
                    context.DataAccessLayer,
                    owner,
                    repo,
                    issue,
                    number,
                    context.CancellationToken).ConfigureAwait(false);
                entitiesCreatedOrUpdated++;

                if (this.timelineRunner is not null)
                {
                    await this.ProcessTimelineAsync(
                        context.DataAccessLayer,
                        owner,
                        repo,
                        number,
                        workItemEntity,
                        context.CancellationToken).ConfigureAwait(false);
                }
            }

            this.logger.LogInformation(
                "Finished scanning {Owner}/{Repo}: {Count} work item(s) upserted",
                owner, repo, entitiesCreatedOrUpdated);
        }

        var summary = $"Scanned {repositoriesScanned} repository/repositories. Upserted {entitiesCreatedOrUpdated} work item(s).";
        return new WorkspaceToolExecutionResult { ResultContent = summary };
    }

    private async Task<EntitySnapshot> UpsertWorkItemAsync(
        IDataAccessLayer dataAccessLayer,
        string owner,
        string repo,
        JsonElement issue,
        int number,
        CancellationToken cancellationToken)
    {
        var title = issue.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String
            ? titleEl.GetString() ?? string.Empty
            : string.Empty;

        var state = issue.TryGetProperty("state", out var stateEl) && stateEl.ValueKind == JsonValueKind.String
            ? stateEl.GetString() ?? string.Empty
            : string.Empty;
        var status = MapGitHubStateToStatus(state);

        var url = issue.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String
            ? urlEl.GetString() ?? string.Empty
            : string.Empty;

        var labels = new List<string>();
        if (issue.TryGetProperty("labels", out var labelsEl) && labelsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var label in labelsEl.EnumerateArray())
            {
                if (label.TryGetProperty("name", out var nameEl)
                    && nameEl.ValueKind == JsonValueKind.String
                    && nameEl.GetString() is { Length: > 0 } labelName)
                {
                    labels.Add(labelName);
                }
            }
        }

        var entityName = new EntityName("github", owner, repo, "work-items", number.ToString());
        var entityData = BuildWorkItemJson(entityName, title, status, url, labels);

        return await WorkspaceToolEntityUtilities.UpsertEntityByPrimaryNameAsync(
            dataAccessLayer,
            entityName,
            entityData,
            "Discover GitHub work item.",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessTimelineAsync(
        IDataAccessLayer dataAccessLayer,
        string owner,
        string repo,
        int issueNumber,
        EntitySnapshot workItemEntity,
        CancellationToken cancellationToken)
    {
        string timelineJson;
        try
        {
            timelineJson = await this.timelineRunner!(owner + "/" + repo, issueNumber, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Failed to fetch timeline for {Owner}/{Repo}#{Number}", owner, repo, issueNumber);
            return;
        }

        JsonElement timelineArray;
        try
        {
            using var doc = JsonDocument.Parse(timelineJson);
            timelineArray = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }

        if (timelineArray.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var timelineEvent in timelineArray.EnumerateArray())
        {
            if (!timelineEvent.TryGetProperty("event", out var eventEl)
                || eventEl.GetString() != "cross-referenced")
            {
                continue;
            }

            if (!timelineEvent.TryGetProperty("source", out var source)
                || !source.TryGetProperty("issue", out var sourceIssue)
                || !sourceIssue.TryGetProperty("pull_request", out _)
                || !sourceIssue.TryGetProperty("number", out var prNumberEl)
                || !prNumberEl.TryGetInt32(out var prNumber))
            {
                continue;
            }

            var prEntityName = new EntityName("github", owner, repo, "pull-requests", prNumber.ToString());
            var prEntity = await WorkspaceToolEntityUtilities.TryGetEntityByNameAsync(
                dataAccessLayer, prEntityName, cancellationToken).ConfigureAwait(false);

            if (prEntity is null)
            {
                continue;
            }

            var relationshipName = new EntityName(
                "github", owner, repo, "work-items", issueNumber.ToString(),
                "related", prEntity.EntityId.Value.ToString());

            var relationshipData = BuildRelatedJson(relationshipName, workItemEntity.EntityId, prEntity.EntityId);

            await WorkspaceToolEntityUtilities.UpsertEntityByPrimaryNameAsync(
                dataAccessLayer,
                relationshipName,
                relationshipData,
                "Link GitHub work item to related pull request.",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string> FetchIssuesAsync(
        string owner,
        string repo,
        int maxItems,
        CancellationToken cancellationToken)
    {
        if (this.issueListRunner is not null)
        {
            return await this.issueListRunner(owner + "/" + repo, cancellationToken).ConfigureAwait(false);
        }

        return await DefaultFetchIssuesAsync(owner, repo, maxItems, this.logger, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<string> DefaultFetchIssuesAsync(
        string owner,
        string repo,
        int maxItems,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var parameters = BuildGhParameters(
            owner, repo, maxItems);
        var result = await ProcessRunner.RunAndLogAsync(
            parameters,
            logger,
            operationDescription: $"gh issue list {owner}/{repo}",
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"gh issue list exited with code {result.ExitCode} for {owner}/{repo}");
        }

        return result.StandardOut;
    }

    internal static RunProcessParameters BuildGhParameters(string owner, string repo, int maxItems)
    {
        if (OperatingSystem.IsWindows())
        {
            return new RunProcessParameters(
                Command: "cmd.exe",
                Arguments:
                [
                    "/c", "gh", "issue", "list",
                    "--repo", $"{owner}/{repo}",
                    "--json", "number,title,state,body,labels,url,createdAt,updatedAt",
                    "--limit", maxItems.ToString(),
                    "--state", "all",
                ]);
        }

        return new RunProcessParameters(
            Command: "gh",
            Arguments:
            [
                "issue", "list",
                "--repo", $"{owner}/{repo}",
                "--json", "number,title,state,body,labels,url,createdAt,updatedAt",
                "--limit", maxItems.ToString(),
                "--state", "all",
            ]);
    }

    private static (string Owner, string Repo)? TryExtractGitHubOwnerRepo(EntitySnapshot participant)
    {
        var types = WorkspaceEntitySnapshotReader.GetEntityTypes(participant);
        if (!types.Contains("git-repository", StringComparer.Ordinal))
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
        return TryParseGitHubUrl(url);
    }

    internal static (string Owner, string Repo)? TryParseGitHubUrl(string url)
    {
        if (!url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var path = url["https://github.com/".Length..].TrimEnd('/');
        var slashIndex = path.IndexOf('/', StringComparison.Ordinal);
        if (slashIndex <= 0 || slashIndex == path.Length - 1)
        {
            return null;
        }

        var owner = path[..slashIndex];
        var repoWithSuffix = path[(slashIndex + 1)..];
        var repo = repoWithSuffix.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? repoWithSuffix[..^4]
            : repoWithSuffix;

        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
        {
            return null;
        }

        return (owner, repo);
    }

    private static string MapGitHubStateToStatus(string state)
    {
        return state.ToUpperInvariant() switch
        {
            "CLOSED" => "closed",
            _ => "open",
        };
    }

    private static JsonObject BuildWorkItemJson(
        EntityName entityName,
        string title,
        string status,
        string url,
        IReadOnlyList<string> labels)
    {
        var namesArray = new JsonArray(
            new JsonArray(
                entityName.Components.Select(c => (JsonNode)c).ToArray()));

        var labelsArray = new JsonArray(
            labels.Select(l => (JsonNode)l).ToArray());

        return new JsonObject
        {
            ["entity-types"] = new JsonArray("entity", "work-item", "git-work-item", "external"),
            ["names"] = namesArray,
            ["display-name"] = new JsonObject
            {
                ["default"] = title,
            },
            ["title"] = title,
            ["status"] = status,
            ["labels"] = labelsArray,
            ["urls"] = new JsonObject
            {
                ["default"] = url,
            },
        };
    }

    private static System.Text.Json.Nodes.JsonObject BuildRelatedJson(
        EntityName entityName,
        EntityId workItemId,
        EntityId prId)
    {
        var namesArray = new JsonArray(
            new JsonArray(
                entityName.Components.Select(c => (JsonNode)c).ToArray()));

        return new JsonObject
        {
            ["entity-types"] = new JsonArray("entity", "relationship", "related"),
            ["names"] = namesArray,
            ["participants"] = new JsonObject
            {
                ["entities"] = new JsonArray(
                    workItemId.Value.ToString(),
                    prId.Value.ToString()),
            },
            ["note"] = "Linked via GitHub cross-reference.",
        };
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
}
