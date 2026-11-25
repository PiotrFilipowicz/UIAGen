//------------------------------------------------------------------------------
// <copyright file="ComInvoker.cs" company="Piotr Filipowicz">
//     Copyright (c) 2026 Piotr Filipowicz.
//     SPDX-License-Identifier: LGPL-3.0-or-later WITH GCC-exception-3.1
//     Licensed under GNU LGPL v3 with Code Generation Exception.
// </copyright>
// <security>
//     Integrity Notice: Official commits, tags, and release artifacts of this
//     project are cryptographically signed.
// </security>
//------------------------------------------------------------------------------

#if UIA_ADAPTER_TEMPLATE
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using UiaAdapter.ComRuntime;

#pragma warning disable CS8602, CS8604, CS8601, CS8625, CS8765

namespace UiaAdapter.TemplateSyntax
{
#endif

    internal static class MissingConfig
    {
        internal static bool ReturnMissingForMissingCollectionItem { get; set; }
        internal static ISet<int> MissingCollectionItemHResults { get; } = new HashSet<int>
        {
            unchecked((int)0x8002000B), // DISP_E_BADINDEX
            unchecked((int)0x8002802B), // TYPE_E_ELEMENTNOTFOUND
            unchecked((int)0x800401E5), // MK_E_NOOBJECT
            unchecked((int)0x80070490)  // HRESULT_FROM_WIN32(ERROR_NOT_FOUND)
        };
    }

    // =========================================================================
    // DTP DOMAIN VALUE OBJECTS
    // =========================================================================
    public readonly struct GeometricBounds : IEquatable<GeometricBounds>
    {
        public double Top { get; }
        public double Left { get; }
        public double Bottom { get; }
        public double Right { get; }

        public GeometricBounds(double top, double left, double bottom, double right)
        {
            Top = top;
            Left = left;
            Bottom = bottom;
            Right = right;
        }

        public double Height => Bottom - Top;
        public double Width => Right - Left;
        public double[] ToArray() => new double[] { Top, Left, Bottom, Right };

        public bool Equals(GeometricBounds other) =>
            Top.Equals(other.Top) &&
            Left.Equals(other.Left) &&
            Bottom.Equals(other.Bottom) &&
            Right.Equals(other.Right);

        public override bool Equals(object obj) => obj is GeometricBounds other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = Top.GetHashCode();
                hash = (hash * 397) ^ Left.GetHashCode();
                hash = (hash * 397) ^ Bottom.GetHashCode();
                hash = (hash * 397) ^ Right.GetHashCode();
                return hash;
            }
        }

        public static bool operator ==(GeometricBounds left, GeometricBounds right) => left.Equals(right);
        public static bool operator !=(GeometricBounds left, GeometricBounds right) => !left.Equals(right);
    }

    public enum ComInvokeKind
    {
        Method = 1,
        PropertyGet = 2,
        PropertyPut = 4,
        PropertyPutRef = 8
    }

    internal enum ComInvocationPath
    {
        Unknown,
        Early,
        Late,
        LateFallback
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPPARAMS
    {
        public IntPtr rgvarg;
        public IntPtr rgdispidNamedArgs;
        public uint cArgs;
        public uint cNamedArgs;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EXCEPINFO
    {
        public ushort wCode;
        public ushort wReserved;
        public IntPtr bstrSource;
        public IntPtr bstrDescription;
        public IntPtr bstrHelpFile;
        public uint dwHelpContext;
        public IntPtr pvReserved;
        public IntPtr pfnDeferredFillIn;
        public int scode;
    }

    // =========================================================================
    // DIRECT VTABLE INVOKER (x86 / x64, NO COMIMPORT)
    // =========================================================================
    public static unsafe class ComInvoker
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public unsafe delegate int GetTypeInfoDelegate(IntPtr pDisp, uint iTInfo, uint lcid, IntPtr* ppTInfo);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public unsafe delegate int GetTypeAttrDelegate(IntPtr pTypeInfo, IntPtr* ppTypeAttr);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public unsafe delegate int GetFuncDescDelegate(IntPtr pTypeInfo, int index, IntPtr* ppFuncDesc);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public unsafe delegate int GetDocumentationDelegate(IntPtr pTypeInfo, int memid, IntPtr* pBstrName, IntPtr* pBstrDocString, uint* pdwHelpContext, IntPtr* pBstrHelpFile);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate void ReleaseTypeAttrDelegate(IntPtr pTypeInfo, IntPtr pTypeAttr);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate void ReleaseFuncDescDelegate(IntPtr pTypeInfo, IntPtr pFuncDesc);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public unsafe delegate int GetIDsOfNamesDelegate(IntPtr pDisp, Guid* riid, IntPtr* rgszNames, uint cNames, uint lcid, int* rgDispId);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public unsafe delegate int InvokeDelegate(IntPtr pDisp, int dispIdMember, Guid* riid, uint lcid, ushort wFlags, DISPPARAMS* pDispParams, void* pVarResult, EXCEPINFO* pExcepInfo, uint* puArgErr);

        public class InvocationContext
        {
            public const string Unknown = " [unknown]";
            public const string Early = "";
            public const string Late = " [late]";
            public const string Fallback = " [fallback]";
            public const string NotEvaluated = " [not evaluated]";
            private static readonly string[] Modes = new string[]
            {
                Unknown,
                Early,
                Late,
                Fallback,
                NotEvaluated
            };
            public static string LastPathLabel => Modes[(int)_lastInvocationPath];
        }

        [global::System.ThreadStatic]
        private static ComInvocationPath _lastInvocationPath;

        [DllImport("oleaut32.dll", PreserveSig = true)]
        public static extern int DispGetIDsOfNames(IntPtr pTypeInfo, [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] rgszNames, int cNames, out int rgDispId);

        public static readonly Guid IID_NULL = Guid.Empty;
        public static readonly DISPPARAMS EmptyDispParams = new DISPPARAMS();
        public static readonly global::System.Collections.Concurrent.ConcurrentDictionary<string, global::System.Collections.Concurrent.ConcurrentDictionary<string, (int DispId, uint Lcid)>> _dispIdCache =
            new global::System.Collections.Concurrent.ConcurrentDictionary<string, global::System.Collections.Concurrent.ConcurrentDictionary<string, (int DispId, uint Lcid)>>(global::System.StringComparer.OrdinalIgnoreCase);

        public static readonly HashSet<string> _comInfrastructureMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "QueryInterface", "AddRef", "Release",
            "GetTypeInfoCount", "GetTypeInfo", "GetIDsOfNames", "Invoke"
        };
        public static readonly HashSet<string> _safeDebuggerMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // __UIA_SAFE_DEBUGGER_METHODS__
        };

        public static bool SelectInvocationDispId(IntPtr dispatch, object target, string name, InvokeMode mode, int knownDispId,
            bool forceLateInvoke, string typeKey, out int dispId, out uint lcid)
        {
            bool bypassCache = forceLateInvoke || (target is IComBindingState bindingState && (bindingState.IsPoly || bindingState.ForceLateInvoke));
            if (!bypassCache && knownDispId != -1 && mode == InvokeMode.Early)
            {
                dispId = knownDispId;
                lcid = 0;
                _lastInvocationPath = ComInvocationPath.Early;
                return true;
            }

            _lastInvocationPath = ComInvocationPath.Late;
            if (!TryResolveDispId(dispatch, name, bypassCache, typeKey, out dispId, out lcid))
                throw new COMException($"Member '{name}' was not found on the COM object ({typeKey}).", unchecked((int)0x80020003));
            return false;
        }

        public static IntPtr GetDispatchPointer(object comObj, out bool releaseNeeded)
        {
            releaseNeeded = false;
            if (comObj is IComWrapper w && w.ComPointer != IntPtr.Zero)
            {
                return w.ComPointer;
            }

            IntPtr pDisp = ComStubOps.GetIDispatch(comObj);
            if (pDisp != IntPtr.Zero)
            {
                releaseNeeded = true;
                return pDisp;
            }

            throw new InvalidOperationException("The target object does not expose a native IDispatch pointer.");
        }

        public static global::UiaAdapter.ComRuntime.ComPointerLease CreateFromProgID(string progId)
        {
            if (ComStubOps.CLSIDFromProgID(progId, out Guid clsid) < 0)
            {
                throw new global::System.InvalidOperationException($"No registered COM server found for '{progId}'.");
            }

            IntPtr pUnk = IntPtr.Zero;
            Guid iidIDispatch = ComStubOps.IID_IDispatch;

            // 5 = CLSCTX_INPROC_SERVER (1) | CLSCTX_LOCAL_SERVER (4)
            int hr = ComStubOps.CoCreateInstance(&clsid, IntPtr.Zero, 5, &iidIDispatch, &pUnk);
            if (hr < 0 || pUnk == IntPtr.Zero)
            {
                global::System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(hr);
            }

            try { return new global::UiaAdapter.ComRuntime.ComPointerLease(pUnk); }
            finally { ComStubOps.Release(pUnk); }
        }

        public static string[] GetAllMemberNames(object comObj, bool parameterlessOnly = false)
        {
            if (comObj == null)
                throw new ArgumentNullException(nameof(comObj));
            if (comObj is ComProxy asComProxy)
                asComProxy.ThrowIfMissing("GetMemberNames");
            IntPtr pDispatch = GetDispatchPointer(comObj, out bool releaseNeeded);
            try
            {
                if (!ComStubOps.TryGetVTableFunction(pDispatch, 4, out IntPtr getTypeInfoFunction))
                    return Array.Empty<string>();
                var getTypeInfo = Marshal.GetDelegateForFunctionPointer<GetTypeInfoDelegate>(getTypeInfoFunction);
                IntPtr pTypeInfo = IntPtr.Zero;
                if (getTypeInfo(pDispatch, 0, 0, &pTypeInfo) != 0 || pTypeInfo == IntPtr.Zero)
                    return Array.Empty<string>();

                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (!ComStubOps.TryGetVTableFunction(pTypeInfo, 3, out IntPtr getTypeAttrFunction) ||
                        !ComStubOps.TryGetVTableFunction(pTypeInfo, 5, out IntPtr getFuncDescFunction) ||
                        !ComStubOps.TryGetVTableFunction(pTypeInfo, 12, out IntPtr getDocumentationFunction) ||
                        !ComStubOps.TryGetVTableFunction(pTypeInfo, 19, out IntPtr releaseTypeAttrFunction) ||
                        !ComStubOps.TryGetVTableFunction(pTypeInfo, 20, out IntPtr releaseFuncDescFunction))
                    {
                        return Array.Empty<string>();
                    }

                    var getTypeAttr = Marshal.GetDelegateForFunctionPointer<GetTypeAttrDelegate>(getTypeAttrFunction);
                    var getFuncDesc = Marshal.GetDelegateForFunctionPointer<GetFuncDescDelegate>(getFuncDescFunction);
                    var getDocumentation = Marshal.GetDelegateForFunctionPointer<GetDocumentationDelegate>(getDocumentationFunction);
                    var releaseTypeAttr = Marshal.GetDelegateForFunctionPointer<ReleaseTypeAttrDelegate>(releaseTypeAttrFunction);
                    var releaseFuncDesc = Marshal.GetDelegateForFunctionPointer<ReleaseFuncDescDelegate>(releaseFuncDescFunction);

                    IntPtr pTypeAttr = IntPtr.Zero;
                    if (getTypeAttr(pTypeInfo, &pTypeAttr) == 0 && pTypeAttr != IntPtr.Zero)
                    {
                        try
                        {
                            int cFuncsOffset = IntPtr.Size == 8 ? 48 : 44;
                            ushort cFuncs = *(ushort*)((byte*)pTypeAttr + cFuncsOffset);

                            for (int i = 0; i < cFuncs; i++)
                            {
                                IntPtr pFuncDesc = IntPtr.Zero;
                                if (getFuncDesc(pTypeInfo, i, &pFuncDesc) == 0 && pFuncDesc != IntPtr.Zero)
                                {
                                    try
                                    {
                                        int memid = *(int*)pFuncDesc;
                                        bool requiresSafeMethodName = false;
                                        if (parameterlessOnly)
                                        {
                                            int parameterDescriptorsOffset = IntPtr.Size == 8 ? 16 : 8;
                                            int invkindOffset = IntPtr.Size == 8 ? 28 : 16;
                                            int cParamsOffset = IntPtr.Size == 8 ? 36 : 24;
                                            int cParamsOptOffset = IntPtr.Size == 8 ? 38 : 26;
                                            var invokeKind = (INVOKEKIND)(*(int*)((byte*)pFuncDesc + invkindOffset));
                                            short parameterCount = *(short*)((byte*)pFuncDesc + cParamsOffset);
                                            short optionalParameterCount = *(short*)((byte*)pFuncDesc + cParamsOptOffset);
                                            bool allParametersOptional = parameterCount == 0;
                                            if (parameterCount > 0)
                                            {
                                                IntPtr parameterDescriptors = *(IntPtr*)((byte*)pFuncDesc + parameterDescriptorsOffset);
                                                if (parameterDescriptors != IntPtr.Zero)
                                                {
                                                    int elementDescriptorSize = IntPtr.Size == 8 ? 32 : 16;
                                                    int parameterFlagsOffset = IntPtr.Size == 8 ? 24 : 12;
                                                    allParametersOptional = true;
                                                    for (int parameterIndex = 0; parameterIndex < parameterCount; parameterIndex++)
                                                    {
                                                        ushort parameterFlags = *(ushort*)((byte*)parameterDescriptors
                                                            + parameterIndex * elementDescriptorSize
                                                            + parameterFlagsOffset);
                                                        bool isTrailingOptionalParameter = optionalParameterCount > 0
                                                            && parameterIndex >= parameterCount - optionalParameterCount;
                                                        const ushort OptionalOrDefaultFlags = 0x10 | 0x20;
                                                        if (!isTrailingOptionalParameter && (parameterFlags & OptionalOrDefaultFlags) == 0)
                                                        {
                                                            allParametersOptional = false;
                                                            break;
                                                        }
                                                    }
                                                }
                                            }
                                            bool canEvaluateWithoutArguments = allParametersOptional &&
                                                (invokeKind == INVOKEKIND.INVOKE_PROPERTYGET || invokeKind == INVOKEKIND.INVOKE_FUNC);
                                            if (!canEvaluateWithoutArguments || memid == -4)
                                                continue;
                                            requiresSafeMethodName = invokeKind == INVOKEKIND.INVOKE_FUNC;
                                        }
                                        IntPtr bstrName = IntPtr.Zero;
                                        if (getDocumentation(pTypeInfo, memid, &bstrName, null, null, null) == 0 && bstrName != IntPtr.Zero)
                                        {
                                            try
                                            {
                                                string name = ComStubOps.BstrToString(bstrName) ?? string.Empty;
                                                if (!string.IsNullOrEmpty(name) &&
                                                    !_comInfrastructureMembers.Contains(name) &&
                                                    (!requiresSafeMethodName ||
                                                        (comObj is ComProxies && _safeDebuggerMethods.Contains(name))))
                                                {
                                                    names.Add(name);
                                                }
                                            }
                                            finally
                                            {
                                                ComStubOps.SysFreeString(bstrName);
                                            }
                                        }
                                    }
                                    finally
                                    {
                                        releaseFuncDesc(pTypeInfo, pFuncDesc);
                                    }
                                }
                            }
                        }
                        finally
                        {
                            releaseTypeAttr(pTypeInfo, pTypeAttr);
                        }
                    }
                }
                finally
                {
                    ComStubOps.Release(pTypeInfo);
                }
                return names.ToArray();
            }
            finally
            {
                if (releaseNeeded && pDispatch != IntPtr.Zero)
                    ComStubOps.Release(pDispatch);
            }
        }

        public static bool TryResolveDispId(IntPtr pDispatch, string name, bool bypassCache, string typeKey, out int dispId, out uint lcid)
        {
            dispId = 0;
            lcid = 0;

            bool canCache = !bypassCache && !string.IsNullOrEmpty(typeKey);
            if (canCache)
            {
                if (_dispIdCache.TryGetValue(typeKey, out var memberMap) && memberMap.TryGetValue(name, out var cached))
                {
                    dispId = cached.DispId;
                    lcid = cached.Lcid;
                    return true;
                }
            }

            uint currentLcid = (uint)global::System.Globalization.CultureInfo.CurrentCulture.LCID;
            uint[] lcidsToTest = new uint[] { 0, 1033, currentLcid, 1024 };
            string[] namesToTest = (name.Length > 1 && char.IsUpper(name[0]))
                ? new string[] { name, char.ToLowerInvariant(name[0]) + name.Substring(1) }
                : new string[] { name };

            if (!ComStubOps.TryGetVTableFunction(pDispatch, 5, out IntPtr getIdsFunction))
                return false;
            var getIDs = Marshal.GetDelegateForFunctionPointer<GetIDsOfNamesDelegate>(getIdsFunction);

            IntPtr* rgszNames = stackalloc IntPtr[1];
            fixed (Guid* pRiid = &IID_NULL)
            {
                foreach (var testName in namesToTest)
                {
                    fixed (char* pChar = testName)
                    {
                        rgszNames[0] = (IntPtr)pChar;

                        foreach (var testLcid in lcidsToTest)
                        {
                            int outDispId = 0;
                            if (getIDs(pDispatch, pRiid, rgszNames, 1, testLcid, &outDispId) >= 0)
                            {
                                dispId = outDispId;
                                lcid = testLcid;
                                if (canCache)
                                {
                                    var map = _dispIdCache.GetOrAdd(typeKey, _ => new global::System.Collections.Concurrent.ConcurrentDictionary<string, (int DispId, uint Lcid)>(global::System.StringComparer.OrdinalIgnoreCase));
                                    map.TryAdd(name, (dispId, lcid));
                                }
                                return true;
                            }
                        }
                    }
                }
            }

            if (!ComStubOps.TryGetVTableFunction(pDispatch, 4, out IntPtr getTypeInfoFunction))
                return false;
            var getTypeInfo = Marshal.GetDelegateForFunctionPointer<GetTypeInfoDelegate>(getTypeInfoFunction);
            IntPtr pTypeInfo = IntPtr.Zero;
            if (getTypeInfo(pDispatch, 0, 0, &pTypeInfo) == 0 && pTypeInfo != IntPtr.Zero)
            {
                try
                {
                    foreach (var testName in namesToTest)
                    {
                        if (DispGetIDsOfNames(pTypeInfo, new string[] { testName }, 1, out int resolvedId) >= 0)
                        {
                            dispId = resolvedId;
                            lcid = 0;
                            if (canCache)
                            {
                                var map = _dispIdCache.GetOrAdd(typeKey, _ => new global::System.Collections.Concurrent.ConcurrentDictionary<string, (int DispId, uint Lcid)>(global::System.StringComparer.OrdinalIgnoreCase));
                                map.TryAdd(name, (dispId, lcid));
                            }
                            return true;
                        }
                    }
                }
                finally { ComStubOps.Release(pTypeInfo); }
            }

            if (string.IsNullOrEmpty(name) || name.Equals("Item", StringComparison.OrdinalIgnoreCase))
            {
                dispId = 0;
                lcid = 0;
                return true;
            }

            if (name.Equals("_NewEnum", StringComparison.OrdinalIgnoreCase))
            {
                dispId = -4;
                lcid = 0;
                return true;
            }

            return false;
        }

        public static object DirectGet(object comObj, string name, InvokeMode invokeMode, int knownDispId, bool forceLateInvoke, string typeKey)
        {
            if (comObj == null)
                throw new ArgumentNullException(nameof(comObj));
            if (comObj is ComProxy asComProxy)
                asComProxy.ThrowIfMissing(name);
            IntPtr pDispatch = GetDispatchPointer(comObj, out bool releaseNeeded);

            try
            {
                string tKey = string.IsNullOrEmpty(typeKey) && comObj is ComProxy proxy ? proxy.TypeCacheKey : typeKey;
                bool usedKnownDispId = SelectInvocationDispId(pDispatch, comObj, name, invokeMode, knownDispId, forceLateInvoke, tKey, out int dispId, out uint lcid);

                if (!ComStubOps.TryGetVTableFunction(pDispatch, 6, out IntPtr invokeFunction))
                {
                    throw new COMException("The IDispatch::Invoke pointer is invalid or does not point to executable memory.", unchecked((int)0x80004003));
                }
                var invoke = Marshal.GetDelegateForFunctionPointer<InvokeDelegate>(invokeFunction);
                int variantSize = IntPtr.Size == 8 ? 24 : 16;
                byte* pVarResult = stackalloc byte[variantSize];
                ComStubOps.VariantInit((IntPtr)pVarResult);

                DISPPARAMS dp = EmptyDispParams;
                EXCEPINFO excepInfo = default(EXCEPINFO);
                uint argErr = 0;

                fixed (Guid* pRiid = &IID_NULL)
                {
                    int hr = invoke(pDispatch, dispId, pRiid, lcid, 3, &dp, pVarResult, &excepInfo, &argErr);

                    if (usedKnownDispId && hr < 0)
                    {
                        if (TryResolveDispId(pDispatch, name, bypassCache: false, tKey, out int dynamicId, out uint dynamicLcid))
                        {
                            _lastInvocationPath = ComInvocationPath.LateFallback;
                            ComStubOps.VariantClear((IntPtr)pVarResult);
                            ComStubOps.VariantInit((IntPtr)pVarResult);
                            hr = invoke(pDispatch, dynamicId, pRiid, dynamicLcid, 3, &dp, pVarResult, &excepInfo, &argErr);
                        }
                    }

                    if (hr < 0)
                    {
                        ComStubOps.VariantClear((IntPtr)pVarResult);

                        string description = string.Empty;
                        if (excepInfo.bstrDescription != IntPtr.Zero)
                        {
                            description = ComStubOps.BstrToString(excepInfo.bstrDescription) ?? string.Empty;
                            ComStubOps.SysFreeString(excepInfo.bstrDescription);
                        }
                        if (excepInfo.bstrSource != IntPtr.Zero)
                            ComStubOps.SysFreeString(excepInfo.bstrSource);
                        if (excepInfo.bstrHelpFile != IntPtr.Zero)
                            ComStubOps.SysFreeString(excepInfo.bstrHelpFile);

                        int realCode = excepInfo.scode != 0 ? excepInfo.scode : hr;
                        string msg = !string.IsNullOrEmpty(description)
                            ? $"COM error invoking '{name}' on '{tKey}': {description} (HRESULT: 0x{realCode:X8})"
                            : $"COM error invoking '{name}' on '{tKey}': DISP_E_EXCEPTION (HRESULT: 0x{realCode:X8})";

                        throw new global::System.Runtime.InteropServices.COMException(msg, realCode);
                    }
                }

                try { return ComStubOps.VariantToObject<ComProxy>(pVarResult); }
                finally { ComStubOps.VariantClear((IntPtr)pVarResult); }
            }
            finally
            {
                if (releaseNeeded && pDispatch != IntPtr.Zero)
                    ComStubOps.Release(pDispatch);
            }
        }

        public static object DirectInvoke(object comObj, string name, InvokeMode invokeMode, bool forceLateInvoke, int knownDispId, ComInvokeKind invokeKind, string typeKey = "", object[] args = null)
        {
            if (comObj == null)
                throw new ArgumentNullException(nameof(comObj));
            if (comObj is ComProxy asComProxy)
                asComProxy.ThrowIfMissing(name);
            IntPtr pDispatch = GetDispatchPointer(comObj, out bool releaseNeeded);

            try
            {
                string tKey = string.IsNullOrEmpty(typeKey) && comObj is ComProxy proxy ? proxy.TypeCacheKey : typeKey;
                bool usedKnownDispId = SelectInvocationDispId(pDispatch, comObj, name, invokeMode, knownDispId, forceLateInvoke, tKey, out int dispId, out uint lcid);

                bool isPut = invokeKind == ComInvokeKind.PropertyPut || invokeKind == ComInvokeKind.PropertyPutRef;
                ushort flags;
                switch (invokeKind)
                {
                    case ComInvokeKind.Method:
                    case ComInvokeKind.PropertyGet:
                        flags = 3;
                        break;
                    case ComInvokeKind.PropertyPut:
                        flags = 4;
                        break;
                    case ComInvokeKind.PropertyPutRef:
                        flags = 8;
                        break;
                    default:
                        flags = 3;
                        break;
                }

                int rawCount = args?.Length ?? 0;
                if (!isPut && rawCount > 0)
                {
                    while (rawCount > 0 && (
                        args[rawCount - 1] == null ||
                        args[rawCount - 1] == global::System.Type.Missing ||
                        (args[rawCount - 1] is IComVariantValue v && v.IsMissing)))
                    {
                        rawCount--;
                    }
                }
                int argCount = rawCount;

                int variantSize = IntPtr.Size == 8 ? 24 : 16;
                if (argCount > 256)
                {
                    throw new ArgumentOutOfRangeException(nameof(args), "A COM call can pass at most 256 arguments on the stack.");
                }
                int argumentBytes = checked(argCount * variantSize);
                byte* pStackBytes = stackalloc byte[argumentBytes];
                IntPtr pArgs = argCount == 0 ? IntPtr.Zero : (IntPtr)pStackBytes;
                for (int i = 0; i < argCount; i++)
                    ComStubOps.VariantInit(pArgs + (i * variantSize));

                byte* pVarResult = stackalloc byte[variantSize];
                ComStubOps.VariantInit((IntPtr)pVarResult);

                try
                {
                    for (int i = 0; i < argCount; i++)
                    {
                        int argIndex = argCount - 1 - i;
                        object val = isPut ? ComProxy.Unwrap(args[argIndex]) : ComProxy.UnwrapForMethod(args[argIndex]);
                        byte* slot = (byte*)pArgs + (i * variantSize);
                        ComStubOps.ObjectToVariant(val, slot);
                    }

                    int putNamedArg = -3;
                    IntPtr pNamedArgs = isPut ? (IntPtr)(&putNamedArg) : IntPtr.Zero;

                    DISPPARAMS dp = new DISPPARAMS
                    {
                        rgvarg = pArgs,
                        rgdispidNamedArgs = pNamedArgs,
                        cArgs = (uint)argCount,
                        cNamedArgs = isPut ? 1u : 0u
                    };

                    if (!ComStubOps.TryGetVTableFunction(pDispatch, 6, out IntPtr invokeFunction))
                    {
                        throw new COMException("The IDispatch::Invoke pointer is invalid or does not point to executable memory.", unchecked((int)0x80004003));
                    }
                    var invoke = Marshal.GetDelegateForFunctionPointer<InvokeDelegate>(invokeFunction);
                    EXCEPINFO excepInfo = default(EXCEPINFO);
                    uint argErr = 0;
                    fixed (Guid* pRiid = &IID_NULL)
                    {
                        int hr = invoke(pDispatch, dispId, pRiid, lcid, flags, &dp, pVarResult, &excepInfo, &argErr);

                        if (usedKnownDispId && hr < 0)
                        {
                            if (TryResolveDispId(pDispatch, name, bypassCache: false, tKey, out int dynamicId, out uint dynamicLcid))
                            {
                                _lastInvocationPath = ComInvocationPath.LateFallback;
                                ComStubOps.VariantClear((IntPtr)pVarResult);
                                ComStubOps.VariantInit((IntPtr)pVarResult);
                                hr = invoke(pDispatch, dynamicId, pRiid, dynamicLcid, flags, &dp, pVarResult, &excepInfo, &argErr);
                            }
                        }

                        if (hr < 0)
                        {
                            ComStubOps.VariantClear((IntPtr)pVarResult);

                            string description = string.Empty;
                            if (excepInfo.bstrDescription != IntPtr.Zero)
                            {
                                description = ComStubOps.BstrToString(excepInfo.bstrDescription) ?? string.Empty;
                                ComStubOps.SysFreeString(excepInfo.bstrDescription);
                            }
                            if (excepInfo.bstrSource != IntPtr.Zero)
                                ComStubOps.SysFreeString(excepInfo.bstrSource);
                            if (excepInfo.bstrHelpFile != IntPtr.Zero)
                                ComStubOps.SysFreeString(excepInfo.bstrHelpFile);

                            int realCode = excepInfo.scode != 0 ? excepInfo.scode : hr;
                            string msg = !string.IsNullOrEmpty(description)
                                ? $"COM error invoking '{name}' on '{tKey}': {description} (HRESULT: 0x{realCode:X8})"
                                : $"COM error invoking '{name}' on '{tKey}': DISP_E_EXCEPTION (HRESULT: 0x{realCode:X8})";

                            throw new global::System.Runtime.InteropServices.COMException(msg, realCode);
                        }
                    }
                    try { return ComStubOps.VariantToObject<ComProxy>(pVarResult); }
                    finally { ComStubOps.VariantClear((IntPtr)pVarResult); }
                }
                finally
                {
                    if (pArgs != IntPtr.Zero)
                    {
                        for (int i = 0; i < argCount; i++)
                            ComStubOps.VariantClear(pArgs + (i * variantSize));
                    }
                }
            }
            finally
            {
                if (releaseNeeded && pDispatch != IntPtr.Zero)
                    ComStubOps.Release(pDispatch);
            }
        }

        private static TWrapper ResolveWrapper<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TWrapper>(object raw, string memberName) where TWrapper : class {
            if (raw == null)
                return ComProxy.Missing<TWrapper>();
            if (raw is ComProxy rawProxy && rawProxy.IsMissing)
                return ComProxy.Missing<TWrapper>();
            if (raw is TWrapper typed)
                return typed;
            if (typeof(TWrapper) == typeof(ComProxy))
                return (TWrapper)(object)ComObjectExtensions.ToComProxy(raw);
            var wrapped = ComObjectExtensions.As<TWrapper>(raw);
            if (wrapped == null)
                throw new InvalidCastException($"Cannot cast the COM instance returned by '{memberName}' to {typeof(TWrapper).Name}.");
            return wrapped;
        }

        public static TValue ResolveValue<TValue>(object raw, string memberName) where TValue : struct
        {
            if (raw == null || (raw is ComProxy rawProxy && rawProxy.IsMissing))
                return default(TValue);
            if (raw is TValue typed)
                return typed;
            Type targetType = typeof(TValue);
            if (targetType.IsEnum)
            {
                object underlyingVal = global::System.Convert.ChangeType(raw, Enum.GetUnderlyingType(targetType), global::System.Globalization.CultureInfo.InvariantCulture);
                return (TValue)Enum.ToObject(targetType, underlyingVal);
            }
            return (TValue)global::System.Convert.ChangeType(raw, targetType, global::System.Globalization.CultureInfo.InvariantCulture);
        }

        public static bool TryGetBlockedMessage(object comObj, out string message)
        {
            if (comObj is ComProxy asComProxy && !asComProxy.IsMissing && !string.IsNullOrEmpty(asComProxy._blockedMessage))
            {
                message = asComProxy._blockedMessage;
                return true;
            }
            message = string.Empty;
            return false;
        }

        public static TResult ApplyInvocationContext<TResult>(TResult result, object comObj, bool forceLateInvoke)
        {
            if (result is ComProxy asComProxy)
            {
                if (asComProxy.ComBehavior == null)
                    asComProxy.AttachBehavior((comObj as ComStub)?.ComBehavior);
                asComProxy._dbgTypeInfo = InvocationContext.LastPathLabel;
                if (forceLateInvoke || (comObj is ComProxy srcProxy && srcProxy.ForceLateInvoke))
                    asComProxy.ForceLateInvoke = true;
            }
            return result;
        }

        public static TWrapper GetWrapper<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TWrapper>(object comObj, string name, InvokeMode invokeMode, bool forceLateInvoke = false, int knownDispId = -1, string typeKey = "") where TWrapper : class {
            if (TryGetBlockedMessage(comObj, out string message))
                return ComProxy.CreateBlocked<TWrapper>(message);
            return ApplyInvocationContext(ResolveWrapper<TWrapper>(DirectGet(comObj, name, invokeMode, knownDispId, forceLateInvoke, typeKey), name), comObj, forceLateInvoke);
        }

        public static TValue GetValue<TValue>(object comObj, string name, InvokeMode invokeMode, bool forceLateInvoke, int knownDispId, string typeKey = "") where TValue : struct
        {
            if (TryGetBlockedMessage(comObj, out _))
                return default(TValue);
            return ResolveValue<TValue>(DirectGet(comObj, name, invokeMode, knownDispId, forceLateInvoke, typeKey), name);
        }

        public static string GetString(object comObj, string name, InvokeMode invokeMode, bool forceLateInvoke, int knownDispId, string typeKey = "")
        {
            if (TryGetBlockedMessage(comObj, out string message))
                return $"[{message}]";
            object raw = DirectGet(comObj, name, invokeMode, knownDispId, forceLateInvoke, typeKey);
            return raw?.ToString() ?? string.Empty;
        }

        public static object GetDynamicValue(object comObj, string name, InvokeMode invokeMode, bool forceLateInvoke, int knownDispId, string typeKey = "")
        {
            if (TryGetBlockedMessage(comObj, out string message))
                return $"[{message}]";
            object raw = DirectGet(comObj, name, invokeMode, knownDispId, forceLateInvoke, typeKey);
            if (raw == null)
                return ApplyInvocationContext(ComProxy.Missing(), comObj, forceLateInvoke);
            if (ComStubOps.IsComObject(raw) || raw is IComWrapper)
                return ApplyInvocationContext(ComObjectExtensions.ToComProxy(raw), comObj, forceLateInvoke);
            return raw;
        }

        public static TWrapper InvokeWrapper<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TWrapper>(object comObj, string name, InvokeMode invokeMode, bool forceLateInvoke, int knownDispId, string typeKey, object[] args) where TWrapper : class {
            if (TryGetBlockedMessage(comObj, out string message))
                return ComProxy.CreateBlocked<TWrapper>(message);
            return ApplyInvocationContext(ResolveWrapper<TWrapper>(DirectInvoke(comObj, name, invokeMode, forceLateInvoke, knownDispId, ComInvokeKind.Method, typeKey, args), name), comObj, forceLateInvoke);
        }

        public static TWrapper InvokeCollectionItem<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TWrapper>(object comObj, string name, InvokeMode invokeMode, bool forceLateInvoke, int knownDispId, string typeKey, object[] args) where TWrapper : class {
            try
            {
                return InvokeWrapper<TWrapper>(comObj, name, invokeMode, forceLateInvoke, knownDispId, typeKey, args);
            }
            catch (global::System.Runtime.InteropServices.COMException exception) when (
                MissingConfig.ReturnMissingForMissingCollectionItem && MissingConfig.MissingCollectionItemHResults.Contains(exception.HResult))
            {
                return ApplyInvocationContext(ComProxy.Missing<TWrapper>(), comObj, forceLateInvoke);
            }
        }

        public static TValue InvokeValue<TValue>(object comObj, string name, InvokeMode invokeMode, bool forceLateInvoke, int knownDispId, string typeKey, object[] args) where TValue : struct
        {
            if (TryGetBlockedMessage(comObj, out _))
                return default(TValue);
            return ResolveValue<TValue>(DirectInvoke(comObj, name, invokeMode, forceLateInvoke, knownDispId, ComInvokeKind.Method, typeKey, args), name);
        }

        public static string InvokeString(object comObj, string name, InvokeMode invokeMode, bool forceLateInvoke, int knownDispId, string typeKey, object[] args)
        {
            if (TryGetBlockedMessage(comObj, out string message))
                return $"[{message}]";
            object raw = DirectInvoke(comObj, name, invokeMode, forceLateInvoke, knownDispId, ComInvokeKind.Method, typeKey, args);
            return raw?.ToString() ?? string.Empty;
        }

        public static object InvokeDynamicValue(object comObj, string name, InvokeMode invokeMode, bool forceLateInvoke, int knownDispId, string typeKey, object[] args)
        {
            if (TryGetBlockedMessage(comObj, out string message))
                return $"[{message}]";
            object raw = DirectInvoke(comObj, name, invokeMode, forceLateInvoke, knownDispId, ComInvokeKind.Method, typeKey, args);
            if (raw == null)
                return ApplyInvocationContext(ComProxy.Missing(), comObj, forceLateInvoke);
            if (ComStubOps.IsComObject(raw) || raw is IComWrapper)
                return ApplyInvocationContext(ComObjectExtensions.ToComProxy(raw), comObj, forceLateInvoke);
            return raw;
        }

        public static void SetProperty<T>(object comObj, string name, InvokeMode invokeMode, T value, bool forceLateInvoke, int knownDispId, string typeKey)
        {
            DirectInvoke(comObj, name, invokeMode, forceLateInvoke, knownDispId, ComInvokeKind.PropertyPut, typeKey, new object[] { value });
        }

        public static void SetIndexer(object comObj, string name, InvokeMode invokeMode, bool forceLateInvoke, int knownDispId, bool isSetRef, string typeKey, object[] args)
        {
            DirectInvoke(comObj, name, invokeMode, forceLateInvoke, knownDispId, isSetRef ? ComInvokeKind.PropertyPutRef : ComInvokeKind.PropertyPut, typeKey, args);
        }

        public static object GetProperty(object comObj, string name, InvokeMode invokeMode, bool forceLateInvoke, int knownDispId, string typeKey) => DirectGet(comObj, name, invokeMode, knownDispId, forceLateInvoke, typeKey);

        public static object GetIndexer(object comObj, object index, InvokeMode invokeMode, bool forceLateInvoke, string typeKey)
        {
            try
            {
                return DirectInvoke(comObj, "Item", invokeMode, forceLateInvoke, 0, ComInvokeKind.PropertyGet, typeKey, new object[] { index });
            }
            catch (global::System.Runtime.InteropServices.COMException exception) when (
                MissingConfig.ReturnMissingForMissingCollectionItem && MissingConfig.MissingCollectionItemHResults.Contains(exception.HResult))
            {
                return ComProxy.Missing();
            }
        }

        public static void SetProperty(object comObj, string name, object value, InvokeMode invokeMode, bool forceLateInvoke, int knownDispId, string typeKey) => DirectInvoke(comObj, name, invokeMode, forceLateInvoke, knownDispId, ComInvokeKind.PropertyPut, typeKey, new object[] { value });
        public static void SetPropertyRef(object comObj, string name, object value, InvokeMode invokeMode, bool forceLateInvoke, int knownDispId, string typeKey) => DirectInvoke(comObj, name, invokeMode, forceLateInvoke, knownDispId, ComInvokeKind.PropertyPutRef, typeKey, new object[] { value });
        public static object InvokeMethod(object comObj, string name, InvokeMode invokeMode, bool forceLateInvoke, int knownDispId, params object[] args) => DirectInvoke(comObj, name, invokeMode, forceLateInvoke, knownDispId, ComInvokeKind.Method, "", args);
        public static object InvokeMethod(object comObj, string name, InvokeMode invokeMode, bool forceLateInvoke, int knownDispId, string typeKey, object[] args) => DirectInvoke(comObj, name, invokeMode, forceLateInvoke, knownDispId, ComInvokeKind.Method, typeKey, args);
    }
#if UIA_ADAPTER_TEMPLATE
}
#endif
