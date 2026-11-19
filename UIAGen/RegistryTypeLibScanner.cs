//------------------------------------------------------------------------------
// <copyright file="RegistryTypeLibScanner.cs" company="Piotr Filipowicz">
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
using System.Security;

namespace UiaAdapter;

internal readonly record struct RegisteredTypeLib(Guid Guid, string? Version, string Source);

internal static class RegistryTypeLibScanner {
    internal static IReadOnlyList<RegisteredTypeLib> Collect(Action<string> report, string? searchName = null) {
        var result = new List<RegisteredTypeLib>();
        RegistryView[] views = Environment.Is64BitOperatingSystem
            ? [RegistryView.Registry64, RegistryView.Registry32]
            : [RegistryView.Registry32];
        foreach (RegistryView view in views) {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
            result.AddRange(Collect(root, report, searchName));
        }
        return result.DistinctBy(entry => (entry.Guid, entry.Version)).ToArray();
    }

    internal static IReadOnlyList<RegisteredTypeLib> Find(string name, Action<string> report) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Collect(report, name);
    }

    internal static IReadOnlyList<RegisteredTypeLib> Collect(RegistryKey root, Action<string> report, string? searchName = null) {
        var result = new List<RegisteredTypeLib>();
        bool Matches(string? value) => string.Equals(value, searchName, StringComparison.OrdinalIgnoreCase);
        ReadBranch("TypeLib", key => {
            foreach (string name in key.GetSubKeyNames().OrderBy(name => name, StringComparer.OrdinalIgnoreCase)) {
                if (!Guid.TryParse(name, out Guid guid) || guid == Guid.Empty)
                    continue;
                ReadEntry(key, name, library => {
                    if (searchName == null || Matches(name) || Matches(library.GetValue("") as string)) {
                        result.Add(new(guid, null, library.Name));
                        return;
                    }
                    foreach (string version in library.GetSubKeyNames()) {
                        ReadEntry(library, version, versionKey => {
                            if (Matches(versionKey.GetValue("") as string))
                                result.Add(new(guid, version, versionKey.Name));
                        });
                    }
                });
            }
        });
        ReadReferences("Interface");
        if (searchName != null)
            ReadReferences("CLSID");

        void ReadReferences(string branch) {
            ReadBranch(branch, key => {
                foreach (string name in key.GetSubKeyNames().OrderBy(name => name, StringComparer.OrdinalIgnoreCase)) {
                    if (!Guid.TryParse(name, out _))
                        continue;
                    ReadEntry(key, name, entry => {
                        if (searchName != null && !Matches(name) && !Matches(entry.GetValue("") as string))
                            return;
                        ReadEntry(entry, "TypeLib", library => {
                            if (library.GetValue("") is string value && Guid.TryParse(value, out Guid guid) && guid != Guid.Empty)
                                result.Add(new(guid, library.GetValue("Version") as string, library.Name));
                        });
                    });
                }
            });
        }
        return result.DistinctBy(entry => (entry.Guid, entry.Version)).ToArray();

        void ReadBranch(string name, Action<RegistryKey> read) => ReadEntry(root, name, read);

        void ReadEntry(RegistryKey parent, string name, Action<RegistryKey> read) {
            try {
                using var key = parent.OpenSubKey(name);
                if (key != null)
                    read(key);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException or IOException) {
                report($"Could not read {parent.Name}\\{name}: {exception.Message}");
            }
        }
    }
}
