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

    internal void GenerateStrongWrapper(string baseName, ITypeInfo typeInfo, Action<string> AppendLine) {
        using var pAttrLease = TypeInfoDescriptor.TypeAttributes(typeInfo);
        nint pAttr = pAttrLease.Pointer;
        TYPEATTR attr = Marshal.PtrToStructure<TYPEATTR>(pAttr);

        bool isObjects = baseName.Equals("Objects", StringComparison.OrdinalIgnoreCase);
        bool isAdobeObjects = isObjects && (HasAdobeObjectsModel || IsAdobeObjectsTypeInfo(typeInfo, attr) || IsAdobeAdapter);
        bool isRealCollection = !isObjects && IsTypeInfoCollection(typeInfo, attr);
        bool isAdobeCollection = isRealCollection && (HasAdobeObjectsModel || IsAdobeAdapter);
        bool usesCollectionBase = isRealCollection || isAdobeObjects;

        string inheritance;
        bool isCollection = false;
        string itemName = baseName;

        if (isObjects) {
            inheritance = isAdobeObjects
                ? " : ComProxies"
                : " : ComProxy, global::System.Collections.Generic.IEnumerable<ComProxy>, global::System.Collections.IEnumerable";
        }
        else if (!isRealCollection) {
            inheritance = " : ComProxy";
        }
        else {
            isCollection = true;
            string? singularCandidate = GetSingularCandidate(baseName);

            bool hasSpecificItemWrapper = singularCandidate != null &&
                (KnownInterfaces.Contains(singularCandidate) ||
                 KnownExternalInterfaces.Contains(singularCandidate) ||
                 singularCandidate.Equals("string", StringComparison.OrdinalIgnoreCase));

            if (hasSpecificItemWrapper) {
                itemName = GetCanonicalTypeName(singularCandidate!);
                inheritance = isAdobeCollection
                    ? $" : Objects<{itemName}>"
                    : $" : ComProxies<{itemName}>";
            }
            else {
                itemName = "ComProxy";
                inheritance = isAdobeCollection ? " : Objects" : " : ComProxies";
            }

            string debuggerViewType = hasSpecificItemWrapper switch {
                true => $"ComProxiesDebugView<{itemName}>",
                false => "ComProxiesUntypedDebugView"
            };
            AppendLine(/*lang=c#*/ $$"""
            [global::System.Diagnostics.DebuggerTypeProxy(typeof({{debuggerViewType}}))]
            """);
        }

        if (isAdobeObjects) {
            AppendLine("[global::System.Diagnostics.DebuggerTypeProxy(typeof(ComProxiesUntypedDebugView))]");
        }
        else if (!usesCollectionBase) {
            AppendLine("[global::System.Diagnostics.DebuggerTypeProxy(typeof(ComProxyDebugView))]");
        }

        string safeTypeName = Sanitize(baseName);
        AppendLine(/*lang=c#*/ $$"""
public class {{safeTypeName}}{{inheritance}} {
    public static {{safeTypeName}} GetMissing() => global::{{RootNamespace}}.ComProxy.Missing<{{safeTypeName}}>();
    {{(!isCollection && baseName.Equals("Application", StringComparison.OrdinalIgnoreCase)
        ? $"public {safeTypeName}(object comInstance) : base(comInstance) {{ AttachBehavior(new ComBehavior());}}"
        : $"public {safeTypeName}(object comInstance) : base(comInstance) {{ }}")}}
{{(!isCollection && baseName.Equals("Application", StringComparison.OrdinalIgnoreCase) ? """
    public static Application Create(string progId) {
        using var appPtr = ComInvoker.CreateFromProgID(progId);
        var app = new Application(appPtr);
        app.ComBehavior?.Tracker.GetCheckPoint();
        return app;
    }
""" : null)}}
""");

        if (isAdobeObjects)
            AppendLine(AdobeObjectsMembers);

        MemberModel members = ReadMembers(typeInfo, attr.cFuncs, itemName, isCollection);
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

        if (isAdobeObjects)
            emittedMemberNames.Add("Add");

        if (usesCollectionBase) {
            emittedMemberNames.Add("Count");
            emittedMemberNames.Add("Item");
            emittedMemberNames.Add("_Default");
            emittedMemberNames.Add("_NewEnum");
            emittedMemberNames.Add("this");
            emittedMemberNames.Add("TryGetValue");
            emittedMemberNames.Add("Exists");
            emittedMemberNames.Add("GetEnumerator");
        }

        if (isAdobeCollection || isAdobeObjects) {
            emittedMemberNames.Add("FirstItem");
            emittedMemberNames.Add("LastItem");
            emittedMemberNames.Add("MiddleItem");
            emittedMemberNames.Add("PreviousItem");
            emittedMemberNames.Add("NextItem");

            emittedMemberNames.Add("AnyItem");
            emittedMemberNames.Add("ItemByID");
            emittedMemberNames.Add("ItemByRange");
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
            .Where(fn => (isAdobeCollection || isAdobeObjects) &&
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

        if (indexerHasGet || indexerHasSet || isCollection) {
            emittedMemberNames.Add("Item");
            emittedMemberNames.Add("_Default");
            emittedMemberNames.Add("_NewEnum");
        }

        if (!usesCollectionBase && (indexerHasGet || indexerHasSet)) {
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
            string indexerItemMethod = indexerHasGet switch {
                true => $"    public {indexerDeclType} Item({indexerParameters}) => this[{indexerArguments}];{Environment.NewLine}",
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

            string newMod = (isAdobeCollection && DiscoveredObjectsMembers.Contains(prop.Name)) ? "new " : "";
            string propIdentifier = SafeMemberName(prop.Name, baseName);
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
            string directGetterDeclaration = (isBlacklisted && prop.HasGet, directGetterExpression) switch {
                (true, string expression) => $"    public {newMod}{propertyType} Get{propIdentifier}Direct() => {expression};",
                _ => string.Empty
            };

            AppendLine(/*lang=c#*/ $$"""
            {{propertyDeclaration}}
            {{directGetterDeclaration}}
            """);
        }

        foreach (var fn in funcs) {
            if (emittedMemberNames.Contains(fn.Name))
                continue;

            string newMod = (isAdobeCollection && DiscoveredObjectsMembers.Contains(fn.Name)) ? "new " : "";
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
            bool isCollectionItemLookup = isCollection &&
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
            List<string> passParamsLate = new();

            int lastRequiredIndex = -1;
            for (int i = 0; i < fn.Params.Count; i++) {
                if (!fn.Params[i].IsOptional)
                    lastRequiredIndex = i;
            }

            for (int i = 0; i < fn.Params.Count; i++) {
                var (pName, pRawType, isOptional, hasDefault, defaultValue, firstEnumMember) = fn.Params[i];
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

                if (pRawType == "void" || cleanPWrapType == "void") {
                    declParams.Add(canEmitDefault ? $"object {pName} = default" : $"object {pName}");
                }
                else if (isParamEnum) {
                    string qualified = QualifyType(cleanPWrapType);
                    if (canEmitDefault) {
                        string defaultLiteral = FormatEnumDefaultLiteral(hasDefault, defaultValue, qualified, firstEnumMember);
                        declParams.Add($"{qualified} {pName} = {defaultLiteral}");
                    }
                    else {
                        declParams.Add(isOptional ? $"{qualified} {pName}" : $"{qualified} {pName}");
                    }
                }
                else if (pRawType == "string" || pName.Equals("VersionComments", StringComparison.OrdinalIgnoreCase)) {
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
                    if (canEmitDefault) {
                        string defaultLiteral = FormatPrimitiveDefaultLiteral(hasDefault, defaultValue, cleanPWrapType);
                        declParams.Add($"{qualified} {pName} = {defaultLiteral}");
                    }
                    else {
                        declParams.Add(isOptional ? $"{qualified} {pName}" : $"{qualified} {pName}");
                    }
                }
                else {
                    string qualified = QualifyType(cleanPWrapType);
                    declParams.Add(canEmitDefault ? $"{qualified} {pName} = default" : $"{qualified} {pName}");
                }

                passParamsLate.Add(pName);
            }

            string signature = $"{newMod}{methodDeclRet} {methodIdentifier}({string.Join(", ", declParams)})";
            string lateArgsStrWithComma = passParamsLate.Count > 0 ? ", " + string.Join(", ", passParamsLate) : "";

            if (emittedWrapperMethodSignatures.Add(signature)) {
                if (isVoidRet) {
                    AppendLine(isApplicationQuit
                        ? /*lang=c#*/ $$"""
                            public {{signature}} {
                                ComBehavior?.Tracker.Clear();
                                InvokeVoid("{{fnComName}}", {{fn.DispId}}{{lateArgsStrWithComma}});
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

        AppendLine(/*lang=c#*/ $$"""
        {{(isObjects && !isAdobeObjects ? /*lang=c#*/"""
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