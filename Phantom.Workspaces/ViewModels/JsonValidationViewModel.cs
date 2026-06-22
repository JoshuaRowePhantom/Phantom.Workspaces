using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Live JSON validation status for the entity editor. Validates working JSON against (a) JSON
/// syntax and (b) the composed schema for the entity's entity types, exposing a status line and an
/// <see cref="IsValid"/> flag used to gate saving. Validation is event-driven (awaitable), not
/// timer-based, so it is deterministic in tests.
/// </summary>
public sealed class JsonValidationViewModel : ViewModelBase
{
    public const string ValidStatusText = "👍 Valid";

    private readonly IEntitySchemaComposer? schemaComposer;
    private bool isValid = true;
    private string statusText = ValidStatusText;
    private bool hasError;

    public JsonValidationViewModel(IEntitySchemaComposer? schemaComposer = null)
    {
        this.schemaComposer = schemaComposer;
    }

    /// <summary>Raised after each completed validation pass.</summary>
    public event EventHandler? ValidationCompleted;

    public bool IsValid
    {
        get => this.isValid;
        private set => this.SetProperty(ref this.isValid, value);
    }

    public string StatusText
    {
        get => this.statusText;
        private set => this.SetProperty(ref this.statusText, value);
    }

    public bool HasError
    {
        get => this.hasError;
        private set => this.SetProperty(ref this.hasError, value);
    }

    /// <summary>
    /// Validates the supplied JSON text, updating <see cref="IsValid"/>, <see cref="StatusText"/>
    /// and <see cref="HasError"/>. The schema-evaluation work runs off the calling thread.
    /// </summary>
    public async Task UpdateAsync(string json)
    {
        JsonElement element;
        try
        {
            using var document = JsonDocument.Parse(json);
            element = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            this.SetResult(isValid: false, exception.Message, hasError: true);
            return;
        }

        if (this.schemaComposer is null)
        {
            this.SetResult(isValid: true, ValidStatusText, hasError: false);
            return;
        }

        var errors = await Task.Run(() => this.schemaComposer.GetValidationErrorsAsync(element)).ConfigureAwait(true);
        if (errors.Count > 0)
        {
            this.SetResult(isValid: false, errors.First(), hasError: true);
            return;
        }

        this.SetResult(isValid: true, ValidStatusText, hasError: false);
    }

    private void SetResult(bool isValid, string statusText, bool hasError)
    {
        this.IsValid = isValid;
        this.StatusText = statusText;
        this.HasError = hasError;
        this.ValidationCompleted?.Invoke(this, EventArgs.Empty);
    }
}
