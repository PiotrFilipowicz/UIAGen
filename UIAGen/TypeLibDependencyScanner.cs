//------------------------------------------------------------------------------
// <copyright file="TypeLibDependencyScanner.cs" company="Piotr Filipowicz">
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
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace UiaAdapter;

internal sealed record DiscoveredTypeLib(
    Guid Guid,
    ITypeLib TypeLib,
    string ShortName,
    string Namespace,
    string FilePrefix,
    bool IsPrimary);

internal static class TypeLibDependencyScanner {
    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal) {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed",
        "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw",
        "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using",
        "virtual", "void", "volatile", "while"
    };

    internal static string SanitizeNamespace(string raw) {
        string[] parts = raw.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return "UiaGeneratedAdapter";
        for (int i = 0; i < parts.Length; i++)
            parts[i] = SanitizeIdentifier(parts[i], "Adapter");
        return string.Join('.', parts);
    }

    internal static List<DiscoveredTypeLib> Discover(
        IReadOnlyList<(string ProgId, ITypeLib TypeLib)> primaryLibs,
        string adapterName) {

        var discoveredByGuid = new Dictionary<Guid, DiscoveredTypeLib>();
        var usedNamespaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<DiscoveredTypeLib>();
        bool singlePrimaryMode = primaryLibs.Count == 1;

        string rootNamespace = SanitizeNamespace(adapterName);

        foreach (var (progId, typeLib) in primaryLibs) {
            TYPELIBATTR libAttr = TypeInfoDescriptor.LibraryAttributes(typeLib);
            if (discoveredByGuid.TryGetValue(libAttr.guid, out DiscoveredTypeLib? existing)) {
                TryUpgradeExisting(typeLib, libAttr, existing, discoveredByGuid, queue);
                continue;
            }

            string fallback = progId.Contains('.') ? progId.Split('.')[0] : progId;
            string shortName = AllocateUniqueName(GetTypeLibShortName(typeLib, fallback), usedNamespaces, rootNamespace, singlePrimaryMode);
            string ns = singlePrimaryMode ? rootNamespace : $"{rootNamespace}.{shortName}";
            string filePrefix = singlePrimaryMode ? rootNamespace : $"{rootNamespace}.{shortName}";

            usedNamespaces.Add(ns);
            var entry = new DiscoveredTypeLib(libAttr.guid, typeLib, shortName, ns, filePrefix, IsPrimary: true);
            discoveredByGuid[libAttr.guid] = entry;
            queue.Enqueue(entry);
        }

        while (queue.Count > 0) {
            DiscoveredTypeLib current = queue.Dequeue();
            ScanTypeLibrary(current, discoveredByGuid, usedNamespaces, rootNamespace, queue);
        }

        return new List<DiscoveredTypeLib>(discoveredByGuid.Values);
    }

    private static void ScanTypeLibrary(
        DiscoveredTypeLib current,
        Dictionary<Guid, DiscoveredTypeLib> discoveredByGuid,
        HashSet<string> usedNamespaces,
        string rootNamespace,
        Queue<DiscoveredTypeLib> queue) {

        int count = current.TypeLib.GetTypeInfoCount();
        for (int i = 0; i < count; i++) {
            current.TypeLib.GetTypeInfo(i, out ITypeInfo typeInfo);
            if (typeInfo == null)
                continue;

            using var attrLease = TypeInfoDescriptor.TypeAttributes(typeInfo);
            TYPEATTR attr = Marshal.PtrToStructure<TYPEATTR>(attrLease.Pointer);

            for (int implIndex = 0; implIndex < attr.cImplTypes; implIndex++) {
                try {
                    typeInfo.GetRefTypeOfImplType(implIndex, out int implHRef);
                    ResolveHRef(typeInfo, implHRef, current.Guid, discoveredByGuid, usedNamespaces, rootNamespace, queue);
                }
                catch { }
            }

            if (attr.typekind == TYPEKIND.TKIND_ALIAS) {
                ScanTypeDesc(typeInfo, attr.tdescAlias, current.Guid, discoveredByGuid, usedNamespaces, rootNamespace, queue);
            }

            for (int funcIndex = 0; funcIndex < attr.cFuncs; funcIndex++) {
                using var funcLease = TypeInfoDescriptor.Function(typeInfo, funcIndex);
                FUNCDESC funcDesc = Marshal.PtrToStructure<FUNCDESC>(funcLease.Pointer);

                ScanTypeDesc(typeInfo, funcDesc.elemdescFunc.tdesc, current.Guid, discoveredByGuid, usedNamespaces, rootNamespace, queue);

                if (funcDesc.cParams > 0 && funcDesc.lprgelemdescParam != IntPtr.Zero) {
                    int elemSize = Marshal.SizeOf<ELEMDESC>();
                    for (int p = 0; p < funcDesc.cParams; p++) {
                        ELEMDESC paramDesc = Marshal.PtrToStructure<ELEMDESC>(funcDesc.lprgelemdescParam + (p * elemSize));
                        ScanTypeDesc(typeInfo, paramDesc.tdesc, current.Guid, discoveredByGuid, usedNamespaces, rootNamespace, queue);
                    }
                }
            }

            for (int varIndex = 0; varIndex < attr.cVars; varIndex++) {
                using var varLease = TypeInfoDescriptor.Variable(typeInfo, varIndex);
                VARDESC varDesc = Marshal.PtrToStructure<VARDESC>(varLease.Pointer);
                ScanTypeDesc(typeInfo, varDesc.elemdescVar.tdesc, current.Guid, discoveredByGuid, usedNamespaces, rootNamespace, queue);
            }
        }
    }

    private static void ScanTypeDesc(
        ITypeInfo typeInfo,
        TYPEDESC tdesc,
        Guid currentLibGuid,
        Dictionary<Guid, DiscoveredTypeLib> discoveredByGuid,
        HashSet<string> usedNamespaces,
        string rootNamespace,
        Queue<DiscoveredTypeLib> queue,
        int depth = 0) {

        if (depth > 8)
            return;

        VarEnum vt = (VarEnum)tdesc.vt;
        if (vt is VarEnum.VT_PTR or VarEnum.VT_SAFEARRAY or VarEnum.VT_CARRAY) {
            if (tdesc.lpValue != IntPtr.Zero) {
                TYPEDESC inner = Marshal.PtrToStructure<TYPEDESC>(tdesc.lpValue);
                ScanTypeDesc(typeInfo, inner, currentLibGuid, discoveredByGuid, usedNamespaces, rootNamespace, queue, depth + 1);
            }
        }
        else if (vt == VarEnum.VT_USERDEFINED) {
            int hRef = unchecked((int)tdesc.lpValue);
            ResolveHRef(typeInfo, hRef, currentLibGuid, discoveredByGuid, usedNamespaces, rootNamespace, queue);
        }
    }

    private static void ResolveHRef(
        ITypeInfo ownerTypeInfo,
        int hRef,
        Guid currentLibGuid,
        Dictionary<Guid, DiscoveredTypeLib> discoveredByGuid,
        HashSet<string> usedNamespaces,
        string rootNamespace,
        Queue<DiscoveredTypeLib> queue) {

        ITypeInfo? refInfo = null;
        try {
            ownerTypeInfo.GetRefTypeInfo(hRef, out refInfo);
        }
        catch {
            return;
        }

        if (refInfo == null)
            return;

        using var refAttrLease = TypeInfoDescriptor.TypeAttributes(refInfo);
        TYPEATTR refAttr = Marshal.PtrToStructure<TYPEATTR>(refAttrLease.Pointer);

        // 1. Resolve the type library via HKCR\Interface\{IID}\TypeLib -> HKCR\TypeLib
        if (refAttr.typekind is TYPEKIND.TKIND_INTERFACE or TYPEKIND.TKIND_DISPATCH && refAttr.guid != Guid.Empty) {
            if (TlbResolver.TypeLib.TryGetTypeLibFromInterface(refAttr.guid, out Guid ifaceTlbGuid, out string? ifaceVersion) &&
                ifaceTlbGuid != Guid.Empty &&
                ifaceTlbGuid != currentLibGuid) {

                ITypeLib? ifaceLib = TlbResolver.TypeLib.GetFromGuid(ifaceTlbGuid, ifaceVersion);
                if (ifaceLib != null) {
                    RegisterOrUpgradeExternalTypeLib(ifaceLib, discoveredByGuid, usedNamespaces, rootNamespace, queue);
                }
            }
        }

        // 2. Get the containing type library and resolve it via HKCR\TypeLib
        try {
            refInfo.GetContainingTypeLib(out ITypeLib? containingLib, out _);
            if (containingLib == null)
                return;

            TYPELIBATTR refLibAttr = TypeInfoDescriptor.LibraryAttributes(containingLib);
            if (refLibAttr.guid == Guid.Empty || refLibAttr.guid == currentLibGuid)
                return;

            ITypeLib resolvedLib = TlbResolver.TypeLib.GetFromGuid(refLibAttr.guid, refLibAttr.wMajorVerNum, refLibAttr.wMinorVerNum)
                ?? containingLib;

            RegisterOrUpgradeExternalTypeLib(resolvedLib, discoveredByGuid, usedNamespaces, rootNamespace, queue);
        }
        catch { }
    }

    private static void RegisterOrUpgradeExternalTypeLib(
        ITypeLib typeLib,
        Dictionary<Guid, DiscoveredTypeLib> discoveredByGuid,
        HashSet<string> usedNamespaces,
        string rootNamespace,
        Queue<DiscoveredTypeLib> queue) {

        TYPELIBATTR libAttr = TypeInfoDescriptor.LibraryAttributes(typeLib);
        if (libAttr.guid == Guid.Empty)
            return;

        if (discoveredByGuid.TryGetValue(libAttr.guid, out DiscoveredTypeLib? existing)) {
            TryUpgradeExisting(typeLib, libAttr, existing, discoveredByGuid, queue);
            return;
        }

        string fallback = $"TypeLib_{libAttr.guid:N}"[..16];
        string shortName = AllocateUniqueName(GetTypeLibShortName(typeLib, fallback), usedNamespaces, rootNamespace, singlePrimaryMode: false);
        string ns = $"{rootNamespace}.{shortName}";
        string filePrefix = $"{rootNamespace}.{shortName}";

        usedNamespaces.Add(ns);
        var entry = new DiscoveredTypeLib(libAttr.guid, typeLib, shortName, ns, filePrefix, IsPrimary: false);
        discoveredByGuid[libAttr.guid] = entry;
        queue.Enqueue(entry);
    }

    private static void TryUpgradeExisting(
        ITypeLib candidateLib,
        TYPELIBATTR candidateAttr,
        DiscoveredTypeLib existing,
        Dictionary<Guid, DiscoveredTypeLib> discoveredByGuid,
        Queue<DiscoveredTypeLib> queue) {

        TYPELIBATTR existingAttr = TypeInfoDescriptor.LibraryAttributes(existing.TypeLib);
        ushort candMajor = unchecked((ushort)candidateAttr.wMajorVerNum);
        ushort candMinor = unchecked((ushort)candidateAttr.wMinorVerNum);
        ushort existMajor = unchecked((ushort)existingAttr.wMajorVerNum);
        ushort existMinor = unchecked((ushort)existingAttr.wMinorVerNum);

        bool isNewerVersion = candMajor > existMajor || (candMajor == existMajor && candMinor > existMinor);
        bool isSameVersionRicher = candMajor == existMajor && candMinor == existMinor &&
            candidateLib.GetTypeInfoCount() > existing.TypeLib.GetTypeInfoCount();

        if (!isNewerVersion && !isSameVersionRicher)
            return;

        DiscoveredTypeLib upgraded = existing with { TypeLib = candidateLib };
        discoveredByGuid[existing.Guid] = upgraded;
        queue.Enqueue(upgraded);
    }

    private static string GetTypeLibShortName(ITypeLib typeLib, string fallback) {
        try {
            typeLib.GetDocumentation(-1, out string name, out _, out _, out _);
            if (!string.IsNullOrWhiteSpace(name))
                return SanitizeIdentifier(name, fallback);
        }
        catch { }
        return SanitizeIdentifier(fallback, "ExternalLib");
    }

    private static string AllocateUniqueName(string baseName, HashSet<string> usedNamespaces, string rootNamespace, bool singlePrimaryMode) {
        string candidate = baseName;
        int suffix = 2;
        while (usedNamespaces.Contains(singlePrimaryMode ? rootNamespace : $"{rootNamespace}.{candidate}")) {
            candidate = $"{baseName}_{suffix++}";
        }
        return candidate;
    }

    private static string SanitizeIdentifier(string raw, string fallback) {
        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw.Trim()) {
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        }

        string cleaned = sb.ToString().Trim('_');
        if (string.IsNullOrEmpty(cleaned))
            cleaned = fallback;
        if (char.IsDigit(cleaned[0]))
            cleaned = "_" + cleaned;
        return CSharpKeywords.Contains(cleaned) ? "@" + cleaned : cleaned;
    }
}