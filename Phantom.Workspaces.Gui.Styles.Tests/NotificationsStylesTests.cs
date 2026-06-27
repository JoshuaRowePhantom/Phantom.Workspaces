using Avalonia.Headless.XUnit;

namespace Phantom.Workspaces.Gui.Styles.Tests;

public sealed class NotificationsStylesTests
{
    [AvaloniaFact(Timeout = 15_000)]
    public void BellRingingAnimation_RenderTransformKeyFrames_DoNotUseStringValues()
    {
        var repositoryRoot = FindRepositoryRoot();
        var stylesPath = Path.Combine(
            repositoryRoot.FullName,
            "Phantom.Workspaces.Gui.Styles",
            "Styles",
            "NotificationsStyles.axaml");
        var content = File.ReadAllText(stylesPath);

        // String-valued RenderTransform setters (e.g. Value="rotate(-18deg)") cause
        // "No animator registered for RenderTransform" at runtime — Avalonia's XAML IL
        // compiler does not apply property type converters inside KeyFrame.Setter elements.
        Assert.DoesNotContain("Value=\"rotate(", content, StringComparison.Ordinal);

        // The correct syntax uses typed RotateTransform as child element inside the setter.
        Assert.Contains("<RotateTransform", content, StringComparison.Ordinal);
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Phantom.Workspaces.slnx")))
            {
                return current;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test base directory.");
    }
}
