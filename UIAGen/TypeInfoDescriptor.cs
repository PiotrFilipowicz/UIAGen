//------------------------------------------------------------------------------
// <copyright file="TypeInfoDescriptor.cs" company="Piotr Filipowicz">
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
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace UiaAdapter;

/// <summary>Owns a descriptor borrowed from ITypeInfo until the current scope ends.</summary>
internal sealed class TypeInfoDescriptor : IDisposable {
    private readonly Action<IntPtr> _release;
    internal IntPtr Pointer { get; private set; }

    private TypeInfoDescriptor(IntPtr pointer, Action<IntPtr> release) {
        Pointer = pointer;
        _release = release;
    }

    internal static TypeInfoDescriptor TypeAttributes(ITypeInfo source) {
        source.GetTypeAttr(out var pointer);
        return new TypeInfoDescriptor(pointer, source.ReleaseTypeAttr);
    }

    internal static TypeInfoDescriptor Function(ITypeInfo source, int index) {
        source.GetFuncDesc(index, out var pointer);
        return new TypeInfoDescriptor(pointer, source.ReleaseFuncDesc);
    }

    internal static TypeInfoDescriptor Variable(ITypeInfo source, int index) {
        source.GetVarDesc(index, out var pointer);
        return new TypeInfoDescriptor(pointer, source.ReleaseVarDesc);
    }

    internal static TYPELIBATTR LibraryAttributes(ITypeLib source) {
        source.GetLibAttr(out var pointer);
        try { return Marshal.PtrToStructure<TYPELIBATTR>(pointer); }
        finally { source.ReleaseTLibAttr(pointer); }
    }

    public void Dispose() {
        var pointer = Pointer;
        Pointer = IntPtr.Zero;
        if (pointer != IntPtr.Zero) _release(pointer);
    }
}
