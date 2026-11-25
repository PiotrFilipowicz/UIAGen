//------------------------------------------------------------------------------
// <copyright file="ComStubOps.Variants.cs" company="Piotr Filipowicz">
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

#pragma warning disable CS8603, CS8601

namespace UiaAdapter.ComRuntime
{
    public static unsafe partial class ComStubOps
    {
        // --- Safely unpack a stack-allocated VARIANT without an RCW ---
        private static TEmpty EmptyResult<TEmpty>() where TEmpty : class, IComVariantValue, new() => new TEmpty();

        private static object LeaseComPointer<TEmpty>(IntPtr pointer) where TEmpty : class, IComVariantValue, new() =>
            pointer == IntPtr.Zero ? (object)EmptyResult<TEmpty>() : new ComPointerLease(pointer);

        private static object TakeOwnershipOfComPointer<TEmpty>(IntPtr pointer) where TEmpty : class, IComVariantValue, new() =>
            pointer == IntPtr.Zero ? (object)EmptyResult<TEmpty>() : ComPointerLease.TakeOwnership(pointer);

        private static bool TryConvertToInt32(object value, out int result)
        {
            if (!(value is int || value is short || value is ushort || value is byte || value is sbyte ||
                  value is uint || value is long || value is ulong || value is global::System.Enum))
            {
                result = default(int);
                return false;
            }
            try
            {
                result = global::System.Convert.ToInt32(value);
                return true;
            }
            catch (OverflowException)
            {
                result = default(int);
                return false;
            }
        }

        private static decimal VariantDecimalToObject(byte* slot)
        {
            byte scale = *(byte*)(slot + 2);
            bool negative = (*(byte*)(slot + 3) & 0x80) != 0;
            int high = *(int*)(slot + 4);
            ulong low64 = *(ulong*)(slot + 8);
            return new decimal((int)(uint)low64, (int)(uint)(low64 >> 32), high, negative, scale);
        }

        private static TResult[] ReadSafeArray1D<TNative, TResult>(IntPtr psa, int lowerBound, int count, Func<TNative, TResult> convert)
            where TNative : unmanaged
        {
            var result = new TResult[count];
            for (int i = 0; i < count; i++)
            {
                int index = lowerBound + i;
                TNative element = default(TNative);
                Marshal.ThrowExceptionForHR(SafeArrayGetElement(psa, &index, &element));
                result[i] = convert(element);
            }
            return result;
        }

        private static TResult[,] ReadSafeArray2D<TNative, TResult>(IntPtr psa, int rowLowerBound, int rows,
            int columnLowerBound, int columns, Func<TNative, TResult> convert) where TNative : unmanaged
        {
            var result = new TResult[rows, columns];
            int* indices = stackalloc int[2];
            for (int row = 0; row < rows; row++)
            {
                for (int column = 0; column < columns; column++)
                {
                    indices[0] = columnLowerBound + column;
                    indices[1] = rowLowerBound + row;
                    TNative element = default(TNative);
                    Marshal.ThrowExceptionForHR(SafeArrayGetElement(psa, indices, &element));
                    result[row, column] = convert(element);
                }
            }
            return result;
        }

        private static void PutSafeArrayElementOrDestroy(IntPtr psa, int* indices, void* element)
        {
            int hr = SafeArrayPutElement(psa, indices, element);
            if (hr >= 0)
                return;
            SafeArrayDestroy(psa);
            Marshal.ThrowExceptionForHR(hr);
        }

        private static object[] ReadVariantSafeArray1D<TEmpty>(IntPtr psa, int lowerBound, int count)
            where TEmpty : class, IComVariantValue, new()
        {
            var result = new object[count];
            int variantSize = IntPtr.Size == 8 ? 24 : 16;
            byte* element = stackalloc byte[variantSize];
            for (int i = 0; i < count; i++)
            {
                int index = lowerBound + i;
                VariantInit((IntPtr)element);
                try
                {
                    Marshal.ThrowExceptionForHR(SafeArrayGetElement(psa, &index, element));
                    result[i] = VariantToObject<TEmpty>(element) ?? string.Empty;
                }
                finally
                {
                    VariantClear((IntPtr)element);
                }
            }
            return result;
        }

        private static object[,] ReadVariantSafeArray2D<TEmpty>(IntPtr psa, int rowLowerBound, int rows,
            int columnLowerBound, int columns) where TEmpty : class, IComVariantValue, new()
        {
            var result = new object[rows, columns];
            int variantSize = IntPtr.Size == 8 ? 24 : 16;
            byte* element = stackalloc byte[variantSize];
            int* indices = stackalloc int[2];
            for (int row = 0; row < rows; row++)
            {
                for (int column = 0; column < columns; column++)
                {
                    indices[0] = columnLowerBound + column;
                    indices[1] = rowLowerBound + row;
                    VariantInit((IntPtr)element);
                    try
                    {
                        Marshal.ThrowExceptionForHR(SafeArrayGetElement(psa, indices, element));
                        result[row, column] = VariantToObject<TEmpty>(element) ?? string.Empty;
                    }
                    finally
                    {
                        VariantClear((IntPtr)element);
                    }
                }
            }
            return result;
        }

        public static object VariantToObject<TEmpty>(void* pVar) where TEmpty : class, IComVariantValue, new()
        {
            if (pVar == null) return EmptyResult<TEmpty>();
            byte* slot = (byte*)pVar;
            ushort vt = *(ushort*)slot;

            // --- SAFEARRAY HANDLING (VT_ARRAY) ---
            if ((vt & 0x2000) != 0)
            {
                IntPtr psa = *(IntPtr*)(slot + 8);
                if (psa == IntPtr.Zero) return Array.Empty<object>();

                uint dims = SafeArrayGetDim(psa);
                ushort elementVt = (ushort)(vt & 0x0FFF);
                if (elementVt == 0)
                    Marshal.ThrowExceptionForHR(SafeArrayGetVartype(psa, out elementVt));

                if (dims == 1)
                {
                    Marshal.ThrowExceptionForHR(SafeArrayGetLBound(psa, 1, out int lowerBound));
                    Marshal.ThrowExceptionForHR(SafeArrayGetUBound(psa, 1, out int upperBound));
                    int count = checked(upperBound - lowerBound + 1);

                    switch (elementVt)
                    {
                        case 2: return ReadSafeArray1D<short, short>(psa, lowerBound, count, value => value);       // VT_I2
                        case 3: return ReadSafeArray1D<int, int>(psa, lowerBound, count, value => value);           // VT_I4
                        case 4: return ReadSafeArray1D<float, float>(psa, lowerBound, count, value => value);       // VT_R4
                        case 5: return ReadSafeArray1D<double, double>(psa, lowerBound, count, value => value);     // VT_R8
                        case 6: return ReadSafeArray1D<long, decimal>(psa, lowerBound, count, decimal.FromOACurrency);     // VT_CY
                        case 7: return ReadSafeArray1D<double, DateTime>(psa, lowerBound, count, DateTime.FromOADate);     // VT_DATE
                        case 8: return ReadSafeArray1D<IntPtr, string>(psa, lowerBound, count, value =>           // VT_BSTR
                        {
                            try { return BstrToString(value) ?? string.Empty; }
                            finally { if (value != IntPtr.Zero) SysFreeString(value); }
                        });
                        case 9: return ReadSafeArray1D<IntPtr, object>(psa, lowerBound, count, value => TakeOwnershipOfComPointer<TEmpty>(value)); // VT_DISPATCH
                        case 10: return ReadSafeArray1D<int, int>(psa, lowerBound, count, value => value);          // VT_ERROR
                        case 11: return ReadSafeArray1D<short, bool>(psa, lowerBound, count, value => value != 0);  // VT_BOOL
                        case 12: return ReadVariantSafeArray1D<TEmpty>(psa, lowerBound, count);                            // VT_VARIANT
                        case 13: return ReadSafeArray1D<IntPtr, object>(psa, lowerBound, count, value => TakeOwnershipOfComPointer<TEmpty>(value)); // VT_UNKNOWN
                        case 16: return ReadSafeArray1D<sbyte, sbyte>(psa, lowerBound, count, value => value);      // VT_I1
                        case 17: return ReadSafeArray1D<byte, byte>(psa, lowerBound, count, value => value);        // VT_UI1
                        case 18: return ReadSafeArray1D<ushort, ushort>(psa, lowerBound, count, value => value);    // VT_UI2
                        case 19: return ReadSafeArray1D<uint, uint>(psa, lowerBound, count, value => value);        // VT_UI4
                        case 20: return ReadSafeArray1D<long, long>(psa, lowerBound, count, value => value);        // VT_I8
                        case 21: return ReadSafeArray1D<ulong, ulong>(psa, lowerBound, count, value => value);      // VT_UI8
                        case 22: return ReadSafeArray1D<int, int>(psa, lowerBound, count, value => value);          // VT_INT
                        case 23: return ReadSafeArray1D<uint, uint>(psa, lowerBound, count, value => value);        // VT_UINT
                        default: throw new NotSupportedException($"Unsupported SAFEARRAY element type: VT_{elementVt}.");
                    }
                }

                if (dims == 2)
                {
                    Marshal.ThrowExceptionForHR(SafeArrayGetLBound(psa, 1, out int rowLowerBound));
                    Marshal.ThrowExceptionForHR(SafeArrayGetUBound(psa, 1, out int rowUpperBound));
                    Marshal.ThrowExceptionForHR(SafeArrayGetLBound(psa, 2, out int columnLowerBound));
                    Marshal.ThrowExceptionForHR(SafeArrayGetUBound(psa, 2, out int columnUpperBound));
                    int rows = checked(rowUpperBound - rowLowerBound + 1);
                    int columns = checked(columnUpperBound - columnLowerBound + 1);

                    switch (elementVt)
                    {
                        case 2: return ReadSafeArray2D<short, short>(psa, rowLowerBound, rows, columnLowerBound, columns, value => value);
                        case 3: return ReadSafeArray2D<int, int>(psa, rowLowerBound, rows, columnLowerBound, columns, value => value);
                        case 4: return ReadSafeArray2D<float, float>(psa, rowLowerBound, rows, columnLowerBound, columns, value => value);
                        case 5: return ReadSafeArray2D<double, double>(psa, rowLowerBound, rows, columnLowerBound, columns, value => value);
                        case 6: return ReadSafeArray2D<long, decimal>(psa, rowLowerBound, rows, columnLowerBound, columns, decimal.FromOACurrency);
                        case 7: return ReadSafeArray2D<double, DateTime>(psa, rowLowerBound, rows, columnLowerBound, columns, DateTime.FromOADate);
                        case 8: return ReadSafeArray2D<IntPtr, string>(psa, rowLowerBound, rows, columnLowerBound, columns, value =>
                        {
                            try { return BstrToString(value) ?? string.Empty; }
                            finally { if (value != IntPtr.Zero) SysFreeString(value); }
                        });
                        case 9: return ReadSafeArray2D<IntPtr, object>(psa, rowLowerBound, rows, columnLowerBound, columns, value => TakeOwnershipOfComPointer<TEmpty>(value));
                        case 10: return ReadSafeArray2D<int, int>(psa, rowLowerBound, rows, columnLowerBound, columns, value => value);
                        case 11: return ReadSafeArray2D<short, bool>(psa, rowLowerBound, rows, columnLowerBound, columns, value => value != 0);
                        case 12: return ReadVariantSafeArray2D<TEmpty>(psa, rowLowerBound, rows, columnLowerBound, columns);
                        case 13: return ReadSafeArray2D<IntPtr, object>(psa, rowLowerBound, rows, columnLowerBound, columns, value => TakeOwnershipOfComPointer<TEmpty>(value));
                        case 16: return ReadSafeArray2D<sbyte, sbyte>(psa, rowLowerBound, rows, columnLowerBound, columns, value => value);
                        case 17: return ReadSafeArray2D<byte, byte>(psa, rowLowerBound, rows, columnLowerBound, columns, value => value);
                        case 18: return ReadSafeArray2D<ushort, ushort>(psa, rowLowerBound, rows, columnLowerBound, columns, value => value);
                        case 19: return ReadSafeArray2D<uint, uint>(psa, rowLowerBound, rows, columnLowerBound, columns, value => value);
                        case 20: return ReadSafeArray2D<long, long>(psa, rowLowerBound, rows, columnLowerBound, columns, value => value);
                        case 21: return ReadSafeArray2D<ulong, ulong>(psa, rowLowerBound, rows, columnLowerBound, columns, value => value);
                        case 22: return ReadSafeArray2D<int, int>(psa, rowLowerBound, rows, columnLowerBound, columns, value => value);
                        case 23: return ReadSafeArray2D<uint, uint>(psa, rowLowerBound, rows, columnLowerBound, columns, value => value);
                        default: throw new NotSupportedException($"Unsupported two-dimensional SAFEARRAY element type: VT_{elementVt}.");
                    }
                }

                throw new NotSupportedException($"SAFEARRAYs with {dims} dimensions are not supported.");
            }

            // --- STANDARD SCALAR TYPES ---
            switch (vt)
            {
                case 0: return EmptyResult<TEmpty>();                                   // VT_EMPTY
                case 1: return EmptyResult<TEmpty>();                                   // VT_NULL
                case 2: return *(short*)(slot + 8);                                     // VT_I2
                case 3: return *(int*)(slot + 8);                                       // VT_I4
                case 4: return *(float*)(slot + 8);                                     // VT_R4
                case 5: return *(double*)(slot + 8);                                    // VT_R8
                case 6: return decimal.FromOACurrency(*(long*)(slot + 8));              // VT_CY
                case 7: return DateTime.FromOADate(*(double*)(slot + 8));               // VT_DATE
                case 8: return BstrToString(*(IntPtr*)(slot + 8)) ?? string.Empty;      // VT_BSTR
                case 9: return LeaseComPointer<TEmpty>(*(IntPtr*)(slot + 8));           // VT_DISPATCH (NULL means Nothing)
                case 11: return *(short*)(slot + 8) != 0;                               // VT_BOOL
                case 13: return LeaseComPointer<TEmpty>(*(IntPtr*)(slot + 8));          // VT_UNKNOWN (e.g. IEnumVARIANT)
                case 14: return VariantDecimalToObject(slot);                           // VT_DECIMAL
                case 16: return *(sbyte*)(slot + 8);                                    // VT_I1
                case 17: return *(byte*)(slot + 8);                                     // VT_UI1
                case 18: return *(ushort*)(slot + 8);                                   // VT_UI2
                case 19: return *(uint*)(slot + 8);                                     // VT_UI4
                case 20: return *(long*)(slot + 8);                                     // VT_I8
                case 21: return *(ulong*)(slot + 8);                                    // VT_UI8
                case 22: return *(int*)(slot + 8);                                      // VT_INT
                case 23: return *(uint*)(slot + 8);                                     // VT_UINT
                default: throw new NotSupportedException($"Unsupported VARIANT type 0x{vt:X4}; an explicit Native AOT-compatible decoder is required.");
            }
        }

        private static object Unwrap(object value)
        {
            var variantValue = value as IComVariantValue;
            if (variantValue == null) return value;
            if (variantValue.IsMissing) return null;
            if (variantValue.Value != null) return variantValue.Value;
            return variantValue.ComPointer != IntPtr.Zero ? variantValue : null;
        }

        // --- Pack an object directly into a stack-allocated VARIANT (Native AOT-compatible) ---
        public static void ObjectToVariant(object val, void* pVar)
        {
            if (pVar == null) return;
            byte* slot = (byte*)pVar;
            VariantInit((IntPtr)slot);

            if (val == null)
            {
                *(ushort*)slot = 1; // VT_NULL
            }
            else if (val == global::System.Type.Missing)
            {
                *(ushort*)slot = 10; // VT_ERROR
                *(int*)(slot + 8) = unchecked((int)0x80020004); // DISP_E_PARAMNOTFOUND
            }
            else if (val is IComVariantValue comVal)
            {
                if (comVal.IsMissing)
                {
                    *(ushort*)slot = 1; // VT_NULL
                }
                else if (comVal.Value != null)
                {
                    ObjectToVariant(comVal.Value, pVar);
                }
                else if (comVal.ComPointer != IntPtr.Zero)
                {
                    *(ushort*)slot = 9; // VT_DISPATCH
                    *(IntPtr*)(slot + 8) = comVal.ComPointer;
                    AddRef(comVal.ComPointer);
                }
            }
            else if (val is int intVal)
            {
                *(ushort*)slot = 3; // VT_I4
                *(int*)(slot + 8) = intVal;
            }
            else if (val is uint uintVal)
            {
                *(ushort*)slot = 19; // VT_UI4
                *(uint*)(slot + 8) = uintVal;
            }
            else if (val is short shortVal)
            {
                *(ushort*)slot = 2; // VT_I2
                *(short*)(slot + 8) = shortVal;
            }
            else if (val is ushort ushortVal)
            {
                *(ushort*)slot = 18; // VT_UI2
                *(ushort*)(slot + 8) = ushortVal;
            }
            else if (val is long longVal)
            {
                *(ushort*)slot = 20; // VT_I8
                *(long*)(slot + 8) = longVal;
            }
            else if (val is ulong ulongVal)
            {
                *(ushort*)slot = 21; // VT_UI8
                *(ulong*)(slot + 8) = ulongVal;
            }
            else if (val is byte byteVal)
            {
                *(ushort*)slot = 17; // VT_UI1
                *(byte*)(slot + 8) = byteVal;
            }
            else if (val is sbyte sbyteVal)
            {
                *(ushort*)slot = 16; // VT_I1
                *(sbyte*)(slot + 8) = sbyteVal;
            }
            else if (val is bool boolVal)
            {
                *(ushort*)slot = 11; // VT_BOOL
                *(short*)(slot + 8) = (short)(boolVal ? -1 : 0);
            }
            else if (val is double dblVal)
            {
                *(ushort*)slot = 5; // VT_R8
                *(double*)(slot + 8) = dblVal;
            }
            else if (val is float fltVal)
            {
                *(ushort*)slot = 4; // VT_R4
                *(float*)(slot + 8) = fltVal;
            }
            else if (val is string strVal)
            {
                *(ushort*)slot = 8; // VT_BSTR
                *(IntPtr*)(slot + 8) = StringToBstr(strVal);
            }
            else if (val is DateTime dtVal)
            {
                *(ushort*)slot = 7; // VT_DATE
                *(double*)(slot + 8) = dtVal.ToOADate();
            }
            else if (val is decimal decimalVal)
            {
                int[] bits = decimal.GetBits(decimalVal);
                *(ushort*)slot = 14; // VT_DECIMAL
                *(byte*)(slot + 2) = (byte)((bits[3] >> 16) & 0x7F);
                *(byte*)(slot + 3) = (byte)((bits[3] & int.MinValue) != 0 ? 0x80 : 0);
                *(uint*)(slot + 4) = (uint)bits[2];
                *(ulong*)(slot + 8) = (uint)bits[0] | ((ulong)(uint)bits[1] << 32);
            }
            else if (val is string[] strArray)
            {
                // VT_ARRAY | VT_BSTR (1D) — COM convention: lLbound = 0
                SAFEARRAYBOUND bound = new SAFEARRAYBOUND { cElements = (uint)strArray.Length, lLbound = 0 };
                IntPtr psa = SafeArrayCreate(8 /* VT_BSTR */, 1, &bound);
                if (psa == IntPtr.Zero) throw new OutOfMemoryException("Failed to create SAFEARRAY(BSTR).");

                for (int i = 0; i < strArray.Length; i++)
                {
                    IntPtr bstr = StringToBstr(strArray[i]);
                    int idx = i;
                    try
                    {
                        // For VT_BSTR, pv is already a BSTR pointer; do not add another
                        // level of indirection (&bstr).
                        int hr = SafeArrayPutElement(psa, &idx, (void*)bstr);
                        if (hr < 0)
                        {
                            SafeArrayDestroy(psa);
                            Marshal.ThrowExceptionForHR(hr);
                        }
                    }
                    finally
                    {
                        // SafeArrayPutElement copies the BSTR into the array.
                        if (bstr != IntPtr.Zero) SysFreeString(bstr);
                    }
                }

                *(ushort*)slot = 0x2000 | 8; // VT_ARRAY | VT_BSTR
                *(IntPtr*)(slot + 8) = psa;
            }
            else if (val is int[] intArray)
            {
                // VT_ARRAY | VT_I4 (1D) — COM convention: lLbound = 0
                SAFEARRAYBOUND bound = new SAFEARRAYBOUND { cElements = (uint)intArray.Length, lLbound = 0 };
                IntPtr psa = SafeArrayCreate(3 /* VT_I4 */, 1, &bound);
                if (psa == IntPtr.Zero) throw new OutOfMemoryException("Failed to create SAFEARRAY(VT_I4).");

                for (int i = 0; i < intArray.Length; i++)
                {
                    int idx = i;
                    int elem = intArray[i];
                    PutSafeArrayElementOrDestroy(psa, &idx, &elem);
                }

                *(ushort*)slot = 0x2000 | 3; // VT_ARRAY | VT_I4
                *(IntPtr*)(slot + 8) = psa;
            }
            else if (val is double[] dblArray)
            {
                // VT_ARRAY | VT_R8 (1D)
                SAFEARRAYBOUND bound = new SAFEARRAYBOUND { cElements = (uint)dblArray.Length, lLbound = 0 };
                IntPtr psa = SafeArrayCreate(5 /* VT_R8 */, 1, &bound);
                if (psa == IntPtr.Zero) throw new OutOfMemoryException("Failed to create SAFEARRAY(VT_R8).");

                for (int i = 0; i < dblArray.Length; i++)
                {
                    int idx = i;
                    double elem = dblArray[i];
                    PutSafeArrayElementOrDestroy(psa, &idx, &elem);
                }

                *(ushort*)slot = 0x2000 | 5; // VT_ARRAY | VT_R8
                *(IntPtr*)(slot + 8) = psa;
            }
            else if (val is float[] fltArray)
            {
                // VT_ARRAY | VT_R4 (1D)
                SAFEARRAYBOUND bound = new SAFEARRAYBOUND { cElements = (uint)fltArray.Length, lLbound = 0 };
                IntPtr psa = SafeArrayCreate(4 /* VT_R4 */, 1, &bound);
                if (psa == IntPtr.Zero) throw new OutOfMemoryException("Failed to create SAFEARRAY(VT_R4).");

                for (int i = 0; i < fltArray.Length; i++)
                {
                    int idx = i;
                    float elem = fltArray[i];
                    PutSafeArrayElementOrDestroy(psa, &idx, &elem);
                }

                *(ushort*)slot = 0x2000 | 4; // VT_ARRAY | VT_R4
                *(IntPtr*)(slot + 8) = psa;
            }
            else if (val is bool[] boolArray)
            {
                // VT_ARRAY | VT_BOOL (1D) — VARIANT_BOOL to short (-1 = true, 0 = false)
                SAFEARRAYBOUND bound = new SAFEARRAYBOUND { cElements = (uint)boolArray.Length, lLbound = 0 };
                IntPtr psa = SafeArrayCreate(11 /* VT_BOOL */, 1, &bound);
                if (psa == IntPtr.Zero) throw new OutOfMemoryException("Failed to create SAFEARRAY(VT_BOOL).");

                for (int i = 0; i < boolArray.Length; i++)
                {
                    int idx = i;
                    short elem = (short)(boolArray[i] ? -1 : 0);
                    PutSafeArrayElementOrDestroy(psa, &idx, &elem);
                }

                *(ushort*)slot = 0x2000 | 11; // VT_ARRAY | VT_BOOL
                *(IntPtr*)(slot + 8) = psa;
            }
            else if (val is byte[] byteArray)
            {
                // VT_ARRAY | VT_UI1 (1D)
                SAFEARRAYBOUND bound = new SAFEARRAYBOUND { cElements = (uint)byteArray.Length, lLbound = 0 };
                IntPtr psa = SafeArrayCreate(17 /* VT_UI1 */, 1, &bound);
                if (psa == IntPtr.Zero) throw new OutOfMemoryException("Failed to create SAFEARRAY(VT_UI1).");

                for (int i = 0; i < byteArray.Length; i++)
                {
                    int idx = i;
                    byte elem = byteArray[i];
                    PutSafeArrayElementOrDestroy(psa, &idx, &elem);
                }

                *(ushort*)slot = 0x2000 | 17; // VT_ARRAY | VT_UI1
                *(IntPtr*)(slot + 8) = psa;
            }
            else if (val is short[] shortArray)
            {
                // VT_ARRAY | VT_I2 (1D)
                SAFEARRAYBOUND bound = new SAFEARRAYBOUND { cElements = (uint)shortArray.Length, lLbound = 0 };
                IntPtr psa = SafeArrayCreate(2 /* VT_I2 */, 1, &bound);
                if (psa == IntPtr.Zero) throw new OutOfMemoryException("Failed to create SAFEARRAY(VT_I2).");

                for (int i = 0; i < shortArray.Length; i++)
                {
                    int idx = i;
                    short elem = shortArray[i];
                    PutSafeArrayElementOrDestroy(psa, &idx, &elem);
                }

                *(ushort*)slot = 0x2000 | 2; // VT_ARRAY | VT_I2
                *(IntPtr*)(slot + 8) = psa;
            }
            else if (val is long[] longArray)
            {
                // VT_ARRAY | VT_I8 (1D)
                SAFEARRAYBOUND bound = new SAFEARRAYBOUND { cElements = (uint)longArray.Length, lLbound = 0 };
                IntPtr psa = SafeArrayCreate(20 /* VT_I8 */, 1, &bound);
                if (psa == IntPtr.Zero) throw new OutOfMemoryException("Failed to create SAFEARRAY(VT_I8).");

                for (int i = 0; i < longArray.Length; i++)
                {
                    int idx = i;
                    long elem = longArray[i];
                    PutSafeArrayElementOrDestroy(psa, &idx, &elem);
                }

                *(ushort*)slot = 0x2000 | 20; // VT_ARRAY | VT_I8
                *(IntPtr*)(slot + 8) = psa;
            }
            else if (val is DateTime[] dtArray)
            {
                // VT_ARRAY | VT_DATE (1D)
                SAFEARRAYBOUND bound = new SAFEARRAYBOUND { cElements = (uint)dtArray.Length, lLbound = 0 };
                IntPtr psa = SafeArrayCreate(7 /* VT_DATE */, 1, &bound);
                if (psa == IntPtr.Zero) throw new OutOfMemoryException("Failed to create SAFEARRAY(VT_DATE).");

                for (int i = 0; i < dtArray.Length; i++)
                {
                    int idx = i;
                    double elem = dtArray[i].ToOADate();
                    PutSafeArrayElementOrDestroy(psa, &idx, &elem);
                }

                *(ushort*)slot = 0x2000 | 7; // VT_ARRAY | VT_DATE
                *(IntPtr*)(slot + 8) = psa;
            }
            else if (val is object[] objArray)
            {
                if (objArray.Length == 0)
                {
                    SAFEARRAYBOUND bound = new SAFEARRAYBOUND { cElements = 0, lLbound = 0 };
                    IntPtr psa = SafeArrayCreate(12 /* VT_VARIANT */, 1, &bound);
                    if (psa == IntPtr.Zero) throw new OutOfMemoryException("Failed to create SAFEARRAY(VARIANT).");
                    *(ushort*)slot = 0x2000 | 12;
                    *(IntPtr*)(slot + 8) = psa;
                }
                else
                {
                    // Inspect the contents of the object[]:
                    // Office COM APIs such as PowerPoint Shapes.Range reject SAFEARRAY(VT_VARIANT) with HRESULT 0x80004001.
                    // If all elements are integers or all are strings, convert to SAFEARRAY(VT_I4) or SAFEARRAY(VT_BSTR), respectively.
                    bool allInts = true;
                    bool allStrings = true;
                    int[] convertedInts = new int[objArray.Length];

                    for (int i = 0; i < objArray.Length; i++)
                    {
                        object unwrapped = Unwrap(objArray[i]);
                        if (!TryConvertToInt32(unwrapped, out convertedInts[i])) allInts = false;
                        if (!(unwrapped is string)) allStrings = false;
                    }

                    if (allInts)
                    {
                        ObjectToVariant(convertedInts, pVar);
                    }
                    else if (allStrings)
                    {
                        string[] convertedStrings = new string[objArray.Length];
                        for (int i = 0; i < objArray.Length; i++)
                        {
                            convertedStrings[i] = Unwrap(objArray[i])?.ToString() ?? string.Empty;
                        }
                        ObjectToVariant(convertedStrings, pVar);
                    }
                    else
                    {
                        // VT_ARRAY | VT_VARIANT (1D) — General-purpose container for mixed types
                        SAFEARRAYBOUND bound = new SAFEARRAYBOUND { cElements = (uint)objArray.Length, lLbound = 0 };
                        IntPtr psa = SafeArrayCreate(12 /* VT_VARIANT */, 1, &bound);
                        if (psa == IntPtr.Zero) throw new OutOfMemoryException("Failed to create SAFEARRAY(VARIANT).");

                        int variantSize = IntPtr.Size == 8 ? 24 : 16;
                        byte* elemVar = stackalloc byte[variantSize];

                        try
                        {
                            for (int i = 0; i < objArray.Length; i++)
                            {
                                int idx = i;
                                VariantInit((IntPtr)elemVar);
                                try
                                {
                                    ObjectToVariant(objArray[i], elemVar);
                                    Marshal.ThrowExceptionForHR(SafeArrayPutElement(psa, &idx, elemVar));
                                }
                                finally { VariantClear((IntPtr)elemVar); }
                            }
                        }
                        catch { SafeArrayDestroy(psa); throw; }

                        *(ushort*)slot = 0x2000 | 12; // VT_ARRAY | VT_VARIANT
                        *(IntPtr*)(slot + 8) = psa;
                    }
                }
            }
            else if (val is Array genArray && genArray.Rank == 1)
            {
                // Handle arbitrary one-dimensional arrays (e.g. Enum[], uint[], sbyte[])
                object[] objArr = new object[genArray.Length];
                for (int i = 0; i < genArray.Length; i++)
                {
                    objArr[i] = genArray.GetValue(i);
                }
                ObjectToVariant(objArr, pVar);
            }
            else if (val is object[,] matrix)
            {
                // VT_ARRAY | VT_VARIANT (2D — Excel Range.Value)
                int rows = matrix.GetLength(0);
                int cols = matrix.GetLength(1);

                SAFEARRAYBOUND* bounds = stackalloc SAFEARRAYBOUND[2];
                bounds[0] = new SAFEARRAYBOUND { cElements = (uint)cols, lLbound = 1 };
                bounds[1] = new SAFEARRAYBOUND { cElements = (uint)rows, lLbound = 1 };

                IntPtr psa = SafeArrayCreate(12 /* VT_VARIANT */, 2, bounds);
                if (psa == IntPtr.Zero) throw new OutOfMemoryException("Failed to create a two-dimensional SAFEARRAY(VARIANT).");

                int variantSize = IntPtr.Size == 8 ? 24 : 16;
                byte* elemVar = stackalloc byte[variantSize];

                int* indices = stackalloc int[2];
                try
                {
                    for (int r = 0; r < rows; r++)
                    {
                        for (int c = 0; c < cols; c++)
                        {
                            indices[0] = c + 1; // dim 0 = columns
                            indices[1] = r + 1; // dim 1 = rows

                            VariantInit((IntPtr)elemVar);
                            try
                            {
                                ObjectToVariant(matrix[r, c], elemVar);
                                Marshal.ThrowExceptionForHR(SafeArrayPutElement(psa, indices, elemVar));
                            }
                            finally { VariantClear((IntPtr)elemVar); }
                        }
                    }
                }
                catch { SafeArrayDestroy(psa); throw; }

                *(ushort*)slot = 0x2000 | 12; // VT_ARRAY | VT_VARIANT
                *(IntPtr*)(slot + 8) = psa;
            }
            else if (val is IComWrapper cw && cw.ComPointer != IntPtr.Zero)
            {
                *(ushort*)slot = 9; // VT_DISPATCH
                *(IntPtr*)(slot + 8) = cw.ComPointer;
                AddRef(cw.ComPointer);
            }
            else if (val is IntPtr ptr && ptr != IntPtr.Zero)
            {
                *(ushort*)slot = 9; // VT_DISPATCH
                *(IntPtr*)(slot + 8) = ptr;
                AddRef(ptr);
            }
            else
            {
                throw new NotSupportedException($"Type {val.GetType().FullName} has no explicit Native AOT-compatible VARIANT encoder.");
            }
        }
    }
}
