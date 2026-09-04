namespace Phantom.Workspaces.Llm.Secrets;

/// <summary>
/// The manifest-agnostic input to the secret-use consent dialog: one <see cref="SecretRequest"/> per
/// row. The contract deliberately never exposes <c>AgentManifest</c> or <see cref="SecretUseScope"/>
/// types, so <c>Llm.Core</c> never depends on the GUI.
/// </summary>
public record SecretUseDialogInput(IReadOnlyList<SecretRequest> Rows);
