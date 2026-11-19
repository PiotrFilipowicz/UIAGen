//------------------------------------------------------------------------------
// <copyright file="ComBehavior.cs" company="Piotr Filipowicz">
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
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;

namespace UiaAdapter.ComRuntime;

public enum InvokeMode {
    Early, // Prefer the known DISPID; fall back to name lookup.
    Late, // Resolve by name; reuse a cached DISPID when permitted.
    Static = Early // Compatibility alias.
}

public interface ITracker {
    void MoveCheckPoint(int checkPoint);
    int GetCheckPoint();
    void Add(object element);
    void Clear();
}

public interface IComBehavior {
    ITracker Tracker { get; }
    InvokeMode InvokeType { get; set; }
    InvokeMode InvocationMode {
        get => InvokeType;
        set => InvokeType = value;
    }
}


public sealed class Tracker : ITracker {
    private int _index = 0;
    private List<object>? _list = null;

    public void MoveCheckPoint(int checkPoint) {
        if(_list == null)
            return;
        if ((uint)checkPoint > (uint)_index)
            throw new ArgumentOutOfRangeException(nameof(checkPoint));

        for (int i = _index - 1; i >= checkPoint; i--)
            (_list[i] as IDisposable)?.Dispose();
        if (checkPoint < _list.Count)
            _list.RemoveRange(checkPoint, _list.Count - checkPoint);
        _index = checkPoint;
    }

    public int GetCheckPoint() { // Return the current checkpoint, initializing the list if needed.
        if (_list==null)
            _list = new List<object>();
        return _index;
    }

    public void Add(object element) {
        if (_list == null)
            return;
        if (_index >= _list.Count)
            _list.Add(element);
        else
            _list[_index] = element;
        _index++;
    }

    public void Clear() => MoveCheckPoint(0);
}

public sealed class ComBehavior : IComBehavior {
    public ITracker Tracker { get; } = new Tracker();
    public InvokeMode InvocationMode { get; set; } = InvokeMode.Early;
    public InvokeMode InvokeType {
        get => InvocationMode;
        set => InvocationMode = value;
    }
}
