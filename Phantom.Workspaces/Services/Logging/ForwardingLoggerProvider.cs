using Microsoft.Extensions.Logging;

namespace Phantom.Workspaces.Services.Logging;

/// <summary>Routes host-DI logging through the already-owned process factory, not a second file provider.</summary>
public sealed class ForwardingLoggerProvider(ILoggerFactory processFactory) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => processFactory.CreateLogger(categoryName);

    public void Dispose()
    {
        // The process factory is owned by the application, not the embedded web host.
    }
}
