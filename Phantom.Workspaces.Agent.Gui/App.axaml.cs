using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Phantom.Workspaces.Agent.Gui.ViewModels;

namespace Phantom.Workspaces.Agent.Gui;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
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
                    desktop.MainWindow = new MainWindow(new MainWindowViewModel(parseResult));
                }
                catch (Exception ex)
                {
                    desktop.MainWindow = new ErrorWindow(ex.Message);
                }
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
