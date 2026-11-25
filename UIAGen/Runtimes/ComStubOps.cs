//------------------------------------------------------------------------------
// <copyright file="ComStubOps.cs" company="Piotr Filipowicz">
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

#pragma warning disable CS8603, CS8625, CS8601

namespace UiaAdapter.ComRuntime
{
    public static unsafe partial class ComStubOps
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal unsafe delegate int QueryInterfaceDelegate(IntPtr pUnk, Guid* riid, IntPtr* ppv);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate uint AddRefDelegate(IntPtr pUnk);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate uint ReleaseDelegate(IntPtr pUnk);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private unsafe delegate int EnumVariantNextDelegate(IntPtr pEnum, uint celt, void* rgVar, uint* pCeltFetched);

        public static readonly Guid IID_IUnknown = new Guid("00000000-0000-0000-C000-000000000046");
        public static readonly Guid IID_IDispatch = new Guid("00020400-0000-0000-C000-000000000046");
        public static readonly Guid IID_IEnumVARIANT = new Guid("00020404-0000-0000-C000-000000000046");
        private static readonly Guid IID_NULL = Guid.Empty;

        [DllImport("ole32.dll", ExactSpelling = true)]
        public static extern int CLSIDFromProgID([MarshalAs(UnmanagedType.LPWStr)] string lpszProgID, out Guid pclsid);

        [DllImport("ole32.dll", ExactSpelling = true)]
        public static extern int CoCreateInstance(Guid* rclsid, IntPtr pUnkOuter, uint dwClsContext, Guid* riid, IntPtr* ppv);

        [DllImport("oleaut32.dll", PreserveSig = true)]
        public static extern void VariantInit(IntPtr pVar);

        [DllImport("oleaut32.dll", PreserveSig = true)]
        public static extern int VariantClear(IntPtr pVar);

        [DllImport("oleaut32.dll", ExactSpelling = true)]
        private static extern IntPtr SysAllocStringLen(char* str, uint length);

        [DllImport("oleaut32.dll", ExactSpelling = true)]
        public static extern void SysFreeString(IntPtr bstr);

        public static IntPtr StringToBstr(string value)
        {
            if (value == null)
                throw new ArgumentNullException(nameof(value));
            fixed (char* chars = value)
            {
                IntPtr bstr = SysAllocStringLen(chars, checked((uint)value.Length));
                if (bstr == IntPtr.Zero) throw new OutOfMemoryException("Failed to allocate a BSTR.");
                return bstr;
            }
        }

        [DllImport("oleaut32.dll", ExactSpelling = true)]
        public static extern IntPtr SafeArrayCreate(ushort vt, uint cDims, SAFEARRAYBOUND* rgsabound);

        [DllImport("oleaut32.dll", ExactSpelling = true)]
        public static extern int SafeArrayDestroy(IntPtr psa);

        [DllImport("oleaut32.dll", ExactSpelling = true)]
        public static extern uint SafeArrayGetDim(IntPtr psa);

        [DllImport("oleaut32.dll", ExactSpelling = true)]
        public static extern int SafeArrayGetLBound(IntPtr psa, uint nDim, out int pLbound);

        [DllImport("oleaut32.dll", ExactSpelling = true)]
        public static extern int SafeArrayGetUBound(IntPtr psa, uint nDim, out int pUbound);

        [DllImport("oleaut32.dll", ExactSpelling = true)]
        public static extern int SafeArrayGetVartype(IntPtr psa, out ushort pVartype);

        [DllImport("oleaut32.dll", ExactSpelling = true)]
        public static extern int SafeArrayGetElement(IntPtr psa, int* rgIndices, void* pv);

        [DllImport("oleaut32.dll", ExactSpelling = true)]
        public static extern int SafeArrayPutElement(IntPtr psa, int* rgIndices, void* pv);

        // A BSTR stores its byte length in a 4-byte prefix immediately before the first character.
        // Reading it does not require CLR marshaling.
        public static string BstrToString(IntPtr bstr)
        {
            if (bstr == IntPtr.Zero) return null;
            uint byteLength = *(((uint*)bstr) - 1);
            if ((byteLength & 1) != 0 || byteLength / sizeof(char) > int.MaxValue)
            {
                throw new InvalidOperationException("Invalid native BSTR length.");
            }
            return new string((char*)bstr, 0, (int)(byteLength / sizeof(char)));
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORY_BASIC_INFORMATION
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public UIntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
        }

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern UIntPtr VirtualQuery(IntPtr address, out MEMORY_BASIC_INFORMATION buffer, UIntPtr length);

        private const uint MEM_COMMIT = 0x1000;
        private const uint PAGE_GUARD = 0x100;
        private const uint PAGE_NOACCESS = 0x01;
        private const uint PAGE_EXECUTE = 0x10;
        private const uint PAGE_EXECUTE_READ = 0x20;
        private const uint PAGE_EXECUTE_READWRITE = 0x40;
        private const uint PAGE_EXECUTE_WRITECOPY = 0x80;

        private static ulong PointerToUInt64(IntPtr address)
        {
            return IntPtr.Size == 8
                ? unchecked((ulong)address.ToInt64())
                : unchecked((uint)address.ToInt32());
        }

        private static IntPtr UInt64ToPointer(ulong value)
        {
            return IntPtr.Size == 8
                ? new IntPtr(unchecked((long)value))
                : new IntPtr(unchecked((int)checked((uint)value)));
        }

        private static bool IsAccessibleAddress(IntPtr address, ulong byteCount, bool executable)
        {
            ulong current = PointerToUInt64(address);
            if (current < 0x10000UL || byteCount == 0) return false;
            if (VirtualQuery(address, out MEMORY_BASIC_INFORMATION info, new UIntPtr((uint)sizeof(MEMORY_BASIC_INFORMATION))) == UIntPtr.Zero) return false;
            if (info.State != MEM_COMMIT || (info.Protect & (PAGE_GUARD | PAGE_NOACCESS)) != 0) return false;
            if (executable && (info.Protect & (PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY)) == 0) return false;

            ulong start = PointerToUInt64(info.BaseAddress);
            ulong regionSize = info.RegionSize.ToUInt64();
            return current >= start && current - start <= regionSize && byteCount <= regionSize - (current - start);
        }

        public static bool TryGetVTableFunction(IntPtr instance, int slot, out IntPtr function)
        {
            function = IntPtr.Zero;
            if (slot < 0 || !IsAccessibleAddress(instance, (ulong)IntPtr.Size, executable: false)) return false;

            IntPtr vtable = *(IntPtr*)instance;
            ulong vtableAddr = PointerToUInt64(vtable);
            ulong offset = checked((ulong)slot * (ulong)IntPtr.Size);
            IntPtr entry = UInt64ToPointer(checked(vtableAddr + offset));
            if (!IsAccessibleAddress(entry, (ulong)IntPtr.Size, executable: false)) return false;

            function = *(IntPtr*)entry;
            if (!IsAccessibleAddress(function, 1, executable: true))
            {
                function = IntPtr.Zero;
                return false;
            }
            return true;
        }

        // --- Slot 0: QueryInterface directly from the vtable ---
        public static int QueryInterface(IntPtr pUnk, in Guid riid, out IntPtr ppv)
        {
            ppv = IntPtr.Zero;
            if (!TryGetVTableFunction(pUnk, 0, out IntPtr function)) return -2147467261; // E_POINTER
            var qi = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(function);
            fixed (Guid* pRiid = &riid)
            fixed (IntPtr* pOut = &ppv)
            {
                return qi(pUnk, pRiid, pOut);
            }
        }

        // --- Slot 1: AddRef directly from the vtable ---
        public static uint AddRef(IntPtr pUnk)
        {
            if (!TryGetVTableFunction(pUnk, 1, out IntPtr function)) return 0;
            var addRef = Marshal.GetDelegateForFunctionPointer<AddRefDelegate>(function);
            return addRef(pUnk);
        }

        // --- Slot 2: Release directly from the vtable ---
        public static uint Release(IntPtr pUnk)
        {
            if (!TryGetVTableFunction(pUnk, 2, out IntPtr function)) return 0;
            var release = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(function);
            return release(pUnk);
        }

        // --- Slot 3: IEnumVARIANT::Next ---
        public static bool TryEnumVariantNext<TEmpty>(IntPtr pEnum, out object value) where TEmpty : class, IComVariantValue, new()
        {
            value = null;
            if (!TryGetVTableFunction(pEnum, 3, out IntPtr nextFunction)) return false;

            int variantSize = IntPtr.Size == 8 ? 24 : 16;
            byte* variant = stackalloc byte[variantSize];
            VariantInit((IntPtr)variant);
            uint fetched = 0;
            var next = Marshal.GetDelegateForFunctionPointer<EnumVariantNextDelegate>(nextFunction);
            int hr = next(pEnum, 1, variant, &fetched);
            if (hr < 0)
            {
                VariantClear((IntPtr)variant);
                Marshal.ThrowExceptionForHR(hr);
            }
            if (hr != 0 || fetched != 1)
            {
                VariantClear((IntPtr)variant);
                return false;
            }

            try { value = VariantToObject<TEmpty>(variant); }
            finally { VariantClear((IntPtr)variant); }
            return true;
        }

        // --- Check whether an object is a COM object without relying on System.__ComObject ---
        public static bool IsComObject(object obj)
        {
            if (obj == null) return false;
            if (obj is IComWrapper w) return w.ComPointer != IntPtr.Zero;
            if (obj is IntPtr ptr) return ptr != IntPtr.Zero;
            return false;
        }

        // --- Get a native IDispatch* without CLR casts ---
        public static IntPtr GetIDispatch(object obj)
        {
            if (obj == null) return IntPtr.Zero;
            if (obj is IComWrapper w && w.ComPointer != IntPtr.Zero)
            {
                AddRef(w.ComPointer);
                return w.ComPointer;
            }
            if (obj is IntPtr ptr && ptr != IntPtr.Zero)
            {
                if (QueryInterface(ptr, in IID_IDispatch, out IntPtr pDisp) == 0)
                {
                    return pDisp;
                }
                AddRef(ptr);
                return ptr;
            }
            return IntPtr.Zero;
        }
    }
}
