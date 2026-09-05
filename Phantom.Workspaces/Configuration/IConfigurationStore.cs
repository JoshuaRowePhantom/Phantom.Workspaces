using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Configuration;

public interface IConfigurationStore
{
    string ConfigurationPath { get; }

    string WebViewDataFolderPath { get; }

    string LogDirectoryPath { get; }

    bool ConfigurationExists();

    Task<WorkspacesConfiguration> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(WorkspacesConfiguration configuration, CancellationToken ct = default);
}
