using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using System.IO;
using System.Text.Json;

namespace Phantom.Workspaces.Agent.Gui;

public partial class App : Application
{
    public App()
    {
        Button.ClickEvent.AddClassHandler<Button>(OnCopyableTextButtonClick);
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        ApplyPersistedTheme();
#if DEBUG
        this.AttachDeveloperTools();
#endif
    }

    private static void ApplyPersistedTheme()
    {
        var filePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Phantom.Workspaces",
            "profile.json");

        string? themeName = null;
        if (File.Exists(filePath))
        {
            try
            {
                using var stream = File.OpenRead(filePath);
                using var document = JsonDocument.Parse(stream);
                var root = document.RootElement;

                if (root.TryGetProperty("theme", out var themeElement))
                {
                    if (themeElement.ValueKind == JsonValueKind.String)
                    {
                        themeName = themeElement.GetString();
                    }
                    else if (themeElement.ValueKind == JsonValueKind.Object
                        && themeElement.TryGetProperty("name", out var nameElement))
                    {
                        themeName = nameElement.GetString();
                    }
                }
            }
            catch (JsonException)
            {
            }
        }

        Application.Current!.RequestedThemeVariant = themeName?.ToLowerInvariant() switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (Program.ParseError is { } error)
            {
                desktop.MainWindow = new ErrorWindow(error);
            }
            else
            {
                try
                {
                    var parseResult = Program.ParseResult
                        ?? throw new InvalidOperationException("ParseResult not set before Avalonia initialized.");
                    
                    // Create the ViewModel asynchronously
                    MainWindowViewModel.CreateAsync(parseResult)
                        .ContinueWith(task =>
                        {
                            if (task.IsFaulted)
                            {
                                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                                {
                                    desktop.MainWindow = new ErrorWindow(task.Exception?.InnerException?.Message ?? "Unknown error");
                                });
                            }
                            else if (task.IsCompletedSuccessfully)
                            {
                                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                                {
                                    var window = new MainWindow(task.Result);
                                    desktop.MainWindow = window;
                                    window.Show();
                                });
                            }
                        });
                }
                catch (Exception ex)
                {
                    desktop.MainWindow = new ErrorWindow(ex.Message);
                }
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void OnCopyableTextButtonClick(
        Button button,
        RoutedEventArgs eventArgs)
    {
        if (!button.Classes.Contains("copyable-text-button"))
        {
            return;
        }

        var textBox = button.GetVisualAncestors()
            .OfType<TextBox>()
            .FirstOrDefault();
        if (textBox is null)
        {
            return;
        }

        var hasSelection = textBox.SelectionStart != textBox.SelectionEnd;
        if (!hasSelection)
        {
            textBox.SelectAll();
        }

        textBox.Copy();

        if (!hasSelection)
        {
            textBox.ClearSelection();
        }

        eventArgs.Handled = true;
    }
}
