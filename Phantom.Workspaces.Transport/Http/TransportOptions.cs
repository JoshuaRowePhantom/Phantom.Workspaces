namespace Phantom.Workspaces.Transport.Http;

public sealed class TransportOptions
{
    public TimeSpan ServerLeaseDuration { get; set; } = TimeSpan.FromSeconds(90);
}
