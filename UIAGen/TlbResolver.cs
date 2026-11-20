//------------------------------------------------------------------------------
// <copyright file="TlbResolver.cs" company="Piotr Filipowicz">
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
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

internal readonly record struct ResolvedTypeLibInfo(
    ITypeLib TypeLib,
    string FilePath,
    string RegistryLocation,
    string? ServerFilePath = null);

internal static class TlbResolver {
    private static string[] OrderVersions(string[] versions) => versions
        .OrderByDescending(value => Version.TryParse(value, out var version) ? version : new Version(0, 0))
        .ThenBy(value => value, StringComparer.Ordinal)
        .ToArray();

    private enum REGKIND {
        REGKIND_DEFAULT,
        REGKIND_REGISTER,
        REGKIND_NONE
    }

    private const uint LOAD_LIBRARY_AS_DATAFILE = 0x00000002;
    private static readonly IntPtr RT_TYPELIB = new(16);

    [DllImport("oleaut32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int LoadTypeLibEx(string szFile, REGKIND regkind, out ITypeLib pptlib);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);

    public static class Loader {
        private const uint LOAD_LIBRARY_AS_IMAGE_RESOURCE = 0x00000020;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr FindResource(IntPtr hModule, IntPtr lpName, IntPtr lpType);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LoadResource(IntPtr hModule, IntPtr hResInfo);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LockResource(IntPtr hResData);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SizeofResource(IntPtr hModule, IntPtr hResInfo);

        private delegate bool EnumResNameCallback(IntPtr hModule, IntPtr lpszType, IntPtr lpszName, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool EnumResourceNames(IntPtr hModule, IntPtr lpszType, EnumResNameCallback lpEnumFunc, IntPtr lParam);

        public static ITypeLib? LoadEmbeddedTypeLib(string filePath, int resourceIndex = 1) {
            IntPtr hModule = LoadLibraryEx(filePath, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE);
            if (hModule == IntPtr.Zero) {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"LoadLibraryEx failed for {filePath}. Win32 error: {err}");
            }

            string tempTlbPath = Path.Combine(
                Path.GetTempPath(),
                $"{Path.GetFileNameWithoutExtension(filePath)}_{resourceIndex}_{Guid.NewGuid():N}.tlb"
            );

            try {
                IntPtr RT_TYPELIB = (IntPtr)16;
                IntPtr resName = (IntPtr)resourceIndex;

                IntPtr hResInfo = FindResource(hModule, resName, RT_TYPELIB);
                if (hResInfo == IntPtr.Zero) {
                    int err = Marshal.GetLastWin32Error();
                    throw new InvalidOperationException($"TYPELIB resource #{resourceIndex} was not found in {filePath}. Win32 error: {err}");
                }

                IntPtr hResData = LoadResource(hModule, hResInfo);
                IntPtr pData = LockResource(hResData);
                uint size = SizeofResource(hModule, hResInfo);

                if (pData == IntPtr.Zero || size == 0) {
                    throw new InvalidOperationException($"The TYPELIB resource in {filePath} has a size of 0 bytes.");
                }

                byte[] buffer = new byte[size];
                Marshal.Copy(pData, buffer, 0, (int)size);
                File.WriteAllBytes(tempTlbPath, buffer);
                if (LoadTypeLibEx(tempTlbPath, REGKIND.REGKIND_NONE, out ITypeLib typeLib) == 0 && typeLib != null)
                    return typeLib;
                return null;
            }
            finally {
                FreeLibrary(hModule);
                File.Delete(tempTlbPath);
            }
        }

        public static ITypeLib? TypeLibFromFile(string filePath) {
            if (!File.Exists(filePath)) {
                throw new FileNotFoundException($"File not found: '{filePath}'", filePath);
            }

            if (LoadTypeLibEx(filePath, REGKIND.REGKIND_NONE, out ITypeLib directLib) == 0 && directLib != null)
                return directLib;

            List<string> rsids = GetEmbeddedTypeLibResourceIds(filePath);
            foreach (string rsid in rsids) {
                try {
                    if (LoadTypeLibEx($"{filePath}\\{rsid}", REGKIND.REGKIND_NONE, out ITypeLib indexedLib) == 0 && indexedLib != null)
                        return indexedLib;
                }
                catch { }
            }

            foreach (string rsid in rsids) {
                try {
                    var extractedLib = LoadEmbeddedTypeLib(filePath, Convert.ToInt32(rsid));
                    if (extractedLib != null)
                        return extractedLib;
                }
                catch { }
            }
            return null;
        }

        private static List<string> GetEmbeddedTypeLibResourceIds(string filePath) {
            List<string> resourceNames = new();
            IntPtr hModule = LoadLibraryEx(filePath, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE);
            if (hModule == IntPtr.Zero)
                return resourceNames;
            bool IS_INTRESOURCE(IntPtr value) => unchecked((ulong)value.ToInt64()) <= 65535;

            try {
                EnumResourceNames(hModule, RT_TYPELIB, (h, type, name, param) => {
                    if (IS_INTRESOURCE(name)) {
                        resourceNames.Add(name.ToInt64().ToString());
                    }
                    else {
                        string? strName = Marshal.PtrToStringUni(name);
                        if (!string.IsNullOrEmpty(strName)) {
                            resourceNames.Add(strName);
                        }
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            finally {
                FreeLibrary(hModule);
            }

            return resourceNames;
        }
    }

    public static class TypeLib {
        public static bool TryGetTypeLibFromInterface(Guid interfaceGuid, out Guid typeLibGuid, out string? version) {
            typeLibGuid = Guid.Empty;
            version = null;

            string guidText = interfaceGuid.ToString("B");
            string[] roots = [
                $@"Interface\{guidText}\TypeLib",
                $@"WOW6432Node\Interface\{guidText}\TypeLib"
            ];

            foreach (string root in roots) {
                try {
                    using var ifaceTlbKey = Registry.ClassesRoot.OpenSubKey(root);
                    if (ifaceTlbKey == null)
                        continue;

                    if (ifaceTlbKey.GetValue("") is string rawGuid && Guid.TryParse(rawGuid, out Guid parsedGuid)) {
                        typeLibGuid = parsedGuid;
                        version = ifaceTlbKey.GetValue("Version") as string;
                        return true;
                    }
                }
                catch { }
            }

            return false;
        }

        public static ITypeLib? GetFromGuid(Guid typeLibGuid, short majorVersion, short minorVersion) {
            string hexVer = $"{majorVersion:x}.{minorVersion:x}";
            string decVer = $"{majorVersion}.{minorVersion}";
            return TryGetFromGuidCore(typeLibGuid, [hexVer, decVer], pathFilter: null, reportSkip: null, out ResolvedTypeLibInfo info) ? info.TypeLib : null;
        }

        public static ITypeLib? GetFromGuid(Guid typeLibGuid, string? preferredVersion = null) =>
            TryGetFromGuidCore(typeLibGuid, [preferredVersion], pathFilter: null, reportSkip: null, out ResolvedTypeLibInfo info) ? info.TypeLib : null;

        public static bool TryResolveFromGuid(
            Guid typeLibGuid,
            string? preferredVersion,
            out ResolvedTypeLibInfo info,
            Func<string, bool>? pathFilter = null,
            Action<string>? reportSkip = null) =>
            TryGetFromGuidCore(typeLibGuid, [preferredVersion], pathFilter, reportSkip, out info);

        private static bool TryGetFromGuidCore(
            Guid typeLibGuid,
            string?[] preferredVersions,
            Func<string, bool>? pathFilter,
            Action<string>? reportSkip,
            out ResolvedTypeLibInfo info) {

            info = default;
            RegistryView[] views = Environment.Is64BitOperatingSystem
                ? [RegistryView.Registry64, RegistryView.Registry32]
                : [RegistryView.Registry32];
            foreach (RegistryView view in views) {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
                if (TryGetFromRegistry(root, view, typeLibGuid, preferredVersions, pathFilter, reportSkip, out info))
                    return true;
            }
            return false;
        }

        internal static bool TryGetFromRegistry(
            RegistryKey root,
            RegistryView view,
            Guid typeLibGuid,
            string?[] preferredVersions,
            Func<string, bool>? pathFilter,
            Action<string>? reportSkip,
            out ResolvedTypeLibInfo info) {

            info = default;
            string viewLabel = view == RegistryView.Registry32 && Environment.Is64BitOperatingSystem ? " (32-bit)" : string.Empty;
            try {
                using var tlbKey = root.OpenSubKey($@"TypeLib\{typeLibGuid:B}");
                if (tlbKey == null)
                    return false;

                string[] versions = preferredVersions
                    .Where(version => !string.IsNullOrWhiteSpace(version))
                    .Select(version => version!)
                    .Concat(OrderVersions(tlbKey.GetSubKeyNames()))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                string[] platforms = Environment.Is64BitProcess ? ["Win64", "Win32"] : ["Win32", "Win64"];
                foreach (string version in versions) {
                    using var versionKey = tlbKey.OpenSubKey(version);
                    if (versionKey == null)
                        continue;
                    foreach (string locale in versionKey.GetSubKeyNames().OrderBy(locale => locale, StringComparer.OrdinalIgnoreCase)) {
                        if (!uint.TryParse(locale, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out _))
                            continue;
                        foreach (string platform in platforms) {
                            using var pathKey = versionKey.OpenSubKey($@"{locale}\{platform}");
                            if (pathKey?.GetValue("") is not string rawPath || string.IsNullOrWhiteSpace(rawPath))
                                continue;

                            string cleanFilePath = Finder.CleanPath(rawPath);
                            if (pathFilter != null && !pathFilter(cleanFilePath))
                                continue;

                            // Preserve an optional TYPELIB resource suffix (module.dll\2).
                            string loadPath = Environment.ExpandEnvironmentVariables(rawPath.Trim()).Trim('"');
                            if (LoadTypeLibEx(loadPath, REGKIND.REGKIND_NONE, out ITypeLib loaded) == 0 && loaded != null) {
                                string regLocation = $@"HKCR\TypeLib\{typeLibGuid:B}\{version}\{locale}\{platform}{viewLabel}";
                                info = new ResolvedTypeLibInfo(loaded, cleanFilePath, regLocation);
                                return true;
                            }
                            else {
                                reportSkip?.Invoke($"  [SKIP] {pathKey.Name}: could not load type library.");
                            }
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) {
                reportSkip?.Invoke($"  [SKIP] {root.Name}\\TypeLib\\{typeLibGuid:B}: {exception.Message}");
            }
            return false;
        }
    }

    public static class Finder {
        public static ITypeLib? TypeLibFromName(string name) {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ITypeLib? library = TypeLibFromProgId(name);
            if (library != null)
                return library;

            var matches = new List<(Guid Guid, ITypeLib Library)>();
            foreach (var registrations in UiaAdapter.RegistryTypeLibScanner.Find(name, Console.Error.WriteLine).GroupBy(entry => entry.Guid)) {
                string? version = OrderVersions(registrations.Select(entry => entry.Version).OfType<string>().ToArray()).FirstOrDefault();
                ITypeLib? candidate = TypeLib.GetFromGuid(registrations.Key, version);
                if (candidate == null) {
                    Console.Error.WriteLine($"  [SKIP] {registrations.First().Source}: could not load type library {registrations.Key:B}.");
                    continue;
                }
                matches.Add((registrations.Key, candidate));
            }
            if (matches.Count > 1)
                throw new InvalidOperationException($"Ambiguous name '{name}'. Matching type libraries: {string.Join(", ", matches.Select(match => match.Guid.ToString("B")))}. Specify a type library GUID or file path.");
            return matches.Count == 1 ? matches[0].Library : null;
        }

        private static readonly Lazy<List<(string Guid, string Name)>> CachedTypeLibDescriptions = new(LoadTypeLibDescriptions);

        private static List<(string Guid, string Name)> LoadTypeLibDescriptions() {
            var entries = new List<(string Guid, string Name)>();
            try {
                using var typeLibsKey = Registry.ClassesRoot.OpenSubKey("TypeLib");
                if (typeLibsKey == null)
                    return entries;

                foreach (string guid in typeLibsKey.GetSubKeyNames()) {
                    try {
                        using var guidKey = typeLibsKey.OpenSubKey(guid);
                        if (guidKey == null)
                            continue;

                        foreach (string ver in guidKey.GetSubKeyNames()) {
                            try {
                                using var verKey = guidKey.OpenSubKey(ver);
                                if (verKey?.GetValue("") is string name && !string.IsNullOrWhiteSpace(name)) {
                                    entries.Add((guid, name));
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return entries;
        }

        public static ITypeLib? TypeLibFromProgId(string progId) =>
            TryResolveFromProgId(progId, out ResolvedTypeLibInfo info) ? info.TypeLib : null;

        public static bool TryResolveFromProgId(
            string progId,
            out ResolvedTypeLibInfo info,
            Func<string, bool>? pathFilter = null,
            Action<string>? reportSkip = null) {

            info = default;
            try {
                using var progKey = Registry.ClassesRoot.OpenSubKey($@"{progId}\CLSID");
                string? clsidStr = progKey?.GetValue("") as string;
                if (string.IsNullOrEmpty(clsidStr))
                    return false;

                string clsidSubPath = $@"CLSID\{clsidStr}";
                using var clsidKey64 = Registry.ClassesRoot.OpenSubKey(clsidSubPath);
                using var clsidKey32 = clsidKey64 == null ? Registry.ClassesRoot.OpenSubKey($@"WOW6432Node\{clsidSubPath}") : null;
                RegistryKey? clsidKey = clsidKey64 ?? clsidKey32;
                if (clsidKey == null)
                    return false;

                string clsidRootLabel = clsidKey64 != null ? $@"HKCR\CLSID\{clsidStr}" : $@"HKCR\WOW6432Node\CLSID\{clsidStr}";

                string? serverSubKey = null;
                string? modulePath = GetServerPath(clsidKey, "InProcServer32");
                if (!string.IsNullOrEmpty(modulePath)) {
                    serverSubKey = "InProcServer32";
                }
                else {
                    modulePath = GetServerPath(clsidKey, "LocalServer32");
                    if (!string.IsNullOrEmpty(modulePath))
                        serverSubKey = "LocalServer32";
                }

                bool serverMatchesFilter = pathFilter == null || (!string.IsNullOrEmpty(modulePath) && pathFilter(modulePath));
                string? fullServerPath = (!string.IsNullOrEmpty(modulePath) && File.Exists(modulePath))
                    ? Path.GetFullPath(modulePath)
                    : null;

                // 1. Prefer an explicit CLSID\TypeLib registration first so multi-resource binaries (like acad.exe)
                //    resolve to the exact TypeLib GUID & resource index instead of grabbing resource #1 blindly.
                string? typeLibGuidStr = clsidKey.OpenSubKey("TypeLib")?.GetValue("") as string;
                string? preferredVersion = clsidKey.OpenSubKey("Version")?.GetValue("") as string;
                bool fromClsidTypeLibKey = !string.IsNullOrEmpty(typeLibGuidStr);

                if (string.IsNullOrEmpty(typeLibGuidStr))
                    typeLibGuidStr = TypeLibGuidFromProgId(progId);

                if (!string.IsNullOrEmpty(typeLibGuidStr) && Guid.TryParse(typeLibGuidStr, out Guid parsedTlbGuid) && parsedTlbGuid != Guid.Empty) {
                    // If the COM server binary already matches the folder filter (e.g. acad.exe in C:\Program Files\Autodesk),
                    // allow resolving its TypeLib even if the .tlb resides in Common Files\Autodesk Shared.
                    Func<string, bool>? effectiveTypeLibFilter = serverMatchesFilter ? null : pathFilter;

                    if (TypeLib.TryResolveFromGuid(parsedTlbGuid, preferredVersion, out ResolvedTypeLibInfo guidInfo, effectiveTypeLibFilter, reportSkip)) {
                        string origin = fromClsidTypeLibKey
                            ? $@"HKCR\{progId} -> {clsidRootLabel}\TypeLib -> {guidInfo.RegistryLocation}"
                            : $@"HKCR\{progId} (description match) -> {guidInfo.RegistryLocation}";
                        info = guidInfo with {
                            RegistryLocation = origin,
                            ServerFilePath = fullServerPath
                        };
                        return true;
                    }
                }

                // 2. Fallback: extract embedded TYPELIB directly from InProcServer32 / LocalServer32 binary.
                if (fullServerPath != null && serverMatchesFilter) {
                    try {
                        ITypeLib? tlb = Loader.TypeLibFromFile(fullServerPath);
                        if (tlb != null) {
                            info = new ResolvedTypeLibInfo(
                                tlb,
                                fullServerPath,
                                $@"HKCR\{progId} -> {clsidRootLabel}\{serverSubKey}",
                                fullServerPath);
                            return true;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return false;
        }

        private static string? TypeLibGuidFromProgId(string progId) {
            string baseName = progId.Split('.')[0];
            if (string.IsNullOrWhiteSpace(baseName))
                return null;

            string prefix = $"Microsoft {baseName}";
            foreach (var (guid, name) in CachedTypeLibDescriptions.Value) {
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
                    return guid;
                }
            }

            return null;
        }

        private static string? GetServerPath(RegistryKey parentKey, string subKeyName) {
            try {
                using var key = parentKey.OpenSubKey(subKeyName);
                if (key?.GetValue("") is string rawPath) {
                    return CleanPath(rawPath);
                }
            }
            catch { }
            return null;
        }

        public static string CleanPath(string rawPath) {
            if (string.IsNullOrWhiteSpace(rawPath))
                return string.Empty;

            string path = Environment.ExpandEnvironmentVariables(rawPath.Trim());

            if (path.StartsWith("\"")) {
                int secondQuote = path.IndexOf('"', 1);
                if (secondQuote > 1) {
                    return path.Substring(1, secondQuote - 1);
                }
            }

            int flagIndex = path.IndexOf(" /", StringComparison.Ordinal);
            if (flagIndex == -1)
                flagIndex = path.IndexOf(" -", StringComparison.Ordinal);

            if (flagIndex > 0) {
                path = path.Substring(0, flagIndex);
            }

            int lastSlash = path.LastIndexOf('\\');
            if (lastSlash > 0 && lastSlash < path.Length - 1) {
                if (int.TryParse(path.Substring(lastSlash + 1), out _)) {
                    path = path.Substring(0, lastSlash);
                }
            }

            return path.Trim();
        }
    }
}