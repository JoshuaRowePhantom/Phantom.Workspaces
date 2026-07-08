using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Tools.AzureDevOps;

/// <summary>
/// A built-in scheduled tool that discovers Azure DevOps git repositories from
/// <c>azure-devops-project</c> participant entities and upserts each as an
/// <c>azure-devops-repository</c> entity in the workspace store. Repositories are keyed by the
/// stable name path <c>["azure-devops", org, project, repoName]</c>, so re-running the tool
/// updates rather than duplicates.
/// </summary>
public sealed class AzureDevOpsRepositoryDiscoveryTool : IWorkspaceTool
{
    /// <summary>Optional tool-entity property overriding the Personal Access Token.</summary>
    public const string PersonalAccessTokenProperty = "personal-access-token";

    /// <summary>The environment variable used for authentication when the property is absent.</summary>
    public const string PatEnvironmentVariable = "AZURE_DEVOPS_TOKEN";

    private const string ApiVersion = "7.0";

    private readonly ILogger<AzureDevOpsRepositoryDiscoveryTool> logger;
    private readonly Func<string, string?, CancellationToken, Task<string>>? httpGetter;

    public AzureDevOpsRepositoryDiscoveryTool(
        Func<string, string?, CancellationToken, Task<string>>? httpGetter = null,
        ILogger<AzureDevOpsRepositoryDiscoveryTool>? logger = null)
    {
        this.httpGetter = httpGetter;
        this.logger = logger ?? NullLogger<AzureDevOpsRepositoryDiscoveryTool>.Instance;
    }

    public string ToolType => "azure-devops-repository-discovery";

    public async Task<WorkspaceToolExecutionResult> ExecuteAsync(WorkspaceToolExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

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

            this.logger.LogInformation("Scanning Azure DevOps repositories for {Org}/{Project}", org, project);

            JsonElement repositories;
            try
            {
                repositories = await this.FetchRepositoriesAsync(
                    projectUrl, pat, context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "Failed to fetch repositories for {Org}/{Project}", org, project);
                continue;
            }

            if (repositories.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var repo in repositories.EnumerateArray())
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                await this.UpsertRepositoryAsync(
                    context.DataAccessLayer,
                    org, project, participant.EntityId, repo,
                    context.CancellationToken).ConfigureAwait(false);
                entitiesUpserted++;
            }

            this.logger.LogInformation(
                "Finished scanning {Org}/{Project}: {Count} repository/repositories upserted",
                org, project, entitiesUpserted);
        }

        var summary = $"Scanned {projectsScanned} project(s). Upserted {entitiesUpserted} repository/repositories.";
        return new WorkspaceToolExecutionResult { ResultContent = summary };
    }

    private async Task<JsonElement> FetchRepositoriesAsync(
        string projectUrl,
        string? pat,
        CancellationToken cancellationToken)
    {
        var url = $"{projectUrl.TrimEnd('/')}/_apis/git/repositories?api-version={ApiVersion}";
        var getter = this.httpGetter ?? ((u, p, ct) => DefaultHttpGetAsync(u, p, ct));
        var responseJson = await getter(url, pat, cancellationToken).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(responseJson);
        if (doc.RootElement.TryGetProperty("value", out var valueEl))
        {
            return valueEl.Clone();
        }

        return JsonDocument.Parse("[]").RootElement.Clone();
    }

    private async Task UpsertRepositoryAsync(
        IDataAccessLayer dataAccessLayer,
        string org,
        string project,
        EntityId projectEntityId,
        JsonElement repo,
        CancellationToken cancellationToken)
    {
        var repoId = GetStringProperty(repo, "id") ?? string.Empty;
        var repoName = GetStringProperty(repo, "name") ?? string.Empty;
        var remoteUrl = GetStringProperty(repo, "remoteUrl") ?? string.Empty;

        if (string.IsNullOrWhiteSpace(repoName))
        {
            return;
        }

        var defaultUrl = $"https://dev.azure.com/{org}/{project}/_git/{Uri.EscapeDataString(repoName)}";
        var entityName = new EntityName("azure-devops", org, project, repoName);

        var entityData = new JsonObject
        {
            ["entity-types"] = new JsonArray("entity", "repository", "git-repository", "azure-devops-repository", "external"),
            ["names"] = new JsonArray(
                new JsonArray(entityName.Components.Select(c => (JsonNode)c).ToArray())),
            ["display-name"] = new JsonObject { ["default"] = repoName },
            ["urls"] = new JsonObject { ["default"] = defaultUrl },
            ["repository-id"] = repoId,
            ["project"] = projectEntityId.Value.ToString(),
        };

        if (!string.IsNullOrWhiteSpace(remoteUrl))
        {
            entityData["clone-url"] = remoteUrl;
        }

        await WorkspaceToolEntityUtilities.UpsertEntityByPrimaryNameAsync(
            dataAccessLayer,
            entityName,
            entityData,
            "Discover Azure DevOps repository.",
            cancellationToken).ConfigureAwait(false);
    }

    internal static (string Org, string Project, string ProjectUrl)? TryExtractProjectInfo(EntitySnapshot participant)
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
        return AzureDevOpsWorkItemDiscoveryTool.TryParseAzureDevOpsUrl(url);
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
