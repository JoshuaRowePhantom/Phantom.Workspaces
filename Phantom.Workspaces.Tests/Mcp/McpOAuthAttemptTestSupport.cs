using System.Net.Http;
using Phantom.Workspaces.Llm.Mcp;
using Phantom.Workspaces.Services.Mcp;

namespace Phantom.Workspaces.Tests.Mcp;

public sealed partial class McpOAuthRedirectHandlerTests
{
    private static Uri AuthUri(string state) => new($"https://auth.test/authorize?client_id=abc&state={Uri.EscapeDataString(state)}");

    private static async Task<string> SendCallbackAsync(Uri redirectUri, string rawQuery)
    {
        using var client = new HttpClient();
        using var response = await client.GetAsync(new UriBuilder(redirectUri)
        {
            Path = "/callback",
            Query = rawQuery,
        }.Uri, TestContext.Current.CancellationToken);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private static string? GetQueryValue(Uri uri, string name) =>
        System.Web.HttpUtility.ParseQueryString(uri.Query)[name];

    private sealed class FakeSystemBrowserLauncher : ISystemBrowserLauncher
    {
        internal int OpenCount { get; private set; }
        internal Uri? LastUri { get; private set; }
        internal Func<Uri, Task<string>>? OnOpen { get; set; }
        internal Action? OnOpenSideEffect { get; set; }
        internal Task<string>? LastCallbackTask { get; private set; }

        public void Open(Uri uri)
        {
            this.OpenCount++;
            this.LastUri = uri;
            this.OnOpenSideEffect?.Invoke();
            if (this.OnOpen is { } callback)
            {
                this.LastCallbackTask = callback(uri);
            }
        }
    }

}
