namespace Phantom.Workspaces.Llm.Interfaces.Access;

public enum AccessPresentationScope { Runtime, Bootstrap }

public abstract record AccessRequest<TResult>
    where TResult : IAccessResult
{
    public required AccessRequestor Requestor { get; init; }
    public required CredentialUseDescriptor Use { get; init; }
    public required AccessPresentationScope PresentationScope { get; init; }
}

public abstract record CredentialAccessRequest<TResult> : AccessRequest<TResult>
    where TResult : ICredentialAccessResult
{
    public required CredentialReference Credential { get; init; }
    public required IReadOnlyList<CredentialSourceChoiceDescriptor> Sources { get; init; }
}

public abstract record CompletionAuthorizationRequest<TResult> : AccessRequest<TResult>
    where TResult : IAccessResult;
