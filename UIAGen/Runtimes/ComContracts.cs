//------------------------------------------------------------------------------
// <copyright file="ComContracts.cs" company="Piotr Filipowicz">
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

namespace UiaAdapter.ComRuntime;

public interface IComWrapper {
    IntPtr ComPointer { get; }
}

public interface IComVariantValue : IComWrapper {
    bool IsMissing { get; }
    object? Value { get; }
}

public interface IComBindingState : IComVariantValue {
    bool IsPoly { get; set; }
    bool IsLate { get; set; }
    bool ForceLateInvoke {
        get => IsLate;
        set => IsLate = value;
    }
}

public sealed class ComPointerLease : IComWrapper, IDisposable {
    public IntPtr ComPointer { get; private set; }

    public ComPointerLease(IntPtr pointer) : this(pointer, addRef: true) { }

    private ComPointerLease(IntPtr pointer, bool addRef) {
        if (!ComStubOps.TryGetVTableFunction(pointer, 1, out _)) {
            throw new InvalidOperationException("Cannot acquire a reference to an invalid COM pointer.");
        }
        if (addRef)
            ComStubOps.AddRef(pointer);
        ComPointer = pointer;
    }

    public static ComPointerLease TakeOwnership(IntPtr pointer) => new(pointer, addRef: false);

    public void Dispose() {
        IntPtr pointer = ComPointer;
        if (pointer == IntPtr.Zero) return;
        ComPointer = IntPtr.Zero;
        ComStubOps.Release(pointer);
        GC.SuppressFinalize(this);
    }

    ~ComPointerLease() {
        try { Dispose(); } catch { }
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct SAFEARRAYBOUND {
    public uint cElements;
    public int lLbound;
}
