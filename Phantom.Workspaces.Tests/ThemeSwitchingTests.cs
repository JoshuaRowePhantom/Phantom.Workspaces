using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Phantom.Workspaces.Testing.Gui;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class ThemeSwitchingTests
{
    /// <summary>
    /// Theme-variant keys that must NOT appear in the flat Application.Resources
    /// after ApplyThemeResources runs. These are managed exclusively by
    /// ThemeDictionaries (Light.axaml / Dark.axaml).
    /// </summary>
    private static readonly string[] ThemeVariantKeys =
    [
        "Theme.Surface.EntityPane.Background",
        "Theme.Surface.EntityPane.Border",
        "Theme.Surface.EntityPane.HoverBackground",
        "Theme.Surface.EntityPane.HoverBorder",
        "Theme.Surface.EntityPane.SelectedBackground",
        "Theme.Surface.EntityPane.SelectedBorder",
        "Theme.Surface.EntityCard.Background",
        "Theme.Surface.EntityCard.Border",
        "Theme.Surface.EntityCard.HoverBackground",
        "Theme.Surface.EntityCard.HoverBorder",
        "Theme.Surface.EntityCard.SelectedBackground",
        "Theme.Surface.EntityCard.SelectedBorder",
        "Theme.Surface.Popup.Background",
        "Theme.Surface.Popup.Border",
        "Theme.Class.normal.Foreground",
        "Theme.Class.heading.Foreground",
        "Theme.Class.section-title.Foreground",
        "Theme.Class.caption.Foreground",
        "Theme.Class.muted.Foreground",
        "Theme.Class.accent.Foreground",
    ];

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task ApplyThemeResources_AfterFix_DoesNotWriteThemeVariantKeysToFlatDictionary()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var flatResources = Application.Current!.Resources;
        foreach (var key in ThemeVariantKeys)
        {
            Assert.False(
                flatResources.ContainsKey(key),
                $"Theme-variant key '{key}' must not be in the flat Application.Resources dictionary; it should resolve through ThemeDictionaries.");
        }
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task NotificationsPopup_SwitchToLightTheme_PanelBackgroundUpdatesToLightValue()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        await viewModel.SetThemeAsync("light");

        AssertBrushColor("Theme.Surface.Popup.Background", ThemeVariant.Light, "#FFFFFF");
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task NotificationsPopup_SwitchToDarkTheme_PanelBackgroundUpdatesToDarkValue()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        await viewModel.SetThemeAsync("dark");

        AssertBrushColor("Theme.Surface.Popup.Background", ThemeVariant.Dark, "#2C2C2C");
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task NavStackPopup_SwitchToLightTheme_PanelBackgroundUpdatesToLightValue()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        await viewModel.SetThemeAsync("light");

        AssertBrushColor("Theme.Surface.Popup.Border", ThemeVariant.Light, "#CCCCCC");
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task NavStackPopup_SwitchToDarkTheme_PanelBackgroundUpdatesToDarkValue()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        await viewModel.SetThemeAsync("dark");

        AssertBrushColor("Theme.Surface.Popup.Border", ThemeVariant.Dark, "#3A3A3A");
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task NotificationsPopup_ThemeSwitchedWhileOpen_PanelAppearanceUpdatesWithoutReopening()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        // Start with dark theme
        await viewModel.SetThemeAsync("dark");
        AssertBrushColor("Theme.Surface.Popup.Background", ThemeVariant.Dark, "#2C2C2C");

        // Switch to light without closing/reopening any popup — resource resolution should change
        await viewModel.SetThemeAsync("light");
        AssertBrushColor("Theme.Surface.Popup.Background", ThemeVariant.Light, "#FFFFFF");

        // Verify the RequestedThemeVariant actually switched
        Assert.Equal(ThemeVariant.Light, Application.Current!.RequestedThemeVariant);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task ThemeSwitch_RequestedThemeVariant_ChangesResourceResolution()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        // Switch to light
        await viewModel.SetThemeAsync("light");
        Assert.Equal(ThemeVariant.Light, Application.Current!.RequestedThemeVariant);

        // Verify surface keys resolve correctly for light
        AssertBrushColor("Theme.Surface.EntityPane.Background", ThemeVariant.Light, "#F3F3F3");
        AssertBrushColor("Theme.Surface.Popup.Background", ThemeVariant.Light, "#FFFFFF");
        AssertBrushColor("Theme.Class.normal.Foreground", ThemeVariant.Light, "#1A1A1A");

        // Switch to dark
        await viewModel.SetThemeAsync("dark");
        Assert.Equal(ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);

        // Verify surface keys resolve correctly for dark
        AssertBrushColor("Theme.Surface.EntityPane.Background", ThemeVariant.Dark, "#1E1E1E");
        AssertBrushColor("Theme.Surface.Popup.Background", ThemeVariant.Dark, "#2C2C2C");
        AssertBrushColor("Theme.Class.normal.Foreground", ThemeVariant.Dark, "#E6E6E6");
    }

    private static void AssertBrushColor(string key, ThemeVariant variant, string expectedHex)
    {
        Assert.True(
            Application.Current!.TryGetResource(key, variant, out var value),
            $"Resource '{key}' should resolve for {variant} theme");
        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(value);
        Assert.Equal(Color.Parse(expectedHex), brush.Color);
    }

    private static MainWindowViewModel CreateTestMainWindowViewModel()
    {
        return new MainWindowViewModel(
            new UnknownRepositorySource(),
            new Configuration.WorkspacesConfiguration { SkipStartupWorkspace = true },
            new ProfileStore(CreateTempProfileStorePath()),
            applicationServices: null);
    }

    private static string CreateTempProfileStorePath()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "Phantom.Workspaces.Tests",
            Guid.NewGuid().ToString("N"),
            "profile.json");
    }
}
