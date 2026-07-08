using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Tools.AzureDevOps;

/// <summary>
/// A built-in scheduled tool that discovers Azure DevOps pull requests from
/// <c>azure-devops-repository</c> participant entities and upserts each as an
/// <c>azure-devops-pull-request</c> entity in the workspace store. Pull requests are keyed by the
/// stable name path <c>["azure-devops", org, project, repoName, "pull-requests", id]</c>, so
/// re-running the tool updates rather than duplicates.
/// </summary>
public sealed class AzureDevOpsPullRequestDiscoveryTool : IWorkspaceTool
{
    /// <summary>Optional tool-entity property overriding the Personal Access Token.</summary>
    public const string PersonalAccessTokenProperty = "personal-access-token";

    /// <summary>The environment variable used for authentication when the property is absent.</summary>
    public const string PatEnvironmentVariable = "AZURE_DEVOPS_TOKEN";

    private const string ApiVersion = "7.0";

    private readonly ILogger<AzureDevOpsPullRequestDiscoveryTool> logger;
    private readonly Func<string, string?, CancellationToken, Task<string>>? httpGetter;

    public AzureDevOpsPullRequestDiscoveryTool(
        Func<string, string?, CancellationToken, Task<string>>? httpGetter = null,
        ILogger<AzureDevOpsPullRequestDiscoveryTool>? logger = null)
    {
        this.httpGetter = httpGetter;
        this.logger = logger ?? NullLogger<AzureDevOpsPullRequestDiscoveryTool>.Instance;
    }

    public string ToolType => "azure-devops-pull-request-discovery";

    public async Task<WorkspaceToolExecutionResult> ExecuteAsync(WorkspaceToolExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var pat = ReadStringProperty(context.Tool.Data, PersonalAccessTokenProperty)
            ?? Environment.GetEnvironmentVariable(PatEnvironmentVariable);

        var repositoriesScanned = 0;
        var entitiesUpserted = 0;

        foreach (var participant in context.Participants)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var repoInfo = TryExtractRepositoryInfo(participant);
            if (repoInfo is null)
            {
                continue;
            }

            var (org, project, repoName, repoId) = repoInfo.Value;
            repositoriesScanned++;

            this.logger.LogInformation(
                "Scanning Azure DevOps pull requests for {Org}/{Project}/{Repo}", org, project, repoName);

            JsonElement pullRequests;
            try
            {
                pullRequests = await this.FetchPullRequestsAsync(
                    org, project, repoId, pat, context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(
                    ex, "Failed to fetch pull requests for {Org}/{Project}/{Repo}", org, project, repoName);
                continue;
            }

            if (pullRequests.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var pr in pullRequests.EnumerateArray())
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                await this.UpsertPullRequestAsync(
                    context.DataAccessLayer,
                    org, project, repoName, participant.EntityId, pr,
                    context.CancellationToken).ConfigureAwait(false);
                entitiesUpserted++;
            }

            this.logger.LogInformation(
                "Finished scanning {Org}/{Project}/{Repo}: {Count} pull request(s) upserted",
                org, project, repoName, entitiesUpserted);
        }

        var summary = $"Scanned {repositoriesScanned} repository/repositories. Upserted {entitiesUpserted} pull request(s).";
        return new WorkspaceToolExecutionResult { ResultContent = summary };
    }

    private async Task<JsonElement> FetchPullRequestsAsync(
        string org,
        string project,
        string repoId,
        string? pat,
        CancellationToken cancellationToken)
    {
        var url = $"https://dev.azure.com/{Uri.EscapeDataString(org)}/{Uri.EscapeDataString(project)}" +
                  $"/_apis/git/repositories/{Uri.EscapeDataString(repoId)}/pullrequests" +
                  $"?searchCriteria.status=all&api-version={ApiVersion}";

        var getter = this.httpGetter ?? ((u, p, ct) => DefaultHttpGetAsync(u, p, ct));
        var responseJson = await getter(url, pat, cancellationToken).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(responseJson);
        if (doc.RootElement.TryGetProperty("value", out var valueEl))
        {
            return valueEl.Clone();
        }

        return JsonDocument.Parse("[]").RootElement.Clone();
    }

    private async Task UpsertPullRequestAsync(
        IDataAccessLayer dataAccessLayer,
        string org,
        string project,
        string repoName,
        EntityId repositoryEntityId,
        JsonElement pr,
        CancellationToken cancellationToken)
    {
        if (!pr.TryGetProperty("pullRequestId", out var idEl) || !idEl.TryGetInt32(out var prId))
        {
            return;
        }

        var title = GetStringProperty(pr, "title") ?? string.Empty;
        var adoStatus = GetStringProperty(pr, "status") ?? string.Empty;
        var isDraft = pr.TryGetProperty("isDraft", out var isDraftEl)
            && isDraftEl.ValueKind == JsonValueKind.True;
        var mergeStatus = GetStringProperty(pr, "mergeStatus") ?? string.Empty;
        var sourceBranch = GetStringProperty(pr, "sourceRefName") ?? string.Empty;
        var targetBranch = GetStringProperty(pr, "targetRefName") ?? string.Empty;

        var author = string.Empty;
        if (pr.TryGetProperty("createdBy", out var createdBy)
            && createdBy.ValueKind == JsonValueKind.Object)
        {
            author = GetStringProperty(createdBy, "uniqueName") ?? string.Empty;
        }

        var status = MapAdoStatusToStatus(adoStatus, isDraft);
        var defaultUrl = $"https://dev.azure.com/{org}/{project}/_git/{Uri.EscapeDataString(repoName)}/pullrequest/{prId}";

        var entityName = new EntityName("azure-devops", org, project, repoName, "pull-requests", prId.ToString());
        var entityData = new JsonObject
        {
            ["entity-types"] = new JsonArray("entity", "task", "pull-request", "git-pull-request", "azure-devops-pull-request", "external"),
            ["names"] = new JsonArray(
                new JsonArray(entityName.Components.Select(c => (JsonNode)c).ToArray())),
            ["display-name"] = new JsonObject { ["default"] = title },
            ["title"] = title,
            ["status"] = status,
            ["urls"] = new JsonObject { ["default"] = defaultUrl },
            ["pull-request-id"] = prId,
            ["is-draft"] = isDraft,
            ["author"] = author,
            ["merge-status"] = mergeStatus,
            ["source-branch"] = sourceBranch,
            ["target-branch"] = targetBranch,
            ["repository"] = repositoryEntityId.Value.ToString(),
        };

        await WorkspaceToolEntityUtilities.UpsertEntityByPrimaryNameAsync(
            dataAccessLayer,
            entityName,
            entityData,
            "Discover Azure DevOps pull request.",
            cancellationToken).ConfigureAwait(false);
    }

    internal static string MapAdoStatusToStatus(string adoStatus, bool isDraft)
    {
        if (isDraft)
        {
            return "draft";
        }

        return adoStatus.ToLowerInvariant() switch
        {
            "completed" => "merged",
            "abandoned" => "closed",
            _ => "open",
        };
    }

    internal static (string Org, string Project, string RepoName, string RepoId)? TryExtractRepositoryInfo(
        EntitySnapshot participant)
    {
        var types = WorkspaceEntitySnapshotReader.GetEntityTypes(participant);
        if (!types.Contains("azure-devops-repository", StringComparer.Ordinal))
        {
            return null;
        }

        if (participant.Data is not JsonElement data)
        {
            return null;
        }

        var repoId = string.Empty;
        if (data.TryGetProperty("repository-id", out var repoIdEl)
            && repoIdEl.ValueKind == JsonValueKind.String)
        {
            repoId = repoIdEl.GetString() ?? string.Empty;
        }

        if (!data.TryGetProperty("urls", out var urlsEl)
            || urlsEl.ValueKind != JsonValueKind.Object
            || !urlsEl.TryGetProperty("default", out var defaultUrl)
            || defaultUrl.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var url = defaultUrl.GetString() ?? string.Empty;
        var repoUrlInfo = TryParseAzureDevOpsRepositoryUrl(url);
        if (repoUrlInfo is null)
        {
            return null;
        }

        var (org, project, repoName) = repoUrlInfo.Value;
        return (org, project, repoName, repoId);
    }

    internal static (string Org, string Project, string RepoName)? TryParseAzureDevOpsRepositoryUrl(string url)
    {
        // Expected: https://dev.azure.com/{org}/{project}/_git/{repoName}
        const string prefix = "https://dev.azure.com/";
        if (!url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var path = url[prefix.Length..].TrimEnd('/');
        var parts = path.Split('/');

        // Minimum: org/project/_git/repoName → 4 parts
        if (parts.Length < 4)
        {
            return null;
        }

        var org = parts[0];
        var project = parts[1];
        var gitSegment = parts[2];
        var repoName = parts[3];

        if (!string.Equals(gitSegment, "_git", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(org) || string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(repoName))
        {
            return null;
        }

        return (org, project, repoName);
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var el) && el.ValueKind == JsonValueKind.String)
        {
            return el.GetString();
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

    private static async Task<string> DefaultHttpGetAsync(
        string url,
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

        var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }
}
