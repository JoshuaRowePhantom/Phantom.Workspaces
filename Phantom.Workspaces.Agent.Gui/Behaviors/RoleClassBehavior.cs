using System.Text;
using Avalonia;
using Avalonia.Styling;

namespace Phantom.Workspaces.Agent.Gui.Behaviors;

public static class RoleClassBehavior
{
    public static readonly AttachedProperty<object?> RoleProperty =
        AvaloniaProperty.RegisterAttached<StyledElement, object?>("Role", typeof(RoleClassBehavior));

    static RoleClassBehavior()
    {
        RoleProperty.Changed.AddClassHandler<StyledElement>((element, args) =>
        {
            UpdateRoleClass(element, args.NewValue);
        });
    }

    public static object? GetRole(AvaloniaObject obj) => obj.GetValue(RoleProperty);

    public static void SetRole(AvaloniaObject obj, object? value) => obj.SetValue(RoleProperty, value);

    private static void UpdateRoleClass(StyledElement element, object? value)
    {
        var classesToRemove = element.Classes
            .Where(static className => className.StartsWith("role-", StringComparison.Ordinal))
            .ToArray();

        foreach (var className in classesToRemove)
        {
            element.Classes.Remove(className);
        }

        var normalizedRole = NormalizeRole(value?.ToString());
        if (normalizedRole is not null)
        {
            element.Classes.Add($"role-{normalizedRole}");
        }
    }

    private static string? NormalizeRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return null;
        }

        var builder = new StringBuilder(role.Length);
        foreach (var ch in role)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
            else if (builder.Length == 0 || builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        return builder.ToString().Trim('-');
    }
}
