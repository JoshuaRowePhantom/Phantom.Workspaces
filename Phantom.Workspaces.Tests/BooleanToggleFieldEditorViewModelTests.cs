using System.ComponentModel;
using Phantom.Workspaces.ViewModels;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class BooleanToggleFieldEditorViewModelTests
{
    [Fact]
    public void Value_RoundTrips_AndUpdatesDisplayValue()
    {
        var editor = new BooleanToggleFieldEditorViewModel("paused", value: false);
        Assert.False(editor.Value);
        Assert.Equal("false", editor.DisplayValue);

        var displayChanged = false;
        editor.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(BooleanToggleFieldEditorViewModel.DisplayValue))
            {
                displayChanged = true;
            }
        };

        editor.Value = true;

        Assert.True(editor.Value);
        Assert.Equal("true", editor.DisplayValue);
        Assert.True(displayChanged);
    }

    [Fact]
    public void TypeName_IsBoolean()
    {
        var editor = new BooleanToggleFieldEditorViewModel("paused", value: true);
        Assert.Equal("boolean", editor.TypeName);
    }

    [Fact]
    public void Clone_CopiesFieldNameAndValue()
    {
        var editor = new BooleanToggleFieldEditorViewModel("scheduled-tools-paused", value: true);

        var clone = Assert.IsType<BooleanToggleFieldEditorViewModel>(editor.Clone());

        Assert.Equal("scheduled-tools-paused", clone.FieldName);
        Assert.True(clone.Value);
        Assert.NotSame(editor, clone);
    }
}
