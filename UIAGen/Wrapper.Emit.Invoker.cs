//------------------------------------------------------------------------------
// <copyright file="Wrapper.Emit.Invoker.cs" company="Piotr Filipowicz">
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
using System.IO;
using System.Linq;
using System.Reflection;

namespace UiaAdapter;

public partial class UiaAdapterWrapper {
    private const string TemplateResourcePrefix = "UiaAdapter.Templates.";
    private const string SafeDebuggerMethodsMarker = "        // __UIA_SAFE_DEBUGGER_METHODS__";
    private static readonly string[] SafeDebuggerMethodNames = [
        "FirstItem",
        "LastItem",
        "MiddleItem",
        "PreviousItem",
        "NextItem",

        "ItemByID",
        "ItemByRange",
        "AnyItem"
    ];

    //internal void AppendComInvoker(Action<string> append) =>
    //    AppendComInvoker(append, IsAdobeAdapter);

    internal static void AppendComInvoker(Action<string> append, bool includeSafeAdobeMethods) {
        string[] safeMethodNames = includeSafeAdobeMethods
            ? SafeDebuggerMethodNames
            : [];
        string safeDebuggerMethods = string.Join(Environment.NewLine,
            safeMethodNames.Select(name => $"        \"{name}\","));
        append(ReadTemplate("ComInvoker.cs")
            .Replace(SafeDebuggerMethodsMarker, safeDebuggerMethods, StringComparison.Ordinal));
    }

    private static string ReadTemplate(string resourceName) {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(TemplateResourcePrefix + resourceName)
            ?? throw new InvalidOperationException($"Embedded template not found: {resourceName}.");
        using StreamReader reader = new(stream);
        string template = reader.ReadToEnd();

        const string compileHeaderStart = "#if UIA_ADAPTER_TEMPLATE";
        int compileHeaderIndex = template.IndexOf(compileHeaderStart, StringComparison.Ordinal);
        if (compileHeaderIndex < 0)
            return template;

        int headerEnd = template.IndexOf("#endif", compileHeaderIndex + compileHeaderStart.Length, StringComparison.Ordinal);
        if (headerEnd < 0)
            throw new InvalidOperationException($"Incomplete conditional compilation block in template: {resourceName}.");

        int contentStart = headerEnd + "#endif".Length;
        while (contentStart < template.Length && template[contentStart] is '\r' or '\n')
            contentStart++;
        return template[contentStart..];
    }
}