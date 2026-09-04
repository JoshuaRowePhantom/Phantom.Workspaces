using System.Runtime.Versioning;
using Meziantou.Framework.Win32;
using Phantom.Workspaces.Llm.Secrets;

namespace Phantom.Workspaces.Services.Secrets;

public sealed record CredentialPromptResult(string UserName, string Password);

public interface ICredentialPrompt
{
    CredentialPromptResult? Prompt(nint owner, string messageText, string captionText, string userName);
}

public interface ICredentialWriter
{
    bool Exists(string applicationName);

    void Write(string applicationName, string userName, string secret);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialPicker : ICredentialPicker
{
    private const string TargetPrefix = "Phantom.Workspaces:";
    private readonly IHwndProvider hwndProvider;
    private readonly ICredentialPrompt prompt;
    private readonly ICredentialWriter writer;

    public WindowsCredentialPicker(IHwndProvider hwndProvider)
        : this(hwndProvider, new WindowsCredentialPrompt(), new WindowsCredentialWriter())
    {
    }

    internal WindowsCredentialPicker(IHwndProvider hwndProvider, ICredentialPrompt prompt, ICredentialWriter writer)
    {
        this.hwndProvider = hwndProvider;
        this.prompt = prompt;
        this.writer = writer;
    }

    public bool IsSupported => OperatingSystem.IsWindows();

    public Task<string?> PickAsync(string? initialCredentialName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var result = this.prompt.Prompt(
            this.hwndProvider.GetActiveHwnd(),
            "Select or enter a credential to use for this secret.",
            "Phantom.Workspaces — choose credential",
            initialCredentialName ?? string.Empty);
        if (result is null)
        {
            return Task.FromResult<string?>(null);
        }

        var applicationName = TargetPrefix + result.UserName;
        if (!this.writer.Exists(applicationName))
        {
            this.writer.Write(applicationName, result.UserName, result.Password);
        }

        return Task.FromResult<string?>(result.UserName);
    }

    private sealed class WindowsCredentialPrompt : ICredentialPrompt
    {
        public CredentialPromptResult? Prompt(nint owner, string messageText, string captionText, string userName)
        {
#pragma warning disable CA1416
            var result = CredentialManager.PromptForCredentials(
                owner,
                messageText,
                captionText,
                userName,
                CredentialSaveOption.Selected);
#pragma warning restore CA1416
            return result is null ? null : new CredentialPromptResult(result.UserName, result.Password);
        }
    }

    private sealed class WindowsCredentialWriter : ICredentialWriter
    {
        public bool Exists(string applicationName)
        {
#pragma warning disable CA1416
            return CredentialManager.ReadCredential(applicationName) is not null;
#pragma warning restore CA1416
        }

        public void Write(string applicationName, string userName, string secret)
        {
#pragma warning disable CA1416
            CredentialManager.WriteCredential(
                applicationName: applicationName,
                userName: userName,
                secret: secret,
                persistence: CredentialPersistence.LocalMachine);
#pragma warning restore CA1416
        }
    }
}
