using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.ViewModels;

public sealed class ProviderGrantAdministrationViewModel : AdministrationViewModel<ProviderGrantDescriptor>
{
    private CredentialAdministrationFailure? lastFailure;
    public required IProviderGrantAdministration Administration { private get; init; }
    public CredentialAdministrationFailure? LastFailure => this.lastFailure;
    public override string ActionLabel => Services.Access.CredentialAccessText.RevokeAccess;
    protected override ValueTask<IReadOnlyList<ProviderGrantDescriptor>> ListAsync(CancellationToken cancellationToken) =>
        this.Administration.ListAsync(cancellationToken);
    protected override ValueTask RemoveCoreAsync(ProviderGrantDescriptor descriptor, CancellationToken cancellationToken) =>
        this.Administration.RevokeAsync(descriptor.Identifier, cancellationToken);
    protected override bool IsExpectedFailure(Exception exception) => exception is CredentialAdministrationException;
    protected override void OnOperationStarting()
    {
        this.lastFailure = null;
        this.RaisePropertyChanged(nameof(this.LastFailure));
    }
    protected override string GetFailureStatus(Exception exception)
    {
        this.lastFailure = exception is CredentialAdministrationException failure
            ? failure.Failure : CredentialAdministrationFailure.Failed;
        this.RaisePropertyChanged(nameof(this.LastFailure));
        return this.lastFailure == CredentialAdministrationFailure.Denied
            ? "Access revocation was denied. Provider access and cached credentials are unchanged."
            : "Access revocation did not complete. Refresh and try again.";
    }
}
