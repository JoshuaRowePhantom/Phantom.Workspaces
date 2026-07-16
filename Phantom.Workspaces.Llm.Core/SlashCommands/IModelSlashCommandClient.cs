using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot.SDK;

namespace Phantom.Workspaces.Llm.SlashCommands;

/// <summary>
/// Seam consumed by <see cref="CopilotSdkModelSlashCommandHandler"/> for the model-listing
/// and model-switching operations. Implemented in production by <see cref="CopilotSdkChatClient"/>.
/// Extracting this interface lets the handler be unit-tested deterministically with a controlled
/// test double, without depending on ambient Copilot connectivity.
/// </summary>
internal interface IModelSlashCommandClient
{
    /// <summary>The currently active model identifier.</summary>
    string ModelId { get; }

    /// <summary>Changes the active model for this client.</summary>
    void SetModelId(string modelId);

    /// <summary>Returns the models available from the Copilot backend.</summary>
    Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken);
}
