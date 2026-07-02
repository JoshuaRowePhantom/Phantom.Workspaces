using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Windows.Input;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// A <see cref="DefaultJsonTypeInfoResolver"/> for the Phantom workspace dock layout.
/// Replicates <c>DockModelPolymorphicTypeResolver</c> (internal in Dock.Serializer.SystemTextJson)
/// and additionally removes properties whose declared type is <see cref="Type"/> (e.g. the Avalonia
/// <c>StyledElement.StyleKey</c>) so that they do not cause a <see cref="NotSupportedException"/>
/// during serialization. <c>Owner</c> back-references are excluded from all dockable types because
/// <c>DockSerializer.JsonConverterList</c> creates isolated serialization contexts per list that
/// bypass <c>ReferenceHandler.Preserve</c>, so <c>Owner</c> must be filtered here to prevent
/// infinite recursion through the <c>Owner → VisibleDockables → Owner</c> cycle.
/// </summary>
internal sealed class WorkspaceDockTypeInfoResolver : DefaultJsonTypeInfoResolver
{
    private static readonly Type[] s_polymorphicBaseTypes =
    [
        typeof(IDockable),
        typeof(IDock),
        typeof(IRootDock),
        typeof(IDockWindow),
        typeof(IDocumentTemplate),
        typeof(IToolTemplate),
    ];

    private static readonly Lazy<IReadOnlyDictionary<Type, HashSet<string>>> s_interfaceIgnoredMembers =
        new(BuildInterfaceIgnoredMembers);

    private static readonly Lazy<JsonPolymorphismOptions> s_dockableOptions =
        new(() => CreateOptions(typeof(IDockable), JsonUnknownDerivedTypeHandling.FallBackToBaseType));

    private static readonly Lazy<JsonPolymorphismOptions> s_dockOptions =
        new(() => CreateOptions(typeof(IDock), JsonUnknownDerivedTypeHandling.FallBackToBaseType));

    private static readonly Lazy<JsonPolymorphismOptions> s_rootDockOptions =
        new(() => CreateOptions(typeof(IRootDock), JsonUnknownDerivedTypeHandling.FallBackToNearestAncestor));

    private static readonly Lazy<JsonPolymorphismOptions> s_windowOptions =
        new(() => CreateOptions(typeof(IDockWindow), JsonUnknownDerivedTypeHandling.FallBackToBaseType));

    private static readonly Lazy<JsonPolymorphismOptions> s_documentTemplateOptions =
        new(() => CreateOptions(typeof(IDocumentTemplate), JsonUnknownDerivedTypeHandling.FallBackToNearestAncestor));

    private static readonly Lazy<JsonPolymorphismOptions> s_toolTemplateOptions =
        new(() => CreateOptions(typeof(IToolTemplate), JsonUnknownDerivedTypeHandling.FallBackToNearestAncestor));

    /// <inheritdoc/>
    public override JsonTypeInfo GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        var jsonTypeInfo = base.GetTypeInfo(type, options);
        RemoveIgnoredMembers(jsonTypeInfo);

        if (type == typeof(IDockable))
            jsonTypeInfo.PolymorphismOptions = CloneOptions(s_dockableOptions.Value);
        else if (type == typeof(IDock))
            jsonTypeInfo.PolymorphismOptions = CloneOptions(s_dockOptions.Value);
        else if (type == typeof(IRootDock))
            jsonTypeInfo.PolymorphismOptions = CloneOptions(s_rootDockOptions.Value);
        else if (type == typeof(IDockWindow))
            jsonTypeInfo.PolymorphismOptions = CloneOptions(s_windowOptions.Value);
        else if (type == typeof(IDocumentTemplate))
            jsonTypeInfo.PolymorphismOptions = CloneOptions(s_documentTemplateOptions.Value);
        else if (type == typeof(IToolTemplate))
            jsonTypeInfo.PolymorphismOptions = CloneOptions(s_toolTemplateOptions.Value);

        return jsonTypeInfo;
    }

    private static void RemoveIgnoredMembers(JsonTypeInfo jsonTypeInfo)
    {
        for (var i = jsonTypeInfo.Properties.Count - 1; i >= 0; i--)
        {
            var property = jsonTypeInfo.Properties[i];
            if (property.AttributeProvider?.IsDefined(typeof(IgnoreDataMemberAttribute), true) == true
                || typeof(ICommand).IsAssignableFrom(property.PropertyType)
                || property.PropertyType == typeof(Type)
                || (property.PropertyType is { IsGenericType: true } pt && pt.GetGenericTypeDefinition() == typeof(Nullable<>) && Nullable.GetUnderlyingType(pt) == typeof(Type))
                || IsIgnoredInterfaceMember(jsonTypeInfo.Type, property.Name)
                || IsAvaloniaFrameworkProperty(property)
                || IsDockOwnerBackReference(property))
            {
                jsonTypeInfo.Properties.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Returns <c>true</c> for properties that are declared by Avalonia framework types
    /// (namespace starts with "Avalonia"). These properties are not part of the Dock model
    /// layout and can cause serialization issues (e.g. abstract collection types like
    /// <c>IResourceDictionary</c>, or deep reference chains that overflow the STJ depth limit).
    /// </summary>
    private static bool IsAvaloniaFrameworkProperty(JsonPropertyInfo property)
    {
        var declaringType = (property.AttributeProvider as MemberInfo)?.DeclaringType;
        return declaringType?.Namespace?.StartsWith("Avalonia", StringComparison.Ordinal) == true;
    }

    /// <summary>
    /// Returns <c>true</c> for the <c>Owner</c> back-reference property on dockable types.
    /// <c>Owner</c> is a runtime back-reference set by the dock factory; it must not appear in
    /// the persisted layout JSON. <c>DockSerializer.JsonConverterList</c> creates isolated
    /// serialization contexts per list, bypassing <c>ReferenceHandler.Preserve</c>, so filtering
    /// <c>Owner</c> here prevents infinite recursion through Owner→VisibleDockables→Owner cycles.
    /// </summary>
    private static bool IsDockOwnerBackReference(JsonPropertyInfo property)
    {
        return string.Equals(property.Name, "Owner", StringComparison.Ordinal)
            && typeof(IDockable).IsAssignableFrom(property.PropertyType);
    }

    private static bool IsIgnoredInterfaceMember(Type type, string propertyName)
    {
        if (!type.IsInterface)
            return false;

        if (!s_interfaceIgnoredMembers.Value.TryGetValue(type, out var ignoredMembers))
            return false;

        return ignoredMembers.Contains(propertyName);
    }

    private static JsonPolymorphismOptions CreateOptions(Type baseType, JsonUnknownDerivedTypeHandling handling)
    {
        var options = new JsonPolymorphismOptions
        {
            TypeDiscriminatorPropertyName = "$type",
            UnknownDerivedTypeHandling = handling,
            IgnoreUnrecognizedTypeDiscriminators = true,
        };

        foreach (var derivedType in GetDerivedTypes(baseType))
            options.DerivedTypes.Add(new JsonDerivedType(derivedType, derivedType.FullName ?? derivedType.Name));

        return options;
    }

    private static JsonPolymorphismOptions CloneOptions(JsonPolymorphismOptions template)
    {
        var clone = new JsonPolymorphismOptions
        {
            TypeDiscriminatorPropertyName = template.TypeDiscriminatorPropertyName,
            UnknownDerivedTypeHandling = template.UnknownDerivedTypeHandling,
            IgnoreUnrecognizedTypeDiscriminators = template.IgnoreUnrecognizedTypeDiscriminators,
        };

        foreach (var derivedType in template.DerivedTypes)
        {
            if (derivedType.TypeDiscriminator is int intDiscriminator)
                clone.DerivedTypes.Add(new JsonDerivedType(derivedType.DerivedType, intDiscriminator));
            else if (derivedType.TypeDiscriminator is string stringDiscriminator)
                clone.DerivedTypes.Add(new JsonDerivedType(derivedType.DerivedType, stringDiscriminator));
            else
                clone.DerivedTypes.Add(new JsonDerivedType(derivedType.DerivedType));
        }

        return clone;
    }

    private static IReadOnlyList<Type> GetDerivedTypes(Type baseType)
    {
        var types = new HashSet<Type>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic)
                continue;

            foreach (var type in GetAssemblyTypesSafely(assembly))
            {
                if (type is null || !type.IsClass || type.IsAbstract || type.ContainsGenericParameters)
                    continue;

                if (!IsPublicType(type) || !baseType.IsAssignableFrom(type))
                    continue;

                types.Add(type);
            }
        }

        return types.OrderBy(t => t.FullName, StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<Type?> GetAssemblyTypesSafely(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types;
        }
    }

    private static bool IsPublicType(Type type) => type.IsPublic || type.IsNestedPublic;

    private static IReadOnlyDictionary<Type, HashSet<string>> BuildInterfaceIgnoredMembers()
    {
        var map = new Dictionary<Type, HashSet<string>>();
        foreach (var baseType in s_polymorphicBaseTypes)
        {
            var ignoredMembers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var type in GetAssignableTypes(baseType))
            {
                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (property.IsDefined(typeof(IgnoreDataMemberAttribute), true)
                        || typeof(ICommand).IsAssignableFrom(property.PropertyType))
                    {
                        ignoredMembers.Add(property.Name);
                    }
                }
            }

            if (ignoredMembers.Count > 0)
                map[baseType] = ignoredMembers;
        }

        return map;
    }

    private static IEnumerable<Type> GetAssignableTypes(Type baseType)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic)
                continue;

            foreach (var type in GetAssemblyTypesSafely(assembly))
            {
                if (type is null || !type.IsClass || type.ContainsGenericParameters)
                    continue;

                if (!IsPublicType(type) || !baseType.IsAssignableFrom(type))
                    continue;

                yield return type;
            }
        }
    }
}
