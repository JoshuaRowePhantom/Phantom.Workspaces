using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Services.Access;

public sealed class ReleaseMemoryAdministration : IReleaseMemoryAdministration
{
    public required IReleaseAuthorizationPolicy Policy { private get; init; }

    public ValueTask<IReadOnlyList<ReleaseMemoryDescriptor>> ListAsync(CancellationToken cancellationToken = default) =>
        this.Policy.ListAsync(cancellationToken);

    public ValueTask ForgetAsync(ReleaseMemoryIdentifier identifier, CancellationToken cancellationToken = default) =>
        this.Policy.ForgetAsync(identifier, cancellationToken);
}
