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
            var parseResult = Program.ParseResult
                ?? throw new InvalidOperationException("ParseResult not set before Avalonia initialized.");
            var viewModel = new MainWindowViewModel(parseResult);
            desktop.MainWindow = new MainWindow(viewModel);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
