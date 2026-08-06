using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Secrets;

namespace Phantom.Workspaces.Services.Secrets;

public sealed class SecretProvider : ISecretProvider
{
    private readonly IAllowedSecretsStore allowedSecretsStore;
    private readonly IPlatformSecretStore platformSecretStore;
    private readonly ISecretUseDialogHost dialogHost;
    private readonly Func<CancellationToken, Task<string?>> gitHubTokenResolver;
    private readonly Action<string>? log;

    public SecretProvider(
        IAllowedSecretsStore allowedSecretsStore,
        IPlatformSecretStore platformSecretStore,
        ISecretUseDialogHost dialogHost)
        : this(
            allowedSecretsStore,
            platformSecretStore,
            dialogHost,
            static ct => GitHubAuthTokenResolver.ResolveAsync(cancellationToken: ct),
            log: null)
    {
    }

    internal SecretProvider(
        IAllowedSecretsStore allowedSecretsStore,
        IPlatformSecretStore platformSecretStore,
        ISecretUseDialogHost dialogHost,
        Func<CancellationToken, Task<string?>> gitHubTokenResolver,
        Action<string>? log)
    {
        this.allowedSecretsStore = allowedSecretsStore;
        this.platformSecretStore = platformSecretStore;
        this.dialogHost = dialogHost;
        this.gitHubTokenResolver = gitHubTokenResolver;
        this.log = log;
    }

    public async Task<RequestSecretsResult?> RequestSecretsAsync(
        IReadOnlyList<SecretRequest> requests,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);

        if (requests.Count == 0)
        {
            return new RequestSecretsResult([], []);
        }

        var allowed = await this.allowedSecretsStore.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        var choices = new ResolvedSecretChoice?[requests.Count];
        var needsConsent = new List<int>();

        for (var index = 0; index < requests.Count; index++)
        {
            var request = requests[index];
            var memorized = FindMatchingMemory(request, allowed);
            if (memorized is not null)
            {
                choices[index] = new ResolvedSecretChoice(request, memorized.Memory, memorized.Source);
                this.log?.Invoke($"Secret '{request.SecretName}' granted by remembered consent '{memorized.Memory.Hash}'.");
            }
            else
            {
                needsConsent.Add(index);
            }
        }

        if (needsConsent.Count > 0)
        {
            var dialogInput = new SecretUseDialogInput(needsConsent.Select(index => requests[index]).ToArray());
            var dialogResult = await this.dialogHost.ShowAsync(dialogInput, cancellationToken).ConfigureAwait(false);
            if (!dialogResult.Accepted)
            {
                return null;
            }

            foreach (var index in needsConsent)
            {
                var request = requests[index];
                var row = FindDialogRow(dialogResult.Rows, request);
                if (row is null)
                {
                    choices[index] = new ResolvedSecretChoice(
                        request,
                        request.Memories.FirstOrDefault() ?? new SecretUseMemory(SecretUseScope.AlwaysAsk, "Always Ask", string.Empty),
                        request.DefaultSecretSource ?? request.CandidateSecretSources.FirstOrDefault());
                    continue;
                }

                choices[index] = new ResolvedSecretChoice(request, row.ChosenMemory, row.ChosenSource);
                if (row.ChosenMemory.Scope != SecretUseScope.AlwaysAsk && !string.IsNullOrEmpty(row.ChosenMemory.Hash))
                {
                    await this.allowedSecretsStore.PutAsync(
                        row.ChosenMemory.Hash,
                        new MemorizedSecret(row.ChosenMemory, row.ChosenSource, DateTimeOffset.UtcNow),
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }

        var retrievers = new List<SecretRetriever>();
        var failures = new List<SecretRequestFailure>();
        foreach (var choice in choices)
        {
            if (choice is null)
            {
                continue;
            }

            var result = await this.ResolveChoiceAsync(choice, cancellationToken).ConfigureAwait(false);
            if (result.Retriever is not null)
            {
                retrievers.Add(result.Retriever);
            }
            else if (result.Failure is not null)
            {
                failures.Add(result.Failure);
            }
        }

        return new RequestSecretsResult(retrievers, failures);
    }

    private static MemorizedSecret? FindMatchingMemory(
        SecretRequest request,
        IReadOnlyDictionary<string, MemorizedSecret> allowed)
    {
        foreach (var memory in request.Memories)
        {
            if (!string.IsNullOrEmpty(memory.Hash) && allowed.TryGetValue(memory.Hash, out var memorized))
            {
                return memorized;
            }
        }

        return null;
    }

    private static SecretUseDialogRow? FindDialogRow(IReadOnlyList<SecretUseDialogRow> rows, SecretRequest request)
    {
        foreach (var row in rows)
        {
            if (ReferenceEquals(row.Request, request) || row.Request.Equals(request))
            {
                return row;
            }
        }

        return null;
    }

    private async Task<ResolutionResult> ResolveChoiceAsync(ResolvedSecretChoice choice, CancellationToken cancellationToken)
    {
        return choice.Source switch
        {
            GitHubLoginSecretSource => ResolutionResult.Success(new SecretRetriever
            {
                SecretName = choice.Request.SecretName,
                Secret = ct => this.ResolveGitHubSecretAsync(choice.Request.SecretName, ct),
            }),
            AwsLoginSecretSource => ResolutionResult.Failed(new SecretRequestFailure(
                choice.Request.SecretName,
                "AWS login is not yet implemented",
                SecretRequestFailureReason.Other)),
            AzureLoginSecretSource => ResolutionResult.Failed(new SecretRequestFailure(
                choice.Request.SecretName,
                "Azure login is not yet implemented",
                SecretRequestFailureReason.Other)),
            CredentialStoreSecretSource credentialStore => await this.ResolveCredentialStoreSecretAsync(
                choice.Request.SecretName,
                credentialStore.CredentialName,
                cancellationToken).ConfigureAwait(false),
            null => ResolutionResult.Failed(new SecretRequestFailure(
                choice.Request.SecretName,
                "No secret source was selected",
                SecretRequestFailureReason.Other)),
            _ => ResolutionResult.Failed(new SecretRequestFailure(
                choice.Request.SecretName,
                $"Unsupported secret source '{choice.Source.GetType().Name}'",
                SecretRequestFailureReason.Other)),
        };
    }

    private async Task<ResolutionResult> ResolveCredentialStoreSecretAsync(
        string secretName,
        string credentialName,
        CancellationToken cancellationToken)
    {
        SecureString? secret;
        try
        {
            secret = await this.platformSecretStore.ReadAsync(credentialName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return ResolutionResult.Failed(new SecretRequestFailure(
                secretName,
                $"Credential '{credentialName}' could not be read",
                SecretRequestFailureReason.ErrorReading));
        }

        if (secret is null)
        {
            return ResolutionResult.Failed(new SecretRequestFailure(
                secretName,
                $"Credential '{credentialName}' does not exist",
                SecretRequestFailureReason.DoesntExist));
        }

        return ResolutionResult.Success(new SecretRetriever
        {
            SecretName = secretName,
            Secret = _ => Task.FromResult(secret),
        });
    }

    private async Task<SecureString> ResolveGitHubSecretAsync(string secretName, CancellationToken cancellationToken)
    {
        string? token = null;
        try
        {
            token = await this.gitHubTokenResolver(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(token))
            {
                throw new InvalidOperationException($"GitHub login did not return a token for secret '{secretName}'.");
            }

            return CopyToSecureString(token);
        }
        finally
        {
            token = null;
        }
    }

    private static SecureString CopyToSecureString(string value)
    {
        var secure = new SecureString();
        foreach (var ch in value)
        {
            secure.AppendChar(ch);
        }

        secure.MakeReadOnly();
        return secure;
    }

    private sealed record ResolvedSecretChoice(SecretRequest Request, SecretUseMemory Memory, SecretSource? Source);

    private sealed record ResolutionResult(SecretRetriever? Retriever, SecretRequestFailure? Failure)
    {
        public static ResolutionResult Success(SecretRetriever retriever) => new(retriever, null);

        public static ResolutionResult Failed(SecretRequestFailure failure) => new(null, failure);
    }
}

