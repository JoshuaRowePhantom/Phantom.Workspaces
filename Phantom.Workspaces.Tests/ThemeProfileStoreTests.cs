namespace Phantom.Workspaces.Tests;

public sealed class ProfileStoreTests
{
    [Fact]
    public async Task GetOrInitializeProfileAsync_DefaultsThemeAndDebugging()
    {
        var path = CreateTempFilePath();
        try
        {
            var store = new ProfileStore(path);

            var profile = await store.GetOrInitializeProfileAsync();

            Assert.Equal("dark", profile.Theme.Name);
            Assert.Equal("#1E1E1E", profile.Theme.Surfaces.EntityPane.Background);
            Assert.Equal("#343434", profile.Theme.Surfaces.EntityCard.HoverBackground);
            Assert.Equal("#5EA0FF", profile.Theme.Surfaces.EntityCard.SelectedBorder);
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

    [Fact]
    public async Task SetThemeAndDebuggingAsync_PreserveEachOther()
    {
        var path = CreateTempFilePath();
        try
        {
            var store = new ProfileStore(path);

            await store.ChangeProfileAsync(
                profile => profile with
                {
                    Debugging = true,
                });
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
                });

            var persistedProfile = await store.GetOrInitializeProfileAsync();

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
