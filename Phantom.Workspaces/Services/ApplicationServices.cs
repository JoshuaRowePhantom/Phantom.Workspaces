using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Services.Secrets;
using Phantom.Workspaces.Services.Logging;
using Phantom.Workspaces.Services.Updates;

namespace Phantom.Workspaces.Services;

public sealed class ApplicationServices
{
    public ApplicationServices(
        IRunningAgentChatTable runningAgentChats,
        IAgentPersistenceStoreCache agentPersistenceStoreCache,
        IUpdateController? updateController = null,
        ILoggerFactory? loggerFactory = null,
        ILogDirectoryProvider? logDirectoryProvider = null,
        ConfigurationPersistenceService? configurationPersistence = null,
        ISecretProvider? secretProvider = null,
        ICredentialPicker? credentialPicker = null)
    {
        this.RunningAgentChats = runningAgentChats;
        this.AgentPersistenceStoreCache = agentPersistenceStoreCache;
        this.UpdateController = updateController;
        this.LoggerFactory = loggerFactory;
        this.LogDirectoryProvider = logDirectoryProvider;
        this.ConfigurationPersistence = configurationPersistence;
        if (secretProvider is null || credentialPicker is null)
        {
            var defaults = CreateDefaultSecretServices();
            secretProvider ??= defaults.SecretProvider;
            credentialPicker ??= defaults.CredentialPicker;
        }

        this.SecretProvider = secretProvider;
        this.CredentialPicker = credentialPicker;
    }

    internal static (ISecretProvider SecretProvider, ICredentialPicker CredentialPicker) CreateDefaultSecretServices()
    {
        IPlatformSecretStore platformStore;
        if (OperatingSystem.IsWindows())
        {
            platformStore = new WindowsCredentialManagerSecretStore();
        }
        else
        {
            platformStore = new NullPlatformSecretStore();
        }

        var allowedSecretsStore = new AllowedSecretsStore(new AllowedSecretsStoreConfiguration());
        var hwndProvider = new AvaloniaHwndProvider();
        ICredentialPicker credentialPicker;
        if (OperatingSystem.IsWindows())
        {
            credentialPicker = new WindowsCredentialPicker(hwndProvider);
        }
        else
        {
            credentialPicker = new NullCredentialPicker();
        }

        var dialogHost = new AvaloniaSecretUseDialogHost(credentialPicker);
        var secretProvider = new SecretProvider(allowedSecretsStore, platformStore, dialogHost);
        return (secretProvider, credentialPicker);
    }

    public IRunningAgentChatTable RunningAgentChats { get; }

    public IAgentPersistenceStoreCache AgentPersistenceStoreCache { get; }

    public IUpdateController? UpdateController { get; }

    /// <summary>
    /// The process logger factory backed by the #1086 rolling file provider, or <c>null</c> when no
    /// file logging has been wired (for example in tests), in which case consumers fall back to a
    /// null logger.
    /// </summary>
    public ILoggerFactory? LoggerFactory { get; }

    /// <summary>The single log-directory resolver for this process, or <c>null</c> when unwired.</summary>
    public ILogDirectoryProvider? LogDirectoryProvider { get; }

    /// <summary>
    /// The configuration persistence service used to save runtime user preferences (for example,
    /// the pinned AI-usage metric selection). Null in tests that don't exercise persistence.
    /// </summary>
    public ConfigurationPersistenceService? ConfigurationPersistence { get; }


    /// <summary>The process-wide provider that materializes manifest secret requests.</summary>
    public ISecretProvider SecretProvider { get; }

    /// <summary>The process-wide picker for choosing or creating platform credentials.</summary>
    public ICredentialPicker CredentialPicker { get; }

    /// <summary>
    /// The canonical URL-opening service (#1172). Populated post-construction by <c>App.axaml.cs</c>
    /// after <c>MainWindowViewModel</c> exists (the opener depends on the view model as
    /// <see cref="Phantom.Workspaces.ViewModels.IWorkspaceTabService"/>). Null in tests that don't
    /// exercise URL opening.
    /// </summary>
    public IUrlOpener? UrlOpener { get; private set; }

    /// <summary>
    /// Post-construction slot for <see cref="UrlOpener"/>. Breaks the
    /// <c>MainWindowViewModel</c> ↔ <c>UrlOpener</c> construction cycle (the opener is
    /// created after the view model, then registered here).
    /// </summary>
    internal void SetUrlOpener(IUrlOpener opener)
    {
        this.UrlOpener = opener;
    }
}
