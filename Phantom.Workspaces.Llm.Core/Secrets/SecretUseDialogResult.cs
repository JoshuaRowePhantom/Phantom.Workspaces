namespace Phantom.Workspaces.Llm.Secrets;

/// <summary>
/// A single resolved row of the secret-use consent dialog: the original <see cref="SecretRequest"/>
/// together with the <see cref="SecretUseMemory"/> and <see cref="SecretSource"/> the user chose.
/// </summary>
public record SecretUseDialogRow(
    SecretRequest Request,
    SecretUseMemory ChosenMemory,
    SecretSource ChosenSource);

/// <summary>
/// The result of showing the secret-use consent dialog. <see cref="Accepted"/> is
/// <see langword="false"/> when the user cancelled; <see cref="Rows"/> then carries no meaningful
/// choices.
/// </summary>
public record SecretUseDialogResult(bool Accepted, IReadOnlyList<SecretUseDialogRow> Rows);
