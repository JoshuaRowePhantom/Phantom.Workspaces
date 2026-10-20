namespace Phantom.Workspaces.Llm.Interfaces.Access;

public interface IReleaseMemoryAdministration
{
    ValueTask<IReadOnlyList<ReleaseMemoryDescriptor>> ListAsync(CancellationToken cancellationToken = default);
    ValueTask ForgetAsync(ReleaseMemoryIdentifier identifier, CancellationToken cancellationToken = default);
}
