namespace Phantom.Workspaces.Llm.Secrets;

/// <summary>
/// A single discovered use of a <c>${SECRET:Name}</c> placeholder in an agent definition. Records
/// only the secret <em>name</em> and the JSON path at which it appears; never any secret value.
/// </summary>
/// <param name="SecretName">The human-readable secret name from <c>${SECRET:Name}</c>.</param>
/// <param name="JsonPath">
/// The dotted JSON path (with array indices) at which the placeholder appears, e.g.
/// <c>definition.model.options.additionalProperties.ApiToken</c>. Also used as the
/// <c>useDisplayString</c> in the consent dialog.
/// </param>
public sealed record SecretUsage(string SecretName, string JsonPath);
