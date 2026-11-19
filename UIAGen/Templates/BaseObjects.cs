//------------------------------------------------------------------------------
// <copyright file="BaseObjects.cs" company="Piotr Filipowicz">
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
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using UiaAdapter.ComRuntime;

namespace UiaAdapter.TemplateSyntax;

#pragma warning disable CS0109, CS8602, CS8625
#endif

// =========================================================================
// ComStub BASE CLASS (POINTER OWNERSHIP AND NATIVE AOT COM HELPERS)
// =========================================================================
public unsafe class ComStub : IDisposable, IComWrapper {
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public IntPtr ComPointer { get; private set; }
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public IComBehavior? ComBehavior { get; private set; }

    protected ComStub(object source) {
        ArgumentNullException.ThrowIfNull(source);
        ComBehavior = (source as ComStub)?.ComBehavior;

        if (source is IntPtr pointer) {
            if (pointer == IntPtr.Zero)
                return;
            if (ComStubOps.QueryInterface(pointer, in ComStubOps.IID_IDispatch, out IntPtr dispatch) == 0 && dispatch != IntPtr.Zero) {
                ComPointer = dispatch;
            }
            else {
                ComPointer = pointer;
                ComStubOps.AddRef(pointer);
            }
        }
        else if (source is IComWrapper wrapper && wrapper.ComPointer != IntPtr.Zero) {
            ComPointer = wrapper.ComPointer;
            ComStubOps.AddRef(ComPointer);
        }
        else {
            ComPointer = ComStubOps.GetIDispatch(source);
        }

        if (ComPointer != IntPtr.Zero)
            ComBehavior?.Tracker.Add(this);
    }

    internal void AttachBehavior(IComBehavior? comBehavior) {
        if (comBehavior == null || ReferenceEquals(ComBehavior, comBehavior))
            return;
        if (ComBehavior != null)
            throw new InvalidOperationException("The COM wrapper is already associated with another Registry instance.");

        ComBehavior = comBehavior;
        if (ComPointer != IntPtr.Zero)
            comBehavior.Tracker.Add(this);
    }

    internal TResult InheritTracker<TResult>(TResult result) {
        if (result is ComStub stub)
            stub.AttachBehavior(ComBehavior);
        return result;
    }

    public virtual void Dispose() {
        if (ComPointer != IntPtr.Zero) {
            ComStubOps.Release(ComPointer);
            ComPointer = IntPtr.Zero;
        }
        GC.SuppressFinalize(this);
    }
}

[DebuggerDisplay("{DbgInfoOfValue(),nq}", Name = "{DbgInfoOfName(),nq}")]
public partial class ComProxy : ComStub, IComBindingState {
    private static readonly string[] DbgNameProperties = ["Name", "NameLocal", "CommandName", "Title"];
    private static readonly string[] DbgValueProperties = ["Index"];
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public object? Value { get; }
    public bool IsPoly { get; set; }
    public bool ForceLateInvoke { get; set; }
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public bool IsLate {
        get => ForceLateInvoke;
        set => ForceLateInvoke = value;
    }
    internal string? _blockedMessage;
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private bool _nullVariant;
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    internal bool _missing;
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    internal string _dbgTypeInfo = ComInvoker.InvocationContext.Unknown;
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    internal string TypeCacheKey => GetType().FullName ?? GetType().Name;
    public bool IsMissing => _missing;
    public bool Found => !_missing;
    public bool IsCreated => !_missing && ComPointer != IntPtr.Zero;
    public DynamicComTree _DynamicDisplay => new DynamicComTree(this);
    internal virtual ComGeneratedMember[] GetDebuggerProperties() => Array.Empty<ComGeneratedMember>();
    internal virtual object? ReadDebuggerProperty(int index) => throw new ArgumentOutOfRangeException(nameof(index));
    internal void ThrowIfMissing(string memberName) {
        if (_missing) {
            throw new InvalidOperationException($"Cannot access member '{memberName}' on a missing COM object of type '{GetType().Name}' (Missing).");
        }
    }

    private string DebuggerDisplay {
        get {
            if (_nullVariant)
                return "[null]";
            if (_missing)
                return "[nothing]";
            if (!string.IsNullOrEmpty(_blockedMessage))
                return $"[{_blockedMessage}]";
            if (Value != null)
                return $"{VariantDebuggerDisplay.FormatValue(Value)} [{VariantDebuggerDisplay.GetTypeName(Value)}]";
            if (this is ComProxies) {
                int c = 0;
                try { c = ((ComProxies)this).Count; }
                catch { }
                return $"Count = {CollectionDebuggerDisplay.FormatCount(c)}{ComInvoker.InvocationContext.LastPathLabel}";
            }
            var comName = ComObjectExtensions.GetComTypeName(this);
            return !string.IsNullOrEmpty(comName)
                ? comName + (IsPoly ? " (Poly)" : "")
                : $"[Pointer: 0x{ComPointer:X}]" + (IsPoly ? " (Poly)" : "");
        }
    }

    protected internal virtual string DbgInfoOfName() {
        string typeName = GetType().Name;
        if (_missing || ComPointer == IntPtr.Zero)
            return typeName;

        foreach (string propertyName in DbgNameProperties) {
            if (TryGetDbgProperty(propertyName, out object? value) && TryFormatDbgProperty(value, out string text))
                return $"{typeName} [{propertyName} = {text}]";
        }

        return typeName;
    }

    protected internal virtual string DbgInfoOfValue() {
        string valueInfo = DebuggerDisplay;
        if (_missing || Value != null || ComPointer == IntPtr.Zero)
            return valueInfo;

        var properties = new global::System.Collections.Generic.List<string>(3);
        foreach (string propertyName in DbgValueProperties) {
            if (TryGetDbgProperty(propertyName, out object? value) && TryFormatDbgProperty(value, out string text))
                properties.Add($"{propertyName} = {text}");
        }

        return properties.Count == 0
            ? valueInfo
            : $"{valueInfo}; {string.Join(", ", properties)}";
    }

    protected internal virtual string DbgInfoOfType() => _dbgTypeInfo;

    private bool TryGetDbgProperty(string propertyName, out object? value) {
        value = null;
        try {
            ComGeneratedMember[] members = GetDebuggerProperties();
            for (int index = 0; index < members.Length; index++) {
                ComGeneratedMember member = members[index];
                if (member.IsBlocked || !member.Name.Equals(propertyName, global::System.StringComparison.OrdinalIgnoreCase))
                    continue;
                value = ReadDebuggerProperty(member.Index);
                return true;
            }
        }
        catch { }
        return false;
    }

    private static bool TryFormatDbgProperty(object? value, out string text) {
        if (value is ComProxy asComProxy) {
            if (asComProxy.IsMissing) {
                text = string.Empty;
                return false;
            }
            value = asComProxy.Value;
        }

        if (value == null) {
            text = string.Empty;
            return false;
        }

        text = VariantDebuggerDisplay.FormatValue(value);
        return !string.IsNullOrWhiteSpace(text);
    }

    public static ComProxy Missing() => new ComProxy(IntPtr.Zero) { _missing = true };

    public static T Missing<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>() where T : class {
        var missing = Missing();
        if (typeof(T) == typeof(ComProxy))
            return (T)(object)missing;
        if (typeof(T) == typeof(ComProxy[]))
            return (T)(object)Array.Empty<ComProxy>();
        try {
            var result = (T)Activator.CreateInstance(typeof(T), [IntPtr.Zero])!;
            if (result is ComProxy asComProxy)
                asComProxy._missing = true;
            return result;
        }
        catch (global::System.Exception ex) {
            throw new InvalidOperationException($"Cannot create a missing wrapper of type {typeof(T).FullName}.", ex);
        }
    }

    public static T CreateBlocked<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(string message) where T : class {
        var dummy = new ComProxy(IntPtr.Zero) { _missing = true, _blockedMessage = message };
        try {
            var result = (T)Activator.CreateInstance(typeof(T), [dummy])!;
            if (result is ComProxy asComProxy) {
                asComProxy._missing = true;
                asComProxy._blockedMessage = message;
            }
            return result;
        }
        catch (global::System.Exception ex) {
            throw new InvalidOperationException($"Cannot create a blocked wrapper of type {typeof(T).FullName}.", ex);
        }
    }

    public ComProxy() : this(IntPtr.Zero) {
        _missing = true;
        _nullVariant = true;
    }

    public ComProxy(object comInstance) : base(comInstance) {
        Value = ComPointer == IntPtr.Zero && comInstance is not IntPtr && comInstance is not ComProxy
            ? comInstance
            : (comInstance as ComProxy)?.Value;
        if (comInstance is ComProxy asComProxy && !string.IsNullOrEmpty(asComProxy._blockedMessage))
            _blockedMessage = asComProxy._blockedMessage;
    }

    public static bool operator ==(ComProxy? x, ComProxy? y) {
        bool xMissing = x is null || x._missing;
        bool yMissing = y is null || y._missing;
        if (xMissing && yMissing)
            return true;
        if (xMissing || yMissing)
            return false;
        return ReferenceEquals(x, y) || x.ComPointer == y.ComPointer;
    }

    public static bool operator !=(ComProxy? x, ComProxy? y) => !(x == y);

    public override bool Equals(object? obj) {
        if (obj is null)
            return _missing;
        if (obj is ComProxy asComProxy)
            return this == asComProxy;
        return false;
    }

    public override int GetHashCode() => _missing ? 0 : ComPointer.GetHashCode();

    public string[] GetMemberNames() {
        return ComInvoker.GetAllMemberNames(this);
    }

    internal string[] GetDebuggerMemberNames() {
        return ComInvoker.GetAllMemberNames(this, parameterlessOnly: true);
    }

    [global::System.Runtime.CompilerServices.IndexerName("Indexer")]
    public object this[string propertyName] {
        get => GetProperty(propertyName);
        set => SetProperty(propertyName, value);
    }

    [global::System.Runtime.CompilerServices.IndexerName("Indexer")]
    public ComProxy this[int index] {
        get {
            return InheritTracker(ComObjectExtensions.ToComProxy(ComInvoker.GetIndexer(this, index, ComBehavior.InvocationMode, ForceLateInvoke, TypeCacheKey)));
        }
    }

    public ComProxy Item(object index) {
        return InheritTracker(ComObjectExtensions.ToComProxy(ComInvoker.GetIndexer(this, index, ComBehavior.InvocationMode, ForceLateInvoke, TypeCacheKey)));
    }

    public ComProxy Item(object row, object column) {
        return InheritTracker(ComObjectExtensions.ToComProxy(ComInvoker.InvokeMethod(this, "Item", ComBehavior.InvocationMode, ForceLateInvoke, -1, TypeCacheKey, new object[] { row, column })));
    }

    public object GetProperty(string propertyName) {
        return InheritTracker(ComObjectExtensions.ToComProxy(ComInvoker.GetProperty(this, propertyName, ComBehavior.InvocationMode, ForceLateInvoke, -1, TypeCacheKey)));
    }

    public void SetProperty(string propertyName, object value) {
        ComInvoker.SetProperty(this, propertyName, value, ComBehavior.InvocationMode, ForceLateInvoke, -1, TypeCacheKey);
    }

    public object InvokeMethod(string methodName, params object[] args) {
        object raw = ComInvoker.InvokeMethod(this, methodName, ComBehavior.InvocationMode, ForceLateInvoke, -1, TypeCacheKey, args);
        if (raw == null)
            return InheritTracker(Missing());
        if (ComStubOps.IsComObject(raw) || raw is IComWrapper)
            return InheritTracker(ComObjectExtensions.ToComProxy(raw));
        return raw;
    }

    public TWrapper Wrap<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TWrapper>(object rawCom) where TWrapper : class {
        if (rawCom == null)
            return Missing<TWrapper>();
        bool poly = IsPoly;
        bool forceLateInvoke = ForceLateInvoke;
        if (rawCom is TWrapper existing) {
            InheritTracker(existing);
            if (poly && existing is ComProxy cpExisting) { cpExisting.IsPoly = true; }
            if (forceLateInvoke && existing is ComProxy cpExistingForcedLate) { cpExistingForcedLate.ForceLateInvoke = true; }
            return existing;
        }
        var res = ComObjectExtensions.As<TWrapper>(rawCom);
        InheritTracker(res);
        if (poly && res is ComProxy asComProxyResult) { asComProxyResult.IsPoly = true; }
        if (forceLateInvoke && res is ComProxy forcedLateProxy) { forcedLateProxy.ForceLateInvoke = true; }
        return res;
    }

    public override string ToString() => _missing ? string.Empty : $"[COM: 0x{ComPointer:X}]";

    public static implicit operator GeometricBounds(ComProxy comProxy) {
        if (comProxy is null || comProxy.IsMissing)
            throw new InvalidOperationException("Cannot convert a missing ComProxy object to GeometricBounds.");
        if (comProxy.Value is Array arr && arr.Length >= 4) {
            double top = global::System.Convert.ToDouble(arr.GetValue(0), global::System.Globalization.CultureInfo.InvariantCulture);
            double left = global::System.Convert.ToDouble(arr.GetValue(1), global::System.Globalization.CultureInfo.InvariantCulture);
            double bottom = global::System.Convert.ToDouble(arr.GetValue(2), global::System.Globalization.CultureInfo.InvariantCulture);
            double right = global::System.Convert.ToDouble(arr.GetValue(3), global::System.Globalization.CultureInfo.InvariantCulture);
            return new GeometricBounds(top, left, bottom, right);
        }
        throw new InvalidCastException("The native COM array does not contain the four coordinates required by GeometricBounds.");
    }

    public static implicit operator ComProxy(GeometricBounds bounds) => new ComProxy(bounds.ToArray());
    public static implicit operator string(ComProxy comProxy) => (comProxy == null || comProxy.IsMissing) ? string.Empty : comProxy.ToString();
    public static implicit operator int(ComProxy comProxy) => comProxy?.Value is int i ? i : global::System.Convert.ToInt32(comProxy?.Value);
    public static implicit operator double(ComProxy comProxy) => comProxy?.Value is double d ? d : global::System.Convert.ToDouble(comProxy?.Value, global::System.Globalization.CultureInfo.InvariantCulture);
    public static implicit operator bool(ComProxy comProxy) => comProxy?.Value is bool b ? b : global::System.Convert.ToBoolean(comProxy?.Value);

    public static implicit operator ComProxy(string[] values) => new ComProxy(values ?? Array.Empty<string>());
    public static implicit operator ComProxy(object[] values) => new ComProxy(values ?? Array.Empty<object>());
    public static implicit operator ComProxy(double[] values) => new ComProxy(values ?? Array.Empty<double>());
    public static implicit operator ComProxy(int[] values) => new ComProxy(values ?? Array.Empty<int>());

    public static implicit operator string[](ComProxy? comProxy) {
        if (comProxy is null || comProxy.IsMissing)
            return Array.Empty<string>();
        if (comProxy.Value is string[] strArr)
            return strArr;
        if (comProxy.Value is Array arr) {
            var result = new string[arr.Length];
            for (int i = 0; i < arr.Length; i++)
                result[i] = arr.GetValue(i)?.ToString() ?? string.Empty;
            return result;
        }
        return Array.Empty<string>();
    }

    public static implicit operator ComProxy(string value) => new ComProxy(value ?? string.Empty);
    public static implicit operator ComProxy(int value) => new ComProxy(value);
    public static implicit operator ComProxy(bool value) => new ComProxy(value);

    public static object? Unwrap(object? value) {
        if (value is null)
            return null;
        if (value is ComProxy asComProxy) {
            if (asComProxy.IsMissing)
                return null;
            if (asComProxy.Value != null)
                return Unwrap(asComProxy.Value);
            return asComProxy.ComPointer;
        }
        if (value is IComWrapper wrapper)
            return wrapper.ComPointer;
        if (value is global::System.Enum e)
            return global::System.Convert.ToInt32(e);
        return value;
    }

    public static object UnwrapForMethod(object? value) => Unwrap(value) ?? global::System.Type.Missing;

    protected TValue GetValue<TValue>(string name, int knownDispId) where TValue : struct {
        return ComInvoker.GetValue<TValue>(this, name, ComBehavior.InvocationMode, ForceLateInvoke, knownDispId, TypeCacheKey);
    }

    protected TValue InvokeValue<TValue>(string name, int knownDispId, params object[] args) where TValue : struct {
        return ComInvoker.InvokeValue<TValue>(this, name, ComBehavior.InvocationMode, ForceLateInvoke, knownDispId, TypeCacheKey, args);
    }

    protected TWrapper GetWrapper<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TWrapper>(string name, int knownDispId) where TWrapper : class {
        return ComInvoker.GetWrapper<TWrapper>(this, name, ComBehavior.InvocationMode, ForceLateInvoke, knownDispId, TypeCacheKey);
    }

    protected TWrapper InvokeWrapper<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TWrapper>(string name, int knownDispId, params object[] args) where TWrapper : class {
        return ComInvoker.InvokeWrapper<TWrapper>(this, name, ComBehavior.InvocationMode, ForceLateInvoke, knownDispId, TypeCacheKey, args);
    }

    protected TWrapper InvokeCollectionItem<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TWrapper>(string name, int knownDispId, params object[] args) where TWrapper : class {
        return ComInvoker.InvokeCollectionItem<TWrapper>(this, name, ComBehavior.InvocationMode, ForceLateInvoke, knownDispId, TypeCacheKey, args);
    }

    protected string GetString(string name, int knownDispId) {
        return ComInvoker.GetString(this, name, ComBehavior.InvocationMode, ForceLateInvoke, knownDispId, TypeCacheKey);
    }

    protected string InvokeString(string name, int knownDispId, params object[] args) {
        return ComInvoker.InvokeString(this, name, ComBehavior.InvocationMode, ForceLateInvoke, knownDispId, TypeCacheKey, args);
    }

    protected object GetDynamicValue(string name, int knownDispId) {
        return ComInvoker.GetDynamicValue(this, name, ComBehavior.InvocationMode, ForceLateInvoke, knownDispId, TypeCacheKey);
    }

    protected object InvokeDynamicValue(string name, int knownDispId, params object[] args) {
        return ComInvoker.InvokeDynamicValue(this, name, ComBehavior.InvocationMode, ForceLateInvoke, knownDispId, TypeCacheKey, args);
    }

    protected T[] InvokeArray<T>(string name, int knownDispId, params object[] args) {
        object value = ComInvoker.InvokeDynamicValue(this, name, ComBehavior.InvocationMode, ForceLateInvoke, knownDispId, TypeCacheKey, args);
        if (value is T[] typedArray)
            return typedArray;
        if (value is Array array) {
            T[] result = new T[array.Length];
            for (int index = 0; index < array.Length; index++)
                result[index] = (T)array.GetValue(index)!;
            return result;
        }
        throw new InvalidCastException($"The result of {name} is not an array of type {typeof(T).Name}[].");
    }

    protected void Set<TValue>(string name, TValue value, int knownDispId) {
        ComInvoker.SetProperty(this, name, ComBehavior.InvocationMode, value, ForceLateInvoke, knownDispId, TypeCacheKey);
    }

    protected void SetRef<TValue>(string name, TValue value, int knownDispId) {
        ComInvoker.SetPropertyRef(this, name, value!, ComBehavior.InvocationMode, ForceLateInvoke, knownDispId, TypeCacheKey);
    }

    protected void SetAuto<TValue>(string name, TValue value, int knownDispId) {
        bool isComReference = value is IComWrapper wrapper && wrapper.ComPointer != IntPtr.Zero;
        if (isComReference)
            ComInvoker.SetPropertyRef(this, name, value!, ComBehavior.InvocationMode, ForceLateInvoke, knownDispId, TypeCacheKey);
        else
            ComInvoker.SetProperty(this, name, ComBehavior.InvocationMode, value, ForceLateInvoke, knownDispId, TypeCacheKey);
    }

    protected void SetIndexer<TValue>(string name, TValue value, int knownDispId, bool supportsPut, bool supportsPutRef, params object[] indices) {
        object[] args = new object[indices.Length + 1];
        for (int i = 0; i < indices.Length; i++)
            args[i] = indices[i] ?? global::System.Type.Missing;
        args[^1] = value!;
        bool isComReference = value is IComWrapper wrapper && wrapper.ComPointer != IntPtr.Zero;
        bool isSetRef = supportsPutRef && (!supportsPut || isComReference);
        ComInvoker.SetIndexer(this, name, ComBehavior.InvocationMode, ForceLateInvoke, knownDispId, isSetRef, TypeCacheKey, args);
    }

    protected void InvokeVoid(string name, int knownDispId, params object[] args) {
        ComInvoker.InvokeMethod(this, name, ComBehavior.InvocationMode, ForceLateInvoke, knownDispId, TypeCacheKey, args);
    }
}

[DebuggerTypeProxy(typeof(ComProxiesDebugView<>))]
[DebuggerDisplay("{DbgInfoOfValue(),nq}", Name = "{DbgInfoOfName(),nq}")]
public class ComProxies<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TItem> : ComProxies, IEnumerable<TItem> where TItem : class {
    public ComProxies(object comInstance) : base(comInstance) { }

    // __UIA_TYPED_COLLECTION_MEMBERS_BEGIN__
    [global::System.Runtime.CompilerServices.IndexerName("Indexer")]
    public new TItem this[object index] {
        get {
            var raw = ComInvoker.GetIndexer(this, index, ComBehavior.InvocationMode, ForceLateInvoke, TypeCacheKey);
            if (typeof(TItem) == typeof(string)) {
                return (TItem)(object)(raw?.ToString() ?? string.Empty);
            }
            var item = ComObjectExtensions.As<TItem>(raw);
            InheritTracker(item);
            if (ForceLateInvoke && item is ComProxy asComProxy) { asComProxy.ForceLateInvoke = true; }
            return item;
        }
    }

    [global::System.Runtime.CompilerServices.IndexerName("Indexer")]
    public new TItem this[int index] => this[(object)index];

    [global::System.Runtime.CompilerServices.IndexerName("Indexer")]
    public new TItem this[string name] => this[(object)name];

    [global::System.Runtime.CompilerServices.IndexerName("Indexer")]
    public new TItem this[object row, object column] {
        get {
            var raw = ComInvoker.InvokeMethod(this, "Item", ComBehavior.InvocationMode, ForceLateInvoke, 0, TypeCacheKey, new object[] { row, column });
            if (typeof(TItem) == typeof(string)) {
                return (TItem)(object)(raw?.ToString() ?? string.Empty);
            }
            var item = ComObjectExtensions.As<TItem>(raw);
            InheritTracker(item);
            if (ForceLateInvoke && item is ComProxy asComProxy) { asComProxy.ForceLateInvoke = true; }
            return item;
        }
    }

    public new TItem Item(object index) => this[index];
    
    public new TItem Item(object row, object column) => this[row, column];

    public bool TryGetValue(object index, out TItem item) {
        if (_missing) { item = default!; return false; }
        try { item = this[index]; return true; }
        catch { item = default!; return false; }
    }

    public bool Exists(object index) {
        if (_missing)
            return false;
        try { _ = this[index]; return true; }
        catch { return false; }
    }

    public new IEnumerator<TItem> GetEnumerator() {
        object? rawEnum = null;
        try { rawEnum = ComInvoker.DirectInvoke(this, "_NewEnum", ComBehavior.InvocationMode, ForceLateInvoke, -4, ComInvokeKind.PropertyGet, TypeCacheKey, null); }
        catch (global::System.Runtime.InteropServices.COMException) when (!IsMissing) { }

        int fetchedCount = 0;
        if (rawEnum is ComPointerLease enumLease) {
            IntPtr pEnumVariant = IntPtr.Zero;
            try {
                if (ComStubOps.QueryInterface(enumLease.ComPointer, in ComStubOps.IID_IEnumVARIANT, out pEnumVariant) < 0)
                    pEnumVariant = IntPtr.Zero;
                while (pEnumVariant != IntPtr.Zero && ComStubOps.TryEnumVariantNext<ComProxy>(pEnumVariant, out object? rawItem)) {
                    TItem item = default!;
                    if (typeof(TItem) == typeof(string)) {
                        item = (TItem)(object)(rawItem?.ToString() ?? string.Empty);
                    }
                    else if (rawItem != null) {
                        item = ComObjectExtensions.As<TItem>(rawItem);
                        InheritTracker(item);
                        if (ForceLateInvoke && item is ComProxy asComProxy) { asComProxy.ForceLateInvoke = true; }
                    }

                    if (item != null) {
                        fetchedCount++;
                        yield return item;
                    }
                }
            }
            finally {
                if (pEnumVariant != IntPtr.Zero)
                    ComStubOps.Release(pEnumVariant);
                enumLease.Dispose();
            }
        }

        if (fetchedCount == 0) {
            int count = Count;
            for (int i = 1; i <= count; i++) {
                TItem item = this[i];
                if (item != null)
                    yield return item;
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    // __UIA_TYPED_COLLECTION_MEMBERS_END__
}

internal static class VariantDebuggerDisplay {
    internal static string FormatValue(object value) => value switch {
        string text => $"\"{text}\"",
        char character => $"'{character}'",
        float number => number.ToString("R", global::System.Globalization.CultureInfo.InvariantCulture),
        double number => number.ToString("R", global::System.Globalization.CultureInfo.InvariantCulture),
        decimal number => number.ToString(global::System.Globalization.CultureInfo.InvariantCulture),
        DateTime dateTime => dateTime.ToString("O", global::System.Globalization.CultureInfo.InvariantCulture),
        Array array => $"{{{GetTypeName(array.GetType().GetElementType())}[{array.Length}]}}",
        _ => global::System.Convert.ToString(value, global::System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
    };

    internal static string GetTypeName(object? value) => GetTypeName(value?.GetType());

    private static string GetTypeName(global::System.Type? type) {
        if (type == null)
            return "null";
        if (type.IsArray)
            return $"{GetTypeName(type.GetElementType())}[]";
        if (type == typeof(string))
            return "string";
        if (type == typeof(bool))
            return "bool";
        if (type == typeof(byte))
            return "byte";
        if (type == typeof(sbyte))
            return "sbyte";
        if (type == typeof(short))
            return "short";
        if (type == typeof(ushort))
            return "ushort";
        if (type == typeof(int))
            return "int";
        if (type == typeof(uint))
            return "uint";
        if (type == typeof(long))
            return "long";
        if (type == typeof(ulong))
            return "ulong";
        if (type == typeof(float))
            return "float";
        if (type == typeof(double))
            return "double";
        if (type == typeof(decimal))
            return "decimal";
        if (type == typeof(char))
            return "char";
        if (type == typeof(DateTime))
            return "DateTime";
        return type.FullName ?? type.Name;
    }
}

internal readonly record struct ComGeneratedMember(string Name, string ComName, int DispId, bool IsBlocked, int Index);

internal sealed class ComProxyDebugView {
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly ComProxy _target;

    public ComProxyDebugView(ComProxy target) => _target = target;

    public bool Found => _target != null && _target.Found;
    public bool IsCreated => _target != null && _target.IsCreated;
    public bool IsMissing => _target == null || _target.IsMissing;
    public bool IsPoly => _target != null && _target.IsPoly;
    public bool ForceLateInvoke => _target != null && _target.ForceLateInvoke;
    public DynamicComTree _DynamicDisplay => _target._DynamicDisplay;

    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    public ComGeneratedPropertyNode[] Properties => CreateGeneratedPropertyNodes(_target);

    internal static ComGeneratedPropertyNode[] CreateGeneratedPropertyNodes(ComProxy target) {
        if (target == null || target.IsMissing || target.ComPointer == IntPtr.Zero)
            return Array.Empty<ComGeneratedPropertyNode>();

        ComGeneratedMember[] members = target.GetDebuggerProperties();
        Array.Sort(members, static (left, right) => global::System.StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
        var nodes = new ComGeneratedPropertyNode[members.Length];
        for (int index = 0; index < members.Length; index++)
            nodes[index] = new ComGeneratedPropertyNode(target, members[index]);
        return nodes;
    }
}

[DebuggerDisplay("{Value}", Name = "{Name,nq}", Type = "{TypeName,nq}")]
internal sealed class ComGeneratedPropertyNode {
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly ComProxy _target;
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly ComGeneratedMember _member;
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private bool _evaluated;
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private object? _value;
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private string _invoking = ComInvoker.InvocationContext.Unknown;

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string Name => _member.Name;

    public ComGeneratedPropertyNode(ComProxy target, ComGeneratedMember member) {
        _target = target;
        _member = member;
    }

    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    public object? Value {
        get {
            Evaluate();
            return _value;
        }
    }

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string TypeName {
        get {
            Evaluate();
            return $"{GetDebuggerTypeName(_value)}{DbgInfoOfType()}";
        }
    }

    private string DbgInfoOfType() => _value is ComProxy asComProxy ? asComProxy.DbgInfoOfType() : _invoking;

    private void Evaluate() {
        if (_evaluated)
            return;
        _evaluated = true;

        if (_member.IsBlocked) {
            _value = "[Property excluded from automatic debugger evaluation]";
            _invoking = " [not evaluated]";
            return;
        }

        try {
            _value = _target.ReadDebuggerProperty(_member.Index);
            _invoking = ComInvoker.InvocationContext.LastPathLabel;
            if (_value is ComProxy asComProxy) {
                asComProxy._dbgTypeInfo = _invoking;
                if (_target.ForceLateInvoke)
                    asComProxy.ForceLateInvoke = true;
            }
        }
        catch {
            _value = new ComProxy(new object()) { _missing = true, _blockedMessage = "Unavailable in the debugger preview" };
            _invoking = ComInvoker.InvocationContext.LastPathLabel;
            ((ComProxy)_value)._dbgTypeInfo = _invoking;
        }
    }

    internal static string GetDebuggerTypeName(object? value) => value switch {
        null => "object",
        string => "string",
        bool => "bool",
        byte => "byte",
        sbyte => "sbyte",
        short => "short",
        ushort => "ushort",
        int => "int",
        uint => "uint",
        long => "long",
        ulong => "ulong",
        float => "float",
        double => "double",
        decimal => "decimal",
        char => "char",
        _ => value.GetType().FullName ?? value.GetType().Name
    };
}

[DebuggerTypeProxy(typeof(DynamicComTreeDebugView))]
[DebuggerDisplay("Expand (▶) to retrieve COM members using late invocation [late]")]
public class DynamicComTree {
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    internal readonly ComProxy Target;

    public DynamicComTree(ComProxy target) {
        Target = target;
    }
}

internal class DynamicComTreeDebugView {
    private readonly DynamicComTree _tree;

    public DynamicComTreeDebugView(DynamicComTree tree) {
        _tree = tree;
    }

    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    public ComPropertyNode[] Properties {
        get {
            var target = _tree.Target;
            if (target == null || target.IsMissing || target.ComPointer == IntPtr.Zero)
                return Array.Empty<ComPropertyNode>();

            try {
                string[] names = target.GetDebuggerMemberNames();
                Array.Sort(names, global::System.StringComparer.OrdinalIgnoreCase);
                var list = new global::System.Collections.Generic.List<ComPropertyNode>(names.Length);

                foreach (string name in names) {
                    if (name != "QueryInterface" && name != "AddRef" && name != "Release" &&
                        name != "GetTypeInfoCount" && name != "GetTypeInfo" && name != "GetIDsOfNames" && name != "Invoke") {
                        list.Add(new ComPropertyNode(target, name));
                    }
                }
                return list.ToArray();
            }
            catch {
                return Array.Empty<ComPropertyNode>();
            }
        }
    }

    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    public ComCollectionItemNode[] CollectionItems {
        get {
            if (_tree.Target is not ComProxies collection || collection.IsMissing || collection.ComPointer == IntPtr.Zero)
                return Array.Empty<ComCollectionItemNode>();

            try {
                object rawCount = ComInvoker.GetDynamicValue(collection, "Count", collection.ComBehavior.InvocationMode, forceLateInvoke: true, knownDispId: -1, typeKey: string.Empty);
                int count = global::System.Convert.ToInt32(rawCount, global::System.Globalization.CultureInfo.InvariantCulture);
                int displayedCount = global::System.Math.Min(global::System.Math.Max(0, count), CollectionDebuggerDisplay.ElementsLimit);
                var items = new ComCollectionItemNode[displayedCount];
                for (int index = 1; index <= displayedCount; index++)
                    items[index - 1] = new ComCollectionItemNode(collection, index, forceLateInvoke: true);

                return items;
            }
            catch {
                return Array.Empty<ComCollectionItemNode>();
            }
        }
    }
}

[DebuggerDisplay("{DebuggerValue,nq}", Name = "{Name,nq}", Type = "{TypeName,nq} [late]")]
internal class ComPropertyNode {
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly ComProxy _target;
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private bool _evaluated;
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private object? _value;

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string Name { get; }

    public ComPropertyNode(ComProxy target, string name) {
        _target = target;
        Name = name;
    }

    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    public object? Value => EvaluateCurrent();

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public object? DebuggerValue {
        get {
            object? value = EvaluateCurrent();
            if (_target is ComProxies && Name.Equals("Count", global::System.StringComparison.OrdinalIgnoreCase)) {
                try {
                    int count = global::System.Convert.ToInt32(value, global::System.Globalization.CultureInfo.InvariantCulture);
                    return CollectionDebuggerDisplay.FormatCount(count);
                }
                catch { }
            }
            return value;
        }
    }

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string TypeName {
        get {
            object? value = EvaluateCurrent();
            return value switch {
                null => "object",
                string => "string",
                bool => "bool",
                byte => "byte",
                sbyte => "sbyte",
                short => "short",
                ushort => "ushort",
                int => "int",
                uint => "uint",
                long => "long",
                ulong => "ulong",
                float => "float",
                double => "double",
                decimal => "decimal",
                char => "char",
                _ => value.GetType().FullName ?? value.GetType().Name
            };
        }
    }

    private object? EvaluateCurrent() {
        if (_evaluated)
            return _value;
        _evaluated = true;
        try {
            _value = ComInvoker.GetDynamicValue(_target, Name, _target.ComBehavior.InvocationMode, forceLateInvoke: true, knownDispId: -1, typeKey: string.Empty);
            if (_value is ComProxy asComProxy) {
                asComProxy.ForceLateInvoke = true;
                asComProxy._dbgTypeInfo = ComInvoker.InvocationContext.LastPathLabel;
            }
            return _value;
        }
        catch {
            return _value = new ComProxy(new object()) { _missing = true, _blockedMessage = "Unavailable in the debugger preview" };
        }
    }
}

internal class ComProxiesUntypedDebugView {
    private readonly ComProxies _collection;
    public ComProxiesUntypedDebugView(ComProxies collection) => _collection = collection;

    public bool Found => _collection != null && _collection.Found;
    public bool IsCreated => _collection != null && _collection.IsCreated;
    public bool IsMissing => _collection == null || _collection.IsMissing;
    public bool IsPoly => _collection != null && _collection.IsPoly;
    public bool ForceLateInvoke => _collection != null && _collection.ForceLateInvoke;

    public DebugValue<int> Count {
        get {
            if (_collection == null || _collection._missing)
                return new DebugValue<int>(0, "int", ComInvoker.InvocationContext.Unknown);
            try {
                int value = _collection.Count;
                return new DebugValue<int>(value, "int", ComInvoker.InvocationContext.LastPathLabel, CollectionDebuggerDisplay.FormatCount(value));
            }
            catch { return new DebugValue<int>(-1, "int", ComInvoker.InvocationContext.LastPathLabel); }
        }
    }

    public DynamicComTree _DynamicDisplay => _collection._DynamicDisplay;

    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    public ComGeneratedPropertyNode[] Properties => ComProxyDebugView.CreateGeneratedPropertyNodes(_collection);

    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    public ComCollectionItemNode[] Items {
        get {
            if (_collection == null || _collection._missing)
                return Array.Empty<ComCollectionItemNode>();
            try {
                int count = global::System.Math.Min(_collection.Count, CollectionDebuggerDisplay.ElementsLimit);
                var items = new ComCollectionItemNode[global::System.Math.Max(0, count)];
                for (int index = 1; index <= count; index++)
                    items[index - 1] = new ComCollectionItemNode(_collection, index, forceLateInvoke: false);
                return items;
            }
            catch { return Array.Empty<ComCollectionItemNode>(); }
        }
    }
}

[DebuggerTypeProxy(typeof(ComProxiesUntypedDebugView))]
[DebuggerDisplay("{DbgInfoOfValue(),nq}", Name = "{DbgInfoOfName(),nq}")]
public class ComProxies : ComProxy, IEnumerable<ComProxy> {
    public ComProxies(object comInstance) : base(comInstance) { }
    public virtual int Count => GetValue<int>(nameof(Count), -1);

    public IEnumerator<ComProxy> GetEnumerator() {
        object? rawEnum = null;
        try { rawEnum = ComInvoker.DirectInvoke(this, "_NewEnum", ComBehavior.InvocationMode, ForceLateInvoke, -4, ComInvokeKind.PropertyGet, TypeCacheKey, null); }
        catch (global::System.Runtime.InteropServices.COMException) when (!IsMissing) { }

        int fetchedCount = 0;
        if (rawEnum is ComPointerLease enumLease) {
            IntPtr pEnumVariant = IntPtr.Zero;
            try {
                if (ComStubOps.QueryInterface(enumLease.ComPointer, in ComStubOps.IID_IEnumVARIANT, out pEnumVariant) < 0)
                    pEnumVariant = IntPtr.Zero;
                while (pEnumVariant != IntPtr.Zero && ComStubOps.TryEnumVariantNext<ComProxy>(pEnumVariant, out object? rawItem)) {
                    if (rawItem == null)
                        continue;
                    fetchedCount++;
                    yield return InheritTracker(ComObjectExtensions.ToComProxy(rawItem));
                }
            }
            finally {
                if (pEnumVariant != IntPtr.Zero)
                    ComStubOps.Release(pEnumVariant);
                enumLease.Dispose();
            }
        }

        if (fetchedCount == 0) {
            int count = Count;
            for (int i = 1; i <= count; i++) {
                ComProxy item = this[i];
                if (item != null)
                    yield return item;
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

[DebuggerDisplay("{Value}", Name = "{Key,nq}", Type = "{TypeName,nq}")]
internal readonly struct DebugEntry<T> {
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string Key { get; }

    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    public T Value { get; }

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly string _invoking;

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string TypeName {
        get {
            string typeName = Value?.GetType().FullName ?? typeof(T).FullName ?? typeof(T).Name;
            return $"{typeName}{_invoking}";
        }
    }

    public DebugEntry(string key, T value, string invoking = "") {
        Key = key;
        Value = value;
        _invoking = invoking;
    }
}

[DebuggerDisplay("{Value}", Name = "{Name,nq}", Type = "{TypeName,nq}")]
internal sealed class ComCollectionItemNode {
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly ComProxies _collection;
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly int _index;
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly bool _forceLateInvoke;
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private bool _evaluated;
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private object? _value;
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private string _name;
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private string _invoking = ComInvoker.InvocationContext.Unknown;

    public ComCollectionItemNode(ComProxies collection, int index, bool forceLateInvoke) {
        _collection = collection;
        _index = index;
        _forceLateInvoke = forceLateInvoke;
        _name = $"[{index}]";
    }

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string Name {
        get {
            Evaluate();
            return _name;
        }
    }

    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    public object? Value {
        get {
            Evaluate();
            return _value;
        }
    }

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string TypeName {
        get {
            Evaluate();
            return $"{ComGeneratedPropertyNode.GetDebuggerTypeName(_value)}{DbgInfoOfType()}";
        }
    }

    private string DbgInfoOfType() => _value is ComProxy asComProxy ? asComProxy.DbgInfoOfType() : _invoking;

    private void Evaluate() {
        if (_evaluated)
            return;
        _evaluated = true;

        try {
            object rawItem = ComInvoker.GetIndexer(
                _collection,
                _index,
                _collection.ComBehavior.InvocationMode,
                forceLateInvoke: _forceLateInvoke || _collection.ForceLateInvoke,
                typeKey: _forceLateInvoke ? string.Empty : _collection.TypeCacheKey);
            _invoking = ComInvoker.InvocationContext.LastPathLabel;
            _value = ComStubOps.IsComObject(rawItem) || rawItem is IComWrapper
                ? ComObjectExtensions.ToComProxy(rawItem)
                : rawItem;
            if (_value is ComProxy comProxy) {
                comProxy.AttachBehavior(_collection.ComBehavior);
                comProxy._dbgTypeInfo = _invoking;
                if (_forceLateInvoke || _collection.ForceLateInvoke)
                    comProxy.ForceLateInvoke = true;
                _name = CollectionDebugItemKey.Resolve(comProxy, _index, _collection.ComBehavior.InvocationMode);
            }
        }
        catch {
            _invoking = ComInvoker.InvocationContext.LastPathLabel;
            _value = new ComProxy(new object()) { _missing = true, _blockedMessage = "Unavailable in the debugger preview" };
            ((ComProxy)_value)._dbgTypeInfo = _invoking;
        }
    }
}

internal static class CollectionDebugItemKey {
    public static string Resolve(object? item, int fallbackIndex, InvokeMode invokeType) {
        if (item == null || !ComStubOps.IsComObject(item))
            return $"[{fallbackIndex}]";

        try {
            object? commandName = null;
            try { commandName = ComInvoker.GetProperty(item, "CommandName", invokeType, forceLateInvoke: true, knownDispId: -1, typeKey: string.Empty); }
            catch { }

            object? dialogType = null;
            try { dialogType = ComInvoker.GetProperty(item, "Type", invokeType, forceLateInvoke: true, knownDispId: -1, typeKey: string.Empty); }
            catch { }

            string commandNameText = commandName?.ToString()?.Trim() ?? string.Empty;
            if (!string.IsNullOrEmpty(commandNameText) && dialogType != null) {
                string typeText = dialogType.ToString()?.Trim() ?? string.Empty;
                return typeText.Equals(commandNameText, global::System.StringComparison.OrdinalIgnoreCase)
                    ? $"[{commandNameText}]"
                    : $"[{typeText}: {commandNameText}]";
            }
            if (!string.IsNullOrEmpty(commandNameText))
                return $"[\"{commandNameText}\"]";
            if (dialogType != null)
                return $"[{dialogType}]";

            object? id = null;
            try { id = ComInvoker.GetProperty(item, "ID", invokeType, forceLateInvoke: true, knownDispId: -1, typeKey: string.Empty); }
            catch { }

            object? name = null;
            try { name = ComInvoker.GetProperty(item, "Name", invokeType, forceLateInvoke: true, knownDispId: -1, typeKey: string.Empty) ?? ComInvoker.GetProperty(item, "NameLocal", invokeType, forceLateInvoke: true, knownDispId: -1, typeKey: string.Empty ); }
            catch { }

            string nameText = name?.ToString()?.Trim() ?? string.Empty;
            if (id != null && !string.IsNullOrEmpty(nameText)) {
                string idText = id.ToString()?.Trim() ?? string.Empty;
                return idText.Equals(nameText, global::System.StringComparison.OrdinalIgnoreCase)
                    ? $"[{nameText}]"
                    : $"[{idText}: {nameText}]";
            }
            if (id != null)
                return $"[{id}]";
            if (!string.IsNullOrEmpty(nameText))
                return $"[\"{nameText}\"]";

            object? index = null;
            try { index = ComInvoker.GetProperty(item, "Index", invokeType, forceLateInvoke: true, knownDispId: -1, typeKey: string.Empty); }
            catch { }
            if (index != null)
                return $"[Index: {index}]";
        }
        catch { }

        return $"[{fallbackIndex}]";
    }
}

[DebuggerDisplay("{DebuggerValue,nq}", Type = "{TypeName,nq}")]
internal readonly struct DebugValue<T> {
    public T Value { get; }

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string TypeName { get; }

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string DebuggerValue { get; }

    public DebugValue(T value, string typeName, string invoking, string? debuggerValue = null) {
        Value = value;
        TypeName = $"{typeName}{invoking}";
        DebuggerValue = debuggerValue ?? global::System.Convert.ToString(value, global::System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }
}

internal static class CollectionDebuggerDisplay {
    internal const int ElementsLimit = int.MaxValue;

    internal static string FormatCount(int count) => (count > ElementsLimit)
        ? $"{count} (view limited to the first {ElementsLimit} items)"
        : count.ToString(global::System.Globalization.CultureInfo.InvariantCulture);
}

internal class BlockedCollectionDebugView<TItem> where TItem : class {
    private readonly ComProxies _collection;
    private ComCollectionItemNode[]? _cachedItems;

    public BlockedCollectionDebugView(ComProxies collection) => _collection = collection;

    public string Status => _collection == null || _collection._missing
        ? "No match (Nothing)"
        : "Full enumeration blocked to prevent COM timeouts";

    public int LiczbaWszystkichElementów {
        get {
            if (_collection == null || _collection._missing)
                return 0;
            try { return _collection.Count; }
            catch { return -1; }
        }
    }

    public string Wskazówka => $"Access items directly through the indexer {_collection.GetType().Name}[key] or by enumerating the collection.";

    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    public ComCollectionItemNode[] Pierwsze25Elementów {
        get {
            if (_collection == null || _collection._missing)
                return Array.Empty<ComCollectionItemNode>();
            if (_cachedItems != null)
                return _cachedItems;
            const int collectionDisplayLimit = CollectionDebuggerDisplay.ElementsLimit;

            try {
                int count = global::System.Math.Min(_collection.Count, collectionDisplayLimit);
                var items = new ComCollectionItemNode[global::System.Math.Max(0, count)];
                for (int index = 1; index <= count; index++)
                    items[index - 1] = new ComCollectionItemNode(_collection, index, forceLateInvoke: false);
                return _cachedItems = items;
            }
            catch { }

            return _cachedItems = Array.Empty<ComCollectionItemNode>();
        }
    }
}

internal class BlockedCollectionDebugView {
    private readonly ComProxies _collection;
    public BlockedCollectionDebugView(ComProxies collection) => _collection = collection;

    public string Status => _collection == null || _collection._missing
        ? "No match (Nothing)"
        : "Full enumeration blocked to prevent timeouts and protect the COM thread";

    public int LiczbaElementów {
        get {
            if (_collection == null || _collection._missing)
                return 0;
            try { return _collection.Count; }
            catch { return -1; }
        }
    }

    public string Wskazówka => $"Access items only through the indexer, e.g. app.{_collection.GetType().Name}[key], or by enumerating the collection.";
}

internal class ComProxiesDebugView<TItem> where TItem : class {
    private readonly ComProxies _collection;
    public ComProxiesDebugView(ComProxies collection) => _collection = collection;

    public bool Found => _collection != null && _collection.Found;
    public bool IsCreated => _collection != null && _collection.IsCreated;
    public bool IsMissing => _collection == null || _collection.IsMissing;
    public bool IsPoly => _collection != null && _collection.IsPoly;
    public bool ForceLateInvoke => _collection != null && _collection.ForceLateInvoke;

    public DebugValue<int> Count {
        get {
            if (_collection == null || _collection._missing)
                return new DebugValue<int>(0, "int", ComInvoker.InvocationContext.NotEvaluated);
            try {
                int value = _collection.Count;
                return new DebugValue<int>(value, "int", ComInvoker.InvocationContext.LastPathLabel, CollectionDebuggerDisplay.FormatCount(value));
            }
            catch { return new DebugValue<int>(-1, "int", ComInvoker.InvocationContext.LastPathLabel); }
        }
    }

    public DynamicComTree _DynamicDisplay => _collection._DynamicDisplay;

    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    public ComGeneratedPropertyNode[] Properties => ComProxyDebugView.CreateGeneratedPropertyNodes(_collection);

    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    public ComCollectionItemNode[] Items {
        get {
            if (_collection == null || _collection._missing)
                return Array.Empty<ComCollectionItemNode>();
            try {
                int count = global::System.Math.Min(_collection.Count, CollectionDebuggerDisplay.ElementsLimit);
                var items = new ComCollectionItemNode[global::System.Math.Max(0, count)];
                for (int index = 1; index <= count; index++)
                    items[index - 1] = new ComCollectionItemNode(_collection, index, forceLateInvoke: false);
                return items;
            }
            catch { return Array.Empty<ComCollectionItemNode>(); }
        }
    }
}

public static class ComObjectExtensions {
    internal static unsafe bool TryGetComTypeIdentity(object value, out Guid typeLibGuid, out string typeName) {
        typeLibGuid = Guid.Empty;
        typeName = string.Empty;
        if (value == null)
            return false;

        IntPtr pDispatch = IntPtr.Zero;
        bool releaseDispatch = false;

        if (value is IComWrapper wrapper) {
            pDispatch = wrapper.ComPointer;
        }
        else if (value is IntPtr pointer && pointer != IntPtr.Zero) {
            if (ComStubOps.QueryInterface(pointer, in ComStubOps.IID_IDispatch, out pDispatch) < 0)
                return false;
            releaseDispatch = true;
        }
        else {
            return false;
        }

        if (pDispatch == IntPtr.Zero)
            return false;

        try {
            if (ComStubOps.TryGetVTableFunction(pDispatch, 4, out IntPtr getTypeInfoFunction)) {
                var getTypeInfo = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, int>)getTypeInfoFunction;
                IntPtr pTypeInfo = IntPtr.Zero;

                if (getTypeInfo(pDispatch, 0, 0, &pTypeInfo) == 0 && pTypeInfo != IntPtr.Zero) {
                    try {
                        if (ComStubOps.TryGetVTableFunction(pTypeInfo, 12, out IntPtr getDocumentationFunction)) {
                            var getDocumentation = (delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, IntPtr*, uint*, IntPtr*, int>)getDocumentationFunction;
                            IntPtr bstrName = IntPtr.Zero;
                            if (getDocumentation(pTypeInfo, -1, &bstrName, null, null, null) == 0 && bstrName != IntPtr.Zero) {
                                try {
                                    string docName = ComStubOps.BstrToString(bstrName) ?? string.Empty;
                                    if (!string.IsNullOrEmpty(docName)) {
                                        typeName = docName.StartsWith('_') && docName.Length > 1 ? docName[1..] : docName;
                                    }
                                }
                                finally {
                                    ComStubOps.SysFreeString(bstrName);
                                }
                            }
                        }

                        if (!string.IsNullOrEmpty(typeName) &&
                            ComStubOps.TryGetVTableFunction(pTypeInfo, 18, out IntPtr getContainingTypeLibFunction)) {
                            var getContainingTypeLib = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, uint*, int>)getContainingTypeLibFunction;
                            IntPtr pTypeLib = IntPtr.Zero;
                            uint typeIndex = 0;
                            if (getContainingTypeLib(pTypeInfo, &pTypeLib, &typeIndex) == 0 && pTypeLib != IntPtr.Zero) {
                                try {
                                    if (ComStubOps.TryGetVTableFunction(pTypeLib, 7, out IntPtr getLibAttrFunction)) {
                                        var getLibAttr = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)getLibAttrFunction;
                                        IntPtr pLibAttr = IntPtr.Zero;
                                        if (getLibAttr(pTypeLib, &pLibAttr) == 0 && pLibAttr != IntPtr.Zero) {
                                            try {
                                                typeLibGuid = *(Guid*)pLibAttr;
                                            }
                                            finally {
                                                if (ComStubOps.TryGetVTableFunction(pTypeLib, 12, out IntPtr releaseLibAttrFunction)) {
                                                    var releaseLibAttr = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)releaseLibAttrFunction;
                                                    releaseLibAttr(pTypeLib, pLibAttr);
                                                }
                                            }
                                        }
                                    }
                                }
                                finally {
                                    ComStubOps.Release(pTypeLib);
                                }
                            }
                        }
                    }
                    finally {
                        ComStubOps.Release(pTypeInfo);
                    }
                }
            }
        }
        catch { }
        finally {
            if (releaseDispatch)
                ComStubOps.Release(pDispatch);
        }

        return !string.IsNullOrEmpty(typeName);
    }

    public static string GetComTypeName(object value) =>
        TryGetComTypeIdentity(value, out _, out string typeName) ? typeName : string.Empty;

    public static ComProxy ToComProxy(this object ComStub) {
        if (ComStub == null)
            return ComProxy.Missing();
        bool isPoly = (ComStub as ComProxy)?.IsPoly ?? false;
        bool forceLateInvoke = (ComStub as ComProxy)?.ForceLateInvoke ?? false;
        if (ComStub is ComProxy asComProxy) { return asComProxy; }
        if (ComStub is ComPointerLease lease) {
            try {
                var resolvedLease = ComProxyFactory.Resolve(lease);
                if (isPoly)
                    resolvedLease.IsPoly = true;
                if (forceLateInvoke)
                    resolvedLease.ForceLateInvoke = true;
                return resolvedLease;
            }
            finally { lease.Dispose(); }
        }
        var resolved = ComProxyFactory.Resolve(ComStub);
        if (isPoly)
            resolved.IsPoly = true;
        if (forceLateInvoke)
            resolved.ForceLateInvoke = true;
        return resolved;
    }

    public static ComProxies AsCollection(this ComProxy comProxy) {
        if (comProxy == null || comProxy.ComPointer == IntPtr.Zero)
            throw new ArgumentNullException(nameof(comProxy));
        var collection = new ComProxies(comProxy);
        if (comProxy.IsPoly)
            collection.IsPoly = true;
        if (comProxy.ForceLateInvoke)
            collection.ForceLateInvoke = true;
        return collection;
    }

    public static ComProxies<TItem> AsCollection<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TItem>(this ComProxy comProxy) where TItem : class {
        if (comProxy == null || comProxy.ComPointer == IntPtr.Zero)
            throw new ArgumentNullException(nameof(comProxy));
        var collection = new ComProxies<TItem>(comProxy);
        if (comProxy.IsPoly)
            collection.IsPoly = true;
        if (comProxy.ForceLateInvoke)
            collection.ForceLateInvoke = true;
        return collection;
    }

    public static TExt AsExternal<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TExt>(this object value) where TExt : class {
        if (value == null)
            throw new ArgumentNullException(nameof(value));
        bool isPoly = (value as IComBindingState)?.IsPoly ?? false;
        bool forceLateInvoke = (value as IComBindingState)?.ForceLateInvoke ?? false;
        if (value is TExt ext) {
            if (ext is IComBindingState externalState) {
                if (isPoly)
                    externalState.IsPoly = true;
                if (forceLateInvoke)
                    externalState.ForceLateInvoke = true;
            }
            return ext;
        }
        if (!ComStubOps.IsComObject(value))
            throw new InvalidOperationException("The object is not a valid COM pointer.");
        object target = value;
        try {
            var inst = (TExt)global::System.Activator.CreateInstance(typeof(TExt), target)!;
            if (inst is IComBindingState instanceState) {
                if (isPoly)
                    instanceState.IsPoly = true;
                if (forceLateInvoke)
                    instanceState.ForceLateInvoke = true;
            }
            return inst;
        }
        catch (global::System.Exception ex) { throw new global::System.InvalidCastException($"Failed to initialize external type {typeof(TExt).Name}.", ex); }
    }

    public static ComProxy[] NormalizeSelection(object rawSelection) {
        if (rawSelection == null || rawSelection is global::System.Reflection.Missing)
            return Array.Empty<ComProxy>();
        if (rawSelection is ComProxy[] existingArr)
            return existingArr;
        var single = rawSelection.ToComProxy();
        return [single];
    }

    public static object As(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type wrapperType,
        object comStub) {

        if (comStub == null)
            throw new ArgumentNullException(nameof(comStub));

        ComProxy? asComProxy = comStub as ComProxy;
        IComBindingState? wrapperState = comStub as IComBindingState;

        bool asComProxyIsPoly = wrapperState?.IsPoly ?? false;
        bool forceLateInvoke = wrapperState?.ForceLateInvoke ?? false;

        if (wrapperType.IsInstanceOfType(comStub)) {
            return comStub;
        }

        if (comStub is ComPointerLease lease) {
            try {
                object result = wrapperType == typeof(ComProxy)
                    ? new ComProxy(lease)
                    : Activator.CreateInstance(wrapperType, [lease])!;
                if (result is IComBindingState resultState) {
                    if (asComProxyIsPoly)
                        resultState.IsPoly = true;
                    if (forceLateInvoke)
                        resultState.ForceLateInvoke = true;
                }
                return result;
            }
            catch (global::System.Exception ex) {
                throw new global::System.InvalidCastException($"Failed to create a wrapper of type {wrapperType.Name} from a COM pointer.", ex);
            }
            finally {
                lease.Dispose();
            }
        }

        if (asComProxy == null) {
            if (comStub is IntPtr ptr) {
                asComProxy = new ComProxy(ptr);
            }
            else {
                throw new InvalidCastException($"The input object is neither a ComProxy nor an IComWrapper (type: {comStub.GetType().FullName}).");
            }
        }

        if (wrapperType == typeof(ComProxy)) {
            return asComProxy;
        }

        try {
            object result = Activator.CreateInstance(wrapperType, [asComProxy])!;
            if (result is IComBindingState resultState) {
                if (asComProxyIsPoly)
                    resultState.IsPoly = true;
                if (forceLateInvoke)
                    resultState.ForceLateInvoke = true;
            }
            if (result is ComProxy localResult)
                localResult._missing = asComProxy._missing;
            return result;
        }
        catch (global::System.Exception ex) {
            throw new global::System.InvalidCastException($"Failed to create a wrapper of type {wrapperType.Name} from a ComProxy object.", ex);
        }
    }

    public static TWrapper As<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TWrapper>(this object ComStub) where TWrapper : class {
        return (TWrapper)As(typeof(TWrapper), ComStub);
    }
}

// =========================================================================
// STATIC TYPE FACTORY (REFLECTION-FREE PATTERN MATCHING FOR NATIVE AOT)
// =========================================================================
public static partial class ComProxyFactory {
    private sealed class ScopedTypeKeyComparer : IEqualityComparer<(Guid TypeLibGuid, string TypeName)> {
        public bool Equals((Guid TypeLibGuid, string TypeName) x, (Guid TypeLibGuid, string TypeName) y) =>
            x.TypeLibGuid == y.TypeLibGuid &&
            string.Equals(x.TypeName, y.TypeName, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((Guid TypeLibGuid, string TypeName) obj) =>
            HashCode.Combine(obj.TypeLibGuid, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.TypeName));
    }

    private static readonly Dictionary<(Guid TypeLibGuid, string TypeName), Func<object, ComProxy>> _scopedTypeRegistry = new(new ScopedTypeKeyComparer());
    private static readonly Dictionary<string, Func<object, ComProxy>> _typeRegistry = new(StringComparer.OrdinalIgnoreCase);

    public static ComProxy Resolve(object rawCom) {
        if (rawCom == null)
            return ComProxy.Missing();
        if (rawCom is ComProxy proxy)
            return proxy;
        object target = rawCom;
        if (!ComStubOps.IsComObject(target))
            return new ComProxy(target);

        if (ComObjectExtensions.TryGetComTypeIdentity(target, out Guid typeLibGuid, out string comTypeName)) {
            if (typeLibGuid != Guid.Empty && _scopedTypeRegistry.TryGetValue((typeLibGuid, comTypeName), out var scopedCreator)) {
                return scopedCreator(target);
            }
            if (!string.IsNullOrEmpty(comTypeName) && _typeRegistry.TryGetValue(comTypeName, out var creator)) {
                return creator(target);
            }
        }

        return new ComProxy(target);
    }
}