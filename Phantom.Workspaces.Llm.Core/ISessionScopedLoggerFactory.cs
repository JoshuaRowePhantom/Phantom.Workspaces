using Microsoft.Extensions.Logging;

namespace Phantom.Workspaces.Llm;

/// <summary>A session logger that can create an independent child session sink.</summary>
public interface ISessionScopedLoggerFactory : ILoggerFactory
{
    ILoggerFactory SessionMemoryFactory { get; }

    ILoggerFactory CreateChildSessionFactory();
}
