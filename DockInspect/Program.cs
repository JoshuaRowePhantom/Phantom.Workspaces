using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using Dock.Serializer.SystemTextJson;

var factory = new Factory();
var doc = new Document { Id = "test-doc-1", Title = "Doc 1" };
var contentDock = new DocumentDock
{
    Id = "ContentDock",
    CanCreateDocument = false,
    VisibleDockables = factory.CreateList<IDockable>(),
};
var root = factory.CreateRootDock();
root.Id = "Root";
root.VisibleDockables = factory.CreateList<IDockable>(contentDock);
root.DefaultDockable = contentDock;
root.ActiveDockable = contentDock;
factory.InitLayout(root);
factory.AddDockable(contentDock, doc);
factory.SetActiveDockable(doc);
Console.WriteLine($"doc.Owner = {doc.Owner?.GetType().Name ?? "null"}");

// Test 1: Bare DockSerializer
Console.WriteLine("\n--- Test 1: Bare DockSerializer ---");
try
{
    var s1 = new DockSerializer(typeof(ObservableCollection<>));
    var json1 = s1.Serialize(root);
    Console.WriteLine("SUCCESS, length=" + json1?.Length);
}
catch (Exception e)
{
    Console.WriteLine("FAIL: " + e.GetType().Name + ": " + e.Message);
}

// Test 2: Inspect DockSerializer options via reflection
Console.WriteLine("\n--- Test 2: DockSerializer Options ---");
var s2 = new DockSerializer(typeof(ObservableCollection<>));
var allFields = s2.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
foreach (var f in allFields)
{
    Console.Write($"  {f.Name} : {f.FieldType.Name}");
    if (f.FieldType == typeof(JsonSerializerOptions))
    {
        var opts = (JsonSerializerOptions)f.GetValue(s2);
        Console.Write($" -> ReferenceHandler={opts.ReferenceHandler}, Resolver={opts.TypeInfoResolver?.GetType().Name}");
    }
    Console.WriteLine();
}

// Test 3: Check if DockableBase.Owner has [IgnoreDataMember]
Console.WriteLine("\n--- Test 3: DockableBase.Owner IgnoreDataMember check ---");
var ownerProp = typeof(Document).GetProperty("Owner");
if (ownerProp != null)
{
    Console.WriteLine($"  Document.Owner declaring type: {ownerProp.DeclaringType?.FullName}");
    Console.WriteLine($"  IsDefined(IgnoreDataMemberAttribute, false): {ownerProp.IsDefined(typeof(IgnoreDataMemberAttribute), false)}");
    Console.WriteLine($"  IsDefined(IgnoreDataMemberAttribute, true): {ownerProp.IsDefined(typeof(IgnoreDataMemberAttribute), true)}");
}
else
    Console.WriteLine("  Document.Owner NOT found");

// Test 4: Check Document properties visible to DefaultJsonTypeInfoResolver
Console.WriteLine("\n--- Test 4: Document STJ properties ---");
var resolver = new DefaultJsonTypeInfoResolver();
var opts4 = new JsonSerializerOptions();
var typeInfo = resolver.GetTypeInfo(typeof(Document), opts4);
Console.WriteLine($"  Properties for Document ({typeInfo.Properties.Count} total):");
foreach (var p in typeInfo.Properties)
{
    var hasIgnoreDataMember = p.AttributeProvider?.IsDefined(typeof(IgnoreDataMemberAttribute), true) == true;
    Console.WriteLine($"    {p.Name}: hasIgnoreDataMember={hasIgnoreDataMember}");
}

// Test 6: Custom resolver options
Console.WriteLine("\n--- Test 6: Custom resolver DockSerializer options ---");
// Simulate WorkspaceDockTypeInfoResolver with a simple DefaultJsonTypeInfoResolver
var customResolver = new DefaultJsonTypeInfoResolver();
var s6 = new DockSerializer(typeof(ObservableCollection<>), customResolver);
var fields6 = s6.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance);
foreach (var f in fields6)
{
    if (f.FieldType == typeof(JsonSerializerOptions))
    {
        var opts6 = (JsonSerializerOptions)f.GetValue(s6);
        Console.WriteLine($"  ReferenceHandler={opts6.ReferenceHandler?.GetType().Name ?? "null"}");
        Console.WriteLine($"  Resolver={opts6.TypeInfoResolver?.GetType().Name}");
    }
}

// Test 7: Document.Owner property and its attribute provider
Console.WriteLine("\n--- Test 7: Document.Owner AttributeProvider ---");
var resolver7 = new DefaultJsonTypeInfoResolver();
var opts7 = new JsonSerializerOptions();
var ti7 = resolver7.GetTypeInfo(typeof(Document), opts7);
var ownerPropInfo7 = ti7.Properties.FirstOrDefault(p => p.Name == "Owner");
if (ownerPropInfo7 != null)
{
    Console.WriteLine($"  AttributeProvider type: {ownerPropInfo7.AttributeProvider?.GetType().Name}");
    if (ownerPropInfo7.AttributeProvider is MemberInfo mi)
    {
        Console.WriteLine($"  MemberInfo.DeclaringType: {mi.DeclaringType?.FullName}");
        Console.WriteLine($"  IsDefined(IgnoreDataMember, false): {mi.IsDefined(typeof(IgnoreDataMemberAttribute), false)}");
        Console.WriteLine($"  IsDefined(IgnoreDataMember, true): {mi.IsDefined(typeof(IgnoreDataMemberAttribute), true)}");
        var allAttrs = mi.GetCustomAttributes(true);
        Console.WriteLine($"  All attributes ({allAttrs.Length}):");
        foreach (var a in allAttrs)
            Console.WriteLine($"    {a.GetType().FullName}");
    }
}
