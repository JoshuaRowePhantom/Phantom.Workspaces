using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ViewModels;

public sealed class AgentManifestEditorViewModel : WorkspaceTabViewModel
{
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    private readonly MainWindowViewModel mainWindowViewModel;
    private string manifestJson = string.Empty;
    private bool isDirty;

    public SubscribedEntityViewModel ManifestEntity { get; }

    public string ManifestJson
    {
        get => this.manifestJson;
        set
        {
            if (this.SetProperty(ref this.manifestJson, value))
            {
                this.IsDirty = true;
            }
        }
    }

    public bool IsDirty
    {
        get => this.isDirty;
        private set => this.SetProperty(ref this.isDirty, value);
    }

    public RelayCommand SaveCommand { get; }
    public RelayCommand StartSessionCommand { get; }

    public AgentManifestEditorViewModel(
        SubscribedEntityViewModel manifestEntity,
        MainWindowViewModel mainWindowViewModel)
    {
        this.ManifestEntity = manifestEntity;
        this.mainWindowViewModel = mainWindowViewModel;

        this.SaveCommand = new RelayCommand(async _ => await this.SaveAsync());
        this.StartSessionCommand = new RelayCommand(async _ => await this.StartSessionAsync());

        this.LoadManifestJson();
    }

    private void LoadManifestJson()
    {
        if (this.ManifestEntity.Data is JsonElement data
            && data.TryGetProperty("manifest", out var manifestElement))
        {
            this.manifestJson = FormatJson(manifestElement.GetRawText());
        }

        this.IsDirty = false;
    }

    private static string FormatJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, IndentedJsonOptions);
        }
        catch
        {
            return json;
        }
    }

    private async Task SaveAsync()
    {
        var currentData = this.ManifestEntity.Data;
        if (currentData is not JsonElement entityData)
        {
            return;
        }

        try
        {
            using var manifestDoc = JsonDocument.Parse(this.ManifestJson);
            var updatedDataJson = MergeManifestIntoEntityData(entityData, manifestDoc.RootElement);
            using var updatedDataDoc = JsonDocument.Parse(updatedDataJson);

            await this.mainWindowViewModel.EntityBroker.UpdateAsync(
                new UpdateRequest
                {
                    UpdateMetadata = new UpdateMetadata
                    {
                        Comment = new Markdown
                        {
                            Text = $"Update agent manifest for {this.ManifestEntity.DisplayName}.",
                        },
                    },
                    Changes =
                    [
                        new EntityChange
                        {
                            EntityChangeMode = EntityChangeMode.Replace,
                            Data = updatedDataDoc.RootElement.Clone(),
                        },
                    ],
                });

            this.IsDirty = false;
        }
        catch
        {
            // Ignore save errors for now
        }
    }

    private static string MergeManifestIntoEntityData(JsonElement entityData, JsonElement newManifest)
    {
        var writer = new System.Text.StringBuilder();
        writer.Append('{');
        var first = true;
        foreach (var prop in entityData.EnumerateObject())
        {
            if (!first)
            {
                writer.Append(',');
            }
            first = false;
            writer.Append($"\"{prop.Name}\":");
            if (prop.Name == "manifest")
            {
                writer.Append(newManifest.GetRawText());
            }
            else
            {
                writer.Append(prop.Value.GetRawText());
            }
        }
        writer.Append('}');
        return writer.ToString();
    }

    private async Task StartSessionAsync()
    {
        if (this.IsDirty)
        {
            await this.SaveAsync();
        }

        await this.mainWindowViewModel.ShortcutManager.HandleShortcutAsync(
            this.mainWindowViewModel,
            Shortcut.Open,
            this.ManifestEntity);
    }
}
