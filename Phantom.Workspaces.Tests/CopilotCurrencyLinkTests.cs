using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phantom.Workspaces.Controls;
using Phantom.Workspaces.Models;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Services.UsageProviders;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class CopilotCurrencyLinkTests
{
    [Theory]
    [InlineData("https://github.com/copilot", 12)]
    [InlineData("https://github.com/settings/billing/summary", 0)]
    [InlineData("https://github.com/organizations/contoso/settings/billing/summary", 12)]
    [InlineData("https://github.com/copilot", 0)]
    [InlineData("https://github.com/settings/billing/summary", 12)]
    [InlineData("https://github.com/organizations/contoso/settings/billing/summary", 0)]
    public async Task GitHubCopilotUsageProvider_CurrencyAndQuantity_PreserveDistinctDestinations(
        string accountUrl,
        int spend)
    {
        var account = await CreateAccountAsync(accountUrl, spend);
        Assert.Collection(account.Metrics,
            quantity =>
            {
                Assert.Equal("Copilot AI Credits", quantity.Title);
                Assert.Equal(42m, quantity.QuantityUsed);
                Assert.Equal(0m, quantity.QuantityTotal);
                Assert.Equal("AICredits", quantity.Unit);
                Assert.Equal(new Uri(accountUrl), quantity.WebUrl);
            },
            currency =>
            {
                Assert.Equal("Copilot AI Credits (Additional Usage)", currency.Title);
                Assert.Equal(spend, currency.QuantityUsed);
                Assert.Equal(50m, currency.QuantityTotal);
                Assert.Equal("{0:C2} / {1:C2}", currency.QuantityPresentationFormatString);
                Assert.Equal(spend != 0, currency.IsSelectedAsShown);
                Assert.Equal("https://github.com/settings/copilot/features", currency.WebUrl!.AbsoluteUri);
            });
        Assert.Equal(new Uri(accountUrl), account.SettingsUrl);
        Assert.Equal(account.Metrics[0].ResetsAt, account.Metrics[1].ResetsAt);
        Assert.Equal(account.Metrics[0].BillingPeriodStart, account.Metrics[1].BillingPeriodStart);
    }

    [AvaloniaTheory(Timeout = 15_000)]
    [InlineData("https://github.com/copilot", 0)]
    [InlineData("https://github.com/copilot", 12)]
    [InlineData("https://github.com/settings/billing/summary", 12)]
    [InlineData("https://github.com/organizations/contoso/settings/billing/summary", 12)]
    public async Task UsageTrackerControl_AiCreditsDollarLink_PointerAndKeyboardOpenWithoutSelection(
        string accountUrl,
        int spend)
    {
        var account = await CreateAccountAsync(accountUrl, spend);
        account.Metrics.Add(new UsageMetric { Title = "Account fallback", QuantityUsed = 7m });
        var metrics = new UsageMetrics();
        metrics.Accounts.Add(account);
        var opener = new RecordingOpener();
        var pinnedKey = UsageAccount.ComposeKey(account.Product, account.Metrics[0].Title);
        using var viewModel = new UsageTrackerViewModel(
            metrics,
            initialSelectedUsageMetricKey: pinnedKey,
            urlOpenerProvider: () => opener);
        viewModel.IsOpen = true;
        var selectedKey = viewModel.SelectedUsageMetricKey;
        Assert.Equal(pinnedKey, selectedKey);
        Assert.True(account.Metrics[0].IsSelectedAsShown);
        Assert.False(account.Metrics[1].IsSelectedAsShown);
        var label = viewModel.TopRightLabel;
        var selected = account.Metrics.Select(metric => metric.IsSelectedAsShown).ToArray();
        var control = new UsageTrackerControl { DataContext = viewModel };
        var window = new Window { Content = control, Width = 600, Height = 600 };
        window.Show();
        try
        {
            var link = window.GetVisualDescendants().OfType<HyperlinkButton>()
                .Single(button => button.Name == "MetricRowHyperlink" &&
                    button.DataContext is UsageMetric metric && metric.Title.EndsWith("(Additional Usage)"));
            Assert.Null(link.NavigateUri);
            Assert.True(link.Focusable);
            var point = link.TranslatePoint(new Point(link.Bounds.Width / 2, link.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Assert.Equal(1, opener.Count);
            Assert.Equal("https://github.com/settings/copilot/features", opener.Request!.Url);
            Assert.Equal(UrlOpenPreference.Auto, opener.Request.Preference);

            window.GetVisualDescendants().OfType<HyperlinkButton>()
                .Single(button => button.Name == "AccountHeaderHyperlink").Focus();
            var focusManager = TopLevel.GetTopLevel(link)!.FocusManager!;
            var visited = new System.Collections.Generic.HashSet<IInputElement>();
            while (!ReferenceEquals(focusManager.GetFocusedElement(), link))
            {
                Assert.True(visited.Add(focusManager.GetFocusedElement()!));
                window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
                window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
            }
            foreach (var key in new[] { Key.Enter, Key.Space })
            {
                var previousCount = opener.Count;
                var physicalKey = key == Key.Enter ? PhysicalKey.Enter : PhysicalKey.Space;
                window.KeyPress(key, RawInputModifiers.None, physicalKey, null);
                window.KeyRelease(key, RawInputModifiers.None, physicalKey, null);
                Assert.Equal(previousCount + 1, opener.Count);
                Assert.Equal("https://github.com/settings/copilot/features", opener.Request!.Url);
                Assert.Equal(UrlOpenPreference.Auto, opener.Request.Preference);
            }
            Assert.Equal("https://github.com/settings/copilot/features", opener.Request!.Url);
            Assert.Equal(UrlOpenPreference.Auto, opener.Request.Preference);
            Assert.True(viewModel.IsOpen);
            Assert.Equal(selectedKey, viewModel.SelectedUsageMetricKey);
            Assert.Equal(label, viewModel.TopRightLabel);
            Assert.Equal(selected, account.Metrics.Select(metric => metric.IsSelectedAsShown));
            Assert.Equal(42m, account.Metrics[0].QuantityUsed);
            Assert.Equal(spend, account.Metrics[1].QuantityUsed);
            var header = window.GetVisualDescendants().OfType<HyperlinkButton>()
                .Single(button => button.Name == "AccountHeaderHyperlink");
            var fallback = window.GetVisualDescendants().OfType<HyperlinkButton>()
                .Single(button => button.Name == "MetricRowHyperlink" &&
                    button.DataContext == account.Metrics[2]);
            Assert.Null(account.Metrics[2].WebUrl);
            foreach (var accountLink in new[] { header, fallback })
            {
                var previousCount = opener.Count;
                var accountPoint = accountLink.TranslatePoint(
                    new Point(accountLink.Bounds.Width / 2, accountLink.Bounds.Height / 2),
                    window)!.Value;
                window.MouseDown(accountPoint, MouseButton.Left);
                window.MouseUp(accountPoint, MouseButton.Left);
                Assert.Equal(previousCount + 1, opener.Count);
                Assert.Equal(account.SettingsUrl!.AbsoluteUri, opener.Request.Url);
                Assert.Equal(UrlOpenPreference.Auto, opener.Request.Preference);
                Assert.True(viewModel.IsOpen);
                Assert.Equal(pinnedKey, viewModel.SelectedUsageMetricKey);
                Assert.Equal(selected, account.Metrics.Select(metric => metric.IsSelectedAsShown));
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory(Timeout = 15_000)]
    [InlineData("https://github.com/settings/billing/summary")]
    [InlineData("https://github.com/organizations/contoso/settings/billing/summary")]
    public async Task UsageTrackerControl_ActionsQuantityAndCurrency_PreserveAccountFallbackAndValues(
        string accountUrl)
    {
        var account = new UsageAccount
        {
            Product = "GitHub Actions",
            SettingsUrl = new Uri(accountUrl),
        };
        using var httpClient = new HttpClient(new ActionsUsageHandler());
        var provider = new GitHubActionsUsageProvider(
            httpClient,
            Access.GitHubUsageProviderAccessTests.CreateBroker(() => "fake-token"));
        foreach (var metric in await provider.GetMetricsAsync(account, TestContext.Current.CancellationToken))
        {
            account.Metrics.Add(metric);
        }
        Assert.Collection(account.Metrics,
            quantity =>
            {
                Assert.Equal("Included Usage", quantity.Title);
                Assert.Equal(350m, quantity.QuantityUsed);
                Assert.Equal(500m, quantity.QuantityTotal);
                Assert.Equal("minutes", quantity.Unit);
                Assert.Equal("{0:N0} / {1:N0} {2}", quantity.QuantityPresentationFormatString);
                Assert.Null(quantity.WebUrl);
            },
            currency =>
            {
                Assert.Equal("Additional Usage", currency.Title);
                Assert.Equal(12m, currency.QuantityUsed);
                Assert.Equal(0m, currency.QuantityTotal);
                Assert.Equal(string.Empty, currency.Unit);
                Assert.Equal("{0:C2} / {1:C2}", currency.QuantityPresentationFormatString);
                Assert.Null(currency.WebUrl);
            });
        var presentations = account.Metrics.Select(metric => metric.QuantityPresentation).ToArray();
        var metrics = new UsageMetrics();
        metrics.Accounts.Add(account);
        var opener = new RecordingOpener();
        using var viewModel = new UsageTrackerViewModel(metrics, urlOpenerProvider: () => opener);
        var window = new Window
        {
            Content = new UsageTrackerControl { DataContext = viewModel },
            Width = 600,
            Height = 600,
        };
        window.Show();
        try
        {
            var links = window.GetVisualDescendants().OfType<HyperlinkButton>()
                .Where(button => button.Name == "MetricRowHyperlink").ToArray();
            Assert.Equal(2, links.Length);
            foreach (var link in links)
            {
                Assert.Equal(account.SettingsUrl, link.CommandParameter);
                Assert.Null(link.NavigateUri);
                var previousCount = opener.Count;
                var point = link.TranslatePoint(
                    new Point(link.Bounds.Width / 2, link.Bounds.Height / 2),
                    window)!.Value;
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Assert.Equal(previousCount + 1, opener.Count);
                Assert.Equal(accountUrl, opener.Request!.Url);
                Assert.NotEqual("https://github.com/settings/copilot/features", opener.Request.Url);
                Assert.Equal(UrlOpenPreference.Auto, opener.Request.Preference);
                link.Focus();
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                Assert.Equal(previousCount + 2, opener.Count);
                Assert.Equal(accountUrl, opener.Request.Url);
            }
            Assert.Equal(presentations, account.Metrics.Select(metric => metric.QuantityPresentation));
            Assert.Equal(350m, account.Metrics[0].QuantityUsed);
            Assert.Equal(12m, account.Metrics[1].QuantityUsed);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void UsageTrackerControl_AbsentMetrics_DoesNotSynthesizeDollarLink()
    {
        var metrics = new UsageMetrics();
        metrics.Accounts.Add(new UsageAccount { Product = "github.com" });
        var opener = new RecordingOpener();
        using var viewModel = new UsageTrackerViewModel(metrics, urlOpenerProvider: () => opener);
        var window = new Window { Content = new UsageTrackerControl { DataContext = viewModel } };
        window.Show();
        try
        {
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<HyperlinkButton>(),
                button => button.Name == "MetricRowHyperlink");
            Assert.Equal(0, opener.Count);
        }
        finally
        {
            window.Close();
        }
    }

    internal static async Task<UsageAccount> CreateAccountAsync(
        string accountUrl,
        int spend)
    {
        var account = new UsageAccount
        {
            Product = "github.com",
            UserName = "alice",
            SettingsUrl = new Uri(accountUrl),
            MonthlyBudget = 50m,
        };
        using var httpClient = new HttpClient(new UsageHandler(spend));
        var provider = new GitHubCopilotUsageProvider(
            httpClient,
            Access.GitHubUsageProviderAccessTests.CreateBroker(() => "fake-token"));
        foreach (var metric in await provider.GetMetricsAsync(account, TestContext.Current.CancellationToken))
        {
            account.Metrics.Add(metric);
        }
        return account;
    }

    private sealed class UsageHandler(int spend) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""
                    {"usageItems":[{"product":"copilot","sku":"Copilot AI Credits","quantity":42,"unitType":"AICredits","netAmount":{{spend}}}]}
                    """),
            });
    }

    private sealed class ActionsUsageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"total_minutes_used":350,"included_minutes":500,"total_paid_minutes_used":12}
                    """),
            });
    }

    internal sealed class RecordingOpener : IUrlOpener
    {
        public int Count { get; private set; }
        public OpenUrlRequest? Request { get; private set; }

        public Task OpenAsync(
            OpenUrlRequest request,
            CancellationToken cancellationToken = default)
        {
            this.Count++;
            this.Request = request;
            return Task.CompletedTask;
        }
    }
}
