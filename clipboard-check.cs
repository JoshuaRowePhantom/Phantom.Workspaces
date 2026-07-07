using System;
using System.Linq;
using System.Reflection;
using Avalonia.Input.Platform;

var type = typeof(IClipboard);
var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance);
foreach (var method in methods.OrderBy(m => m.Name))
{
    Console.WriteLine($"{method.Name}({string.Join(", ", method.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
}
