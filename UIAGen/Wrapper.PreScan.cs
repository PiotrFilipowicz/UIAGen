//------------------------------------------------------------------------------
// <copyright file="Wrapper.PreScan.cs" company="Piotr Filipowicz">
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

namespace UiaAdapter;

public partial class UiaAdapterWrapper {
    internal HashSet<string> KnownExternalInterfaces { get; } = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _preScannedTypeLibs = new();

    [DllImport("oleaut32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void LoadTypeLibEx(string szFile, REGKIND regkind, out ITypeLib pptlib);

    private enum REGKIND {
        REGKIND_DEFAULT = 0,
        REGKIND_REGISTER = 1,
        REGKIND_NONE = 2
    }

    internal void PreScanTypeLibrary(string typeLibPath, string? externalNamespacePrefix = null) {
        if (string.IsNullOrWhiteSpace(typeLibPath))
            return;

        LoadTypeLibEx(typeLibPath, REGKIND.REGKIND_NONE, out ITypeLib typeLib);
        try {
            PreScanTypeLibrary(typeLib, externalNamespacePrefix);
        }
        finally {
            Marshal.ReleaseComObject(typeLib);
        }
    }

    internal void PreScanTypeLibrary(ITypeLib typeLib, string? externalNamespacePrefix = null) {
        if (typeLib == null)
            return;

        TYPELIBATTR libAttr = TypeInfoDescriptor.LibraryAttributes(typeLib);
        if (!_preScannedTypeLibs.Add(libAttr.guid))
            return;

        bool isExternal = !string.IsNullOrEmpty(externalNamespacePrefix);
        string normalizedPrefix = isExternal
            ? (externalNamespacePrefix!.EndsWith('.') ? externalNamespacePrefix : externalNamespacePrefix + ".")
            : string.Empty;

        int typeCount = typeLib.GetTypeInfoCount();
        var localExternalEnums = isExternal ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : null;

        // Pass 1: collect all enums first to keep their names out of KnownInterfaces.
        for (int i = 0; i < typeCount; i++) {
            typeLib.GetTypeInfoType(i, out TYPEKIND kind);
            if (kind != TYPEKIND.TKIND_ENUM)
                continue;

            typeLib.GetDocumentation(i, out string rawName, out _, out _, out _);
            if (string.IsNullOrWhiteSpace(rawName))
                continue;

            string cleanName = Sanitize(GetBaseName(rawName));
            if (string.IsNullOrWhiteSpace(cleanName))
                continue;

            if (isExternal) {
                localExternalEnums!.Add(cleanName);
                KnownExternalEnums.Add(cleanName);
                KnownExternalEnums.Add($"{normalizedPrefix}{cleanName}");
            }
            else {
                KnownEnums.Add(cleanName);
            }
        }

        // Pass 2: collect interfaces, dispinterfaces, and coclasses, excluding enum names.
        for (int i = 0; i < typeCount; i++) {
            typeLib.GetTypeInfoType(i, out TYPEKIND kind);
            if (kind is not (TYPEKIND.TKIND_INTERFACE or TYPEKIND.TKIND_DISPATCH or TYPEKIND.TKIND_COCLASS))
                continue;

            typeLib.GetDocumentation(i, out string rawName, out _, out _, out _);
            if (string.IsNullOrWhiteSpace(rawName))
                continue;

            string cleanName = Sanitize(GetBaseName(rawName));
            if (string.IsNullOrWhiteSpace(cleanName))
                continue;

            if (isExternal) {
                if (localExternalEnums!.Contains(cleanName))
                    continue;
                KnownExternalInterfaces.Add(cleanName);
                KnownExternalInterfaces.Add($"{normalizedPrefix}{cleanName}");
            }
            else {
                if (KnownEnums.Contains(cleanName))
                    continue;
                KnownInterfaces.Add(cleanName);
            }
        }
    }
}