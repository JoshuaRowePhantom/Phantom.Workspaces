using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Services.Logging;
using Phantom.Workspaces.Transport.Logging;
using Phantom.Workspaces.Transport.ReverseHttp;

namespace Phantom.Workspaces.Web.Server;

/// <summary>Shared production registration for the standalone server's file-backed logger consumers.</summary>
public static class StandaloneWebServerLoggingComposition
{
    public static void Configure(
        WebApplicationBuilder builder,
        string logDirectory,
        ReverseConnectionStatusRegistry statusRegistry)
    {
        builder.Logging.Services.AddSingleton<ILoggerProvider>(
            _ => new RollingFileLoggerProvider(logDirectory, HostFileLoggerFactory.DefaultRetention));
        SafeLoggingFilters.Configure(
            builder.Logging, TransportMetadataLoggingOptions.FromEnvironment().Enabled);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

        builder.Services.AddSingleton(statusRegistry);
        builder.Services.AddSingleton(sp => new ReverseHttpServerTransportFactory(
            statusRegistry, loggerFactory: sp.GetRequiredService<ILoggerFactory>()));
        builder.Services.AddSingleton(sp => new AgentChatSessionCache(new AgentServices
        {
            LoggerFactory = sp.GetRequiredService<ILoggerFactory>(),
            AgentPersistenceStoreOverride = sp.GetRequiredService<IAgentPersistenceStore>(),
        }));
    }
}
