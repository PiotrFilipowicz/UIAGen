//------------------------------------------------------------------------------
// <copyright file="Wrapper.Models.cs" company="Piotr Filipowicz">
//     Copyright (c) 2026 Piotr Filipowicz.
//     SPDX-License-Identifier: LGPL-3.0-or-later WITH GCC-exception-3.1
//     Licensed under GNU LGPL v3 with Code Generation Exception.
// </copyright>
// <security>
//     Integrity Notice: Official commits, tags, and release artifacts of this
//     project are cryptographically signed.
// </security>
//------------------------------------------------------------------------------

using System.Collections.Generic;

namespace UiaAdapter;

public partial class UiaAdapterWrapper {
    private sealed record IndexerModel(bool HasGet, bool HasSet, bool HasSetPut, bool HasSetRef, int ParameterCount,
        string RawType, string WrapperType);

    private sealed record MemberModel(Dictionary<string, PropModel> Properties,
        List<FuncModel> Methods, IndexerModel Indexer);

    private class PropModel {
        public string Name { get; set; } = "";
        public int DispId { get; set; }
        public string RawType { get; set; } = "";
        public string RawSetType { get; set; } = "";
        public bool HasGet { get; set; }
        public bool HasSet { get; set; }
        public bool HasSetPut { get; set; }
        public bool IsSetRef { get; set; }
    }

    private sealed record ParamModel(string Name, string RawType, bool IsOptional, bool HasDefault = false, object? DefaultValue = null); 
    
    private class FuncModel {
        public string Name { get; set; } = "";
        public int DispId { get; set; }
        public string RawReturnType { get; set; } = "void";
        public List<ParamModel> Params { get; set; } = new();
    }
}
