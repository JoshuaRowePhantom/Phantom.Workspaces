using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces;

public sealed class ProfileStore
{
    private const string FileName = "profile.json";
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };
    private readonly string filePath;

    public ProfileStore(
        string filePath)
    {
        this.filePath = filePath;
    }

    public static ProfileStore ForCurrentUser()
    {
        var baseDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Phantom.Workspaces");
        Directory.CreateDirectory(baseDirectory);
        return new ProfileStore(Path.Combine(baseDirectory, FileName));
    }

    public async Task<ProfileSettings> GetOrInitializeProfileAsync(
        CancellationToken cancellationToken = default)
    {
        var profile = await this.ReadProfileAsync(cancellationToken);
        if (profile is not null)
        {
            return profile;
        }

        var initialProfile = ProfileSettings.Default;
        await this.WriteProfileAsync(initialProfile, cancellationToken);
        return initialProfile;
    }

    public async Task<ProfileSettings> ChangeProfileAsync(
        Func<ProfileSettings, ProfileSettings> change,
        CancellationToken cancellationToken = default)
    {
        var current = await this.GetOrInitializeProfileAsync(cancellationToken);
        var next = NormalizeProfile(change(current));
        await this.WriteProfileAsync(next, cancellationToken);
        return next;
    }

    private async Task<ProfileSettings?> ReadProfileAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(this.filePath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(this.filePath);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var debugging = root.TryGetProperty("debugging", out var debuggingElement)
                && debuggingElement.ValueKind == JsonValueKind.True;
            var theme = root.TryGetProperty("theme", out var themeElement)
                ? ReadTheme(themeElement)
                : ProfileThemeSettings.Dark;
            return NormalizeProfile(
                new ProfileSettings(
                    theme,
                    debugging));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task WriteProfileAsync(
        ProfileSettings profile,
        CancellationToken cancellationToken)
    {
        var parentDirectory = Path.GetDirectoryName(this.filePath);
        if (!string.IsNullOrWhiteSpace(parentDirectory))
        {
            Directory.CreateDirectory(parentDirectory);
        }

        await using var stream = File.Create(this.filePath);
        await JsonSerializer.SerializeAsync(
            stream,
            PersistedProfile.FromSettings(profile),
            SerializerOptions,
            cancellationToken);
    }

    private static ProfileThemeSettings ReadTheme(
        JsonElement themeElement)
    {
        if (themeElement.ValueKind == JsonValueKind.String)
        {
            return ProfileThemeSettings.ForName(themeElement.GetString());
        }

        if (themeElement.ValueKind != JsonValueKind.Object)
        {
            return ProfileThemeSettings.Dark;
        }

        var themeName = themeElement.TryGetProperty("name", out var themeNameElement)
            && themeNameElement.ValueKind == JsonValueKind.String
            ? themeNameElement.GetString()
            : null;

        var baseTheme = ProfileThemeSettings.ForName(themeName);
        var legacySurfaceColors = ReadLegacySurfaceColors(themeElement);
        var colors = ReadColors(themeElement, baseTheme.Colors);
        var surfaces = ReadSurfaces(themeElement, baseTheme.Surfaces, legacySurfaceColors);
        var fonts = ReadFonts(themeElement, baseTheme.Fonts);
        var classes = ReadClasses(themeElement, baseTheme.Classes);
        return baseTheme with
        {
            Colors = colors,
            Surfaces = surfaces,
            Fonts = fonts,
            Classes = classes,
        };
    }

    private static ProfileThemeColors ReadColors(
        JsonElement themeElement,
        ProfileThemeColors fallback)
    {
        if (!themeElement.TryGetProperty("colors", out var colorsElement)
            || colorsElement.ValueKind != JsonValueKind.Object)
        {
            return fallback;
        }

        return fallback with
        {
            TextPrimary = ReadOptionalString(colorsElement, "textPrimary", fallback.TextPrimary),
            TextMuted = ReadOptionalString(colorsElement, "textMuted", fallback.TextMuted),
            TextInverse = ReadOptionalString(colorsElement, "textInverse", fallback.TextInverse),
            Accent = ReadOptionalString(colorsElement, "accent", fallback.Accent),
        };
    }

    private static ProfileThemeSurfaces ReadSurfaces(
        JsonElement themeElement,
        ProfileThemeSurfaces fallback,
        LegacySurfaceColors legacySurfaceColors)
    {
        if (themeElement.TryGetProperty("surfaces", out var surfacesElement)
            && surfacesElement.ValueKind == JsonValueKind.Object)
        {
            return new ProfileThemeSurfaces(
                EntityPane: ReadSurfaceSet(surfacesElement, "entityPane", fallback.EntityPane),
                EntityCard: ReadSurfaceSet(surfacesElement, "entityCard", fallback.EntityCard));
        }

        return fallback with
        {
            EntityPane = fallback.EntityPane with
            {
                Background = legacySurfaceColors.EntityPaneBackground ?? fallback.EntityPane.Background,
                Border = legacySurfaceColors.EntityPaneBorder ?? fallback.EntityPane.Border,
            },
            EntityCard = fallback.EntityCard with
            {
                Background = legacySurfaceColors.EntityCardBackground ?? fallback.EntityCard.Background,
                Border = legacySurfaceColors.EntityCardBorder ?? fallback.EntityCard.Border,
            },
        };
    }

    private static ProfileThemeSurfaceSet ReadSurfaceSet(
        JsonElement surfacesElement,
        string surfaceName,
        ProfileThemeSurfaceSet fallback)
    {
        if (!surfacesElement.TryGetProperty(surfaceName, out var surfaceElement)
            || surfaceElement.ValueKind != JsonValueKind.Object)
        {
            return fallback;
        }

        return fallback with
        {
            Background = ReadOptionalString(surfaceElement, "background", fallback.Background),
            Border = ReadOptionalString(surfaceElement, "border", fallback.Border),
            HoverBackground = ReadOptionalString(surfaceElement, "hoverBackground", fallback.HoverBackground),
            HoverBorder = ReadOptionalString(surfaceElement, "hoverBorder", fallback.HoverBorder),
            SelectedBackground = ReadOptionalString(surfaceElement, "selectedBackground", fallback.SelectedBackground),
            SelectedBorder = ReadOptionalString(surfaceElement, "selectedBorder", fallback.SelectedBorder),
        };
    }

    private static ProfileThemeFonts ReadFonts(
        JsonElement themeElement,
        ProfileThemeFonts fallback)
    {
        if (!themeElement.TryGetProperty("fonts", out var fontsElement)
            || fontsElement.ValueKind != JsonValueKind.Object)
        {
            return fallback;
        }

        var baseFamily = ReadOptionalString(fontsElement, "baseFamily", fallback.BaseFamily);
        var baseSize = ReadOptionalDouble(fontsElement, "baseSize", fallback.BaseSize);
        var globalScale = ReadOptionalScale(fontsElement, "globalScale", fallback.GlobalScale);
        var headingScale = ReadScale(
            fontsElement,
            scalePropertyName: "headingScale",
            absolutePropertyName: "headingSize",
            baseSize * globalScale.Value,
            fallback.HeadingScale);
        var sectionTitleScale = ReadScale(
            fontsElement,
            scalePropertyName: "sectionTitleScale",
            absolutePropertyName: "sectionTitleSize",
            baseSize * globalScale.Value,
            fallback.SectionTitleScale);
        var captionScale = ReadScale(
            fontsElement,
            scalePropertyName: "captionScale",
            absolutePropertyName: "captionSize",
            baseSize * globalScale.Value,
            fallback.CaptionScale);
        return fallback with
        {
            BaseFamily = baseFamily,
            BaseSize = baseSize,
            GlobalScale = globalScale,
            HeadingScale = headingScale,
            SectionTitleScale = sectionTitleScale,
            CaptionScale = captionScale,
        };
    }

    private static ProfileThemeClasses ReadClasses(
        JsonElement themeElement,
        ProfileThemeClasses fallback)
    {
        if (!themeElement.TryGetProperty("classes", out var classesElement)
            || classesElement.ValueKind != JsonValueKind.Object)
        {
            return fallback;
        }

        return new ProfileThemeClasses(
            Normal: ReadThemeClass(classesElement, "normal", fallback.Normal),
            Heading: ReadThemeClass(classesElement, "heading", fallback.Heading),
            SectionTitle: ReadThemeClass(classesElement, "sectionTitle", fallback.SectionTitle),
            Caption: ReadThemeClass(classesElement, "caption", fallback.Caption),
            Muted: ReadThemeClass(classesElement, "muted", fallback.Muted),
            Accent: ReadThemeClass(classesElement, "accent", fallback.Accent));
    }

    private static string ReadOptionalString(
        JsonElement element,
        string propertyName,
        string fallback)
    {
        return element.TryGetProperty(propertyName, out var propertyElement)
            && propertyElement.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(propertyElement.GetString())
            ? propertyElement.GetString()!
            : fallback;
    }

    private static LegacySurfaceColors ReadLegacySurfaceColors(
        JsonElement themeElement)
    {
        if (!themeElement.TryGetProperty("colors", out var colorsElement)
            || colorsElement.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        return new LegacySurfaceColors(
            EntityPaneBackground: TryReadOptionalString(colorsElement, "entityPaneBackground"),
            EntityPaneBorder: TryReadOptionalString(colorsElement, "entityPaneBorder"),
            EntityCardBackground: TryReadOptionalString(colorsElement, "entityCardBackground"),
            EntityCardBorder: TryReadOptionalString(colorsElement, "entityCardBorder"));
    }

    private static string? TryReadOptionalString(
        JsonElement element,
        string propertyName)
    {
        return element.TryGetProperty(propertyName, out var propertyElement)
            && propertyElement.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(propertyElement.GetString())
            ? propertyElement.GetString()
            : null;
    }

    private static double ReadOptionalDouble(
        JsonElement element,
        string propertyName,
        double fallback)
    {
        return element.TryGetProperty(propertyName, out var propertyElement)
            && propertyElement.ValueKind == JsonValueKind.Number
            && propertyElement.TryGetDouble(out var parsed)
            ? parsed
            : fallback;
    }

    private static FontScale ReadOptionalScale(
        JsonElement element,
        string propertyName,
        FontScale fallback)
    {
        var scale = ReadOptionalDouble(element, propertyName, double.NaN);
        return !double.IsNaN(scale) && scale > 0
            ? new FontScale(scale)
            : fallback;
    }

    private static FontScale ReadScale(
        JsonElement element,
        string scalePropertyName,
        string absolutePropertyName,
        double baseSize,
        FontScale fallback)
    {
        var explicitScale = ReadOptionalDouble(element, scalePropertyName, double.NaN);
        if (!double.IsNaN(explicitScale) && explicitScale > 0)
        {
            return new FontScale(explicitScale);
        }

        var absoluteValue = ReadOptionalDouble(element, absolutePropertyName, double.NaN);
        if (!double.IsNaN(absoluteValue) && absoluteValue > 0 && baseSize > 0)
        {
            return new FontScale(absoluteValue / baseSize);
        }

        return fallback;
    }

    private static ProfileThemeClass ReadThemeClass(
        JsonElement classesElement,
        string className,
        ProfileThemeClass fallback)
    {
        if (!classesElement.TryGetProperty(className, out var classElement)
            || classElement.ValueKind != JsonValueKind.Object)
        {
            return fallback;
        }

        return fallback with
        {
            Foreground = ReadOptionalString(classElement, "foreground", fallback.Foreground),
            Opacity = ReadOptionalDouble(classElement, "opacity", fallback.Opacity),
            FontScale = ReadOptionalScale(classElement, "fontScale", fallback.FontScale),
            FontWeight = ReadOptionalString(classElement, "fontWeight", fallback.FontWeight),
        };
    }

    private static ProfileSettings NormalizeProfile(
        ProfileSettings profile)
    {
        var normalizedTheme = ProfileThemeSettings.ForName(profile.Theme.Name) with
        {
            Colors = profile.Theme.Colors,
            Surfaces = profile.Theme.Surfaces,
            Fonts = profile.Theme.Fonts,
            Classes = profile.Theme.Classes,
        };
        return profile with
        {
            Theme = normalizedTheme,
        };
    }

    private readonly record struct LegacySurfaceColors(
        string? EntityPaneBackground,
        string? EntityPaneBorder,
        string? EntityCardBackground,
        string? EntityCardBorder);

    private sealed record PersistedProfile(
        [property: JsonPropertyName("theme")] PersistedTheme Theme,
        [property: JsonPropertyName("debugging")] bool Debugging)
    {
        public static PersistedProfile FromSettings(
            ProfileSettings settings)
        {
            return new PersistedProfile(
                new PersistedTheme(
                    settings.Theme.Name,
                    new PersistedColors(
                        settings.Theme.Colors.TextPrimary,
                        settings.Theme.Colors.TextMuted,
                        settings.Theme.Colors.TextInverse,
                        settings.Theme.Colors.Accent),
                    new PersistedSurfaces(
                        PersistedSurfaceSet.FromSurfaceSet(settings.Theme.Surfaces.EntityPane),
                        PersistedSurfaceSet.FromSurfaceSet(settings.Theme.Surfaces.EntityCard)),
                    new PersistedFonts(
                        settings.Theme.Fonts.BaseFamily,
                        settings.Theme.Fonts.BaseSize,
                        settings.Theme.Fonts.GlobalScale.Value,
                        settings.Theme.Fonts.HeadingScale.Value,
                        settings.Theme.Fonts.SectionTitleScale.Value,
                        settings.Theme.Fonts.CaptionScale.Value),
                    new PersistedClasses(
                        PersistedClass.FromThemeClass(settings.Theme.Classes.Normal),
                        PersistedClass.FromThemeClass(settings.Theme.Classes.Heading),
                        PersistedClass.FromThemeClass(settings.Theme.Classes.SectionTitle),
                        PersistedClass.FromThemeClass(settings.Theme.Classes.Caption),
                        PersistedClass.FromThemeClass(settings.Theme.Classes.Muted),
                        PersistedClass.FromThemeClass(settings.Theme.Classes.Accent))),
                settings.Debugging);
        }
    }

    private sealed record PersistedTheme(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("colors")] PersistedColors Colors,
        [property: JsonPropertyName("surfaces")] PersistedSurfaces Surfaces,
        [property: JsonPropertyName("fonts")] PersistedFonts Fonts,
        [property: JsonPropertyName("classes")] PersistedClasses Classes);

    private sealed record PersistedColors(
        [property: JsonPropertyName("textPrimary")] string TextPrimary,
        [property: JsonPropertyName("textMuted")] string TextMuted,
        [property: JsonPropertyName("textInverse")] string TextInverse,
        [property: JsonPropertyName("accent")] string Accent);

    private sealed record PersistedSurfaces(
        [property: JsonPropertyName("entityPane")] PersistedSurfaceSet EntityPane,
        [property: JsonPropertyName("entityCard")] PersistedSurfaceSet EntityCard);

    private sealed record PersistedSurfaceSet(
        [property: JsonPropertyName("background")] string Background,
        [property: JsonPropertyName("border")] string Border,
        [property: JsonPropertyName("hoverBackground")] string HoverBackground,
        [property: JsonPropertyName("hoverBorder")] string HoverBorder,
        [property: JsonPropertyName("selectedBackground")] string SelectedBackground,
        [property: JsonPropertyName("selectedBorder")] string SelectedBorder)
    {
        public static PersistedSurfaceSet FromSurfaceSet(
            ProfileThemeSurfaceSet surfaceSet)
        {
            return new PersistedSurfaceSet(
                surfaceSet.Background,
                surfaceSet.Border,
                surfaceSet.HoverBackground,
                surfaceSet.HoverBorder,
                surfaceSet.SelectedBackground,
                surfaceSet.SelectedBorder);
        }
    }

    private sealed record PersistedFonts(
        [property: JsonPropertyName("baseFamily")] string BaseFamily,
        [property: JsonPropertyName("baseSize")] double BaseSize,
        [property: JsonPropertyName("globalScale")] double GlobalScale,
        [property: JsonPropertyName("headingScale")] double HeadingScale,
        [property: JsonPropertyName("sectionTitleScale")] double SectionTitleScale,
        [property: JsonPropertyName("captionScale")] double CaptionScale);

    private sealed record PersistedClasses(
        [property: JsonPropertyName("normal")] PersistedClass Normal,
        [property: JsonPropertyName("heading")] PersistedClass Heading,
        [property: JsonPropertyName("sectionTitle")] PersistedClass SectionTitle,
        [property: JsonPropertyName("caption")] PersistedClass Caption,
        [property: JsonPropertyName("muted")] PersistedClass Muted,
        [property: JsonPropertyName("accent")] PersistedClass Accent);

    private sealed record PersistedClass(
        [property: JsonPropertyName("foreground")] string Foreground,
        [property: JsonPropertyName("opacity")] double Opacity,
        [property: JsonPropertyName("fontScale")] double FontScale,
        [property: JsonPropertyName("fontWeight")] string FontWeight)
    {
        public static PersistedClass FromThemeClass(
            ProfileThemeClass themeClass)
        {
            return new PersistedClass(
                themeClass.Foreground,
                themeClass.Opacity,
                themeClass.FontScale.Value,
                themeClass.FontWeight);
        }
    }
}

public sealed record ProfileSettings(ProfileThemeSettings Theme, bool Debugging)
{
    public static ProfileSettings Default { get; } = new(ProfileThemeSettings.Dark, false);
}

public sealed record Profile(ProfileSettings Data)
{
    public static Profile Default { get; } = new(ProfileSettings.Default);

    public ProfileThemeSettings Theme => this.Data.Theme;

    public bool Debugging => this.Data.Debugging;

    public bool DebugOnlyIsVisible => this.Debugging;
}

public sealed record ProfileThemeSettings(
    string Name,
    ProfileThemeColors Colors,
    ProfileThemeSurfaces Surfaces,
    ProfileThemeFonts Fonts,
    ProfileThemeClasses Classes)
{
    public static IReadOnlyList<string> ThemeNames { get; } = ["dark", "light"];

    public static ProfileThemeSettings Dark { get; } = new(
        "dark",
        new ProfileThemeColors(
            TextPrimary: "#E6E6E6",
            TextMuted: "#B3B3B3",
            TextInverse: "#111111",
            Accent: "#5EA0FF"),
        new ProfileThemeSurfaces(
            EntityPane: new ProfileThemeSurfaceSet(
                Background: "#1E1E1E",
                Border: "#2A2A2A",
                HoverBackground: "#242424",
                HoverBorder: "#353535",
                SelectedBackground: "#2A2A2A",
                SelectedBorder: "#444444"),
            EntityCard: new ProfileThemeSurfaceSet(
                Background: "#2A2A2A",
                Border: "#3A3A3A",
                HoverBackground: "#343434",
                HoverBorder: "#4A4A4A",
                SelectedBackground: "#3A3A3A",
                SelectedBorder: "#5EA0FF")),
        new ProfileThemeFonts(
            BaseFamily: "Inter",
            BaseSize: 13,
            GlobalScale: FontScale.One,
            HeadingScale: 16d / 13d,
            SectionTitleScale: 14d / 13d,
            CaptionScale: 11d / 13d),
        new ProfileThemeClasses(
            Normal: new ProfileThemeClass(Foreground: "#E6E6E6", Opacity: 1.0, FontScale: FontScale.One, FontWeight: "Normal"),
            Heading: new ProfileThemeClass(Foreground: "#E6E6E6", Opacity: 1.0, FontScale: 16d / 13d, FontWeight: "Bold"),
            SectionTitle: new ProfileThemeClass(Foreground: "#E6E6E6", Opacity: 1.0, FontScale: 14d / 13d, FontWeight: "Bold"),
            Caption: new ProfileThemeClass(Foreground: "#E6E6E6", Opacity: 1.0, FontScale: 11d / 13d, FontWeight: "Normal"),
            Muted: new ProfileThemeClass(Foreground: "#B3B3B3", Opacity: 0.75, FontScale: FontScale.One, FontWeight: "Normal"),
            Accent: new ProfileThemeClass(Foreground: "#5EA0FF", Opacity: 1.0, FontScale: FontScale.One, FontWeight: "Normal")));

    public static ProfileThemeSettings Light { get; } = new(
        "light",
        new ProfileThemeColors(
            TextPrimary: "#1A1A1A",
            TextMuted: "#5C5C5C",
            TextInverse: "#FFFFFF",
            Accent: "#2B67D1"),
        new ProfileThemeSurfaces(
            EntityPane: new ProfileThemeSurfaceSet(
                Background: "#F3F3F3",
                Border: "#DDDDDD",
                HoverBackground: "#EBEBEB",
                HoverBorder: "#D2D2D2",
                SelectedBackground: "#E5E5E5",
                SelectedBorder: "#C6C6C6"),
            EntityCard: new ProfileThemeSurfaceSet(
                Background: "#FFFFFF",
                Border: "#D0D0D0",
                HoverBackground: "#F6F6F6",
                HoverBorder: "#C2C2C2",
                SelectedBackground: "#EFEFEF",
                SelectedBorder: "#2B67D1")),
        new ProfileThemeFonts(
            BaseFamily: "Inter",
            BaseSize: 13,
            GlobalScale: FontScale.One,
            HeadingScale: 16d / 13d,
            SectionTitleScale: 14d / 13d,
            CaptionScale: 11d / 13d),
        new ProfileThemeClasses(
            Normal: new ProfileThemeClass(Foreground: "#1A1A1A", Opacity: 1.0, FontScale: FontScale.One, FontWeight: "Normal"),
            Heading: new ProfileThemeClass(Foreground: "#1A1A1A", Opacity: 1.0, FontScale: 16d / 13d, FontWeight: "Bold"),
            SectionTitle: new ProfileThemeClass(Foreground: "#1A1A1A", Opacity: 1.0, FontScale: 14d / 13d, FontWeight: "Bold"),
            Caption: new ProfileThemeClass(Foreground: "#1A1A1A", Opacity: 1.0, FontScale: 11d / 13d, FontWeight: "Normal"),
            Muted: new ProfileThemeClass(Foreground: "#5C5C5C", Opacity: 0.75, FontScale: FontScale.One, FontWeight: "Normal"),
            Accent: new ProfileThemeClass(Foreground: "#2B67D1", Opacity: 1.0, FontScale: FontScale.One, FontWeight: "Normal")));

    public static ProfileThemeSettings ForName(
        string? themeName)
    {
        return string.Equals(themeName, "light", StringComparison.OrdinalIgnoreCase)
            ? Light
            : Dark;
    }
}

public sealed record ProfileThemeColors(
    string TextPrimary,
    string TextMuted,
    string TextInverse,
    string Accent);

public sealed record ProfileThemeSurfaces(
    ProfileThemeSurfaceSet EntityPane,
    ProfileThemeSurfaceSet EntityCard);

public sealed record ProfileThemeSurfaceSet(
    string Background,
    string Border,
    string HoverBackground,
    string HoverBorder,
    string SelectedBackground,
    string SelectedBorder);

public sealed record ProfileThemeFonts(
    string BaseFamily,
    double BaseSize,
    FontScale GlobalScale,
    FontScale HeadingScale,
    FontScale SectionTitleScale,
    FontScale CaptionScale);

public sealed record ProfileThemeClasses(
    ProfileThemeClass Normal,
    ProfileThemeClass Heading,
    ProfileThemeClass SectionTitle,
    ProfileThemeClass Caption,
    ProfileThemeClass Muted,
    ProfileThemeClass Accent)
{
    public ProfileThemeClass GetClass(
        string className)
    {
        return className switch
        {
            "normal" => this.Normal,
            "heading" => this.Heading,
            "section-title" => this.SectionTitle,
            "caption" => this.Caption,
            "muted" => this.Muted,
            "accent" => this.Accent,
            _ => this.Normal,
        };
    }
}

public sealed record ProfileThemeClass(
    string Foreground,
    double Opacity,
    FontScale FontScale,
    string FontWeight);

[TypeConverter(typeof(FontScaleTypeConverter))]
public readonly record struct FontScale(double Value)
{
    public static FontScale One { get; } = new(1d);

    public static implicit operator FontScale(double value) => new(value);

    public static implicit operator double(FontScale scale) => scale.Value;

    public override string ToString()
    {
        return this.Value.ToString(CultureInfo.InvariantCulture);
    }
}

public sealed class FontScaleTypeConverter : TypeConverter
{
    public override bool CanConvertFrom(
        ITypeDescriptorContext? context,
        Type sourceType)
    {
        return sourceType == typeof(string)
            || sourceType == typeof(double)
            || sourceType == typeof(float)
            || sourceType == typeof(int)
            || sourceType == typeof(long)
            || base.CanConvertFrom(context, sourceType);
    }

    public override object? ConvertFrom(
        ITypeDescriptorContext? context,
        CultureInfo? culture,
        object value)
    {
        return value switch
        {
            FontScale fontScale => fontScale,
            double doubleValue => new FontScale(doubleValue),
            float floatValue => new FontScale(floatValue),
            int intValue => new FontScale(intValue),
            long longValue => new FontScale(longValue),
            string stringValue when double.TryParse(
                stringValue,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed) => new FontScale(parsed),
            _ => base.ConvertFrom(context, culture, value),
        };
    }
}
