using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.ViewModels;

public sealed class CredentialCacheAdministrationViewModel : AdministrationViewModel<CredentialCacheDescriptor>
{
    private CredentialAdministrationFailure? lastFailure;
    public required ICredentialCacheAdministration Administration { private get; init; }
    public CredentialAdministrationFailure? LastFailure => this.lastFailure;
    public override string ActionLabel => Services.Access.CredentialAccessText.DeleteCredential;
    protected override ValueTask<IReadOnlyList<CredentialCacheDescriptor>> ListAsync(CancellationToken cancellationToken) =>
        this.Administration.ListAsync(cancellationToken);
    protected override ValueTask RemoveCoreAsync(CredentialCacheDescriptor descriptor, CancellationToken cancellationToken) =>
        this.Administration.DeleteAsync(descriptor.Identifier, cancellationToken);
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
        return this.lastFailure == CredentialAdministrationFailure.NoLongerCurrent
            ? "The selected cache generation changed. Refresh the list."
            : "Cache deletion unavailable. Refresh and try again.";
    }
}
