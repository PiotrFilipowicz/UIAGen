//------------------------------------------------------------------------------
// <copyright file="Wrapper.TypeLibIndexer.cs" company="Piotr Filipowicz">
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

public partial class UiaAdapterWrapper {
    internal void GenerateBuilders() {
        int typeInfoCount = typeLib.GetTypeInfoCount();
        IndexTypeLibrary(typeLib, typeInfoCount);
        KnownInterfaces.ExceptWith(KnownEnums);

        var uniqueTypeInfos = new Dictionary<string, ITypeInfo>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < typeInfoCount; i++) {
            typeLib.GetTypeInfoType(i, out TYPEKIND kind);
            if (kind is TYPEKIND.TKIND_INTERFACE or TYPEKIND.TKIND_DISPATCH or TYPEKIND.TKIND_COCLASS) {
                typeLib.GetTypeInfo(i, out ITypeInfo typeInfo);
                typeInfo.GetDocumentation(-1, out string name, out _, out _, out _);
                if (!string.IsNullOrEmpty(name)) {
                    string baseName = GetBaseName(name);
                    if (!KnownEnums.Contains(baseName)) {
                        if (baseName.Equals("Objects", StringComparison.OrdinalIgnoreCase)) {
                            using var pObjAttrLease = TypeInfoDescriptor.TypeAttributes(typeInfo);
                            TYPEATTR objAttr = Marshal.PtrToStructure<TYPEATTR>(pObjAttrLease.Pointer);
                            if (IsAdobeObjectsTypeInfo(typeInfo, objAttr)) {
                                HasAdobeObjectsModel = true;
                            }
                        }

                        if (!uniqueTypeInfos.TryGetValue(baseName, out var existing)) {
                            uniqueTypeInfos[baseName] = typeInfo;
                        }
                        else {
                            using var pAttrNewLease = TypeInfoDescriptor.TypeAttributes(typeInfo);
                            nint pAttrNew = pAttrNewLease.Pointer;
                            TYPEATTR attrNew = Marshal.PtrToStructure<TYPEATTR>(pAttrNew);

                            using var pAttrOldLease = TypeInfoDescriptor.TypeAttributes(existing);
                            nint pAttrOld = pAttrOldLease.Pointer;
                            TYPEATTR attrOld = Marshal.PtrToStructure<TYPEATTR>(pAttrOld);

                            int scoreNew = attrNew.cFuncs + attrNew.cVars;
                            int scoreOld = attrOld.cFuncs + attrOld.cVars;

                            if (scoreNew > scoreOld || (attrOld.typekind == TYPEKIND.TKIND_COCLASS && attrNew.typekind != TYPEKIND.TKIND_COCLASS)) {
                                uniqueTypeInfos[baseName] = typeInfo;
                            }
                        }
                    }
                }
            }
        }

        if (_emitInfrastructure) {
            InfrastructureBuilder = CreateSectionBuilder("Infrastructure");
            Action<string> appendInfrastructure = s => InfrastructureBuilder.AppendLine(s);
            AppendBaseInfrastructure(appendInfrastructure);
        }
        else {
            InfrastructureBuilder = new StringBuilder();
        }

        void AppendLine(string line, StringBuilder sb) {
            sb.AppendLine(line);
        }

        EnumsBuilder = CreateSectionBuilder("Enums");
        Action<string> appendEnums = s => AppendLine(s, EnumsBuilder);
        for (int i = 0; i < typeInfoCount; i++) {
            typeLib.GetTypeInfoType(i, out TYPEKIND kind);
            if (kind == TYPEKIND.TKIND_ENUM) {
                typeLib.GetTypeInfo(i, out ITypeInfo typeInfo);
                GenerateEnum(typeInfo, appendEnums);
            }
        }

        WrappersBuilder = CreateSectionBuilder("Wrappers");
        Action<string> appendWrappers = s => AppendLine(s, WrappersBuilder);
        foreach (var (baseName, typeInfo) in uniqueTypeInfos) {
            GenerateStrongWrapper(baseName, typeInfo, appendWrappers);
        }
        if (HasAdobeObjectsModel)
            AppendAdobeObjectsTypes(appendWrappers, uniqueTypeInfos.ContainsKey("Objects"));
    }

    internal StringBuilder CreateSectionBuilder(string sectionName) =>
        CreateSectionBuilder(sectionName, AdapterClassName, RootNamespace);

    internal static StringBuilder CreateSectionBuilder(string sectionName, string targetNamespace, string rootNamespace) {
        string sharedUsing = !string.Equals(targetNamespace, rootNamespace, StringComparison.Ordinal)
            ? $"using {rootNamespace};{Environment.NewLine}"
            : string.Empty;

        return new StringBuilder(/*lang=c#*/ $$"""
// <auto-generated>
//   Complete UiaAdapter object model generated for .NET 10
// </auto-generated>

//  Section: {{sectionName}}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using UiaAdapter.ComRuntime;
{{sharedUsing}}
namespace {{targetNamespace}} {


""");
    }

    internal void IndexTypeLibrary(ITypeLib typeLib, int count) {
        for (int i = 0; i < count; i++) {
            typeLib.GetTypeInfoType(i, out TYPEKIND kind);
            if (kind == TYPEKIND.TKIND_ENUM) {
                typeLib.GetTypeInfo(i, out ITypeInfo typeInfo);
                typeInfo.GetDocumentation(-1, out string name, out _, out _, out _);
                if (!string.IsNullOrEmpty(name)) {
                    KnownEnums.Add(GetBaseName(name));
                }
            }
        }

        for (int i = 0; i < count; i++) {
            typeLib.GetTypeInfoType(i, out TYPEKIND kind);
            if (kind is TYPEKIND.TKIND_INTERFACE or TYPEKIND.TKIND_DISPATCH or TYPEKIND.TKIND_COCLASS) {
                typeLib.GetTypeInfo(i, out ITypeInfo typeInfo);
                typeInfo.GetDocumentation(-1, out string name, out _, out _, out _);
                if (!string.IsNullOrEmpty(name)) {
                    string baseName = GetBaseName(name);
                    if (!KnownEnums.Contains(baseName)) {
                        KnownInterfaces.Add(baseName);
                    }
                }
            }
        }

        DiscoveredObjectsMembers.Add("Count");
        DiscoveredObjectsMembers.Add("Item");
        DiscoveredObjectsMembers.Add("this");
        DiscoveredObjectsMembers.Add("FirstItem");
        DiscoveredObjectsMembers.Add("LastItem");
        DiscoveredObjectsMembers.Add("MiddleItem");
        DiscoveredObjectsMembers.Add("PreviousItem");
        DiscoveredObjectsMembers.Add("NextItem");

        DiscoveredObjectsMembers.Add("AnyItem");
        DiscoveredObjectsMembers.Add("ItemByID");
        DiscoveredObjectsMembers.Add("ItemByRange");
        DiscoveredObjectsMembers.Add("Remove");
        DiscoveredObjectsMembers.Add("Add");
    }

    private static bool IsAdobeObjectsTypeInfo(ITypeInfo typeInfo, TYPEATTR attr) {
        var seenSignatureMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<Guid>();

        void InspectTypeInfo(ITypeInfo currentInfo, TYPEATTR currentAttr, int depth) {
            if (depth > 4)
                return;
            if (currentAttr.guid != Guid.Empty && !visited.Add(currentAttr.guid))
                return;

            for (int i = 0; i < currentAttr.cFuncs; i++) {
                using var pFuncDescLease = TypeInfoDescriptor.Function(currentInfo, i);
                FUNCDESC funcDesc = Marshal.PtrToStructure<FUNCDESC>(pFuncDescLease.Pointer);

                string[] names = new string[1];
                currentInfo.GetNames(funcDesc.memid, names, 1, out int cNames);
                if (cNames > 0 && !string.IsNullOrEmpty(names[0])) {
                    string memberName = names[0];
                    if (AdobeObjectsSignatureMethods.Contains(memberName))
                        seenSignatureMethods.Add(memberName);
                }
            }

            for (int i = 0; i < currentAttr.cVars; i++) {
                using var pVarDescLease = TypeInfoDescriptor.Variable(currentInfo, i);
                VARDESC varDesc = Marshal.PtrToStructure<VARDESC>(pVarDescLease.Pointer);

                string[] names = new string[1];
                currentInfo.GetNames(varDesc.memid, names, 1, out int cNames);
                if (cNames > 0 && !string.IsNullOrEmpty(names[0])) {
                    string memberName = names[0];
                    if (AdobeObjectsSignatureMethods.Contains(memberName))
                        seenSignatureMethods.Add(memberName);
                }
            }

            for (int i = 0; i < currentAttr.cImplTypes; i++) {
                try {
                    currentInfo.GetRefTypeOfImplType(i, out int hRef);
                    currentInfo.GetRefTypeInfo(hRef, out ITypeInfo implInfo);
                    if (implInfo != null) {
                        try {
                            using var pImplAttrLease = TypeInfoDescriptor.TypeAttributes(implInfo);
                            TYPEATTR implAttr = Marshal.PtrToStructure<TYPEATTR>(pImplAttrLease.Pointer);
                            InspectTypeInfo(implInfo, implAttr, depth + 1);
                        }
                        finally {
                            Marshal.ReleaseComObject(implInfo);
                        }
                    }
                }
                catch { }
            }
        }

        InspectTypeInfo(typeInfo, attr, 0);
        return seenSignatureMethods.Count >= 6;
    }

    private static bool IsTypeInfoCollection(ITypeInfo typeInfo, TYPEATTR attr) {
        for (int i = 0; i < attr.cFuncs; i++) {
            using var pFuncDescLease = TypeInfoDescriptor.Function(typeInfo, i);
            nint pFuncDesc = pFuncDescLease.Pointer;
            FUNCDESC funcDesc = Marshal.PtrToStructure<FUNCDESC>(pFuncDesc);
            int memid = funcDesc.memid;

            if (memid == -4)
                return true;

            string[] names = new string[1];
            typeInfo.GetNames(memid, names, 1, out int cNames);
            if (cNames > 0 && !string.IsNullOrEmpty(names[0])) {
                if (names[0].Equals("Count", StringComparison.OrdinalIgnoreCase) ||
                    names[0].Equals("_NewEnum", StringComparison.OrdinalIgnoreCase)) {
                    return true;
                }
            }
        }

        for (int i = 0; i < attr.cVars; i++) {
            using var pVarDescLease = TypeInfoDescriptor.Variable(typeInfo, i);
            nint pVarDesc = pVarDescLease.Pointer;
            VARDESC varDesc = Marshal.PtrToStructure<VARDESC>(pVarDesc);
            int memid = varDesc.memid;

            if (memid == -4)
                return true;

            string[] names = new string[1];
            typeInfo.GetNames(memid, names, 1, out int cNames);
            if (cNames > 0 && !string.IsNullOrEmpty(names[0])) {
                if (names[0].Equals("Count", StringComparison.OrdinalIgnoreCase) ||
                    names[0].Equals("_NewEnum", StringComparison.OrdinalIgnoreCase)) {
                    return true;
                }
            }
        }

        for (int i = 0; i < attr.cImplTypes; i++) {
            try {
                typeInfo.GetRefTypeOfImplType(i, out int hRef);
                typeInfo.GetRefTypeInfo(hRef, out ITypeInfo implInfo);
                if (implInfo != null) {
                    try {
                        using var pImplAttrLease = TypeInfoDescriptor.TypeAttributes(implInfo);
                        nint pImplAttr = pImplAttrLease.Pointer;
                        TYPEATTR implAttr = Marshal.PtrToStructure<TYPEATTR>(pImplAttr);
                        bool isCol = IsTypeInfoCollection(implInfo, implAttr);
                        if (isCol)
                            return true;
                    }
                    finally {
                        Marshal.ReleaseComObject(implInfo);
                    }
                }
            }
            catch { }
        }

        return false;
    }

    private string? GetSingularCandidate(string collectionName) {
        string baseCol = GetBaseName(collectionName);

        // 1. Application-specific dictionary mappings
        foreach (var entry in AppSpecificCollectionMappings) {
            if (MatchesAppKey(entry.Key)) {
                if (entry.Value.TryGetValue(baseCol, out string[]? candidates)) {
                    foreach (var candidate in candidates) {
                        if (candidate.Equals("string", StringComparison.OrdinalIgnoreCase) ||
                            KnownInterfaces.Contains(candidate) ||
                            KnownInterfaces.Contains(GetBaseName(candidate))) {
                            return candidate;
                        }
                    }
                }
                break;
            }
        }

        // 2. Shape family: *Shapes -> Shape
        if (baseCol.EndsWith("Shapes", StringComparison.OrdinalIgnoreCase)) {
            if (KnownInterfaces.Contains("Shape"))
                return "Shape";
            string shapeCandidate = baseCol[..^1];
            if (KnownInterfaces.Contains(shapeCandidate))
                return shapeCandidate;
        }

        // 3. Suffix rules for irregular nouns and compounds
        if (baseCol.EndsWith("Entries", StringComparison.OrdinalIgnoreCase)) {
            string candidate = baseCol[..^3] + "y";
            if (KnownInterfaces.Contains(candidate))
                return candidate;
        }

        if (baseCol.EndsWith("Galleries", StringComparison.OrdinalIgnoreCase)) {
            string candidate = baseCol[..^3] + "y";
            if (KnownInterfaces.Contains(candidate))
                return candidate;
        }

        if (baseCol.EndsWith("Axes", StringComparison.OrdinalIgnoreCase)) {
            string candidate = baseCol[..^3] + "is";
            if (KnownInterfaces.Contains(candidate))
                return candidate;
        }

        if (baseCol.EndsWith("ices", StringComparison.OrdinalIgnoreCase)) {
            string candidate = baseCol[..^4] + "ix";
            if (KnownInterfaces.Contains(candidate))
                return candidate;
            candidate = baseCol[..^4] + "ex";
            if (KnownInterfaces.Contains(candidate))
                return candidate;
        }

        if (baseCol.EndsWith("SelectedRanges", StringComparison.OrdinalIgnoreCase)) {
            if (KnownInterfaces.Contains("Range"))
                return "Range";
        }

        if (baseCol.EndsWith("Ranges", StringComparison.OrdinalIgnoreCase)) {
            string candidate = baseCol[..^1];
            if (KnownInterfaces.Contains(candidate))
                return candidate;
            if (KnownInterfaces.Contains("Range"))
                return "Range";
        }

        // 4. Remove suffixes "-esCollection", "-PivotList", "-Collection", "-List"
        if (baseCol.EndsWith("esCollection", StringComparison.OrdinalIgnoreCase)) {
            string candidate = baseCol[..^12] + "e";
            if (KnownInterfaces.Contains(candidate))
                return candidate;
        }

        if (baseCol.EndsWith("PivotList", StringComparison.OrdinalIgnoreCase)) {
            if (KnownInterfaces.Contains("PivotTable"))
                return "PivotTable";
            if (KnownInterfaces.Contains("PivotField"))
                return "PivotField";
        }

        if (baseCol.EndsWith("Collection", StringComparison.OrdinalIgnoreCase)) {
            string candidate = baseCol[..^10];
            if (KnownInterfaces.Contains(candidate))
                return candidate;
        }

        if (baseCol.EndsWith("List", StringComparison.OrdinalIgnoreCase)) {
            string candidate = baseCol[..^4];
            if (KnownInterfaces.Contains(candidate))
                return candidate;
        }

        // 5. Standard English plural endings
        if (baseCol.EndsWith("ies", StringComparison.OrdinalIgnoreCase) && baseCol.Length > 3) {
            string candidate = baseCol[..^3] + "y";
            if (KnownInterfaces.Contains(candidate))
                return candidate;
        }

        if (baseCol.EndsWith("es", StringComparison.OrdinalIgnoreCase) && baseCol.Length > 2) {
            string candidateNoEs = baseCol[..^2];
            if (KnownInterfaces.Contains(candidateNoEs))
                return candidateNoEs;

            string candidateNoS = baseCol[..^1];
            if (KnownInterfaces.Contains(candidateNoS))
                return candidateNoS;
        }

        // 6. Default fallback: remove the trailing 's'
        if (baseCol.EndsWith("s", StringComparison.OrdinalIgnoreCase) && baseCol.Length > 1) {
            string candidate = baseCol[..^1];
            if (KnownInterfaces.Contains(candidate))
                return candidate;
        }

        return null;
    }
}