//------------------------------------------------------------------------------
// <copyright file="ProgramHelpers.cs" company="Piotr Filipowicz">
//     Copyright (c) 2026 Piotr Filipowicz.
//     SPDX-License-Identifier: LGPL-3.0-or-later WITH GCC-exception-3.1
//     Licensed under GNU LGPL v3 with Code Generation Exception.
// </copyright>
// <security>
//     Integrity Notice: Official commits, tags, and release artifacts of this
//     project are cryptographically signed.
// </security>
//------------------------------------------------------------------------------

using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace UiaAdapter;

internal static class ProgramHelpers {

    internal static bool GenerateAllAdapters(string outputFolder, string? combinedAdapterName = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);
        string fullOutputFolder = Path.GetFullPath(outputFolder);
        Directory.CreateDirectory(fullOutputFolder);

        Console.WriteLine("Scanning the HKCR registry: ProgID, TypeLib, and Interface (32-bit and 64-bit views)...");
        string[] allProgIds = CollectRegisteredProgIds();
        Console.WriteLine($"Found {allProgIds.Length} ProgIDs with associated CLSIDs. Resolving type libraries...");

        var uniqueLibs = new Dictionary<Guid, (string ProgId, ITypeLib TypeLib)>();
        int progIdsWithTypeLib = 0;

        foreach (string progId in allProgIds) {
            try {
                ITypeLib? lib = TlbResolver.Finder.TypeLibFromProgId(progId);
                if (lib == null)
                    continue;

                progIdsWithTypeLib++;
                TYPELIBATTR attr = TypeInfoDescriptor.LibraryAttributes(lib);
                if (attr.guid == Guid.Empty)
                    continue;

                if (!uniqueLibs.TryGetValue(attr.guid, out var existing)) {
                    uniqueLibs[attr.guid] = (progId, lib);
                }
                else if (IsBetterRepresentative(progId, existing.ProgId)) {
                    uniqueLibs[attr.guid] = (progId, lib);
                }
            }
            catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException or System.Security.SecurityException) {
                Console.Error.WriteLine($"  [SKIP] ProgID {progId}: {exception.Message}");
            }
        }

        Console.WriteLine($"Resolved {progIdsWithTypeLib} ProgIDs to {uniqueLibs.Count} unique type libraries.");
        IReadOnlyList<RegisteredTypeLib> registrations = RegistryTypeLibScanner.Collect(Console.Error.WriteLine);
        int additionalLibraries = 0;
        foreach (RegisteredTypeLib registration in registrations) {
            if (uniqueLibs.ContainsKey(registration.Guid))
                continue;
            try {
                ITypeLib? library = TlbResolver.TypeLib.GetFromGuid(registration.Guid, registration.Version);
                if (library == null) {
                    Console.Error.WriteLine($"  [SKIP] {registration.Source}, version {registration.Version}: could not load type library {registration.Guid:B}.");
                    continue;
                }
                TYPELIBATTR attr = TypeInfoDescriptor.LibraryAttributes(library);
                if (attr.guid != registration.Guid) {
                    Console.Error.WriteLine($"  [SKIP] {registration.Source}: the file contains a different type library GUID ({attr.guid:B}).");
                    continue;
                }
                if (uniqueLibs.TryAdd(attr.guid, (registration.Source, library)))
                    additionalLibraries++;
            }
            catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException or System.Security.SecurityException) {
                Console.Error.WriteLine($"  [SKIP] {registration.Source}: {exception.Message}");
            }
        }
        Console.WriteLine($"TypeLib/Interface: {registrations.Count} registrations, {additionalLibraries} libraries added. Total: {uniqueLibs.Count}.");
        if (uniqueLibs.Count == 0)
            return false;

        if (!string.IsNullOrWhiteSpace(combinedAdapterName)) {
            return GenerateAdapter(uniqueLibs.Values.ToArray(), fullOutputFolder, combinedAdapterName);
        }

        var usedAdapterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int succeeded = 0;
        var failures = new List<(string ProgId, string AdapterName, string Error)>();

        foreach (var (_, (progId, lib)) in uniqueLibs) {
            string baseAdapterName = ResolveDefaultAdapterName(lib, progId);
            string adapterName = baseAdapterName;
            int suffix = 2;
            while (!usedAdapterNames.Add(adapterName)) {
                adapterName = $"{baseAdapterName}_{suffix++}";
            }

            string adapterFolder = Path.Combine(fullOutputFolder, adapterName);
            try {
                if (GenerateAdapter(new (string ProgId, ITypeLib TypeLib)[] { (progId, lib) }, adapterFolder, adapterName)) {
                    succeeded++;
                }
                else {
                    failures.Add((progId, adapterName, "GenerateAdapter returned false"));
                }
            }
            catch (Exception ex) {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Failed to generate '{adapterName}' ({progId}): {ex.Message}");
                Console.ResetColor();
                failures.Add((progId, adapterName, ex.Message));
            }
        }

        Console.WriteLine();
        Console.ForegroundColor = failures.Count == 0 ? ConsoleColor.Green : ConsoleColor.Yellow;
        Console.WriteLine($"--all summary: generated {succeeded}/{uniqueLibs.Count} adapters in '{fullOutputFolder}' (failures: {failures.Count}).");
        Console.ResetColor();

        foreach (var (progId, adapterName, error) in failures) {
            Console.Error.WriteLine($"  [FAIL] {adapterName} ({progId}): {error}");
        }

        return failures.Count == 0;
    }

    private static string[] CollectRegisteredProgIds() {
        var versionIndependent = new List<string>();
        var versioned = new List<string>();

        foreach (string keyName in Registry.ClassesRoot.GetSubKeyNames()) {
            if (string.IsNullOrWhiteSpace(keyName) ||
                keyName.StartsWith('.') ||
                keyName.StartsWith('{') ||
                keyName.StartsWith('*')) {
                continue;
            }

            try {
                using var progKey = Registry.ClassesRoot.OpenSubKey(keyName);
                if (progKey == null)
                    continue;

                using var clsidKey = progKey.OpenSubKey("CLSID");
                if (clsidKey?.GetValue("") is not string clsid || !Guid.TryParse(clsid, out _))
                    continue;

                using var curVerKey = progKey.OpenSubKey("CurVer");
                if (curVerKey?.GetValue("") is string curVer && !string.IsNullOrWhiteSpace(curVer))
                    versionIndependent.Add(keyName);
                else
                    versioned.Add(keyName);
            }
            catch { }
        }

        versionIndependent.Sort(StringComparer.OrdinalIgnoreCase);
        versioned.Sort(StringComparer.OrdinalIgnoreCase);

        var result = new List<string>(versionIndependent.Count + versioned.Count);
        result.AddRange(versionIndependent);
        result.AddRange(versioned);
        return result.ToArray();
    }

    private static bool IsBetterRepresentative(string candidate, string current) {
        bool candApp = candidate.EndsWith(".Application", StringComparison.OrdinalIgnoreCase);
        bool currApp = current.EndsWith(".Application", StringComparison.OrdinalIgnoreCase);
        if (candApp != currApp)
            return candApp;
        return candidate.Length < current.Length;
    }

    private static string ResolveDefaultAdapterName(ITypeLib typeLib, string progId) {
        string rawName = progId.Split('.')[0];
        try {
            typeLib.GetDocumentation(-1, out string docName, out _, out _, out _);
            if (!string.IsNullOrWhiteSpace(docName))
                rawName = docName;
        }
        catch { }

        string sanitized = TypeLibDependencyScanner.SanitizeNamespace(rawName).TrimStart('@');
        return sanitized.EndsWith("Adapter", StringComparison.OrdinalIgnoreCase)
            ? sanitized
            : $"{sanitized}Adapter";
    }

    internal static bool GenerateAdapter(string[] progIds, string outputFolder, string adapterName) {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);
        ArgumentNullException.ThrowIfNull(progIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterName);
        if (progIds.Length == 0)
            throw new ArgumentException("The list of ProgIDs cannot be empty.", nameof(progIds));

        var primaryLibs = new List<(string ProgId, ITypeLib TypeLib)>();
        bool allSucceeded = true;

        foreach (string progId in progIds) {
            if (string.IsNullOrWhiteSpace(progId))
                continue;

            ITypeLib? lib = File.Exists(progId)
                ? TlbResolver.Loader.TypeLibFromFile(Path.GetFullPath(progId))
                : TlbResolver.Finder.TypeLibFromName(progId);

            if (lib == null) {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: Could not load the type library for '{progId}'.");
                Console.ResetColor();
                allSucceeded = false;
                continue;
            }

            primaryLibs.Add((progId, lib));
        }

        if (primaryLibs.Count == 0 || !allSucceeded)
            return false;
        return GenerateAdapter(primaryLibs, outputFolder, adapterName) && allSucceeded;
    }

    private static bool GenerateAdapter(IReadOnlyList<(string ProgId, ITypeLib TypeLib)> primaryLibs, string outputFolder, string adapterName) {
        string fullOutputFolder = Path.GetFullPath(outputFolder);
        Directory.CreateDirectory(fullOutputFolder);
        string rootNamespace = TypeLibDependencyScanner.SanitizeNamespace(adapterName);
        foreach (string staleFile in Directory.GetFiles(fullOutputFolder, $"Generated.UIA.{rootNamespace}.*.cs")) {
            File.Delete(staleFile);
        }

        List<DiscoveredTypeLib> allLibs = TypeLibDependencyScanner.Discover(primaryLibs, rootNamespace);

        Console.WriteLine($"Found {allLibs.Count} type libraries for '{rootNamespace}' ({allLibs.Count - primaryLibs.Count} external).");
        WriteRuntimeSources(fullOutputFolder);

        var builtWrappers = new List<UiaAdapterWrapper>(allLibs.Count);
        foreach (DiscoveredTypeLib current in allLibs) {
            var externalLibsForCurrent = new Dictionary<string, ITypeLib>(StringComparer.OrdinalIgnoreCase);
            foreach (DiscoveredTypeLib other in allLibs) {
                if (other.Guid != current.Guid) {
                    externalLibsForCurrent[other.Namespace] = other.TypeLib;
                }
            }

            Console.WriteLine($"Generating: {current.Namespace} -> Generated.UIA.{current.FilePrefix}.{{Enums,Wrap}}.cs");
            var comWrapper = new UiaAdapterWrapper(
                current.Namespace,
                current.TypeLib,
                externalLibsForCurrent,
                rootNamespace: rootNamespace,
                emitInfrastructure: false);

            builtWrappers.Add(comWrapper);
            CreateTemplateFile(comWrapper.EnumsBuilder, fullOutputFolder, current.FilePrefix, "Enums.cs");
            CreateTemplateFile(comWrapper.WrappersBuilder, fullOutputFolder, current.FilePrefix, "Wrap.cs");
        }

        Console.WriteLine($"Generating shared infrastructure: {rootNamespace} -> Generated.UIA.{rootNamespace}.Infra.cs");
        StringBuilder sharedInfra = UiaAdapterWrapper.BuildSharedInfrastructure(rootNamespace, builtWrappers);
        CreateTemplateFile(sharedInfra, fullOutputFolder, rootNamespace, "Infra.cs");

        Console.WriteLine($"Generating base objects: {rootNamespace} -> Generated.UIA.{rootNamespace}.BaseObjects.cs");
        StringBuilder baseObjects = UiaAdapterWrapper.BuildSharedBaseObjects(rootNamespace);
        CreateTemplateFile(baseObjects, fullOutputFolder, rootNamespace, "BaseObjects.cs");

        Console.WriteLine($"Generating type mappings: {rootNamespace} -> Generated.UIA.{rootNamespace}.TypeMappings.cs");
        StringBuilder typeMappings = UiaAdapterWrapper.BuildSharedTypeMappings(rootNamespace, builtWrappers);
        CreateTemplateFile(typeMappings, fullOutputFolder, rootNamespace, "TypeMappings.cs");
        CreateProjectFile(fullOutputFolder, $"{Path.GetFileName(fullOutputFolder)}.csproj");

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Successfully generated adapter '{rootNamespace}' in: {fullOutputFolder}");
        Console.ResetColor();
        return true;
    }

    static string LicenseHeader(string csFileName) {
        return $$"""
//------------------------------------------------------------------------------
// <copyright file="{{csFileName}}" company="Piotr Filipowicz">
//     Copyright (c) 2026 Piotr Filipowicz. All rights reserved.
//     Licensed under GNU LGPL v3 with Code Generation Exception.
// </copyright>
// <security>
//     Integrity Notice: Official commits, tags, and release artifacts of this
//     project are cryptographically signed.
// </security>
//------------------------------------------------------------------------------


""";

    }

    private static StreamReader GetStreamFromResource(string resourceName) {
        Assembly assembly = typeof(Program).Assembly;
        Stream stream = assembly.GetManifestResourceStream(resourceName)
           ?? throw new InvalidOperationException($"Embedded source not found: {resourceName}.");
        return new StreamReader(stream);
    }

    private static StreamReader GetStreamFromStringBuilder(StringBuilder sb) {
        MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
        return new StreamReader(stream);
    }

    private static void WriteGeneratedStream(StreamReader reader, string generatedName, string Header) {
        using TextWriter writer = File.CreateText(generatedName);
        writer.Write(Header);
        string? line;
        bool remarksRead = false;
        bool blockStart = false;
        bool blockEnd = false;
        while ((line = reader.ReadLine()) != null) {
            if (!remarksRead) { // remove any existing header comments from the embedded source
                if (line.StartsWith("//") || string.IsNullOrWhiteSpace(line))
                    continue;
                remarksRead = true;
            }
            if (!blockStart && line.StartsWith("#if")) { // skip first conditional compilation block in the embedded source
                blockStart = true;
                continue;
            }
            if (!blockEnd && blockStart) {
                if (line.StartsWith("#endif"))
                    blockEnd = true;
                continue;
            }
            writer.WriteLine(line);
        }
    }


    static void CreateTemplateFile(StringBuilder sb, string outputDirectory, string prefixName, string csName) {
        using var reader = GetStreamFromStringBuilder(sb);
        string fileName = $"Generated.UIA.{prefixName}.{csName}";
        WriteGeneratedStream(reader, $"{outputDirectory}\\{fileName}", LicenseHeader(fileName));
    }

    private static void CreateProjectFile(string outputDirectory, string projectName) {
        string project = """
                <Project Sdk="Microsoft.NET.Sdk">
                   	<PropertyGroup>
            		<TargetFramework>net10.0</TargetFramework>
            		<ImplicitUsings>enable</ImplicitUsings>
            		<Nullable>enable</Nullable>
            		<Platforms>x64</Platforms>
            		<EnableDefaultCompileItems>true</EnableDefaultCompileItems>
            		<AllowUnsafeBlocks>true</AllowUnsafeBlocks>
            		<IsAotCompatible>true</IsAotCompatible>
            		<EnableTrimAnalyzer>true</EnableTrimAnalyzer>
            		<EnableAotAnalyzer>true</EnableAotAnalyzer>
            		<DebugType>none</DebugType>
            	</PropertyGroup>
            	<!-- Bezpieczne i szybkie odtwarzanie struktury assets tylko w razie potrzeby -->
            	<Target Name="EnsureRestoreCheck" BeforeTargets="PrepareForBuild" Condition="!Exists('$(MSBuildProjectExtensionsPath)project.assets.json')">
            		<Message Text="Brak pliku assets.json - automatyczne przywracanie metadanych..." Importance="high" />
            		<Exec Command="dotnet restore &quot;$(MSBuildProjectFullPath)&quot;" />
            	</Target>
            </Project>
            
            """;
        string projectFilePath = Path.Combine(outputDirectory, projectName);
        if (File.Exists(projectFilePath)) {
            Console.WriteLine($"Project file '{projectName}' already exists in '{outputDirectory}'. Skipping creation.");
            return;
        }
        File.WriteAllText(projectFilePath, project);
    }

    private static void WriteRuntimeSources(string outputDirectory) {
        string[] RuntimeSourceNames = [
            "ComBehavior.cs",
            "ComContracts.cs",
            "ComStubOps.cs",
            "ComStubOps.Variants.cs"
        ];

        foreach (string fileName in RuntimeSourceNames) {
            string resourceName = $"UiaAdapter.Runtimes.{fileName}";
            string csFileName = $"Runtime.{fileName}";
            string generatedName = $"{outputDirectory}\\{csFileName}";
            using StreamReader reader = GetStreamFromResource(resourceName);
            WriteGeneratedStream(reader, generatedName, LicenseHeader(csFileName));

        }
    }
}