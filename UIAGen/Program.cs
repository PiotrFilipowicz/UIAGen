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
using System.Linq;

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

    internal static void PrintCliUsage(string errorMessage) {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(errorMessage);
        Console.WriteLine("Usage: UIAGen.exe {modules|-mso|-adobe|-all} outputFolder [className]");
        Console.WriteLine("       modules is a list of comma-separated module names that can be .tlb, .exe, or .dll files, ProgIDs, or names from HKCR\\CLSID, TypeLib, or Interface. Double quotes can enclose individual module names.");
        Console.WriteLine("       -mso: Generate adapter for Microsoft Office applications.");
        Console.WriteLine("       -adobe: Generate adapter for Adobe applications.");
        Console.WriteLine("       -all: Generate adapters for all registered typelibs.");
        Console.ResetColor();
    }

    private static void Run(string[] args) {

        string[] modules;
        string outputFolder;
        string? adapterName = null;
        if (args.Length < 2) {
            PrintCliUsage("Error: Expected at least 2 arguments.0");
            Environment.ExitCode = 2;
            return;
        }
        outputFolder = Path.GetFullPath(args[1]);
        string args0 = args[0].Trim().ToLowerInvariant();


        if (args0.Equals("-all")) {
            if (!GenerateAllAdapters(outputFolder))
                Environment.ExitCode = 1;
            return;
        }
        if (args0.Equals("-mso")) {
            modules = [
                "Word.Application",
                "Excel.Application",
                "PowerPoint.Application",
                "Access.Application",
                "Outlook.Application"
                ];
            adapterName = args.Length > 2 ? args[2] : "MSOfficeAdapter";

        }
        else if (args0.Equals("-adobe")) {
            modules = [
                "InCopy.Application",
                "InDesign.Application",
                "Photoshop.Application"
            ];
            adapterName = args.Length > 2 ? args[2] : "AdobeAdapter";
        }
        else if(TryParseModules(args[0], out modules) && args.Length > 2) {
            adapterName = args[2];
        }
        else {
            Environment.ExitCode = 1;
            return;
        }

        if (!GenerateAdapter(modules, outputFolder, adapterName)) {
            Environment.ExitCode = 1;
            return;
        }
       Console.WriteLine($"Adapter generated successfully: {adapterName} in {outputFolder}");
    }

    private static bool TryParseModules(string argument, out string[] modules) {
    modules = [];
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
            modules = names.ToArray();
            return true;
        }
        if (remaining[0] != ',')
            return false;

        remaining = remaining[1..].TrimStart();
    }
    return false;
}
}