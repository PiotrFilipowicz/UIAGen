//------------------------------------------------------------------------------
// <copyright file="Wrapper.TypeResolver.cs" company="Piotr Filipowicz">
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
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace UiaAdapter;

public partial class UiaAdapterWrapper {
    private string QualifyType(string typeName) {
        if (string.IsNullOrEmpty(typeName) || typeName == "object" || typeName == "string" || typeName == "bool" ||
            typeName == "int" || typeName == "double" || typeName == "float" || typeName == "void" || typeName == "byte" ||
            typeName == "short" || typeName == "ushort" || typeName == "uint" || typeName == "long" || typeName == "ulong") {
            return typeName;
        }

        typeName = GetCanonicalTypeName(typeName);
        if (typeName.StartsWith("global::"))
            return typeName;

        if (typeName.StartsWith("ComProxies<") && typeName.EndsWith(">")) {
            string inner = typeName[11..^1].Trim();
            return $"{RootNamespace}.ComProxies<{QualifyType(inner)}>";
        }

        if (typeName.StartsWith("Objects<") && typeName.EndsWith(">")) {
            string inner = typeName[8..^1].Trim();
            return $"{RootNamespace}.ComProxies<{QualifyType(inner)}>";
        }

        if (typeName is "ComProxy" or "ComProxies")
            return $"{RootNamespace}.{typeName}";

        if (typeName.StartsWith($"{AdapterClassName}.") || typeName.StartsWith($"{RootNamespace}."))
            return typeName;

        string baseType = GetBaseName(typeName);
        if (KnownInterfaces.Contains(baseType) || KnownInterfaces.Contains(typeName) || KnownEnums.Contains(baseType) || KnownEnums.Contains(typeName) || typeName == "Objects") {
            return $"{AdapterClassName}.{typeName}";
        }

        return typeName;
    }

    private string ResolveRawTypeName(TYPEDESC tdesc, ITypeInfo typeInfo, int depth = 0) {
        VarEnum vt = (VarEnum)tdesc.vt;
        if ((vt & VarEnum.VT_ARRAY) != 0) {
            VarEnum elementType = vt & ~VarEnum.VT_ARRAY;
            return ResolveRawArrayElementType(elementType) + "[]";
        }

        return vt switch {
            VarEnum.VT_VOID => "void",
            VarEnum.VT_BOOL => "bool",
            VarEnum.VT_I1 or VarEnum.VT_UI1 => "byte",
            VarEnum.VT_I2 => "short",
            VarEnum.VT_UI2 => "ushort",
            VarEnum.VT_I4 or VarEnum.VT_INT => "int",
            VarEnum.VT_UI4 or VarEnum.VT_UINT => "uint",
            VarEnum.VT_I8 => "long",
            VarEnum.VT_UI8 => "ulong",
            VarEnum.VT_R4 => "float",
            VarEnum.VT_R8 => "double",
            VarEnum.VT_BSTR => "string",
            VarEnum.VT_DISPATCH or VarEnum.VT_UNKNOWN or VarEnum.VT_VARIANT => "object",
            VarEnum.VT_PTR => ResolveRawPointerType(tdesc.lpValue, typeInfo, depth + 1),
            VarEnum.VT_SAFEARRAY => ResolveRawPointerType(tdesc.lpValue, typeInfo, depth + 1) + "[]",
            VarEnum.VT_USERDEFINED => ResolveRawUserDefined(tdesc.lpValue, typeInfo),
            _ => "object"
        };
    }

    private string ResolveRawReturnTypeName(TYPEDESC tdesc, ITypeInfo typeInfo) {
        return (VarEnum)tdesc.vt == VarEnum.VT_VARIANT
            ? "ComProxy"
            : ResolveRawTypeName(tdesc, typeInfo);
    }

    private static string ResolveRawArrayElementType(VarEnum vt) => vt switch {
        VarEnum.VT_BOOL => "bool",
        VarEnum.VT_I1 or VarEnum.VT_UI1 => "byte",
        VarEnum.VT_I2 => "short",
        VarEnum.VT_UI2 => "ushort",
        VarEnum.VT_I4 or VarEnum.VT_INT => "int",
        VarEnum.VT_UI4 or VarEnum.VT_UINT => "uint",
        VarEnum.VT_I8 => "long",
        VarEnum.VT_UI8 => "ulong",
        VarEnum.VT_R4 => "float",
        VarEnum.VT_R8 => "double",
        VarEnum.VT_BSTR => "string",
        _ => "object"
    };

    private string ResolveRawPointerType(nint pTarget, ITypeInfo typeInfo, int depth = 0) {
        if (pTarget == 0 || depth > 3)
            return "object";
        try {
            TYPEDESC inner = Marshal.PtrToStructure<TYPEDESC>(pTarget);
            return ResolveRawTypeName(inner, typeInfo, depth + 1);
        }
        catch {
            return "object";
        }
    }

    private string ResolveRawUserDefined(nint hRef, ITypeInfo typeInfo) {
        try {
            int refHandle = unchecked((int)hRef);
            typeInfo.GetRefTypeInfo(refHandle, out ITypeInfo refInfo);
            refInfo.GetDocumentation(-1, out string name, out _, out _, out _);

            if (string.IsNullOrEmpty(name))
                return "object";
            string baseName = GetBaseName(name);

            using var pAttrLease = TypeInfoDescriptor.TypeAttributes(refInfo);
            nint pAttr = pAttrLease.Pointer;
            TYPEATTR attr = Marshal.PtrToStructure<TYPEATTR>(pAttr);
            TYPEKIND kind = attr.typekind;

            string prefix = "";
            refInfo.GetContainingTypeLib(out ITypeLib containingTypeLib, out _);
            if (containingTypeLib != null) {
                TYPELIBATTR libAttr = TypeInfoDescriptor.LibraryAttributes(containingTypeLib);

                typeInfo.GetContainingTypeLib(out ITypeLib currentTypeLib, out _);
                TYPELIBATTR currentLibAttr = TypeInfoDescriptor.LibraryAttributes(currentTypeLib);

                if (libAttr.guid != currentLibAttr.guid) {
                    if (ExternalTypeLibs.TryGetValue(libAttr.guid, out string? extNamespace)) {
                        prefix = $"global::{extNamespace}.";
                    }
                    else if (kind is TYPEKIND.TKIND_DISPATCH or TYPEKIND.TKIND_INTERFACE &&
                             attr.guid != Guid.Empty &&
                             TlbResolver.TypeLib.TryGetTypeLibFromInterface(attr.guid, out Guid ifaceTlbGuid, out _) &&
                             ExternalTypeLibs.TryGetValue(ifaceTlbGuid, out extNamespace)) {
                        prefix = $"global::{extNamespace}.";
                    }
                    else {
                        return kind == TYPEKIND.TKIND_ENUM ? "int" : "object";
                    }
                }
            }

            if (kind == TYPEKIND.TKIND_ENUM) {
                if (!string.IsNullOrEmpty(prefix)) {
                    string cleanBase = Sanitize(baseName);
                    string extEnum = $"{prefix}{cleanBase}";
                    KnownExternalEnums.Add(cleanBase);
                    KnownExternalEnums.Add(extEnum);
                    return extEnum;
                }
                if (KnownEnums.Contains(baseName) || KnownEnums.Contains(name))
                    return Sanitize(baseName);
                return "int";
            }

            if (kind is TYPEKIND.TKIND_DISPATCH or TYPEKIND.TKIND_INTERFACE or TYPEKIND.TKIND_COCLASS) {
                if (!string.IsNullOrEmpty(prefix)) {
                    string cleanBase = Sanitize(baseName);
                    string extTypeName = $"{prefix}{cleanBase}";
                    KnownExternalInterfaces.Add(cleanBase);
                    KnownExternalInterfaces.Add(extTypeName);
                    return extTypeName;
                }
                if (KnownInterfaces.Contains(baseName) || KnownInterfaces.Contains(name))
                    return Sanitize(baseName);
                return "object";
            }
            return "object";
        }
        catch {
            return "object";
        }
    }

    private string ResolveWrapperTypeName(string rawType, string fallbackOrPropName) {
        rawType = GetCanonicalTypeName(rawType);
        int lastDot = rawType.LastIndexOf('.');
        string prefix = lastDot > 0 ? rawType.Substring(0, lastDot + 1) : "";
        string typeNameOnly = lastDot > 0 ? rawType.Substring(lastDot + 1) : rawType;

        if (typeNameOnly.StartsWith("I")) {
            string candidate = GetBaseName(typeNameOnly);

            if (candidate.Equals("Objects", StringComparison.OrdinalIgnoreCase)) {
                string? inferredItem = GetSingularCandidate(fallbackOrPropName);
                if (inferredItem != null) {
                    return string.IsNullOrEmpty(prefix)
                        ? $"ComProxies<{inferredItem}>"
                        : $"ComProxies<{prefix}{inferredItem}>";
                }
            }

            if (!string.IsNullOrEmpty(prefix) || KnownInterfaces.Contains(candidate))
                return prefix + candidate;
        }

        string baseRaw = GetBaseName(typeNameOnly);
        if (!string.IsNullOrEmpty(prefix) || KnownEnums.Contains(baseRaw))
            return prefix + baseRaw;

        string baseFallback = GetBaseName(fallbackOrPropName);
        if (typeNameOnly == "object" && KnownInterfaces.Contains(baseFallback))
            return baseFallback;

        return rawType;
    }
}