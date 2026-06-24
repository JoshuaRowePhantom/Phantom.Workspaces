namespace Phantom.Workspaces.Install;

/// <summary>The outcome of an <see cref="UpdateService.CheckAsync"/> call.</summary>
public sealed record UpdateCheckResult
{
    /// <summary>Whether a strictly newer stable release is available.</summary>
    public required bool IsUpdateAvailable { get; init; }

    /// <summary>The latest available release, when <see cref="IsUpdateAvailable"/> is true.</summary>
    public ReleaseInfo? LatestRelease { get; init; }

    /// <summary>A shared "no update available" result.</summary>
    public static UpdateCheckResult None { get; } = new() { IsUpdateAvailable = false };
}

/// <summary>Raised when a check finds a newer release.</summary>
public sealed class UpdateAvailableEventArgs : EventArgs
{
    /// <summary>Creates the event for <paramref name="release"/>.</summary>
    public UpdateAvailableEventArgs(ReleaseInfo release)
    {
        ArgumentNullException.ThrowIfNull(release);
        this.Release = release;
    }

    /// <summary>The available release.</summary>
    public ReleaseInfo Release { get; }
}

/// <summary>Thrown when a downloaded update fails SHA256 verification.</summary>
public sealed class UpdateVerificationException : Exception
{
    /// <summary>Creates the exception with a descriptive <paramref name="message"/>.</summary>
    public UpdateVerificationException(string message)
        : base(message)
    {
    }
}
