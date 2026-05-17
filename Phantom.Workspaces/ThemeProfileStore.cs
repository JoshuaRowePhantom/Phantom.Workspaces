using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

namespace Phantom.Workspaces;

public sealed class ThemeProfileStore
{
    private const string FileName = "theme-profile.json";
    private readonly string filePath;

    public ThemeProfileStore(
        string filePath)
    {
        this.filePath = filePath;
    }

    public static ThemeProfileStore ForCurrentUser()
    {
        var baseDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Phantom.Workspaces");
        Directory.CreateDirectory(baseDirectory);
        return new ThemeProfileStore(Path.Combine(baseDirectory, FileName));
    }

    public async Task<string> GetOrInitializeThemeAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(this.filePath))
        {
            await this.SetThemeAsync("dark", cancellationToken);
            return "dark";
        }

        await using var stream = File.OpenRead(this.filePath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.TryGetProperty("theme", out var themeElement)
            && themeElement.ValueKind == JsonValueKind.String)
        {
            return NormalizeTheme(themeElement.GetString());
        }

        await this.SetThemeAsync("dark", cancellationToken);
        return "dark";
    }

    public async Task SetThemeAsync(
        string themeName,
        CancellationToken cancellationToken = default)
    {
        var normalizedTheme = NormalizeTheme(themeName);
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new Dictionary<string, string>
            {
                ["theme"] = normalizedTheme,
            });

        await File.WriteAllBytesAsync(this.filePath, payload, cancellationToken);
    }

    private static string NormalizeTheme(
        string? themeName)
    {
        return string.Equals(themeName, "light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
    }
}
