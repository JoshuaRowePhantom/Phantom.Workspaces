using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Templates;

public partial class AgentManifestLaunchpadView : UserControl
{
    public AgentManifestLaunchpadView()
    {
        this.InitializeComponent();
    }

    private async void OnBrowseButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button
            || button.DataContext is not AgentManifestParameterRowViewModel row)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        var options = new FolderPickerOpenOptions
        {
            Title = $"Select {row.DisplayName}",
            AllowMultiple = false,
        };

        if (!string.IsNullOrWhiteSpace(row.Value))
        {
            var suggestedStartLocation = await topLevel.StorageProvider
                .TryGetFolderFromPathAsync(row.Value);
            if (suggestedStartLocation is not null)
            {
                options.SuggestedStartLocation = suggestedStartLocation;
            }
        }

        var result = await topLevel.StorageProvider.OpenFolderPickerAsync(options);
        if (result.Count > 0)
        {
            row.Value = result[0].Path.LocalPath;
        }
    }
}
