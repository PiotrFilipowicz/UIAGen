//------------------------------------------------------------------------------
// <copyright file="CanonicalTypeNames.cs" company="Piotr Filipowicz">
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

namespace UiaAdapter;

/// <summary>Resolves canonical spelling without reading COM or emitting code.</summary>
internal static class CanonicalTypeNames {
    internal static string Resolve(string typeName, string adapterNamespace,
        HashSet<string> interfaces, HashSet<string> enums,
        HashSet<string> externalInterfaces, HashSet<string> externalEnums) {
        if (string.IsNullOrWhiteSpace(typeName))
            return typeName;

        string suffix = typeName.EndsWith("?") ? "?" : "";
        string clean = typeName.TrimEnd('?');

        string globalPrefix = clean.StartsWith("global::") ? "global::" : "";
        if (globalPrefix.Length > 0)
            clean = clean.Substring(8);

        int lastDot = clean.LastIndexOf('.');
        string nsPrefix = lastDot >= 0 ? clean.Substring(0, lastDot + 1) : "";
        string shortName = lastDot >= 0 ? clean.Substring(lastDot + 1) : clean;

        string? canonical = null;
        if (nsPrefix.Length > 0 && nsPrefix != adapterNamespace + ".") {
            string qualified = nsPrefix + shortName;
            if (externalInterfaces.TryGetValue(qualified, out var externalInterface) ||
                externalEnums.TryGetValue(qualified, out externalInterface)) {
                canonical = externalInterface[(externalInterface.LastIndexOf('.') + 1)..];
            }
        } else if (interfaces.TryGetValue(shortName, out var localInterface) ||
                   enums.TryGetValue(shortName, out localInterface) ||
                   externalInterfaces.TryGetValue(shortName, out localInterface) ||
                   externalEnums.TryGetValue(shortName, out localInterface)) {
            canonical = localInterface;
        }

        if (canonical != null)
            return globalPrefix + nsPrefix + canonical + suffix;

        return typeName;
        }
}
