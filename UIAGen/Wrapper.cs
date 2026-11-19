//------------------------------------------------------------------------------
// <copyright file="Wrapper.cs" company="Piotr Filipowicz">
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
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;

[assembly: SupportedOSPlatform("windows")]

namespace UiaAdapter;

[SupportedOSPlatform("windows")]
public partial class UiaAdapterWrapper {
    // --- COLLECTION MAPPING OVERRIDES BY APPLICATION ---
    private static readonly Dictionary<string, Dictionary<string, string[]>> AppSpecificCollectionMappings = new(StringComparer.OrdinalIgnoreCase) {
        ["MSWord"] = new(StringComparer.OrdinalIgnoreCase)
        {
            { "Characters",                     ["Range"] },
            { "Words",                          ["Range"] },
            { "Sentences",                      ["Range"] },
            { "Runs",                           ["Range"] },
            { "StoryRanges",                    ["Range"] },
            { "ProofreadingErrors",             ["Range"] },
            { "SpellingErrors",                 ["Range"] },
            { "GrammaticalErrors",              ["Range"] },
            { "FontNames",                      ["string"] },
            { "PortraitFontNames",              ["string"] },
            { "LandscapeFontNames",             ["string"] },
            { "TablesOfAuthoritiesCategories",  ["TableOfAuthoritiesCategory"] },
            { "TaskPanes",                      ["TaskPane"] },
            { "Dialogs",                        ["Dialog"] },
            { "Languages",                      ["Language"] },
            { "HeadersFooters",                 ["HeaderFooter"] }
        },
        ["MSExcel"] = new(StringComparer.OrdinalIgnoreCase)
        {
            { "Sheets",      ["Worksheet"] },
            { "Worksheets",  ["Worksheet"] },
            { "Range",       ["Range"] },
            { "Cells",       ["Range"] },
            { "Rows",        ["Range"] },
            { "Columns",     ["Range"] },
            { "Areas",       ["Range"] }
        },
        ["MSPowerPoint"] = new(StringComparer.OrdinalIgnoreCase)
        {
            { "Characters",  ["TextRange"] },
            { "Words",       ["TextRange"] },
            { "Sentences",   ["TextRange"] },
            { "SlideRange",  ["Slide"] },
            { "ShapeRange",  ["Shape"] }
        },
        ["MSAccess"] = new(StringComparer.OrdinalIgnoreCase)
        {
            { "TableDefs",   ["TableDef"] },
            { "QueryDefs",   ["QueryDef"] },
            { "Recordsets",  ["Recordset"] },
            { "AllForms",    ["AccessObject"] },
            { "AllReports",  ["AccessObject"] },
            { "AllTables",   ["AccessObject"] },
            { "AllQueries",  ["AccessObject"] }
        },
        ["MSOutlook"] = new(StringComparer.OrdinalIgnoreCase)
        {
            { "Reminders",      ["Reminder"] },
            { "Folders",        ["Folder"] },
            { "Attachments",    ["Attachment"] },
            { "Accounts",       ["Account"] },
            { "Rules",          ["Rule"] },
            { "Views",          ["View"] },
            { "ItemProperties", ["ItemProperty"] },
            { "UserProperties", ["UserProperty"] }
        }
    };

    // --- PROPERTIES EXCLUDED FROM DEBUGGER EVALUATION ---
    private static readonly Dictionary<string, HashSet<string>> AppDebuggerBlacklist = new(StringComparer.OrdinalIgnoreCase) {
        ["MSWord"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "MailEnvelope",
            "Document.MailEnvelope",
            "MailMerge",
            "Document.MailMerge",
            "RoutingSlip",
            "Document.RoutingSlip",
            "Mailer",
            "Document.Mailer"
        },
        ["MSExcel"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "MailEnvelope",
            "Mailer"
        }
    };

    // --- COLLECTIONS WITH FULL ENUMERATION BLOCKED IN THE DEBUGGER ---
    private static readonly Dictionary<string, HashSet<string>> BlockedEnumerationCollections = new(StringComparer.OrdinalIgnoreCase) {
        ["MSWord"] = new(StringComparer.OrdinalIgnoreCase) {
            "Dialogs",
            "Languages"
        }
    };

    internal bool IsAdobeAdapter =>
        AdapterClassName.Equals("AdobeAdapter", StringComparison.OrdinalIgnoreCase) ||
        AdapterClassName.EndsWith(".InDesign", StringComparison.OrdinalIgnoreCase) ||
        AdapterClassName.EndsWith(".InCopy", StringComparison.OrdinalIgnoreCase);

    private bool MatchesAppKey(string appKey) {
        if (AdapterClassName.Contains(appKey, StringComparison.OrdinalIgnoreCase))
            return true;

        string stripped = appKey.StartsWith("MS", StringComparison.OrdinalIgnoreCase)
            ? appKey[2..]
            : appKey;

        foreach (string segment in AdapterClassName.Split('.')) {
            if (segment.Equals(appKey, StringComparison.OrdinalIgnoreCase) ||
                segment.Equals(stripped, StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
        }

        return false;
    }

    private bool IsBlockedEnumeration(string collectionName) {
        foreach (var appKey in BlockedEnumerationCollections.Keys) {
            if (MatchesAppKey(appKey)) {
                if (BlockedEnumerationCollections[appKey].Contains(collectionName))
                    return true;
            }
        }
        return false;
    }

    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
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

    private readonly Dictionary<Guid, string> ExternalTypeLibs = new();

    internal readonly HashSet<string> KnownEnums = new(StringComparer.OrdinalIgnoreCase);
    internal readonly HashSet<string> KnownExternalEnums = new(StringComparer.OrdinalIgnoreCase);
    internal readonly HashSet<string> KnownInterfaces = new(StringComparer.OrdinalIgnoreCase);
    internal readonly HashSet<string> DiscoveredObjectsMembers = new(StringComparer.OrdinalIgnoreCase);
    internal readonly string AdapterClassName;
    internal readonly string RootNamespace;
    internal readonly Guid TypeLibGuid;
    private readonly bool _emitInfrastructure;
    private readonly ITypeLib typeLib;

    internal StringBuilder InfrastructureBuilder { get; private set; } = new();
    internal StringBuilder EnumsBuilder { get; private set; } = new();
    internal StringBuilder WrappersBuilder { get; private set; } = new();

    internal UiaAdapterWrapper(
        string AdapterClassName,
        ITypeLib typeLib,
        Dictionary<string, ITypeLib>? externalLibs = null,
        string? rootNamespace = null,
        bool emitInfrastructure = true) {

        this.AdapterClassName = AdapterClassName;
        this.RootNamespace = string.IsNullOrWhiteSpace(rootNamespace) ? AdapterClassName : rootNamespace;
        this._emitInfrastructure = emitInfrastructure;
        this.typeLib = typeLib;
        this.TypeLibGuid = TypeInfoDescriptor.LibraryAttributes(typeLib).guid;

        if (externalLibs != null) {
            foreach (var kvp in externalLibs) {
                if (kvp.Value != null) {
                    TYPELIBATTR attr = TypeInfoDescriptor.LibraryAttributes(kvp.Value);
                    ExternalTypeLibs[attr.guid] = kvp.Key;
                    PreScanTypeLibrary(kvp.Value, kvp.Key);
                }
            }
        }
        PreScanTypeLibrary(typeLib);
        GenerateBuilders();
    }

    private bool IsDebuggerBlacklisted(string className, string memberName) {
        string fullPath = $"{AdapterClassName}.{className}.{memberName}";
        string classMember = $"{className}.{memberName}";

        foreach (var appKey in AppDebuggerBlacklist.Keys) {
            if (MatchesAppKey(appKey)) {
                var set = AppDebuggerBlacklist[appKey];
                if (set.Contains(memberName) || set.Contains(classMember) || set.Contains(fullPath)) {
                    return true;
                }
            }
        }
        return false;
    }

    internal static string GetBaseName(string name) {
        if (string.IsNullOrEmpty(name))
            return "Object";
        string clean = name.StartsWith("_") && name.Length > 1 ? name[1..] : name;
        if (clean.Length > 2 && clean[0] == 'I' && char.IsUpper(clean[1]) && char.IsLetter(clean[1])) {
            return clean[1..];
        }
        return clean;
    }

    internal static string CleanName(string name) => GetBaseName(name);

    private static bool IsPrimitiveType(string typeName) {
        return typeName switch {
            "bool" or "byte" or "sbyte" or "short" or "ushort" or "int" or "uint" or "long" or "ulong" or "float" or "double" or "decimal" => true,
            _ => false
        };
    }

    private static string Sanitize(string name) => Sanitize(name, "_param");

    private static string Sanitize(string name, string fallback) {
        if (string.IsNullOrWhiteSpace(name))
            return fallback;

        var sb = new StringBuilder(name.Length);
        bool hasLetterOrDigit = false;
        foreach (char c in name) {
            if (char.IsLetterOrDigit(c)) {
                sb.Append(c);
                hasLetterOrDigit = true;
            }
            else if (c == '_') {
                sb.Append(c);
            }
            else if (!char.IsControl(c)) {
                sb.Append('_');
            }
        }

        if (!hasLetterOrDigit || sb.Length == 0)
            return fallback;

        string cleaned = sb.ToString();
        if (char.IsDigit(cleaned[0]))
            cleaned = "_" + cleaned;

        return CSharpKeywords.Contains(cleaned) ? "@" + cleaned : cleaned;
    }

    private static string SafeMemberName(string memberName, string className) {
        string sanitized = Sanitize(memberName);
        if (string.Equals(sanitized.TrimStart('@'), className.TrimStart('@'), StringComparison.OrdinalIgnoreCase) ||
            sanitized.Equals("Missing", StringComparison.OrdinalIgnoreCase) ||
            sanitized.Equals("GetMissing", StringComparison.OrdinalIgnoreCase)) {
            return sanitized + "Value";
        }
        return sanitized;
    }

    private static long ReadEnumConstant(nint variant) {
        ushort vt = unchecked((ushort)Marshal.ReadInt16(variant));
        return vt switch {
            2 => Marshal.ReadInt16(variant, 8),                         // VT_I2
            3 or 22 => Marshal.ReadInt32(variant, 8),                   // VT_I4 / VT_INT
            16 => unchecked((sbyte)Marshal.ReadByte(variant, 8)),       // VT_I1
            17 => Marshal.ReadByte(variant, 8),                         // VT_UI1
            18 => unchecked((ushort)Marshal.ReadInt16(variant, 8)),     // VT_UI2
            19 or 23 => unchecked((uint)Marshal.ReadInt32(variant, 8)), // VT_UI4 / VT_UINT
            20 => Marshal.ReadInt64(variant, 8),                        // VT_I8
            21 => unchecked((long)(ulong)Marshal.ReadInt64(variant, 8)),// VT_UI8
            _ => throw new NotSupportedException($"Unsupported VARIANT type for an enum constant: 0x{vt:X4}.")
        };
    }

    internal void GenerateEnum(ITypeInfo typeInfo, Action<string> AppendLine) {
        typeInfo.GetDocumentation(-1, out string enumName, out _, out _, out _);
        using var pAttrLease = TypeInfoDescriptor.TypeAttributes(typeInfo);
        nint pAttr = pAttrLease.Pointer;
        TYPEATTR attr = Marshal.PtrToStructure<TYPEATTR>(pAttr);

        string cleanEnumName = GetBaseName(enumName);
        AppendLine($"public enum {Sanitize(cleanEnumName)} : int {{");

        for (int i = 0; i < attr.cVars; i++) {
            using var pVarDescLease = TypeInfoDescriptor.Variable(typeInfo, i);
            nint pVarDesc = pVarDescLease.Pointer;
            VARDESC varDesc = Marshal.PtrToStructure<VARDESC>(pVarDesc);
            typeInfo.GetDocumentation(varDesc.memid, out string varName, out _, out _, out _);
            long rawLong = ReadEnumConstant(varDesc.desc.lpvarValue);
            int safeInt = unchecked((int)rawLong);

            AppendLine($"    {Sanitize(varName)} = {safeInt},");
        }

        AppendLine("}");
        AppendLine("");
    }
}