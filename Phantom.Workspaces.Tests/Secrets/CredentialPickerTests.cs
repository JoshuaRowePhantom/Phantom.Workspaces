using Phantom.Workspaces.Services.Secrets;

namespace Phantom.Workspaces.Tests.Secrets;

public sealed class CredentialPickerTests
{
    [Fact]
    public async Task NullCredentialPicker_PickAsync_Always_ReturnsNull()
    {
        Assert.Null(await new NullCredentialPicker().PickAsync("anything", CancellationToken.None));
    }

    [Fact]
    public void NullCredentialPicker_IsSupported_Always_ReturnsFalse()
    {
        Assert.False(new NullCredentialPicker().IsSupported);
    }

    [Fact]
    public async Task WindowsCredentialPicker_PickAsync_UserEntersNewCredential_WritesItAndReturnsName()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var prompt = new FakePrompt { Result = new("NewCredential", "secret") };
        var writer = new FakeCredentialWriter();
        var picker = new WindowsCredentialPicker(new FakeHwndProvider(), prompt, writer);

        var name = await picker.PickAsync("Initial", CancellationToken.None);

        Assert.Equal("NewCredential", name);
        var write = Assert.Single(writer.Writes);
        Assert.Equal("Phantom.Workspaces:NewCredential", write.ApplicationName);
        Assert.Equal("NewCredential", write.UserName);
        Assert.Equal("secret", write.Secret);
    }

    [Fact]
    public async Task WindowsCredentialPicker_PickAsync_UserSelectsExistingCredential_ReturnsNameWithoutRewrite()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var prompt = new FakePrompt { Result = new("Existing", "secret") };
        var writer = new FakeCredentialWriter();
        writer.Existing.Add("Phantom.Workspaces:Existing");
        var picker = new WindowsCredentialPicker(new FakeHwndProvider(), prompt, writer);

        var name = await picker.PickAsync(null, CancellationToken.None);

        Assert.Equal("Existing", name);
        Assert.Empty(writer.Writes);
    }

    [Fact]
    public async Task WindowsCredentialPicker_PickAsync_UserCancels_ReturnsNull()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var picker = new WindowsCredentialPicker(new FakeHwndProvider(), new FakePrompt { Result = null }, new FakeCredentialWriter());

        Assert.Null(await picker.PickAsync(null, CancellationToken.None));
    }

    [Fact]
    public void WindowsCredentialPicker_IsSupported_OnWindows_IsTrue()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.True(new WindowsCredentialPicker(new FakeHwndProvider(), new FakePrompt(), new FakeCredentialWriter()).IsSupported);
    }

    private sealed class FakeHwndProvider : IHwndProvider
    {
        public nint GetActiveHwnd() => 123;
    }

    private sealed class FakePrompt : ICredentialPrompt
    {
        public CredentialPromptResult? Result { get; set; }
        public CredentialPromptResult? Prompt(nint owner, string messageText, string captionText, string userName)
            => this.Result;
    }

    private sealed class FakeCredentialWriter : ICredentialWriter
    {
        public HashSet<string> Existing { get; } = [];
        public List<(string ApplicationName, string UserName, string Secret)> Writes { get; } = [];
        public bool Exists(string applicationName) => this.Existing.Contains(applicationName);
        public void Write(string applicationName, string userName, string secret) => this.Writes.Add((applicationName, userName, secret));
    }
}
