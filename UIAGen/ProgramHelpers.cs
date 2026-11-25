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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace UiaAdapter;

internal static class ProgramHelpers {

    private sealed record DiscoveredServerEntry(
        Guid TypeLibGuid,
        string ProgId,
        ITypeLib TypeLib,
        string TlbFilePath,
        string RegistryLocation,
        string? ServerFilePath);

    internal static bool GenerateAllAdapters(
        string? outputFolder,
        string? combinedAdapterName = null,
        IReadOnlyList<string>? folderFilters = null,
        bool listOnly = false,
        bool compile = false) {

        if (!listOnly)
            ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);

        string? fullOutputFolder = string.IsNullOrWhiteSpace(outputFolder) ? null : Path.GetFullPath(outputFolder);
        string[] normalizedFilters = NormalizeFolderFilters(folderFilters);
        bool hasFilter = normalizedFilters.Length > 0;
        Func<string, bool>? pathFilter = hasFilter
            ? path => MatchesFolderFilter(path, normalizedFilters)
            : null;
        Action<string>? reportSkip = (!hasFilter && !listOnly) ? Console.Error.WriteLine : null;

        if (hasFilter) {
            Console.WriteLine($"Filtering registered COM servers to {normalizedFilters.Length} folder(s):");
            foreach (string filter in normalizedFilters)
                Console.WriteLine($"  - {filter}");
        }

        Console.WriteLine("Scanning the HKCR registry: ProgID, TypeLib, and Interface (32-bit and 64-bit views)...");
        string[] allProgIds = CollectRegisteredProgIds();

        Console.WriteLine($"Found {allProgIds.Length} ProgIDs with associated CLSIDs. Resolving type libraries...");

        var uniqueLibs = new Dictionary<Guid, DiscoveredServerEntry>();
        int progIdsWithTypeLib = 0;

        foreach (string progId in allProgIds) {
            try {
                if (!TlbResolver.Finder.TryResolveFromProgId(progId, out ResolvedTypeLibInfo resolved, pathFilter, reportSkip))
                    continue;

                progIdsWithTypeLib++;
                TYPELIBATTR attr = TypeInfoDescriptor.LibraryAttributes(resolved.TypeLib);
                if (attr.guid == Guid.Empty)
                    continue;

                if (!uniqueLibs.TryGetValue(attr.guid, out var existing)) {
                    uniqueLibs[attr.guid] = new DiscoveredServerEntry(
                        attr.guid,
                        progId,
                        resolved.TypeLib,
                        resolved.FilePath,
                        resolved.RegistryLocation,
                        resolved.ServerFilePath);
                }
                else if (IsBetterRepresentative(progId, existing.ProgId)) {
                    uniqueLibs[attr.guid] = new DiscoveredServerEntry(
                        attr.guid,
                        progId,
                        resolved.TypeLib,
                        resolved.FilePath,
                        resolved.RegistryLocation,
                        resolved.ServerFilePath ?? existing.ServerFilePath);
                }
                else if (existing.ServerFilePath == null && resolved.ServerFilePath != null) {
                    uniqueLibs[attr.guid] = existing with { ServerFilePath = resolved.ServerFilePath };
                }
            }
            catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException or System.Security.SecurityException) {
                reportSkip?.Invoke($"  [SKIP] ProgID {progId}: {exception.Message}");
            }
        }

        Console.WriteLine($"Resolved {progIdsWithTypeLib} ProgIDs to {uniqueLibs.Count} unique type libraries.");
        IReadOnlyList<RegisteredTypeLib> registrations = RegistryTypeLibScanner.Collect(message => reportSkip?.Invoke(message));
        int additionalLibraries = 0;
        foreach (RegisteredTypeLib registration in registrations) {
            if (uniqueLibs.ContainsKey(registration.Guid))
                continue;
            try {
                if (!TlbResolver.TypeLib.TryResolveFromGuid(registration.Guid, registration.Version, out ResolvedTypeLibInfo resolved, pathFilter, reportSkip)) {
                    reportSkip?.Invoke($"  [SKIP] {registration.Source}, version {registration.Version}: could not load type library {registration.Guid:B}.");
                    continue;
                }
                TYPELIBATTR attr = TypeInfoDescriptor.LibraryAttributes(resolved.TypeLib);
                if (attr.guid != registration.Guid) {
                    reportSkip?.Invoke($"  [SKIP] {registration.Source}: the file contains a different type library GUID ({attr.guid:B}).");
                    continue;
                }
                string combinedLocation = $"{registration.Source} -> {resolved.RegistryLocation}";
                if (uniqueLibs.TryAdd(attr.guid, new DiscoveredServerEntry(
                    attr.guid,
                    registration.Source,
                    resolved.TypeLib,
                    resolved.FilePath,
                    combinedLocation,
                    resolved.ServerFilePath))) {
                    additionalLibraries++;
                }
            }
            catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException or System.Security.SecurityException) {
                reportSkip?.Invoke($"  [SKIP] {registration.Source}: {exception.Message}");
            }
        }
        Console.WriteLine($"TypeLib/Interface: {registrations.Count} registrations, {additionalLibraries} libraries added. Total matched: {uniqueLibs.Count}.");
        Console.WriteLine();

        List<DiscoveredServerEntry> filteredEntries = uniqueLibs.Values
            .Where(entry => MatchesFolderFilter(entry.TlbFilePath, normalizedFilters) ||
                            (!string.IsNullOrEmpty(entry.ServerFilePath) && MatchesFolderFilter(entry.ServerFilePath, normalizedFilters)))
            .ToList();

        if (filteredEntries.Count == 0) {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("No registered COM type libraries matched the specified criteria.");
            Console.ResetColor();
            return listOnly;
        }

        var usedAdapterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plannedAdapters = new List<(string AdapterName, DiscoveredServerEntry Entry)>(filteredEntries.Count);

        foreach (DiscoveredServerEntry entry in filteredEntries.OrderBy(e => ResolveDefaultAdapterName(e.TypeLib, e.ProgId), StringComparer.OrdinalIgnoreCase)) {
            string baseAdapterName = ResolveDefaultAdapterName(entry.TypeLib, entry.ProgId);
            string adapterName = baseAdapterName;
            int suffix = 2;
            while (!usedAdapterNames.Add(adapterName)) {
                adapterName = $"{baseAdapterName}_{suffix++}";
            }
            plannedAdapters.Add((adapterName, entry));
        }

        if (listOnly) {
            PrintDiscoveredServersList(plannedAdapters);
            return true;
        }

        Directory.CreateDirectory(fullOutputFolder!);

        if (!string.IsNullOrWhiteSpace(combinedAdapterName)) {
            var primaryLibs = plannedAdapters
                .Select(item => (item.Entry.ProgId, item.Entry.TypeLib))
                .ToArray();
            return GenerateAdapter(primaryLibs, fullOutputFolder!, combinedAdapterName, compile, fullOutputFolder!, listOnly: false);
        }

        int succeeded = 0;
        var failures = new List<(string ProgId, string AdapterName, string Error)>();

        foreach (var (adapterName, entry) in plannedAdapters) {
            string adapterFolder = Path.Combine(fullOutputFolder!, adapterName);
            try {
                if (GenerateAdapter(new (string ProgId, ITypeLib TypeLib)[] { (entry.ProgId, entry.TypeLib) }, adapterFolder, adapterName, compile, fullOutputFolder!, listOnly: false)) {
                    succeeded++;
                }
                else {
                    failures.Add((entry.ProgId, adapterName, "GenerateAdapter returned false"));
                }
            }
            catch (Exception ex) {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Failed to generate '{adapterName}' ({entry.ProgId}): {ex.Message}");
                Console.ResetColor();
                failures.Add((entry.ProgId, adapterName, ex.Message));
            }
        }

        Console.ForegroundColor = failures.Count == 0 ? ConsoleColor.Green : ConsoleColor.Yellow;
        Console.WriteLine($"-all summary: generated {succeeded}/{plannedAdapters.Count} adapters in '{fullOutputFolder}' (failures: {failures.Count}).");
        Console.ResetColor();

        foreach (var (progId, adapterName, error) in failures) {
            Console.Error.WriteLine($"  [FAIL] {adapterName} ({progId}): {error}");
        }

        return failures.Count == 0;
    }

    private static void PrintDiscoveredServersList(IReadOnlyList<(string AdapterName, DiscoveredServerEntry Entry)> plannedAdapters) {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"Discovered COM servers ({plannedAdapters.Count}):");
        Console.ResetColor();
        Console.WriteLine(new string('-', 100));

        foreach (var (adapterName, entry) in plannedAdapters) {
            string serverIdentity = entry.ProgId.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase)
                ? adapterName
                : $"{adapterName} ({entry.ProgId})";

            TYPELIBATTR attr = TypeInfoDescriptor.LibraryAttributes(entry.TypeLib);
            var (description, _, _) = TypeLibDependencyScanner.ResolveTypeLibRegistryDetails(entry.TypeLib, entry.TypeLibGuid, null, attr.wMajorVerNum, attr.wMinorVerNum);

            Console.WriteLine($"Class name  : {serverIdentity}");
            if (!string.IsNullOrWhiteSpace(description))
                Console.WriteLine($"Description : {description}");
            Console.WriteLine($"TLB path    : {entry.TlbFilePath}");
            if (!string.IsNullOrEmpty(entry.ServerFilePath) &&
                !string.Equals(entry.ServerFilePath, entry.TlbFilePath, StringComparison.OrdinalIgnoreCase)) {
                Console.WriteLine($"Server bin  : {entry.ServerFilePath}");
            }
            Console.WriteLine($"Registry    : {entry.RegistryLocation}");
            Console.WriteLine($"GUID        : {entry.TypeLibGuid:B}");
            Console.WriteLine(new string('-', 100));
        }
    }

    private static string[] NormalizeFolderFilters(IReadOnlyList<string>? folderFilters) {
        if (folderFilters == null || folderFilters.Count == 0)
            return [];

        var result = new List<string>(folderFilters.Count);
        foreach (string rawFolder in folderFilters) {
            if (string.IsNullOrWhiteSpace(rawFolder))
                continue;

            string expanded = Environment.ExpandEnvironmentVariables(rawFolder.Trim()).Trim('"');
            string fullPath = Path.GetFullPath(expanded);
            if (!fullPath.EndsWith(Path.DirectorySeparatorChar) && !fullPath.EndsWith(Path.AltDirectorySeparatorChar)) {
                fullPath += Path.DirectorySeparatorChar;
            }
            result.Add(fullPath);
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool MatchesFolderFilter(string tlbFilePath, string[] normalizedFolderFilters) {
        if (normalizedFolderFilters.Length == 0)
            return true;

        if (string.IsNullOrWhiteSpace(tlbFilePath))
            return false;

        string fullFilePath;
        try {
            fullFilePath = Path.GetFullPath(tlbFilePath);
        }
        catch {
            return false;
        }

        string? directory = Path.GetDirectoryName(fullFilePath);
        if (string.IsNullOrEmpty(directory))
            return false;

        if (!directory.EndsWith(Path.DirectorySeparatorChar) && !directory.EndsWith(Path.AltDirectorySeparatorChar)) {
            directory += Path.DirectorySeparatorChar;
        }

        foreach (string filterDir in normalizedFolderFilters) {
            if (directory.StartsWith(filterDir, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
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

    internal static bool GenerateAdapter(string[] progIds, string? outputFolder, string adapterName, bool compile = false, bool listOnly = false) {
        if (!listOnly)
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

        string? fullOutputFolder = string.IsNullOrWhiteSpace(outputFolder) ? null : Path.GetFullPath(outputFolder);
        return GenerateAdapter(primaryLibs, fullOutputFolder, adapterName, compile, fullOutputFolder, listOnly) && allSucceeded;
    }

    private static bool GenerateAdapter(
        IReadOnlyList<(string ProgId, ITypeLib TypeLib)> primaryLibs,
        string? outputFolder,
        string adapterName,
        bool compile = false,
        string? dllDestinationFolder = null,
        bool listOnly = false) {

        string rootNamespace = TypeLibDependencyScanner.SanitizeNamespace(adapterName);
        List<DiscoveredTypeLib> allLibs = TypeLibDependencyScanner.Discover(primaryLibs, rootNamespace);

        string externalPrefix = $"{rootNamespace}.";
        string[] externalNames = allLibs
            .Where(lib => lib.Namespace.StartsWith(externalPrefix, StringComparison.Ordinal))
            .Select(lib => lib.Namespace[externalPrefix.Length..])
            .ToArray();

        string externalSummary = externalNames.Length > 0
            ? $"{externalNames.Length} external - {string.Join(", ", externalNames)}"
            : "0 external";

        Console.WriteLine($"Found {allLibs.Count} type libraries for '{rootNamespace}' ({externalSummary}).");

        if (listOnly) {
            Console.WriteLine(new string('-', 100));
            foreach (DiscoveredTypeLib lib in allLibs) {
                string docName = string.Empty;
                try {
                    lib.TypeLib.GetDocumentation(-1, out docName, out _, out _, out _);
                }
                catch { }

                Console.WriteLine($"Namespace   : {lib.Namespace}");
                if (!string.IsNullOrWhiteSpace(lib.Description))
                    Console.WriteLine($"Description : {lib.Description}");
                if (!string.IsNullOrWhiteSpace(docName))
                    Console.WriteLine($"Library     : {docName}");
                if (!string.IsNullOrWhiteSpace(lib.ProgId))
                    Console.WriteLine($"ProgId      : {lib.ProgId}");
                if (!string.IsNullOrWhiteSpace(lib.TlbFilePath))
                    Console.WriteLine($"TLB path    : {lib.TlbFilePath}");
                if (!string.IsNullOrWhiteSpace(lib.RegistryLocation))
                    Console.WriteLine($"Registry    : {lib.RegistryLocation}");
                Console.WriteLine($"GUID        : {lib.Guid:B}");
                Console.WriteLine(new string('-', 100));
            }
            return true;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);
        string fullOutputFolder = Path.GetFullPath(outputFolder);
        Directory.CreateDirectory(fullOutputFolder);
        foreach (string staleFile in Directory.GetFiles(fullOutputFolder, $"Generated.UIA.{rootNamespace}.*.cs")) {
            File.Delete(staleFile);
        }

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

        string projectName = $"{Path.GetFileName(fullOutputFolder)}.csproj";
        string projectFilePath = CreateProjectFile(fullOutputFolder, projectName);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Successfully generated adapter '{rootNamespace}' in: {fullOutputFolder}");
        Console.ResetColor();

        if (compile) {
            string targetDllFolder = !string.IsNullOrWhiteSpace(dllDestinationFolder)
                ? Path.GetFullPath(dllDestinationFolder)
                : fullOutputFolder;
            if (!CompileProject(projectFilePath, targetDllFolder))
                return false;
        }
        else {
            Console.WriteLine();
        }

        return true;
    }

    private static bool CompileProject(string projectFilePath, string destinationFolder) {
        string projectDir = Path.GetDirectoryName(projectFilePath)
            ?? throw new InvalidOperationException($"Cannot determine project directory for '{projectFilePath}'.");
        string assemblyName = Path.GetFileNameWithoutExtension(projectFilePath);

        if (!RunDotnetCommand(projectDir, $"restore \"{projectFilePath}\" /nodeReuse:false", out string restoreOut, out string restoreErr, out int restoreExit)) {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Failed to restore adapter '{assemblyName}' (exit code {restoreExit}):");
            Console.ResetColor();
            if (!string.IsNullOrWhiteSpace(restoreOut))
                Console.Error.WriteLine(restoreOut.TrimEnd());
            if (!string.IsNullOrWhiteSpace(restoreErr))
                Console.Error.WriteLine(restoreErr.TrimEnd());
            Console.WriteLine();
            return false;
        }

        if (!RunDotnetCommand(projectDir, $"build \"{projectFilePath}\" -c Release --no-restore -p:UseSharedCompilation=false /nodeReuse:false", out string buildOut, out string buildErr, out int buildExit)) {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Failed to compile adapter '{assemblyName}.dll' (exit code {buildExit}):");
            Console.ResetColor();
            if (!string.IsNullOrWhiteSpace(buildOut))
                Console.Error.WriteLine(buildOut.TrimEnd());
            if (!string.IsNullOrWhiteSpace(buildErr))
                Console.Error.WriteLine(buildErr.TrimEnd());
            Console.WriteLine();
            return false;
        }

        string builtDllPath = Path.Combine(projectDir, "bin", "Release", "net10.0", $"{assemblyName}.dll");
        if (!File.Exists(builtDllPath)) {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Compiled assembly not found at expected path: '{builtDllPath}'.");
            Console.ResetColor();
            Console.WriteLine();
            return false;
        }

        Directory.CreateDirectory(destinationFolder);
        string destinationDllPath = Path.Combine(destinationFolder, $"{assemblyName}.dll");
        File.Copy(builtDllPath, destinationDllPath, overwrite: true);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Successfully compiled adapter '{assemblyName}.dll' in: {destinationFolder}");
        Console.ResetColor();
        Console.WriteLine();
        return true;
    }

    private static bool RunDotnetCommand(string workingDirectory, string arguments, out string stdOut, out string stdErr, out int exitCode) {
        var startInfo = new ProcessStartInfo {
            FileName = "dotnet",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1";

        using Process? process = Process.Start(startInfo);
        if (process == null) {
            stdOut = string.Empty;
            stdErr = $"Failed to start 'dotnet {arguments}'.";
            exitCode = -1;
            return false;
        }

        stdOut = process.StandardOutput.ReadToEnd();
        stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        exitCode = process.ExitCode;
        return exitCode == 0;
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

    private static void WriteGeneratedStream(StreamReader reader, string generatedName, string Header, string? eol) {
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
            if (!blockStart && line.StartsWith("#if UIA_ADAPTER_TEMPLATE")) { // skip first conditional compilation block in the embedded source
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
        if(eol != null)
            writer.WriteLine(eol);
    }


    static void CreateTemplateFile(StringBuilder sb, string outputDirectory, string prefixName, string csName) {
        using var reader = GetStreamFromStringBuilder(sb);
        string fileName = $"Generated.UIA.{prefixName}.{csName}";
        WriteGeneratedStream(reader, $"{outputDirectory}\\{fileName}", LicenseHeader(fileName), "}");
    }

    private static string CreateProjectFile(string outputDirectory, string projectName) {
        string project = """
            <Project Sdk="Microsoft.NET.Sdk">
            	<PropertyGroup>
            		<TargetFrameworks>net10.0;netstandard2.0</TargetFrameworks>
            		<ImplicitUsings>enable</ImplicitUsings>
            		<PlatformTarget>x64</PlatformTarget>
            		<Platforms>x64</Platforms>
            		<AllowUnsafeBlocks>true</AllowUnsafeBlocks>
            		<LangVersion>12.0</LangVersion>
            	</PropertyGroup>
            	<PropertyGroup Condition="'$(TargetFramework)' != 'netstandard2.0'">
            		<Nullable>enable</Nullable>
            		<EnableDefaultCompileItems>true</EnableDefaultCompileItems>
            		<IsAotCompatible>true</IsAotCompatible>
            		<EnableTrimAnalyzer>true</EnableTrimAnalyzer>
            		<EnableAotAnalyzer>true</EnableAotAnalyzer>
            		<DebugType>full</DebugType>
            	</PropertyGroup>
            </Project>
                                    
            """;
        string projectFilePath = Path.Combine(outputDirectory, projectName);
        if (File.Exists(projectFilePath)) {
            Console.WriteLine($"Project file '{projectName}' already exists in '{outputDirectory}'. Skipping creation.");
            return projectFilePath;
        }
        File.WriteAllText(projectFilePath, project);
        return projectFilePath;
    }

    private static void WriteRuntimeSources(string outputDirectory) {
        string[] RuntimeSourceNames = [
            "Polyfill.cs",
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
            WriteGeneratedStream(reader, generatedName, LicenseHeader(csFileName), null);

        }
    }
}