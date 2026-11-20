//------------------------------------------------------------------------------
// <copyright file="Program.cs" company="Piotr Filipowicz">
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
using System.IO;
using System.Reflection;

using static UiaAdapter.ProgramHelpers;

internal static class Program {
    [STAThread]
    static void Main(string[] args) {
        try { Run(args); }
        catch (Exception exception) {
            Console.Error.WriteLine($"Generation failed: {exception}");
            Environment.ExitCode = 1;
        }
    }

    internal static void PrintCliUsage(string? errorMessage = null) {
        if (!string.IsNullOrEmpty(errorMessage)) {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(errorMessage);
            Console.ResetColor();
        }

        Console.WriteLine("Usage: uiagen {modules|-mso|-adobe|-acad} [outputFolder] [className] [-list] [-compile]");
        Console.WriteLine("       uiagen -all [outputFolder] [-filter folderlist] [-list] [-compile]");
        Console.WriteLine("       uiagen {-h|--help|-v|--version}");
        Console.WriteLine("       modules is a list of comma-separated module names that can be .tlb, .exe, or .dll files, ProgIDs, or names from HKCR\\CLSID, TypeLib, or Interface. Double quotes can enclose individual module names.");
        Console.WriteLine("       folderlist is a list of comma-separated folder paths (with optional double quotes) used to restrict -all scanning.");
        Console.WriteLine("       -mso: Generate adapter for Microsoft Office applications.");
        Console.WriteLine("       -adobe: Generate adapter for Adobe applications.");
        Console.WriteLine("       -acad: Generate combined adapter for Autodesk AutoCAD type libraries.");
        Console.WriteLine("       -all: Generate adapters for all registered typelibs (or list them when -list is specified).");
        Console.WriteLine("       -filter folderlist: Restrict -all discovery to servers located inside the specified folders (including subfolders).");
        Console.WriteLine("       -list: List discovered servers and resolved type libraries without generating code (outputFolder is optional).");
        Console.WriteLine("       -compile: Build each generated adapter project into a Release DLL in the parent folder.");
        Console.WriteLine("       -h, --help: Display this help message.");
        Console.WriteLine("       -v, --version: Display the tool version.");
    }

    private static void Run(string[] args) {
        if (args.Length < 1) {
            PrintCliUsage("Error: Expected at least 1 argument.");
            Environment.ExitCode = 2;
            return;
        }

        // 1. Parse all options and positional arguments first
        bool showHelp = false;
        bool showVersion = false;
        bool modeAll = false;
        bool modeMso = false;
        bool modeAdobe = false;
        bool modeAcad = false;
        bool listOnly = false;
        bool compile = false;
        string[] folderFilters = [];
        var positional = new List<string>();

        for (int i = 0; i < args.Length; i++) {
            string current = args[i].Trim();
            if (current.Length == 0) {
                PrintCliUsage("Error: Empty argument.");
                Environment.ExitCode = 2;
                return;
            }

            string lower = current.ToLowerInvariant();
            if (lower is "-h" or "--help" or "/?" or "/h" or "/help") {
                showHelp = true;
            }
            else if (lower is "-v" or "--version" or "/v" or "/version") {
                showVersion = true;
            }
            else if (lower is "-all" or "--all") {
                modeAll = true;
            }
            else if (lower is "-mso" or "--mso") {
                modeMso = true;
            }
            else if (lower is "-adobe" or "--adobe") {
                modeAdobe = true;
            }
            else if (lower is "-acad" or "--acad") {
                modeAcad = true;
            }
            else if (lower is "-list" or "--list") {
                listOnly = true;
            }
            else if (lower is "-compile" or "--compile") {
                compile = true;
            }
            else if (lower is "-filter" or "--filter") {
                if (i + 1 >= args.Length) {
                    PrintCliUsage("Error: Missing folderlist argument after -filter.");
                    Environment.ExitCode = 2;
                    return;
                }
                string rawFolderList = args[++i];
                if (!TryParseCommaSeparatedList(rawFolderList, out string[] parsedFolders)) {
                    PrintCliUsage($"Error: Invalid folderlist format '{rawFolderList}'.");
                    Environment.ExitCode = 2;
                    return;
                }
                folderFilters = parsedFolders;
            }
            else if (current.StartsWith('-')) {
                PrintCliUsage($"Error: Unknown option '{current}'.");
                Environment.ExitCode = 2;
                return;
            }
            else {
                positional.Add(current);
            }
        }

        // 2. Immediate meta-commands
        if (showHelp) {
            PrintCliUsage();
            return;
        }

        if (showVersion) {
            string? infoVersion = typeof(Program).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(infoVersion)) {
                int plusIndex = infoVersion.IndexOf('+');
                Console.WriteLine(plusIndex >= 0 ? infoVersion[..plusIndex] : infoVersion);
            }
            else {
                Version? version = typeof(Program).Assembly.GetName().Version;
                Console.WriteLine(version is null ? "1.0.0" : $"{version.Major}.{version.Minor}.{version.Build}");
            }
            return;
        }

        // 3. Validate mode and option combinations
        int modeCount = (modeAll ? 1 : 0) + (modeMso ? 1 : 0) + (modeAdobe ? 1 : 0) + (modeAcad ? 1 : 0);
        if (modeCount > 1) {
            PrintCliUsage("Error: Options -all, -mso, -adobe, and -acad are mutually exclusive.");
            Environment.ExitCode = 2;
            return;
        }

        if (listOnly && compile) {
            PrintCliUsage("Error: -compile cannot be combined with -list.");
            Environment.ExitCode = 2;
            return;
        }

        // 4. Execute -all mode
        if (modeAll) {
            if (positional.Count > 1) {
                PrintCliUsage($"Error: Unexpected positional argument '{positional[1]}'.");
                Environment.ExitCode = 2;
                return;
            }

            string? outputFolder = positional.Count == 1 ? Path.GetFullPath(positional[0]) : null;
            if (!listOnly && string.IsNullOrWhiteSpace(outputFolder)) {
                PrintCliUsage("Error: outputFolder is required when -list is not specified.");
                Environment.ExitCode = 2;
                return;
            }

            if (!GenerateAllAdapters(outputFolder, folderFilters: folderFilters, listOnly: listOnly, compile: compile))
                Environment.ExitCode = 1;
            return;
        }

        // 5. Validate options specific to -all
        if (folderFilters.Length > 0) {
            PrintCliUsage("Error: -filter can only be used with -all.");
            Environment.ExitCode = 2;
            return;
        }

        // 6. Execute -acad mode (filtered registry discovery combined into one adapter)
        if (modeAcad) {
            int acadPosIndex = 0;
            string? acadOutputFolder = null;
            if (acadPosIndex < positional.Count) {
                acadOutputFolder = Path.GetFullPath(positional[acadPosIndex++]);
            }
            else if (!listOnly) {
                PrintCliUsage("Error: Missing outputFolder argument.");
                Environment.ExitCode = 2;
                return;
            }

            string acadAdapterName = "AutoCADAdapter";
            if (acadPosIndex < positional.Count) {
                acadAdapterName = positional[acadPosIndex++];
            }

            if (acadPosIndex < positional.Count) {
                PrintCliUsage($"Error: Unexpected positional argument '{positional[acadPosIndex]}'.");
                Environment.ExitCode = 2;
                return;
            }

            string[] acadFilters = [
                @"C:\Program Files\Autodesk",
                @"C:\Program Files\Common Files\Autodesk Shared"
            ];

            if (!GenerateAllAdapters(
                acadOutputFolder,
                combinedAdapterName: acadAdapterName,
                folderFilters: acadFilters,
                listOnly: listOnly,
                compile: compile)) {
                Environment.ExitCode = 1;
                return;
            }

            if (!listOnly) {
                Console.WriteLine($"Adapter generated successfully: {acadAdapterName} in {acadOutputFolder}");
            }
            return;
        }

        // 7. Execute explicit module list / -mso / -adobe mode
        string[] modules;
        string? defaultAdapterName = null;
        int posIndex = 0;

        if (modeMso) {
            modules = [
                "Word.Application",
                "Excel.Application",
                "PowerPoint.Application",
                "Access.Application",
                "Outlook.Application"
            ];
            defaultAdapterName = "MSOfficeAdapter";
        }
        else if (modeAdobe) {
            modules = [
                "InCopy.Application",
                "InDesign.Application",
                "Photoshop.Application"
            ];
            defaultAdapterName = "AdobeAdapter";
        }
        else {
            if (positional.Count < 1) {
                PrintCliUsage("Error: Expected module list or mode option (-mso, -adobe, -acad, -all).");
                Environment.ExitCode = 2;
                return;
            }
            if (!TryParseCommaSeparatedList(positional[posIndex++], out modules)) {
                PrintCliUsage($"Error: Invalid module list '{positional[0]}'.");
                Environment.ExitCode = 2;
                return;
            }
        }

        string? resolvedOutputFolder = null;
        if (posIndex < positional.Count) {
            resolvedOutputFolder = Path.GetFullPath(positional[posIndex++]);
        }
        else if (!listOnly) {
            PrintCliUsage("Error: Missing outputFolder argument.");
            Environment.ExitCode = 2;
            return;
        }

        string? adapterName = defaultAdapterName;
        if (posIndex < positional.Count) {
            adapterName = positional[posIndex++];
        }

        if (posIndex < positional.Count) {
            PrintCliUsage($"Error: Unexpected positional argument '{positional[posIndex]}'.");
            Environment.ExitCode = 2;
            return;
        }

        if (!listOnly && string.IsNullOrWhiteSpace(adapterName)) {
            PrintCliUsage("Error: Missing className argument for explicit module list.");
            Environment.ExitCode = 2;
            return;
        }

        if (!GenerateAdapter(modules, resolvedOutputFolder, adapterName ?? "PreviewAdapter", compile, listOnly)) {
            Environment.ExitCode = 1;
            return;
        }

        if (!listOnly) {
            Console.WriteLine($"Adapter generated successfully: {adapterName} in {resolvedOutputFolder}");
        }
    }

    private static bool TryParseCommaSeparatedList(string argument, out string[] items) {
        items = [];
        var names = new List<string>();
        ReadOnlySpan<char> remaining = argument.AsSpan().TrimStart();
        while (!remaining.IsEmpty) {
            ReadOnlySpan<char> name;
            if (remaining[0] == '"') {
                int closingQuote = remaining[1..].IndexOf('"');
                if (closingQuote < 0)
                    return false;

                name = remaining.Slice(1, closingQuote);
                remaining = remaining[(closingQuote + 2)..].TrimStart();
            }
            else {
                int separator = remaining.IndexOf(',');
                if (separator < 0)
                    separator = remaining.Length;

                name = remaining[..separator].TrimEnd();
                remaining = remaining[separator..];
                if (name.Contains('"'))
                    return false;
            }

            if (name.Trim().IsEmpty)
                return false;

            names.Add(name.ToString());
            if (remaining.IsEmpty) {
                items = names.ToArray();
                return true;
            }
            if (remaining[0] != ',')
                return false;

            remaining = remaining[1..].TrimStart();
        }
        return false;
    }
}