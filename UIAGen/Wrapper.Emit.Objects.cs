//------------------------------------------------------------------------------
// <copyright file="Wrapper.Emit.Objects.cs" company="Piotr Filipowicz">
//     Copyright (c) 2026 Piotr Filipowicz.
//     SPDX-License-Identifier: LGPL-3.0-or-later WITH GCC-exception-3.1
//     Licensed under GNU LGPL v3 with Code Generation Exception.
// </copyright>
// <security>
//     Integrity Notice: Official commits, tags, and release artifacts of this
//     project are cryptographically signed.
// </security>
//------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace UiaAdapter;

public partial class UiaAdapterWrapper {
    private const string TypedCollectionMembersBegin = "    // __UIA_TYPED_COLLECTION_MEMBERS_BEGIN__";
    private const string TypedCollectionMembersEnd = "    // __UIA_TYPED_COLLECTION_MEMBERS_END__";

    internal void AppendBaseInfrastructure(Action<string> append) =>
        AppendBaseInfrastructure(append, RootNamespace, [this]);

    internal static StringBuilder BuildSharedInfrastructure(string rootNamespace, IReadOnlyList<UiaAdapterWrapper> wrappers) {
        StringBuilder sb = CreateSectionBuilder("Infrastructure", rootNamespace, rootNamespace);
        bool includeSafeAdobeMethods = wrappers.Any(w => w.IsAdobeAdapter);
        AppendComInvoker(line => sb.AppendLine(line), includeSafeAdobeMethods);
        return sb;
    }

    internal static StringBuilder BuildSharedBaseObjects(string rootNamespace) {
        StringBuilder sb = CreateSectionBuilder("Base objects", rootNamespace, rootNamespace);
        sb.AppendLine(CreateBaseObjectsSource());
        return sb;
    }

    internal static StringBuilder BuildSharedTypeMappings(string rootNamespace, IReadOnlyList<UiaAdapterWrapper> wrappers) {
        StringBuilder sb = CreateSectionBuilder("Type mappings", rootNamespace, rootNamespace);
        sb.AppendLine(CreateTypeMappingsSource(wrappers));
        return sb;
    }

    private static void AppendBaseInfrastructure(Action<string> append, string rootNamespace, IReadOnlyList<UiaAdapterWrapper> wrappers) {
        bool includeSafeAdobeMethods = wrappers.Any(w => w.IsAdobeAdapter);
        AppendComInvoker(append, includeSafeAdobeMethods);
        append(CreateBaseObjectsSource());
        append(CreateTypeMappingsSource(wrappers));
    }

    private static string CreateBaseObjectsSource() => ReadTemplate("BaseObjects.cs");

    private static string CreateTypeMappingsSource(IReadOnlyList<UiaAdapterWrapper> wrappers) {
        var enumConversions = new StringBuilder();
        var seenEnums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (UiaAdapterWrapper wrapper in wrappers) {
            foreach (string enumName in wrapper.KnownEnums) {
                string safeEnumName = Sanitize(enumName);
                string qualifiedEnum = $"global::{wrapper.AdapterClassName}.{safeEnumName}";
                if (!seenEnums.Add(qualifiedEnum))
                    continue;
                enumConversions.AppendLine($"    public static implicit operator ComProxy({qualifiedEnum} val) => new ComProxy((int)val);");
                enumConversions.AppendLine($"    public static explicit operator {qualifiedEnum}(ComProxy comProxy) => comProxy?.Value != null ? ({qualifiedEnum})global::System.Convert.ToInt32(comProxy.Value) : default;");
            }
        }

        var scopedRegistrations = new StringBuilder();
        var fallbackRegistrations = new StringBuilder();
        var seenScoped = new HashSet<(Guid Guid, string Name)>();
        var seenFallback = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (UiaAdapterWrapper wrapper in wrappers) {
            foreach (string interfaceName in wrapper.KnownInterfaces) {
                string safeInterfaceName = Sanitize(interfaceName);
                string qualifiedInterface = $"global::{wrapper.AdapterClassName}.{safeInterfaceName}";

                if (wrapper.TypeLibGuid != Guid.Empty && seenScoped.Add((wrapper.TypeLibGuid, interfaceName.ToUpperInvariant()))) {
                    scopedRegistrations.AppendLine($"        _scopedTypeRegistry.Add((new global::System.Guid(\"{wrapper.TypeLibGuid:D}\"), \"{interfaceName}\"), raw => new {qualifiedInterface}(raw));");
                }
                if (seenFallback.Add(interfaceName)) {
                    fallbackRegistrations.AppendLine($"        _typeRegistry.Add(\"{interfaceName}\", raw => new {qualifiedInterface}(raw));");
                }
            }
        }

        return $$"""
public partial class ComProxy {
{{enumConversions.ToString().TrimEnd()}}
}

public static partial class ComProxyFactory {
    static ComProxyFactory() {
{{scopedRegistrations.ToString().TrimEnd()}}
{{fallbackRegistrations.ToString().TrimEnd()}}
    }
}
""";
    }

    private const string AdobeObjectsMembers = /*lang=c#*/ """
    public override int Count => GetValue<int>(nameof(Count), 1668183141);
    public void Remove(int index) => InvokeVoid("Remove", 1382370678, index);
    public ComProxy ItemByID(int id) => InvokeCollectionItem<ComProxy>("ItemByID", 1115244868, id);
    public Objects ItemByRange(object from, object to) => InvokeWrapper<Objects>("ItemByRange", 1918987879, from, to);
    public ComProxy FirstItem() => InvokeCollectionItem<ComProxy>("FirstItem", 1718186611);
    public ComProxy LastItem() => InvokeCollectionItem<ComProxy>("LastItem", 1818325876);
    public ComProxy MiddleItem() => InvokeCollectionItem<ComProxy>("MiddleItem", 1835623524);
    public ComProxy PreviousItem(object fromHere) => InvokeCollectionItem<ComProxy>("PreviousItem", 1886545270, fromHere);
    public ComProxy NextItem(object fromHere) => InvokeCollectionItem<ComProxy>("NextItem", 1852143732, fromHere);
    public ComProxy AnyItem() => InvokeCollectionItem<ComProxy>("AnyItem", 1634629920);
""";

    private static void AppendAdobeObjectsTypes(Action<string> append, bool hasObjects) {
        if (!hasObjects) {
            append(/*lang=c#*/ $$"""
[DebuggerTypeProxy(typeof(ComProxiesUntypedDebugView))]
public class Objects : ComProxies {
    public static Objects GetMissing() => ComProxy.Missing<Objects>();
    public Objects(object comInstance) : base(comInstance) { }
{{AdobeObjectsMembers}}
}
""");
        }

        string template = ReadTemplate("BaseObjects.cs");
        int start = template.IndexOf(TypedCollectionMembersBegin, StringComparison.Ordinal);
        int end = template.IndexOf(TypedCollectionMembersEnd, StringComparison.Ordinal);
        if (start < 0 || end <= start)
            throw new InvalidOperationException("Typed COM collection template not found.");
        string typedMembers = template[(start + TypedCollectionMembersBegin.Length)..end].Trim('\r', '\n');

        append(/*lang=c#*/ $$"""
[DebuggerTypeProxy(typeof(ComProxiesDebugView<>))]
public class Objects<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TItem> : Objects, IEnumerable<TItem> where TItem : class {
    public new static Objects<TItem> GetMissing() => ComProxy.Missing<Objects<TItem>>();
    public Objects(object comInstance) : base(comInstance) { }
{{typedMembers}}
    public new TItem ItemByID(int id) => InvokeCollectionItem<TItem>("ItemByID", 1115244868, id);
    public new TItem FirstItem() => InvokeCollectionItem<TItem>("FirstItem", 1718186611);
    public new TItem LastItem() => InvokeCollectionItem<TItem>("LastItem", 1818325876);
    public new TItem MiddleItem() => InvokeCollectionItem<TItem>("MiddleItem", 1835623524);
    public new TItem PreviousItem(object fromHere) => InvokeCollectionItem<TItem>("PreviousItem", 1886545270, fromHere);
    public new TItem NextItem(object fromHere) => InvokeCollectionItem<TItem>("NextItem", 1852143732, fromHere);
    public new TItem AnyItem() => InvokeCollectionItem<TItem>("AnyItem", 1634629920);
}
""");
    }
}