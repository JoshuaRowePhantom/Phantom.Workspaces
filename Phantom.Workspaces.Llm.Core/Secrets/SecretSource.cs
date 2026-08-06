using System.Text.Json.Serialization;

namespace Phantom.Workspaces.Llm.Secrets;

/// <summary>
/// Identifies where a secret value can be obtained from. Holds no secret value — only the
/// credential <em>name</em> / display information. Serialized polymorphically so it can
/// round-trip through the allowed-secrets store JSON.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(GitHubLoginSecretSource), "github-login")]
[JsonDerivedType(typeof(AwsLoginSecretSource), "aws-login")]
[JsonDerivedType(typeof(AzureLoginSecretSource), "azure-login")]
[JsonDerivedType(typeof(CredentialStoreSecretSource), "credential-store")]
public abstract record SecretSource;

/// <summary>The secret is obtained from the current GitHub login.</summary>
public sealed record GitHubLoginSecretSource : SecretSource;

/// <summary>The secret is obtained from the current AWS login.</summary>
public sealed record AwsLoginSecretSource : SecretSource;

/// <summary>The secret is obtained from the current Azure login.</summary>
public sealed record AzureLoginSecretSource : SecretSource;

/// <summary>The secret is obtained from the platform credential store under <paramref name="CredentialName"/>.</summary>
public sealed record CredentialStoreSecretSource(string CredentialName) : SecretSource;
