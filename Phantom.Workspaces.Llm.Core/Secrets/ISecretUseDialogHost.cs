namespace Phantom.Workspaces.Llm.Secrets;

/// <summary>
/// The seam through which the secret-use consent dialog is shown. Implemented by the GUI project;
/// <c>Llm.Core</c> depends only on this contract and never on Avalonia.
/// </summary>
public interface ISecretUseDialogHost
{
    /// <summary>
    /// Shows the consent dialog for <paramref name="input"/> and returns the user's decision.
    /// </summary>
    Task<SecretUseDialogResult> ShowAsync(SecretUseDialogInput input, CancellationToken ct);
}
