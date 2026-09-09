namespace Phantom.Workspaces.Transport;

public sealed record TransportPeerIdentity
{
    private string authenticationScheme = string.Empty;
    private string stablePeerId = string.Empty;
    private string? userEntityId;
    private string? userComputerProfileEntityId;

    public required string AuthenticationScheme
    {
        get => this.authenticationScheme;
        init => this.authenticationScheme = RequireNonBlank(value, nameof(AuthenticationScheme));
    }

    public required string StablePeerId
    {
        get => this.stablePeerId;
        init => this.stablePeerId = RequireNonBlank(value, nameof(StablePeerId));
    }

    public string? UserEntityId
    {
        get => this.userEntityId;
        init => this.userEntityId = RequireOptionalNonBlank(value, nameof(UserEntityId));
    }

    public string? UserComputerProfileEntityId
    {
        get => this.userComputerProfileEntityId;
        init => this.userComputerProfileEntityId = RequireOptionalNonBlank(value, nameof(UserComputerProfileEntityId));
    }

    private static string RequireNonBlank(string? value, string name)
        => !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"{name} must be nonblank.", name);

    private static string? RequireOptionalNonBlank(string? value, string name)
        => value is null || !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"{name} must be null or nonblank.", name);
}
