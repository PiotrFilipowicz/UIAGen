//------------------------------------------------------------------------------
// <copyright file="Wrapper.WrapperGenerator.cs" company="Piotr Filipowicz">
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
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace UiaAdapter;

public partial class UiaAdapterWrapper {
    private static readonly HashSet<string> ComProxyPropertyNames = new(StringComparer.Ordinal) {
        "Value",
        "IsMissing"
    };

    private static readonly HashSet<string> ComProxyMethodNames = new(StringComparer.Ordinal) {
        "Item",
        "GetProperty",
        "SetProperty"
    };

    private static readonly HashSet<string> ComProxyMethodSignatures = new(StringComparer.Ordinal) {
        "Item(object)",
        "Item(object, object)",
        "GetProperty(string)",
        "SetProperty(string, object)"
    };

    private string? _cachedAdobeObjectsMembersSource;
    private AdobeObjectsMemberSignatures? _cachedAdobeObjectsSignatures;
    private readonly HashSet<string> _emittedAdobeObjectsPropertyNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _emittedAdobeObjectsMethodNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _emittedAdobeObjectsMethodSignatures = new(StringComparer.Ordinal);
    private bool _hasEmittedAdobeObjectsMembers;

    private sealed record AdobeObjectsMemberSignatures(
        HashSet<string> PropertyNames,
        HashSet<string> MethodNames,
        HashSet<string> MethodSignatures);

    private enum CollectionType {
        none,
        raw,
        typed,
        rawAdobe,
        typedAdobe
    }

    bool IsTypedCollection(CollectionType collectionType) => collectionType == CollectionType.typed || collectionType == CollectionType.typedAdobe;
    bool IsAdobeCollection(CollectionType collectionType) => collectionType == CollectionType.rawAdobe || collectionType == CollectionType.typedAdobe;

    internal void GenerateStrongWrapper(string baseName, ITypeInfo typeInfo, Action<string> AppendLine) {
        using var pAttrLease = TypeInfoDescriptor.TypeAttributes(typeInfo);
        nint pAttr = pAttrLease.Pointer;
        TYPEATTR attr = Marshal.PtrToStructure<TYPEATTR>(pAttr);

        CollectionType collectionType = (baseName.Equals("Objects", StringComparison.OrdinalIgnoreCase), HasAdobeObjectsModel) switch {
            (true, false) => CollectionType.raw,
            (true, true) => CollectionType.rawAdobe,
            (false, false) when IsTypeInfoCollection(typeInfo, attr) => CollectionType.typed,
            (false, true) when IsTypeInfoCollection(typeInfo, attr) => CollectionType.typedAdobe,
            _ => CollectionType.none
        };

        string inheritance;
        string itemName = baseName;
        string debuggerViewType;

        if (IsTypedCollection(collectionType)) {
            string? singularCandidate = GetSingularCandidate(baseName);

            bool hasSpecificItemWrapper = singularCandidate != null &&
                (KnownInterfaces.Contains(singularCandidate) ||
                 KnownExternalInterfaces.Contains(singularCandidate) ||
                 singularCandidate.Equals("string", StringComparison.OrdinalIgnoreCase));

            if (hasSpecificItemWrapper) {
                itemName = GetCanonicalTypeName(singularCandidate!);
                inheritance = collectionType == CollectionType.typedAdobe
                    ? $" : Objects<{itemName}>"
                    : $" : ComProxies<{itemName}>";
                debuggerViewType = $"ComProxiesDebugView<{itemName}>";
            }
            else {
                itemName = "ComProxy";
                inheritance = collectionType == CollectionType.typedAdobe ? " : Objects" : " : ComProxies";
                debuggerViewType = "ComProxiesUntypedDebugView";
            }
        }
        else {
            (inheritance, debuggerViewType) = collectionType switch {
                CollectionType.raw => (" : ComProxy, global::System.Collections.Generic.IEnumerable<ComProxy>, global::System.Collections.IEnumerable", "ComProxyDebugView"),
                CollectionType.rawAdobe => (" : ComProxies", "ComProxiesUntypedDebugView"),
                _ => (" : ComProxy", "ComProxyDebugView")
            };
        }

        AppendLine(/*lang=c#*/ $$"""
        [global::System.Diagnostics.DebuggerTypeProxy(typeof({{debuggerViewType}}))]
        """);

        bool baseHasGetMissing = collectionType == CollectionType.typedAdobe;
        string getMissingNewMod = baseHasGetMissing ? "new " : "";
        string safeTypeName = Sanitize(baseName);
        AppendLine(/*lang=c#*/ $$"""
public class {{safeTypeName}}{{inheritance}} {
    public {{getMissingNewMod}}static {{safeTypeName}} GetMissing() => global::{{RootNamespace}}.ComProxy.Missing<{{safeTypeName}}>();
    {{(!IsTypedCollection(collectionType) && baseName.Equals("Application", StringComparison.OrdinalIgnoreCase)
        ? $"public {safeTypeName}(object comInstance) : base(comInstance) {{ AttachBehavior(new ComBehavior());}}"
        : $"public {safeTypeName}(object comInstance) : base(comInstance) {{ }}")}}
{{(!IsTypedCollection(collectionType) && baseName.Equals("Application", StringComparison.OrdinalIgnoreCase) ? """
    public static Application Create(string progId) {
        using var appPtr = ComInvoker.CreateFromProgID(progId);
        var app = new Application(appPtr);
        app.ComBehavior?.Tracker.GetCheckPoint();
        return app;
    }
""" : null)}}
""");

        if (collectionType == CollectionType.rawAdobe)
            AppendLine(AdobeObjectsMembers);

        AdobeObjectsMemberSignatures? adobeObjectsSignatures = collectionType == CollectionType.typedAdobe ? GetAdobeObjectsMemberSignatures() : null;

        MemberModel members = ReadMembers(typeInfo, attr.cFuncs, itemName, IsTypedCollection(collectionType));
        var props = members.Properties;
        var funcs = members.Methods;
        var (indexerHasGet, indexerHasSet, indexerHasSetPut, indexerHasSetRef, indexerParamCount, indexerRawRet, indexerWrapRet) = members.Indexer;
        HashSet<string> emittedWrapperMethodSignatures = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> emittedMemberNames = new(StringComparer.OrdinalIgnoreCase);

        emittedMemberNames.Add("QueryInterface");
        emittedMemberNames.Add("AddRef");
        emittedMemberNames.Add("Release");
        emittedMemberNames.Add("GetTypeInfoCount");
        emittedMemberNames.Add("GetTypeInfo");
        emittedMemberNames.Add("GetIDsOfNames");
        emittedMemberNames.Add("GetDebuggerProperties");
        emittedMemberNames.Add("ReadDebuggerProperty");

        if (collectionType is not CollectionType.none and not CollectionType.raw) {
            emittedMemberNames.Add("Count");
            emittedMemberNames.Add("Item");
            emittedMemberNames.Add("_Default");
            emittedMemberNames.Add("_NewEnum");
            emittedMemberNames.Add("this");
            emittedMemberNames.Add("TryGetValue");
            emittedMemberNames.Add("Exists");
            emittedMemberNames.Add("GetEnumerator");
        }

        if (IsAdobeCollection(collectionType)) {
            emittedMemberNames.Add("FirstItem");
            emittedMemberNames.Add("LastItem");
            emittedMemberNames.Add("MiddleItem");
            emittedMemberNames.Add("PreviousItem");
            emittedMemberNames.Add("NextItem");

            emittedMemberNames.Add("AnyItem");
            emittedMemberNames.Add("ItemByID");
            emittedMemberNames.Add("ItemByRange");
        }

        if (collectionType == CollectionType.rawAdobe) {
            emittedMemberNames.Add("Add");
            emittedMemberNames.Add("Remove");
        }

        bool IsSafeDebuggerMethod(string name) =>
           SafeDebuggerMethodNames.Contains(name, StringComparer.OrdinalIgnoreCase);

        static string EscapeDebuggerMemberName(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        var debuggerPropertyMembers = props.Values
            .Where(prop => prop.HasGet && !emittedMemberNames.Contains(prop.Name))
            .Select(prop => {
                string displayName = EscapeDebuggerMemberName(prop.Name);
                string comName = EscapeDebuggerMemberName(GetCanonicalTypeName(prop.Name));
                string isBlocked = IsDebuggerBlacklisted(baseName, prop.Name) ? "true" : "false";
                string memberName = SafeMemberName(prop.Name, baseName);
                return (Metadata: $"\"{displayName}\", \"{comName}\", {prop.DispId}, {isBlocked}", Read: memberName);
            });
        var debuggerMethodMembers = funcs
            .Where(fn => IsAdobeCollection(collectionType) &&
                IsSafeDebuggerMethod(fn.Name) &&
                fn.Params.All(parameter => parameter.IsOptional))
            .Where(fn => !props.TryGetValue(fn.Name, out PropModel? prop) || !prop.HasGet)
            .GroupBy(fn => fn.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(fn => {
                string displayName = EscapeDebuggerMemberName(fn.Name);
                string comName = EscapeDebuggerMemberName(GetCanonicalTypeName(fn.Name));
                string isBlocked = IsDebuggerBlacklisted(baseName, fn.Name) ? "true" : "false";
                string memberName = SafeMemberName(fn.Name, baseName);
                return (Metadata: $"\"{displayName}\", \"{comName}\", {fn.DispId}, {isBlocked}", Read: $"{memberName}()");
            });
        var debuggerMembers = debuggerPropertyMembers
            .Concat(debuggerMethodMembers)
            .ToArray();
        AppendLine("    internal override ComGeneratedMember[] GetDebuggerProperties() => [");
        for (int index = 0; index < debuggerMembers.Length; index++)
            AppendLine($"        new ComGeneratedMember({debuggerMembers[index].Metadata}, {index}),");
        AppendLine("    ];");

        AppendLine("    internal override object ReadDebuggerProperty(int index) => index switch {");
        for (int index = 0; index < debuggerMembers.Length; index++)
            AppendLine($"        {index} => {debuggerMembers[index].Read},");
        AppendLine("        _ => throw new global::System.ArgumentOutOfRangeException(nameof(index))");
        AppendLine("    };");

        AppendLine("");

        if (indexerHasGet || indexerHasSet || IsTypedCollection(collectionType)) {
            emittedMemberNames.Add("Item");
            emittedMemberNames.Add("_Default");
            emittedMemberNames.Add("_NewEnum");
        }

        if ((collectionType is CollectionType.none or CollectionType.raw) && (indexerHasGet || indexerHasSet)) {
            string cleanIndexerType = GetCanonicalTypeName(indexerWrapRet.TrimEnd('?'));
            string cleanIndexerNoGlobal = cleanIndexerType.StartsWith("global::", StringComparison.Ordinal)
                ? cleanIndexerType[8..]
                : cleanIndexerType;
            bool isIndexerExternal = cleanIndexerNoGlobal.Contains('.');
            bool isIndexerString = cleanIndexerType == "string";
            bool isIndexerDynamicObject = cleanIndexerType == "object";
            bool isIndexerEnum = isIndexerExternal
                ? (KnownExternalEnums.Contains(cleanIndexerNoGlobal) || KnownExternalEnums.Contains(cleanIndexerType))
                : KnownEnums.Contains(cleanIndexerType);
            string indexerDeclType = isIndexerString ? "string" :
                                     isIndexerDynamicObject ? "object" :
                                     isIndexerEnum ? QualifyType(cleanIndexerType) :
                                     (cleanIndexerType == "ComProxy" || cleanIndexerType == "void") ? QualifyType("ComProxy") : QualifyType(cleanIndexerType);
            string target = (cleanIndexerType == "ComProxy" || cleanIndexerType == "void") ? QualifyType("ComProxy") : indexerDeclType;

            bool isTwoDimensional = indexerParamCount >= 2;
            string indexerParameters = isTwoDimensional ? "object row, object column = default!" : "object index";
            string indexerArguments = isTwoDimensional ? "row, column" : "index";
            string indexerGetExpression = isIndexerString
                ? $"InvokeString(\"Item\", 0, {indexerArguments})"
                : isIndexerDynamicObject
                    ? $"InvokeDynamicValue(\"Item\", 0, {indexerArguments})"
                    : isIndexerEnum || IsPrimitiveType(cleanIndexerType)
                        ? $"InvokeValue<{indexerDeclType}>(\"Item\", 0, {indexerArguments})"
                        : $"InvokeWrapper<{target}>(\"Item\", 0, {indexerArguments})";
            string indexerGetter = indexerHasGet switch {
                true => $"        get => {indexerGetExpression};{Environment.NewLine}",
                false => string.Empty
            };
            string indexerSetter = indexerHasSet switch {
                true => $"        set => SetIndexer(\"Item\", value, 0, {(indexerHasSetPut ? "true" : "false")}, {(indexerHasSetRef ? "true" : "false")}, {indexerArguments});{Environment.NewLine}",
                false => string.Empty
            };
            string indexerItemHidingKey = isTwoDimensional ? "Item(object, object)" : "Item(object)";
            string indexerItemNewMod = GetMethodNewModifier("Item", "Item", indexerItemHidingKey, collectionType, adobeObjectsSignatures);
            string indexerItemMethod = indexerHasGet switch {
                true => $"    public {indexerItemNewMod}{indexerDeclType} Item({indexerParameters}) => this[{indexerArguments}];{Environment.NewLine}",
                false => string.Empty
            };

            AppendLine(/*lang=c#*/ $$"""
                [global::System.Runtime.CompilerServices.IndexerName("Indexer")]
                public {{indexerDeclType}} this[{{indexerParameters}}]
                {
            {{indexerGetter}}{{indexerSetter}}    }

            {{indexerItemMethod}}
            """);
        }

        foreach (var prop in props.Values) {
            if (emittedMemberNames.Contains(prop.Name))
                continue;
            emittedMemberNames.Add(prop.Name);

            string propIdentifier = SafeMemberName(prop.Name, baseName);
            string newMod = GetPropertyNewModifier(propIdentifier, prop.Name, collectionType, adobeObjectsSignatures);
            string sanitizedPropName = Sanitize(prop.Name);
            string propComName = GetCanonicalTypeName(prop.Name);

            string wrapType = GetCanonicalTypeName(ResolveWrapperTypeName(prop.RawType, prop.Name));
            if (wrapType == "void")
                wrapType = "object";

            string cleanWrapType = wrapType.TrimEnd('?');
            string cleanWrapNoGlobal = cleanWrapType.StartsWith("global::", StringComparison.Ordinal)
                ? cleanWrapType[8..]
                : cleanWrapType;
            bool isQualifiedExternal = cleanWrapNoGlobal.Contains('.');
            string typeNameWithoutNs = isQualifiedExternal ? cleanWrapNoGlobal.Split('.').Last() : cleanWrapNoGlobal;
            if (typeNameWithoutNs.StartsWith("I") && typeNameWithoutNs.Length > 1 && char.IsUpper(typeNameWithoutNs[1])) {
                typeNameWithoutNs = GetBaseName(typeNameWithoutNs);
            }

            bool isStringProp = cleanWrapType == "string";

            bool isEnumProp = isQualifiedExternal
                ? (KnownExternalEnums.Contains(cleanWrapNoGlobal) || KnownExternalEnums.Contains(cleanWrapType))
                : KnownEnums.Contains(cleanWrapType);

            bool isKnownWrapper = isQualifiedExternal
                ? (KnownExternalInterfaces.Contains(cleanWrapNoGlobal) || KnownExternalInterfaces.Contains(cleanWrapType))
                : (KnownInterfaces.Contains(cleanWrapType) || KnownInterfaces.Contains(typeNameWithoutNs));
            bool isComProxyProp = cleanWrapType == "ComProxy";
            bool isDynamicObjectProp = cleanWrapType == "object";
            bool isCollectionProp = cleanWrapType.Contains("ComProxies") || cleanWrapType.Contains("Objects<");
            bool isExternalType = cleanWrapType.StartsWith("global::") || cleanWrapType.Contains('.');

            bool isWrapperOrCollection = !isStringProp && !isEnumProp && !isDynamicObjectProp && (isKnownWrapper || isComProxyProp || isCollectionProp || isExternalType || !IsPrimitiveType(cleanWrapType));

            string propDeclType = isStringProp ? "string" :
                                  isDynamicObjectProp ? "object" :
                                  isEnumProp ? QualifyType(cleanWrapType) :
                                  isComProxyProp ? QualifyType("ComProxy") :
                                  isCollectionProp ? QualifyType(cleanWrapType) : QualifyType(cleanWrapType);

            bool isBlacklisted = IsDebuggerBlacklisted(baseName, prop.Name);
            bool isComplexType = !IsPrimitiveType(cleanWrapType) && !isStringProp && !isEnumProp;

            if (isComplexType || isBlacklisted) {
                AppendLine("    [global::System.Diagnostics.DebuggerBrowsable(global::System.Diagnostics.DebuggerBrowsableState.Collapsed)]");
            }

            string propertyType;
            string getterExpression;
            string? directGetterExpression = null;

            if (isStringProp) {
                propertyType = "string";
                getterExpression = isBlacklisted
                    ? "\"[Property excluded from automatic debugger evaluation]\""
                    : $"GetString(\"{propComName}\", {prop.DispId})";
                directGetterExpression = $"GetString(\"{propComName}\", {prop.DispId})";
            }
            else if (isDynamicObjectProp) {
                propertyType = "object";
                getterExpression = isBlacklisted
                    ? "\"[Property excluded from automatic debugger evaluation]\""
                    : $"GetDynamicValue(\"{propComName}\", {prop.DispId})";
                directGetterExpression = $"GetDynamicValue(\"{propComName}\", {prop.DispId})";
            }
            else if (isWrapperOrCollection) {
                propertyType = isComProxyProp ? QualifyType("ComProxy") : QualifyType(cleanWrapType);
                getterExpression = isBlacklisted
                    ? $"ComProxy.CreateBlocked<{propertyType}>(\"Property excluded from automatic debugger evaluation (to prevent COM from hanging)\")"
                    : $"GetWrapper<{propertyType}>(\"{propComName}\", {prop.DispId})";
                directGetterExpression = $"GetWrapper<{propertyType}>(\"{propComName}\", {prop.DispId})";
            }
            else {
                propertyType = propDeclType;
                getterExpression = $"GetValue<{propertyType}>(\"{propComName}\", {prop.DispId})";
            }

            string setterMethod = (prop.HasSetPut, prop.IsSetRef) switch {
                (true, true) => "SetAuto",
                (false, true) => "SetRef",
                _ => "Set"
            };
            string propertyDeclaration = (prop.HasGet, prop.HasSet) switch {
                (true, true) => $$"""
                public {{newMod}}{{propertyType}} {{propIdentifier}}
                {
                    get => {{getterExpression}};
                    set => {{setterMethod}}("{{propComName}}", value, {{prop.DispId}});
                }
            """,
                (true, false) => $"    public {newMod}{propertyType} {propIdentifier} => {getterExpression};",
                (false, true) => $$"""
                public {{newMod}}{{propertyType}} {{propIdentifier}}
                {
                    set => {{setterMethod}}("{{propComName}}", value, {{prop.DispId}});
                }
            """,
                (false, false) => string.Empty
            };
            string directGetterName = $"Get{propIdentifier}Direct";
            string directGetterNewMod = GetMethodNewModifier(directGetterName, directGetterName, $"{directGetterName}()", collectionType, adobeObjectsSignatures);
            string directGetterDeclaration = (isBlacklisted && prop.HasGet, directGetterExpression) switch {
                (true, string expression) => $"    public {directGetterNewMod}{propertyType} {directGetterName}() => {expression};",
                _ => string.Empty
            };

            if (collectionType == CollectionType.rawAdobe && !string.IsNullOrEmpty(propertyDeclaration))
                _emittedAdobeObjectsPropertyNames.Add(propIdentifier);
            if (collectionType == CollectionType.rawAdobe && !string.IsNullOrEmpty(directGetterDeclaration)) {
                _emittedAdobeObjectsMethodNames.Add(directGetterName);
                _emittedAdobeObjectsMethodSignatures.Add($"{directGetterName}()");
            }

            AppendLine(/*lang=c#*/ $$"""
            {{propertyDeclaration}}
            {{directGetterDeclaration}}
            """);
        }

        foreach (var fn in funcs) {
            if (emittedMemberNames.Contains(fn.Name))
                continue;

            string wrapRet = GetCanonicalTypeName(ResolveWrapperTypeName(fn.RawReturnType, fn.Name));
            string cleanWrapRet = wrapRet.TrimEnd('?');
            string cleanWrapRetNoGlobal = cleanWrapRet.StartsWith("global::", StringComparison.Ordinal)
                ? cleanWrapRet[8..]
                : cleanWrapRet;
            bool isQualifiedExternalRet = cleanWrapRetNoGlobal.Contains('.');
            string retTypeNameWithoutNs = isQualifiedExternalRet ? cleanWrapRetNoGlobal.Split('.').Last() : cleanWrapRetNoGlobal;
            if (retTypeNameWithoutNs.StartsWith("I") && retTypeNameWithoutNs.Length > 1 && char.IsUpper(retTypeNameWithoutNs[1])) {
                retTypeNameWithoutNs = GetBaseName(retTypeNameWithoutNs);
            }

            bool isVoidRet = cleanWrapRet == "void";
            bool isStringRet = cleanWrapRet == "string";
            bool isDynamicObjectRet = cleanWrapRet == "object";
            bool isArrayRet = cleanWrapRet.EndsWith("[]", StringComparison.Ordinal);

            bool isEnumFnRet = isQualifiedExternalRet
                ? (KnownExternalEnums.Contains(cleanWrapRetNoGlobal) || KnownExternalEnums.Contains(cleanWrapRet))
                : KnownEnums.Contains(cleanWrapRet);

            bool isKnownWrapperRet = isQualifiedExternalRet
                ? (KnownExternalInterfaces.Contains(cleanWrapRetNoGlobal) || KnownExternalInterfaces.Contains(cleanWrapRet))
                : (KnownInterfaces.Contains(cleanWrapRet) || KnownInterfaces.Contains(retTypeNameWithoutNs));
            bool isComProxyRet = cleanWrapRet == "ComProxy";
            bool isCollectionRet = cleanWrapRet.Contains("ComProxies") || cleanWrapRet.Contains("Objects<");
            bool isExternalRet = cleanWrapRet.StartsWith("global::") || cleanWrapRet.Contains('.');
            bool isCollectionItemLookup = IsTypedCollection(collectionType) &&
                (fn.Name.Equals("ItemByID", StringComparison.OrdinalIgnoreCase) ||
                 fn.Name.Equals("ItemByName", StringComparison.OrdinalIgnoreCase));

            bool isWrapperOrCollectionRet = !isVoidRet && !isStringRet && !isDynamicObjectRet && !isArrayRet && !isEnumFnRet && (isKnownWrapperRet || isComProxyRet || isCollectionRet || isExternalRet || !IsPrimitiveType(cleanWrapRet));

            string methodDeclRet = isVoidRet ? "void" :
                                   isStringRet ? "string" :
                                   isDynamicObjectRet ? "object" :
                                   isEnumFnRet ? QualifyType(cleanWrapRet) :
                                   isComProxyRet ? QualifyType("ComProxy") :
                                   isCollectionRet ? QualifyType(cleanWrapRet) :
                                   isWrapperOrCollectionRet ? QualifyType(cleanWrapRet) : QualifyType(cleanWrapRet);

            string methodIdentifier = SafeMemberName(fn.Name, baseName);
            string sanitizedFnName = Sanitize(fn.Name);
            string fnComName = GetCanonicalTypeName(fn.Name);
            bool isApplicationQuit = baseName.Equals("Application", StringComparison.OrdinalIgnoreCase)
                                  && fnComName.Equals("Quit", StringComparison.OrdinalIgnoreCase);

            List<string> declParams = new();
            List<string> declParamTypes = new();
            List<string> passParamsLate = new();

            int lastRequiredIndex = -1;
            for (int i = 0; i < fn.Params.Count; i++) {
                if (!fn.Params[i].IsOptional)
                    lastRequiredIndex = i;
            }

            for (int i = 0; i < fn.Params.Count; i++) {
                var (pName, pRawType, isOptional, hasDefault, defaultValue, firstEnumMember, isInterface) = fn.Params[i];
                bool canEmitDefault = isOptional && i > lastRequiredIndex;

                string pWrapType = GetCanonicalTypeName(ResolveWrapperTypeName(pRawType, pName));
                string cleanPWrapType = pWrapType.TrimEnd('?');
                string cleanPWrapNoGlobal = cleanPWrapType.StartsWith("global::", StringComparison.Ordinal)
                    ? cleanPWrapType[8..]
                    : cleanPWrapType;
                bool isParamExternal = cleanPWrapNoGlobal.Contains('.');
                bool isParamEnum = isParamExternal
                    ? (KnownExternalEnums.Contains(cleanPWrapNoGlobal) || KnownExternalEnums.Contains(cleanPWrapType))
                    : KnownEnums.Contains(cleanPWrapType);
                bool isStringParam = pRawType == "string" || pName.Equals("VersionComments", StringComparison.OrdinalIgnoreCase);

                string paramTypeForDecl;
                if (pRawType == "void" || cleanPWrapType == "void") {
                    paramTypeForDecl = "object";
                    declParams.Add(canEmitDefault ? $"object {pName} = default" : $"object {pName}");
                }
                else if (isParamEnum) {
                    string qualified = QualifyType(cleanPWrapType);
                    paramTypeForDecl = qualified;
                    if (canEmitDefault) {
                        string defaultLiteral = FormatEnumDefaultLiteral(hasDefault, defaultValue, qualified, firstEnumMember);
                        declParams.Add($"{qualified} {pName} = {defaultLiteral}");
                    }
                    else {
                        declParams.Add($"{qualified} {pName}");
                    }
                }
                else if (isStringParam) {
                    paramTypeForDecl = "string";
                    if (canEmitDefault) {
                        string defaultLiteral = FormatStringDefaultLiteral(hasDefault, defaultValue);
                        declParams.Add($"string {pName} = {defaultLiteral}");
                    }
                    else {
                        declParams.Add($"string {pName}");
                    }
                }
                else if (IsPrimitiveType(cleanPWrapType)) {
                    string qualified = QualifyType(cleanPWrapType);
                    paramTypeForDecl = qualified;
                    if (canEmitDefault) {
                        string defaultLiteral = FormatPrimitiveDefaultLiteral(hasDefault, defaultValue, cleanPWrapType);
                        declParams.Add($"{qualified} {pName} = {defaultLiteral}");
                    }
                    else {
                        declParams.Add($"{qualified} {pName}");
                    }
                }
                else {
                    string qualified = QualifyType(cleanPWrapType);
                    paramTypeForDecl = qualified;
                    declParams.Add(canEmitDefault ? $"{qualified} {pName} = default" : $"{qualified} {pName}");
                }

                declParamTypes.Add(NormalizeTypeForSignature(paramTypeForDecl));

                bool isWrapperOrObjectParam = !isStringParam && !isParamEnum && !IsPrimitiveType(cleanPWrapType) &&
                    pRawType != "void" && cleanPWrapType != "void" &&
                    !cleanPWrapType.EndsWith("[]", StringComparison.Ordinal);
                passParamsLate.Add(isInterface && isWrapperOrObjectParam
                    ? $"global::{RootNamespace}.ComProxy.UnwrapInterface({pName})"
                    : pName);
            }

            string methodHidingKey = $"{methodIdentifier}({string.Join(", ", declParamTypes)})";
            string newMod = GetMethodNewModifier(methodIdentifier, fn.Name, methodHidingKey, collectionType, adobeObjectsSignatures);
            string signatureWithoutNew = $"{methodDeclRet} {methodIdentifier}({string.Join(", ", declParams)})";
            string signature = $"{newMod}{signatureWithoutNew}";
            string lateArgsStrWithComma = passParamsLate.Count > 0 ? ", " + string.Join(", ", passParamsLate) : "";

            if (emittedWrapperMethodSignatures.Add(signatureWithoutNew)) {
                if (collectionType == CollectionType.rawAdobe) {
                    _emittedAdobeObjectsMethodNames.Add(methodIdentifier);
                    _emittedAdobeObjectsMethodSignatures.Add(methodHidingKey);
                }

                if (isVoidRet) {
                    AppendLine(isApplicationQuit
                        ? /*lang=c#*/ $$"""
                            public {{signature}} {
                                ComBehavior?.Tracker.Clear();
                                InvokeVoid("{{fnComName}}", {{fn.DispId}}{{lateArgsStrWithComma}});
                                Dispose();
                            }
                        """
                        : $"    public {signature} => InvokeVoid(\"{fnComName}\", {fn.DispId}{lateArgsStrWithComma});");
                }
                else if (isStringRet) {
                    AppendLine($"    public {signature} => InvokeString(\"{fnComName}\", {fn.DispId}{lateArgsStrWithComma});");
                }
                else if (isDynamicObjectRet) {
                    AppendLine($"    public {signature} => InvokeDynamicValue(\"{fnComName}\", {fn.DispId}{lateArgsStrWithComma});");
                }
                else if (isArrayRet) {
                    string elementType = cleanWrapRet[..^2];
                    AppendLine($"    public {signature} => InvokeArray<{QualifyType(elementType)}>(\"{fnComName}\", {fn.DispId}{lateArgsStrWithComma});");
                }
                else if (isWrapperOrCollectionRet) {
                    string targetWrapper = isComProxyRet ? QualifyType("ComProxy") : QualifyType(cleanWrapRet);
                    string invoker = isCollectionItemLookup ? "InvokeCollectionItem" : "InvokeWrapper";
                    AppendLine($"    public {signature} => {invoker}<{targetWrapper}>(\"{fnComName}\", {fn.DispId}{lateArgsStrWithComma});");
                }
                else {
                    AppendLine($"    public {signature} => InvokeValue<{QualifyType(cleanWrapRet)}>(\"{fnComName}\", {fn.DispId}{lateArgsStrWithComma});");
                }
            }
        }

        if (collectionType == CollectionType.rawAdobe)
            _hasEmittedAdobeObjectsMembers = true;

        AppendLine(/*lang=c#*/ $$"""
        {{(collectionType == CollectionType.raw ? /*lang=c#*/"""
            public global::System.Collections.Generic.IEnumerator<ComProxy> GetEnumerator()
            {
                for (int i = 1; i <= Count; i++) {
                    yield return this[i];
                }
            }
            global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        """ : null)}}
        }

        """);
    }

    private string GetPropertyNewModifier(
        string propIdentifier,
        string rawPropName,
        CollectionType collectionType,
        AdobeObjectsMemberSignatures? adobeObjectsSignatures) {
        if (ComProxyPropertyNames.Contains(propIdentifier) ||
            ComProxyMethodNames.Contains(propIdentifier))
            return "new ";

        if (collectionType != CollectionType.typedAdobe)
            return "";

        if (adobeObjectsSignatures is not null &&
            (adobeObjectsSignatures.PropertyNames.Contains(propIdentifier) ||
             adobeObjectsSignatures.MethodNames.Contains(propIdentifier))) {
            return "new ";
        }

        if (_hasEmittedAdobeObjectsMembers) {
            return (_emittedAdobeObjectsPropertyNames.Contains(propIdentifier) ||
                    _emittedAdobeObjectsMethodNames.Contains(propIdentifier))
                ? "new "
                : "";
        }

        return DiscoveredObjectsMembers.Contains(rawPropName) ? "new " : "";
    }

    private string GetMethodNewModifier(
        string methodIdentifier,
        string rawMethodName,
        string methodHidingKey,
        CollectionType collectionType,
        AdobeObjectsMemberSignatures? adobeObjectsSignatures) {
        if (ComProxyPropertyNames.Contains(methodIdentifier) ||
            ComProxyMethodSignatures.Contains(methodHidingKey))
            return "new ";

        if (collectionType != CollectionType.typedAdobe)
            return "";

        if (adobeObjectsSignatures is not null) {
            if (adobeObjectsSignatures.PropertyNames.Contains(methodIdentifier) ||
                adobeObjectsSignatures.MethodSignatures.Contains(methodHidingKey)) {
                return "new ";
            }
        }

        if (_hasEmittedAdobeObjectsMembers) {
            return (_emittedAdobeObjectsPropertyNames.Contains(methodIdentifier) ||
                    _emittedAdobeObjectsMethodSignatures.Contains(methodHidingKey))
                ? "new "
                : "";
        }

        if (methodIdentifier.Equals("Add", StringComparison.Ordinal) ||
            (adobeObjectsSignatures is not null && adobeObjectsSignatures.MethodNames.Contains(methodIdentifier))) {
            return "";
        }

        return DiscoveredObjectsMembers.Contains(rawMethodName) ? "new " : "";
    }

    private AdobeObjectsMemberSignatures GetAdobeObjectsMemberSignatures() {
        string source = AdobeObjectsMembers ?? string.Empty;
        if (_cachedAdobeObjectsSignatures is not null &&
            string.Equals(_cachedAdobeObjectsMembersSource, source, StringComparison.Ordinal)) {
            return _cachedAdobeObjectsSignatures;
        }

        var parsed = ParseAdobeObjectsMemberSignatures(source);
        _cachedAdobeObjectsMembersSource = source;
        _cachedAdobeObjectsSignatures = parsed;
        return parsed;
    }

    private AdobeObjectsMemberSignatures ParseAdobeObjectsMemberSignatures(string source) {
        HashSet<string> propertyNames = new(StringComparer.Ordinal);
        HashSet<string> methodNames = new(StringComparer.Ordinal);
        HashSet<string> methodSignatures = new(StringComparer.Ordinal);

        int i = 0;
        int len = source.Length;

        while (i < len) {
            SkipWhitespaceAndComments(source, ref i);
            while (i < len && source[i] == '[') {
                SkipBalanced(source, ref i, '[', ']');
                SkipWhitespaceAndComments(source, ref i);
            }

            if (i >= len)
                break;

            int headerStart = i;
            int parenStart = -1;
            int parenEnd = -1;
            bool hasTopLevelAssignmentBeforeParen = false;
            bool sawAssignmentAtTopLevel = false;

            while (i < len) {
                if (TrySkipCommentOrLiteral(source, ref i))
                    continue;

                char ch = source[i];
                if (ch == '(') {
                    if (sawAssignmentAtTopLevel)
                        hasTopLevelAssignmentBeforeParen = true;
                    parenStart = i;
                    SkipBalanced(source, ref i, '(', ')');
                    parenEnd = i - 1;
                    continue;
                }

                if (ch == '[') {
                    SkipBalanced(source, ref i, '[', ']');
                    continue;
                }

                if (ch == '{' || ch == ';')
                    break;

                if (ch == '=' && i + 1 < len && source[i + 1] == '>')
                    break;

                if (ch == '=' && !sawAssignmentAtTopLevel)
                    sawAssignmentAtTopLevel = true;

                i++;
            }

            char terminator = i < len ? source[i] : '\0';
            string header = source[headerStart..i];

            if (!ContainsWord(header, "private")) {
                if (parenStart >= 0 && !hasTopLevelAssignmentBeforeParen) {
                    string? methodName = ExtractMethodNameBeforeParen(source, headerStart, parenStart);
                    if (!string.IsNullOrEmpty(methodName)) {
                        string rawParams = source[(parenStart + 1)..parenEnd];
                        List<string> paramTypes = ParseParameterTypes(rawParams);
                        methodNames.Add(methodName);
                        methodSignatures.Add($"{methodName}({string.Join(", ", paramTypes)})");
                    }
                }
                else if (terminator == '{' || terminator == '=') {
                    string? propName = ExtractTrailingIdentifier(header);
                    if (!string.IsNullOrEmpty(propName))
                        propertyNames.Add(propName);
                }
            }

            if (terminator == '{') {
                SkipBalanced(source, ref i, '{', '}');
            }
            else if (terminator == '=') {
                i += 2;
                while (i < len) {
                    if (TrySkipCommentOrLiteral(source, ref i))
                        continue;

                    char ch = source[i];
                    if (ch == '{') {
                        SkipBalanced(source, ref i, '{', '}');
                        continue;
                    }
                    if (ch == '(') {
                        SkipBalanced(source, ref i, '(', ')');
                        continue;
                    }
                    if (ch == '[') {
                        SkipBalanced(source, ref i, '[', ']');
                        continue;
                    }
                    if (ch == ';') {
                        i++;
                        break;
                    }
                    i++;
                }
            }
            else if (terminator == ';') {
                i++;
            }
        }

        return new AdobeObjectsMemberSignatures(propertyNames, methodNames, methodSignatures);
    }

    private List<string> ParseParameterTypes(string rawParams) {
        List<string> result = new();
        if (string.IsNullOrWhiteSpace(rawParams))
            return result;

        int start = 0;
        int i = 0;
        int angleDepth = 0;

        while (i < rawParams.Length) {
            if (TrySkipCommentOrLiteral(rawParams, ref i))
                continue;

            char ch = rawParams[i];
            if (ch == '(') {
                SkipBalanced(rawParams, ref i, '(', ')');
                continue;
            }
            if (ch == '[') {
                SkipBalanced(rawParams, ref i, '[', ']');
                continue;
            }
            if (ch == '{') {
                SkipBalanced(rawParams, ref i, '{', '}');
                continue;
            }
            if (ch == '<') {
                angleDepth++;
            }
            else if (ch == '>' && angleDepth > 0) {
                angleDepth--;
            }
            else if (ch == ',' && angleDepth == 0) {
                string? paramType = ExtractParameterType(rawParams[start..i]);
                if (!string.IsNullOrEmpty(paramType))
                    result.Add(paramType);
                start = i + 1;
            }

            i++;
        }

        string? lastParamType = ExtractParameterType(rawParams[start..]);
        if (!string.IsNullOrEmpty(lastParamType))
            result.Add(lastParamType);

        return result;
    }

    private string? ExtractParameterType(string parameterSlice) {
        int i = 0;
        int angleDepth = 0;
        int equalsIndex = -1;

        while (i < parameterSlice.Length) {
            if (TrySkipCommentOrLiteral(parameterSlice, ref i))
                continue;

            char ch = parameterSlice[i];
            if (ch == '(') {
                SkipBalanced(parameterSlice, ref i, '(', ')');
                continue;
            }
            if (ch == '[') {
                SkipBalanced(parameterSlice, ref i, '[', ']');
                continue;
            }
            if (ch == '{') {
                SkipBalanced(parameterSlice, ref i, '{', '}');
                continue;
            }
            if (ch == '<') {
                angleDepth++;
            }
            else if (ch == '>' && angleDepth > 0) {
                angleDepth--;
            }
            else if (ch == '=' && angleDepth == 0) {
                equalsIndex = i;
                break;
            }

            i++;
        }

        string decl = (equalsIndex >= 0 ? parameterSlice[..equalsIndex] : parameterSlice).Trim();
        int attrIdx = 0;
        while (attrIdx < decl.Length && decl[attrIdx] == '[') {
            SkipBalanced(decl, ref attrIdx, '[', ']');
            while (attrIdx < decl.Length && char.IsWhiteSpace(decl[attrIdx]))
                attrIdx++;
        }

        decl = decl[attrIdx..].Trim();
        if (decl.Length == 0)
            return null;

        int end = decl.Length;
        while (end > 0 && char.IsWhiteSpace(decl[end - 1]))
            end--;

        int nameStart = end;
        while (nameStart > 0 && (char.IsLetterOrDigit(decl[nameStart - 1]) || decl[nameStart - 1] == '_' || decl[nameStart - 1] == '@'))
            nameStart--;

        if (nameStart == 0 || nameStart == end)
            return null;

        string typePart = decl[..nameStart].Trim();
        while (true) {
            string? trimmedModifier = StripLeadingModifier(typePart, "this")
                ?? StripLeadingModifier(typePart, "params")
                ?? StripLeadingModifier(typePart, "ref")
                ?? StripLeadingModifier(typePart, "out")
                ?? StripLeadingModifier(typePart, "in")
                ?? StripLeadingModifier(typePart, "scoped");
            if (trimmedModifier is null)
                break;
            typePart = trimmedModifier;
        }

        return typePart.Length == 0 ? null : NormalizeTypeForSignature(typePart);
    }

    private string NormalizeTypeForSignature(string typeName) {
        string normalized = typeName.Replace("global::", "", StringComparison.Ordinal).Trim();
        if (!string.IsNullOrEmpty(RootNamespace))
            normalized = normalized.Replace(RootNamespace + ".", "", StringComparison.Ordinal);

        var sb = new StringBuilder(normalized.Length);
        bool pendingSpace = false;
        for (int i = 0; i < normalized.Length; i++) {
            char ch = normalized[i];
            if (char.IsWhiteSpace(ch)) {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (ch is '<' or '>' or ',' or '[' or ']' or '?' or '.') {
                pendingSpace = false;
                sb.Append(ch);
                continue;
            }

            if (pendingSpace && sb.Length > 0 && sb[^1] is not ('<' or ',' or '[' or '.')) {
                sb.Append(ch == ',' ? "" : " ");
            }
            pendingSpace = false;
            sb.Append(ch);
        }

        normalized = sb.ToString()
            .Replace(",", ", ", StringComparison.Ordinal)
            .Replace("System.Object", "object", StringComparison.Ordinal)
            .Replace("System.String", "string", StringComparison.Ordinal)
            .Replace("System.Boolean", "bool", StringComparison.Ordinal)
            .Replace("System.Byte", "byte", StringComparison.Ordinal)
            .Replace("System.SByte", "sbyte", StringComparison.Ordinal)
            .Replace("System.Int16", "short", StringComparison.Ordinal)
            .Replace("System.UInt16", "ushort", StringComparison.Ordinal)
            .Replace("System.Int32", "int", StringComparison.Ordinal)
            .Replace("System.UInt32", "uint", StringComparison.Ordinal)
            .Replace("System.Int64", "long", StringComparison.Ordinal)
            .Replace("System.UInt64", "ulong", StringComparison.Ordinal)
            .Replace("System.Single", "float", StringComparison.Ordinal)
            .Replace("System.Double", "double", StringComparison.Ordinal)
            .Replace("System.Decimal", "decimal", StringComparison.Ordinal);

        if (normalized.EndsWith("?", StringComparison.Ordinal)) {
            string withoutNullable = normalized[..^1];
            if (!IsPrimitiveType(withoutNullable) &&
                withoutNullable is not ("char" or "nint" or "nuint") &&
                !KnownEnums.Contains(withoutNullable) &&
                !KnownExternalEnums.Contains(withoutNullable)) {
                normalized = withoutNullable;
            }
        }

        return normalized;
    }

    private static string? StripLeadingModifier(string value, string modifier) {
        if (value.StartsWith(modifier, StringComparison.Ordinal) &&
            value.Length > modifier.Length &&
            char.IsWhiteSpace(value[modifier.Length])) {
            return value[modifier.Length..].TrimStart();
        }
        return null;
    }

    private static string? ExtractMethodNameBeforeParen(string source, int headerStart, int parenStart) {
        int idx = parenStart - 1;
        while (idx >= headerStart && char.IsWhiteSpace(source[idx]))
            idx--;

        if (idx >= headerStart && source[idx] == '>') {
            int depth = 1;
            idx--;
            while (idx >= headerStart && depth > 0) {
                if (source[idx] == '>')
                    depth++;
                else if (source[idx] == '<')
                    depth--;
                idx--;
            }
            while (idx >= headerStart && char.IsWhiteSpace(source[idx]))
                idx--;
        }

        int end = idx + 1;
        while (idx >= headerStart && (char.IsLetterOrDigit(source[idx]) || source[idx] == '_' || source[idx] == '@'))
            idx--;

        int start = idx + 1;
        if (start >= end)
            return null;

        return source[start..end];
    }

    private static string? ExtractTrailingIdentifier(string header) {
        int end = header.Length;
        while (end > 0 && char.IsWhiteSpace(header[end - 1]))
            end--;

        int start = end;
        while (start > 0 && (char.IsLetterOrDigit(header[start - 1]) || header[start - 1] == '_' || header[start - 1] == '@'))
            start--;

        if (start >= end)
            return null;

        return header[start..end];
    }

    private static bool ContainsWord(string text, string word) {
        int idx = 0;
        while ((idx = text.IndexOf(word, idx, StringComparison.Ordinal)) >= 0) {
            bool leftOk = idx == 0 || !(char.IsLetterOrDigit(text[idx - 1]) || text[idx - 1] == '_');
            int after = idx + word.Length;
            bool rightOk = after == text.Length || !(char.IsLetterOrDigit(text[after]) || text[after] == '_');
            if (leftOk && rightOk)
                return true;
            idx += word.Length;
        }
        return false;
    }

    private static void SkipWhitespaceAndComments(string source, ref int i) {
        while (i < source.Length) {
            if (char.IsWhiteSpace(source[i])) {
                i++;
                continue;
            }
            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '/') {
                i += 2;
                while (i < source.Length && source[i] != '\n')
                    i++;
                continue;
            }
            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '*') {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                    i++;
                i = Math.Min(source.Length, i + 2);
                continue;
            }
            break;
        }
    }

    private static bool TrySkipCommentOrLiteral(string source, ref int i) {
        char ch = source[i];
        if (ch == '/' && i + 1 < source.Length && source[i + 1] == '/') {
            i += 2;
            while (i < source.Length && source[i] != '\n')
                i++;
            return true;
        }
        if (ch == '/' && i + 1 < source.Length && source[i + 1] == '*') {
            i += 2;
            while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                i++;
            i = Math.Min(source.Length, i + 2);
            return true;
        }
        if (ch == '@' && i + 1 < source.Length && source[i + 1] == '"') {
            i += 2;
            while (i < source.Length) {
                if (source[i] == '"') {
                    if (i + 1 < source.Length && source[i + 1] == '"') {
                        i += 2;
                        continue;
                    }
                    i++;
                    break;
                }
                i++;
            }
            return true;
        }
        if (ch == '"') {
            i++;
            while (i < source.Length) {
                if (source[i] == '\\') {
                    i += 2;
                    continue;
                }
                if (source[i] == '"') {
                    i++;
                    break;
                }
                i++;
            }
            return true;
        }
        if (ch == '\'') {
            i++;
            while (i < source.Length) {
                if (source[i] == '\\') {
                    i += 2;
                    continue;
                }
                if (source[i] == '\'') {
                    i++;
                    break;
                }
                i++;
            }
            return true;
        }
        return false;
    }

    private static void SkipBalanced(string source, ref int i, char open, char close) {
        if (i >= source.Length || source[i] != open)
            return;

        int depth = 1;
        i++;
        while (i < source.Length && depth > 0) {
            if (TrySkipCommentOrLiteral(source, ref i))
                continue;

            if (source[i] == open)
                depth++;
            else if (source[i] == close)
                depth--;
            i++;
        }
    }

    private static string FormatEnumDefaultLiteral(bool hasDefault, object? defaultValue, string qualifiedEnumType, string? firstEnumMember) {
        if (!hasDefault || defaultValue is null || defaultValue == Type.Missing)
            return firstEnumMember is null ? "default" : $"{qualifiedEnumType}.{firstEnumMember}";

        long rawLong = Convert.ToInt64(defaultValue, CultureInfo.InvariantCulture);
        int enumValue = unchecked((int)rawLong);
        string valueText = enumValue < 0
            ? $"({enumValue.ToString(CultureInfo.InvariantCulture)})"
            : enumValue.ToString(CultureInfo.InvariantCulture);
        return $"({qualifiedEnumType}){valueText}";
    }

    private static string FormatStringDefaultLiteral(bool hasDefault, object? defaultValue) {
        if (!hasDefault || defaultValue is null || defaultValue == Type.Missing)
            return "null";

        if (defaultValue is not string text)
            return "null";

        var sb = new StringBuilder(text.Length + 2);
        sb.Append('"');
        foreach (char ch in text) {
            switch (ch) {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\0':
                    sb.Append("\\0");
                    break;
                case '\a':
                    sb.Append("\\a");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case '\v':
                    sb.Append("\\v");
                    break;
                default:
                    if (char.IsControl(ch) || char.IsSurrogate(ch))
                        sb.Append($"\\u{(int)ch:X4}");
                    else
                        sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static string FormatPrimitiveDefaultLiteral(bool hasDefault, object? defaultValue, string primitiveType) {
        if (!hasDefault || defaultValue is null || defaultValue == Type.Missing)
            return primitiveType == "bool" ? "false" : "0";

        switch (primitiveType) {
            case "bool":
                return Convert.ToBoolean(defaultValue, CultureInfo.InvariantCulture) ? "true" : "false";
            case "byte":
                return $"(byte){Convert.ToByte(defaultValue, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)}";
            case "sbyte": {
                sbyte v = Convert.ToSByte(defaultValue, CultureInfo.InvariantCulture);
                return v < 0
                    ? $"(sbyte)({v.ToString(CultureInfo.InvariantCulture)})"
                    : $"(sbyte){v.ToString(CultureInfo.InvariantCulture)}";
            }
            case "short": {
                short v = Convert.ToInt16(defaultValue, CultureInfo.InvariantCulture);
                return v < 0
                    ? $"(short)({v.ToString(CultureInfo.InvariantCulture)})"
                    : $"(short){v.ToString(CultureInfo.InvariantCulture)}";
            }
            case "ushort":
                return $"(ushort){Convert.ToUInt16(defaultValue, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)}";
            case "int":
                return Convert.ToInt32(defaultValue, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            case "uint":
                return Convert.ToUInt32(defaultValue, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) + "u";
            case "long":
                return Convert.ToInt64(defaultValue, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) + "L";
            case "ulong":
                return Convert.ToUInt64(defaultValue, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) + "UL";
            case "float": {
                float v = Convert.ToSingle(defaultValue, CultureInfo.InvariantCulture);
                if (float.IsNaN(v))
                    return "float.NaN";
                if (float.IsPositiveInfinity(v))
                    return "float.PositiveInfinity";
                if (float.IsNegativeInfinity(v))
                    return "float.NegativeInfinity";
                return v.ToString("R", CultureInfo.InvariantCulture) + "f";
            }
            case "double": {
                double v = Convert.ToDouble(defaultValue, CultureInfo.InvariantCulture);
                if (double.IsNaN(v))
                    return "double.NaN";
                if (double.IsPositiveInfinity(v))
                    return "double.PositiveInfinity";
                if (double.IsNegativeInfinity(v))
                    return "double.NegativeInfinity";
                return v.ToString("R", CultureInfo.InvariantCulture) + "d";
            }
            case "decimal":
                return Convert.ToDecimal(defaultValue, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) + "m";
            default:
                throw new NotSupportedException($"Unsupported primitive type: {primitiveType}.");
        }
    }

    private string GetCanonicalTypeName(string typeName) => CanonicalTypeNames.Resolve(
        typeName, AdapterClassName, KnownInterfaces, KnownEnums,
        KnownExternalInterfaces, KnownExternalEnums);
}