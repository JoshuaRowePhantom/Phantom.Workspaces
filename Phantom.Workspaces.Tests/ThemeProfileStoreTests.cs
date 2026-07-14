using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Tests;

public sealed class ProfileStoreTests
{
    [Fact]
    public void ProfileThemeSettings_Dark_HasExpectedPopupColors()
    {
        Assert.Equal("#2C2C2C", ProfileThemeSettings.Dark.Surfaces.Popup.Background);
        Assert.Equal("#3A3A3A", ProfileThemeSettings.Dark.Surfaces.Popup.Border);
    }

    [Fact]
    public void ProfileThemeSettings_Light_HasExpectedPopupColors()
    {
        Assert.Equal("#FFFFFF", ProfileThemeSettings.Light.Surfaces.Popup.Background);
        Assert.Equal("#D0D0D0", ProfileThemeSettings.Light.Surfaces.Popup.Border);
    }

    [PhantomAvaloniaFact]
    public async Task GetOrInitializeProfileAsync_DefaultsThemeAndDebugging()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = CreateTempFilePath();
        try
        {
            var store = new ProfileStore(path);

            var profile = await store.GetOrInitializeProfileAsync(ct);

            Assert.Equal("dark", profile.Theme.Name);
            Assert.Equal("#1E1E1E", profile.Theme.Surfaces.EntityPane.Background);
            Assert.Equal("#343434", profile.Theme.Surfaces.EntityCard.HoverBackground);
            Assert.Equal("#5EA0FF", profile.Theme.Surfaces.EntityCard.SelectedBorder);
            Assert.Equal("#2C2C2C", profile.Theme.Surfaces.Popup.Background);
            Assert.Equal("#3A3A3A", profile.Theme.Surfaces.Popup.Border);
            Assert.Equal("Inter", profile.Theme.Fonts.BaseFamily);
            Assert.Equal(FontScale.One, profile.Theme.Fonts.GlobalScale);
            Assert.Equal("#E6E6E6", profile.Theme.Classes.Normal.Foreground);
            Assert.Equal("#5EA0FF", profile.Theme.Classes.Accent.Foreground);
            Assert.Equal(16d / 13d, profile.Theme.Classes.Heading.FontScale.Value, 6);
            Assert.Equal(11d / 13d, profile.Theme.Classes.Caption.FontScale.Value, 6);
            Assert.False(profile.Debugging);
            Assert.True(File.Exists(path));
        }
        finally
        {
            DeleteParentDirectory(path);
        }
    }

    [PhantomAvaloniaFact]
    public async Task SetThemeAndDebuggingAsync_PreserveEachOther()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = CreateTempFilePath();
        try
        {
            var store = new ProfileStore(path);

            await store.ChangeProfileAsync(
                profile => profile with
                {
                    Debugging = true,
                },
                ct);
            await store.ChangeProfileAsync(
                profile => profile with
                {
                    Theme = ProfileThemeSettings.Light with
                    {
                        Surfaces = profile.Theme.Surfaces with
                        {
                            EntityCard = profile.Theme.Surfaces.EntityCard with
                            {
                                HoverBackground = "#FEFEFE",
                                HoverBorder = "#AAAAAA",
                            },
                        },
                        Fonts = profile.Theme.Fonts with
                        {
                            BaseFamily = "Segoe UI",
                            GlobalScale = 1.15,
                        },
                        Classes = profile.Theme.Classes with
                        {
                            Normal = profile.Theme.Classes.Normal with
                            {
                                Foreground = "#101010",
                            },
                            Accent = profile.Theme.Classes.Accent with
                            {
                                Foreground = "#AA33CC",
                            },
                            Heading = profile.Theme.Classes.Heading with
                            {
                                FontScale = 18d / profile.Theme.Fonts.BaseSize,
                            },
                        },
                    },
                },
                ct);

            var persistedProfile = await store.GetOrInitializeProfileAsync(ct);

            Assert.True(persistedProfile.Debugging);
            Assert.Equal("light", persistedProfile.Theme.Name);
            Assert.Equal("#101010", persistedProfile.Theme.Classes.Normal.Foreground);
            Assert.Equal("#AA33CC", persistedProfile.Theme.Classes.Accent.Foreground);
            Assert.Equal("Segoe UI", persistedProfile.Theme.Fonts.BaseFamily);
            Assert.Equal("#FEFEFE", persistedProfile.Theme.Surfaces.EntityCard.HoverBackground);
            Assert.Equal("#AAAAAA", persistedProfile.Theme.Surfaces.EntityCard.HoverBorder);
            Assert.Equal(1.15, persistedProfile.Theme.Fonts.GlobalScale.Value, 6);
            Assert.Equal(18d / persistedProfile.Theme.Fonts.BaseSize, persistedProfile.Theme.Classes.Heading.FontScale.Value, 6);
        }
        finally
        {
            DeleteParentDirectory(path);
        }
    }

    [PhantomAvaloniaFact]
    public async Task SetThemeAsync_SwitchToLight_PopupColorsUpdateToLightValues()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = CreateTempFilePath();
        try
        {
            var store = new ProfileStore(path);

            await store.ChangeProfileAsync(
                profile => profile with { Theme = ProfileThemeSettings.Light },
                ct);

            var persistedProfile = await store.GetOrInitializeProfileAsync(ct);

            Assert.Equal("light", persistedProfile.Theme.Name);
            Assert.Equal("#FFFFFF", persistedProfile.Theme.Surfaces.Popup.Background);
            Assert.Equal("#D0D0D0", persistedProfile.Theme.Surfaces.Popup.Border);
        }
        finally
        {
            DeleteParentDirectory(path);
        }
    }

    private static string CreateTempFilePath()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "Phantom.Workspaces.Tests",
            Guid.NewGuid().ToString("N"),
            "profile.json");
    }

    private static void DeleteParentDirectory(
        string filePath)
    {
        var parentDirectory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(parentDirectory) && Directory.Exists(parentDirectory))
        {
            Directory.Delete(parentDirectory, recursive: true);
        }
    }
}
