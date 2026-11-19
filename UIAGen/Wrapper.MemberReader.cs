//------------------------------------------------------------------------------
// <copyright file="Wrapper.MemberReader.cs" company="Piotr Filipowicz">
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
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace UiaAdapter;

public partial class UiaAdapterWrapper {
    private FuncModel ReadMethod(ITypeInfo typeInfo, FUNCDESC descriptor, string name,
        string[] names, int nameCount) {
        var method = new FuncModel {
            Name = name,
            DispId = descriptor.memid,
            RawReturnType = ResolveRawReturnTypeName(descriptor.elemdescFunc.tdesc, typeInfo)
        };
        var usedParamNames = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < descriptor.cParams; index++) {
            nint pointer = descriptor.lprgelemdescParam + index * Marshal.SizeOf<ELEMDESC>();
            ELEMDESC parameter = Marshal.PtrToStructure<ELEMDESC>(pointer);
            string rawType = ResolveRawTypeName(parameter.tdesc, typeInfo);
            string rawParamName = index + 1 < nameCount ? names[index + 1] : string.Empty;
            string parameterName = Sanitize(rawParamName, $"arg{index}");
            if (!usedParamNames.Add(parameterName)) {
                parameterName = $"arg{index}";
                while (!usedParamNames.Add(parameterName))
                    parameterName += "_";
            }
            PARAMFLAG flags = parameter.desc.paramdesc.wParamFlags;
            bool descriptorOptional = descriptor.cParamsOpt > 0 && index >= descriptor.cParams - descriptor.cParamsOpt;
            bool isOptional = descriptorOptional || (flags & (PARAMFLAG.PARAMFLAG_FOPT | PARAMFLAG.PARAMFLAG_FHASDEFAULT)) != 0;
            method.Params.Add((parameterName, rawType, isOptional));
        }
        return method;
    }

    // Native descriptors are consumed here; the emitter receives managed data only.
    private MemberModel ReadMembers(ITypeInfo typeInfo, int functionCount, string itemName, bool isCollection) {
        bool indexerHasGet = false;
        bool indexerHasSet = false;
        bool indexerHasSetPut = false;
        bool indexerHasSetRef = false;
        int indexerParamCount = 1;
        string indexerRawRet = "ComProxy";
        string indexerWrapRet = "ComProxy";

        Dictionary<string, PropModel> props = new(StringComparer.OrdinalIgnoreCase);
        List<FuncModel> funcs = new();
        for (int i = 0; i < functionCount; i++) {
            using var pFuncDescLease = TypeInfoDescriptor.Function(typeInfo, i);
            nint pFuncDesc = pFuncDescLease.Pointer;
            FUNCDESC funcDesc = Marshal.PtrToStructure<FUNCDESC>(pFuncDesc);

            string[] names = new string[funcDesc.cParams + 1];
            typeInfo.GetNames(funcDesc.memid, names, names.Length, out int cNames);
            string funcName = names[0];
            if (string.IsNullOrWhiteSpace(funcName))
                continue;

            bool isIndexer = (funcDesc.memid == 0 || funcName.Equals("Item", StringComparison.OrdinalIgnoreCase) || funcName.Equals("_Default", StringComparison.OrdinalIgnoreCase)) && (
                (funcDesc.invkind == INVOKEKIND.INVOKE_PROPERTYGET && funcDesc.cParams > 0) ||
                (funcDesc.invkind is INVOKEKIND.INVOKE_PROPERTYPUT or INVOKEKIND.INVOKE_PROPERTYPUTREF && funcDesc.cParams > 1) ||
                (funcDesc.invkind == INVOKEKIND.INVOKE_FUNC && funcDesc.cParams > 0)
            );

            if (isIndexer) {
                int pCount = (funcDesc.invkind is INVOKEKIND.INVOKE_PROPERTYPUT or INVOKEKIND.INVOKE_PROPERTYPUTREF)
                    ? funcDesc.cParams - 1
                    : funcDesc.cParams;
                if (pCount > indexerParamCount) {
                    indexerParamCount = pCount;
                }

                if (funcDesc.invkind is INVOKEKIND.INVOKE_PROPERTYGET or INVOKEKIND.INVOKE_FUNC) {
                    indexerHasGet = true;
                    indexerRawRet = ResolveRawTypeName(funcDesc.elemdescFunc.tdesc, typeInfo);
                    indexerWrapRet = GetCanonicalTypeName(ResolveWrapperTypeName(indexerRawRet, itemName));
                }
                else if (funcDesc.invkind is INVOKEKIND.INVOKE_PROPERTYPUT or INVOKEKIND.INVOKE_PROPERTYPUTREF) {
                    indexerHasSet = true;
                    indexerHasSetPut |= funcDesc.invkind == INVOKEKIND.INVOKE_PROPERTYPUT;
                    indexerHasSetRef |= funcDesc.invkind == INVOKEKIND.INVOKE_PROPERTYPUTREF;
                    if ((indexerRawRet == "object" || indexerRawRet == "ComProxy") && funcDesc.cParams > 0) {
                        nint pElem = funcDesc.lprgelemdescParam + ((funcDesc.cParams - 1) * Marshal.SizeOf<ELEMDESC>());
                        ELEMDESC lastParam = Marshal.PtrToStructure<ELEMDESC>(pElem);
                        indexerRawRet = ResolveRawTypeName(lastParam.tdesc, typeInfo);
                        indexerWrapRet = GetCanonicalTypeName(ResolveWrapperTypeName(indexerRawRet, itemName));
                    }
                }
            }
            else if (funcDesc.invkind == INVOKEKIND.INVOKE_PROPERTYGET) {
                bool isSimpleGet = (funcDesc.cParams == 0);
                if (!isSimpleGet) {
                    bool allOptional = true;
                    for (int p = 0; p < funcDesc.cParams; p++) {
                        nint pElem = funcDesc.lprgelemdescParam + (p * Marshal.SizeOf<ELEMDESC>());
                        ELEMDESC paramDesc = Marshal.PtrToStructure<ELEMDESC>(pElem);
                        if ((paramDesc.desc.paramdesc.wParamFlags & (PARAMFLAG.PARAMFLAG_FOPT | PARAMFLAG.PARAMFLAG_FHASDEFAULT)) == 0) {
                            allOptional = false;
                            break;
                        }
                    }
                    isSimpleGet = allOptional;
                }

                if (isSimpleGet) {
                    if (!props.TryGetValue(funcName, out var p)) {
                        p = new PropModel { Name = funcName, DispId = funcDesc.memid };
                        props[funcName] = p;
                    }
                    p.HasGet = true;
                    string rType = ResolveRawTypeName(funcDesc.elemdescFunc.tdesc, typeInfo);
                    p.RawType = (rType == "void") ? "object" : rType;
                }
                else {
                    funcs.Add(ReadMethod(typeInfo, funcDesc, funcName, names, cNames));
                }
            }
            else if (funcDesc.invkind is INVOKEKIND.INVOKE_PROPERTYPUT or INVOKEKIND.INVOKE_PROPERTYPUTREF) {
                if (!props.TryGetValue(funcName, out var p)) {
                    p = new PropModel { Name = funcName, DispId = funcDesc.memid };
                    props[funcName] = p;
                }
                p.HasSet = true;
                p.HasSetPut |= funcDesc.invkind == INVOKEKIND.INVOKE_PROPERTYPUT;
                p.IsSetRef |= funcDesc.invkind == INVOKEKIND.INVOKE_PROPERTYPUTREF;
                if (funcDesc.cParams > 0) {
                    nint pElem = funcDesc.lprgelemdescParam + ((funcDesc.cParams - 1) * Marshal.SizeOf<ELEMDESC>());
                    ELEMDESC lastParam = Marshal.PtrToStructure<ELEMDESC>(pElem);
                    string sType = ResolveRawTypeName(lastParam.tdesc, typeInfo);
                    p.RawSetType = (sType == "void") ? "object" : sType;
                    if (string.IsNullOrEmpty(p.RawType) || p.RawType == "object" || p.RawType == "ComProxy" || p.RawType == "void") {
                        p.RawType = p.RawSetType;
                    }
                }
            }
            else if (funcDesc.invkind == INVOKEKIND.INVOKE_FUNC) {
                FuncModel method = ReadMethod(typeInfo, funcDesc, funcName, names, cNames);
                if (isCollection &&
                    (funcName.Equals("ItemByID", StringComparison.OrdinalIgnoreCase) ||
                     funcName.Equals("ItemByName", StringComparison.OrdinalIgnoreCase))) {
                    method.RawReturnType = itemName;
                }
                funcs.Add(method);
            }
        }

        for (int i = funcs.Count - 1; i >= 0; i--) {
            if (props.TryGetValue(funcs[i].Name, out var p) && p.HasGet) {
                funcs.RemoveAt(i);
            }
        }

        return new MemberModel(props, funcs, new IndexerModel(indexerHasGet, indexerHasSet, indexerHasSetPut, indexerHasSetRef,
            indexerParamCount, indexerRawRet, indexerWrapRet));
    }
}