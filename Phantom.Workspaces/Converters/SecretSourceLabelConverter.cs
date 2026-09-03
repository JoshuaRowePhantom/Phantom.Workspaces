using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Services.Secrets;

namespace Phantom.Workspaces.Converters;

/// <summary>
/// Renders a <see cref="SecretSource"/> as its friendly, human-readable label (e.g.
/// "GitHub login token", "Saved credential 'Name'") via <see cref="SecretSourceDisplay.GetLabel"/>,
/// so the secret-use dialog's source ComboBox never falls back to the record's technical
/// <c>ToString()</c>.
/// </summary>
public sealed class SecretSourceLabelConverter : IValueConverter
{
    public static readonly SecretSourceLabelConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is SecretSource source ? SecretSourceDisplay.GetLabel(source) : value?.ToString();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
