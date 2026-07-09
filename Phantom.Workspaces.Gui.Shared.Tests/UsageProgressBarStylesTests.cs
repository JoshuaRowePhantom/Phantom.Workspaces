using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Phantom.Workspaces.Gui.Shared.Tests;

public sealed class UsageProgressBarStylesTests
{
    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void UsageProgressBar_YellowFill_ForUsedPortion()
    {
        var sharedStyles = LoadSharedStyles();
        var usageProgressBarStyles = LoadUsageProgressBarStyles();

        var progressBar = new ProgressBar { Value = 50, Minimum = 0, Maximum = 100 };
        progressBar.Classes.Add("usage-progress-bar");

        var host = new StackPanel();
        host.Styles.Add(sharedStyles);
        host.Styles.Add(usageProgressBarStyles);
        host.Children.Add(progressBar);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        var borders = progressBar.GetVisualDescendants().OfType<Border>().ToList();
        var usedBorder = borders.FirstOrDefault(b => b.Name == "UsedPortion");
        Assert.NotNull(usedBorder);
        Assert.NotNull(usedBorder.Background);
        
        var brush = usedBorder.Background as ISolidColorBrush;
        Assert.NotNull(brush);
        Assert.Equal(Color.FromRgb(0xFF, 0xDD, 0x00), brush.Color);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void UsageProgressBar_GreenFill_ForRemainingPortion()
    {
        var sharedStyles = LoadSharedStyles();
        var usageProgressBarStyles = LoadUsageProgressBarStyles();

        var progressBar = new ProgressBar { Value = 50, Minimum = 0, Maximum = 100 };
        progressBar.Classes.Add("usage-progress-bar");

        var host = new StackPanel();
        host.Styles.Add(sharedStyles);
        host.Styles.Add(usageProgressBarStyles);
        host.Children.Add(progressBar);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        var borders = progressBar.GetVisualDescendants().OfType<Border>().ToList();
        var remainingBorder = borders.FirstOrDefault(b => b.Name == "RemainingPortion");
        Assert.NotNull(remainingBorder);
        Assert.NotNull(remainingBorder.Background);
        
        var brush = remainingBorder.Background as ISolidColorBrush;
        Assert.NotNull(brush);
        Assert.Equal(Color.FromRgb(0x90, 0xEE, 0x90), brush.Color);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void UsageProgressBar_BlackText_Overlay()
    {
        var sharedStyles = LoadSharedStyles();
        var usageProgressBarStyles = LoadUsageProgressBarStyles();

        var progressBar = new ProgressBar { Value = 50, Minimum = 0, Maximum = 100 };
        progressBar.Classes.Add("usage-progress-bar");

        var host = new StackPanel();
        host.Styles.Add(sharedStyles);
        host.Styles.Add(usageProgressBarStyles);
        host.Children.Add(progressBar);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        var textBlock = progressBar.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault();
        Assert.NotNull(textBlock);
        Assert.NotNull(textBlock.Foreground);
        
        var brush = textBlock.Foreground as ISolidColorBrush;
        Assert.NotNull(brush);
        Assert.Equal(Colors.Black, brush.Color);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void UsageProgressBar_FullConsumption_AllYellow()
    {
        var sharedStyles = LoadSharedStyles();
        var usageProgressBarStyles = LoadUsageProgressBarStyles();

        var progressBar = new ProgressBar { Value = 100, Minimum = 0, Maximum = 100 };
        progressBar.Classes.Add("usage-progress-bar");

        var host = new StackPanel();
        host.Styles.Add(sharedStyles);
        host.Styles.Add(usageProgressBarStyles);
        host.Children.Add(progressBar);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        var borders = progressBar.GetVisualDescendants().OfType<Border>().ToList();
        var usedBorder = borders.FirstOrDefault(b => b.Name == "UsedPortion");
        var remainingBorder = borders.FirstOrDefault(b => b.Name == "RemainingPortion");
        
        Assert.NotNull(usedBorder);
        Assert.NotNull(remainingBorder);
        Assert.NotNull(usedBorder.Background);
        
        var usedBrush = usedBorder.Background as ISolidColorBrush;
        Assert.NotNull(usedBrush);
        Assert.Equal(Color.FromRgb(0xFF, 0xDD, 0x00), usedBrush.Color);
        
        // Remaining portion should have zero width at 100%
        Assert.True(remainingBorder.Bounds.Width == 0 || !remainingBorder.IsVisible);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void UsageProgressBar_ZeroValue_ShowsDash()
    {
        var sharedStyles = LoadSharedStyles();
        var usageProgressBarStyles = LoadUsageProgressBarStyles();

        var progressBar = new ProgressBar { Value = 0, Minimum = 0, Maximum = 100 };
        progressBar.Classes.Add("usage-progress-bar");
        progressBar.Classes.Add("null-data");

        var host = new StackPanel();
        host.Styles.Add(sharedStyles);
        host.Styles.Add(usageProgressBarStyles);
        host.Children.Add(progressBar);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        var textBlock = progressBar.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault();
        Assert.NotNull(textBlock);
        Assert.Equal("—", textBlock.Text);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void UsageProgressBar_NullData_ShowsGreyBar()
    {
        var sharedStyles = LoadSharedStyles();
        var usageProgressBarStyles = LoadUsageProgressBarStyles();

        var progressBar = new ProgressBar { Value = 0, Minimum = 0, Maximum = 100 };
        progressBar.Classes.Add("usage-progress-bar");
        progressBar.Classes.Add("null-data");

        var host = new StackPanel();
        host.Styles.Add(sharedStyles);
        host.Styles.Add(usageProgressBarStyles);
        host.Children.Add(progressBar);

        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));

        var borders = progressBar.GetVisualDescendants().OfType<Border>().ToList();
        var usedBorder = borders.FirstOrDefault(b => b.Name == "UsedPortion");
        Assert.NotNull(usedBorder);
        Assert.NotNull(usedBorder.Background);
        
        var brush = usedBorder.Background as ISolidColorBrush;
        Assert.NotNull(brush);
        // Grey should be something like #808080 or similar neutral grey
        Assert.True(brush.Color.R == brush.Color.G && brush.Color.G == brush.Color.B, 
            "Null data should show neutral grey (equal RGB values)");
    }

    private static Avalonia.Styling.Styles LoadUsageProgressBarStyles()
    {
        var source = new Uri("avares://Phantom.Workspaces.Gui.Shared/Styles/UsageProgressBarStyles.axaml");
        var baseUri = new Uri("avares://Phantom.Workspaces.Gui.Shared/");
        var loaded = AvaloniaXamlLoader.Load(source, baseUri);
        return Assert.IsType<Avalonia.Styling.Styles>(loaded);
    }

    private static Avalonia.Styling.Styles LoadSharedStyles()
    {
        var source = new Uri("avares://Phantom.Workspaces.Gui.Shared/Styles/SharedStyles.axaml");
        var baseUri = new Uri("avares://Phantom.Workspaces.Gui.Shared/");
        var loaded = AvaloniaXamlLoader.Load(source, baseUri);
        return Assert.IsType<Avalonia.Styling.Styles>(loaded);
    }
}
