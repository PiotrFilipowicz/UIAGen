# UIA: .NET 10 COM automation adapters

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![OS](https://img.shields.io/badge/Platform-Windows-0078D6?logo=windows)](https://microsoft.com/windows)
[![License](https://img.shields.io/badge/License-LGPLv3-blue.svg)](LICENSE)

`UIAGen` reads Windows COM type libraries (TLBs) and generates typed C# adapters for .NET 10, including adapters for Microsoft Office, Adobe Creative Cloud, and Autodesk AutoCAD applications. You write ordinary C# against generated classes, get IntelliSense and compile-time checks, and can inspect the same typed object model in Visual Studio. The generated runtime uses native COM Automation calls without requiring a Primary Interop Assembly (PIA) or the C# `dynamic` binder.

## Static types and IntelliSense

The generated API exposes concrete C# wrapper classes for nearly all objects in the supported COM object models. For example, Word's `Application.Documents.Add()` returns a `Document`, not an untyped COM object. Visual Studio can complete the next member, show its signature, and flag invalid arguments at compile time. `var` works just as well as an explicit declaration: the compiler still knows the type. This checks calls against the generated API; it does not guarantee that the installed COM server will accept every call at runtime.

[Inferring collection element types](#inferring-collection-element-types) is a key part of static wrapper binding: it keeps indexing and `foreach` statically typed even when the COM type library does not declare the element type.

The exceptions are boundaries where COM metadata does not identify a single concrete result type, such as a polymorphic property declared as `object` or `ComProxy`. When you know the expected type, use `As<TWrapper>()` to adapt the result at runtime and continue with a statically typed variable. For example, PowerPoint exposes its chart's workbook as `object`, but the application can work with it as an Excel wrapper:

```csharp
using MSOfficeAdapter;
using Excel = MSOfficeAdapter.Excel;

// chart is a generated MSOfficeAdapter.PowerPoint.Chart wrapper.
Excel.Workbook workbook = chart.ChartData.Workbook.As<Excel.Workbook>();
// IntelliSense now offers Excel.Workbook members, such as Worksheets.
```

Choose the wrapper that matches the actual COM object. The conversion selects the managed wrapper; it does not change the COM object. **No `dynamic` variable is needed**, even when the concrete wrapper is chosen at runtime or COM members are invoked by name.

## Architecture

### Layered architecture

`UIAGen` separates binary COM metadata analysis from static C# emission, deterministic lifetime management, and debugger inspection across five stages:

1. **Metadata extraction and indexing (`TlbResolver`, `TypeLibDependencyScanner`, `Wrapper.PreScan`, `TypeInfoDescriptor`):** Loads binary `ITypeLib` and `ITypeInfo` metadata directly through OLE Automation without .NET reflection or PIAs. Native `TYPEATTR`, `FUNCDESC`, and `VARDESC` structures are acquired and released deterministically via `TypeInfoDescriptor`. The scanner classifies enums, interfaces/dispinterfaces, and coclasses, resolves cross-library references (such as `stdole`, `MSO.DLL`, or `MSForms`), and detects collection patterns—including Adobe's `Objects` container.
2. **Type resolution and canonicalization (`CanonicalTypeNames`, `Wrapper.TypeResolver`):** Maps raw `TYPEDESC` / `VARENUM` types (`BSTR`, `VARIANT`, `VARIANT_BOOL`, `SAFEARRAY`, `DATE`, `CURRENCY`, interface pointers) into canonical C# types and resolves collection element types (`TItem`) using plural-to-singular rules and application-specific overrides.
3. **Static C# code generation (`Wrapper.WrapperGenerator`, `Wrapper.Emit.Objects`):** Emits flat, statically typed wrapper classes with compile-time `DISPID` constants for early invocation, reserving `ComProxy` / untyped `Objects` for polymorphic or unspecified COM boundaries. The generated code avoids `System.Reflection.Emit`, `dynamic`, and built-in runtime RCW/CCW generation so it remains compatible with Native AOT and trimming.
4. **Deterministic lifecycle management (`ComStub`, `ComBehavior`, `Tracker`, `ComInvoker`):** Manages native `IDispatch` / `IUnknown` pointers explicitly and supports scoped LIFO reference cleanup through checkpoints (`GetCheckPoint()` / `MoveCheckPoint(int)`), avoiding reliance on the GC finalizer thread to release out-of-process COM servers.
5. **Developer ergonomics and debugger inspection (`ComProxyDebugView`, `GetDebuggerProperties`, `ReadDebuggerProperty`):** Exposes generated metadata and a single indexed property switch per wrapper so Visual Studio debugger windows inspect COM properties without per-property delegate allocations, while excluding expensive or state-mutating properties from automatic evaluation.

### Wrapper inheritance hierarchy

Generated adapters and runtime base classes follow a single inheritance hierarchy rooted at `ComStub`:

```text
ComStub (IDisposable, IComWrapper)
└── ComProxy (IComBindingState)
    ├── <Generated COM wrappers> (e.g. Document, Workbook, Story)
    └── ComProxies (IEnumerable<ComProxy>)
        ├── ComProxies<TItem> (IEnumerable<TItem>)
        └── Objects (Adobe adapters, IEnumerable<ComProxy>)
            └── Objects<TItem> (Adobe adapters, IEnumerable<TItem>)
```

- **`ComStub`**: Owns the native `ComPointer` (`IntPtr`) and the associated `IComBehavior? ComBehavior`. When constructed from an `IntPtr`, it queries for `IID_IDispatch` (falling back to `AddRef` on the pointer if `QueryInterface` does not return `IDispatch`); when constructed from an `IComWrapper`, it calls `AddRef` on `wrapper.ComPointer`; otherwise it acquires `IDispatch` via `ComStubOps.GetIDispatch`. `Dispose()` releases `ComPointer` via `ComStubOps.Release` and suppresses finalization.
- **`ComProxy`**: Inherits `ComStub` and implements `IComBindingState`. It adds dispatch invocation helpers (`GetValue`, `InvokeValue`, `GetWrapper`, `InvokeWrapper`, `Set`, `SetRef`, `SetAuto`, `InvokeVoid`), binding state (`IsPoly`, `ForceLateInvoke` / `IsLate`), missing-object sentinel state (`IsMissing`, `Found`, `IsCreated`, `Missing()`, `Missing<T>()`), scalar/array `Value` storage, pointer-based equality (`==`, `Equals`, `GetHashCode`), `Wrap<TWrapper>()`, `Unwrap()`, and debugger inspection (`GetDebuggerProperties()`, `ReadDebuggerProperty(int)`, `_DynamicDisplay`). All generated non-collection COM wrappers inherit from `ComProxy` (or generated base classes that inherit from `ComProxy`).
- **`ComProxies`**: Inherits `ComProxy` and implements `IEnumerable<ComProxy>`. It models untyped COM collections, providing `Count`, `_NewEnum` (`DISPID -4`) / `IEnumVARIANT` enumeration with 1-based indexed fallback, and `ComProxiesUntypedDebugView`.
- **`ComProxies<TItem>`**: Inherits `ComProxies` and implements `IEnumerable<TItem>` (where `TItem : class`). It exposes statically typed indexers (`this[object]`, `this[int]`, `this[string]`, `this[object row, object column]`), `Item(...)`, `TryGetValue`, `Exists`, and typed enumeration, wrapping returned elements with `ComObjectExtensions.As<TItem>` (or converting to `string` when `TItem` is `string`) while propagating `ComBehavior` and `ForceLateInvoke`.
- **`Objects` and `Objects<TItem>`** (Adobe adapters): `Objects` inherits `ComProxies`, and typed Adobe collections inherit `Objects<TItem>` (which inherits `Objects`). This preserves the `ComProxies` / `ComProxy` / `ComStub` contract while adding Adobe-specific collection behavior such as `ItemByRange` returning `Objects` and `Add` signatures where declared by the type library.

### Wrapper binding and invocation

**Wrapper binding** determines which managed type represents a COM object. **Invocation** determines how the runtime obtains the dispatch identifier (`DISPID`) for a COM member. Both early and late invocation ultimately call `IDispatch::Invoke`; a typed wrapper does not require early invocation, and casting to a wrapper does not set `IsPoly` by itself.

| Wrapper binding | Invocation | How `DISPID` is selected |
| --- | --- | --- |
| Typed, non-polymorphic | `InvokeMode.Early` (default) | Use the known `DISPID`. If none is available, resolve by name. If the known-ID call fails, retry by name when lookup succeeds. |
| Typed, non-polymorphic | `InvokeMode.Late` | Resolve by name, reusing a cached `DISPID` and LCID when a type key is available. |
| Polymorphic (`IsPoly == true`) | Late invocation, regardless of `InvocationMode` | Resolve by name for each call. Skip both the known `DISPID` and the shared type cache. |

The polymorphic path does not assume that `DISPID` changes on every call. It avoids reusing an ID obtained for a different COM implementation that happens to share the same managed wrapper type. Name lookup uses `GetIDsOfNames`, with a type-info fallback. `ForceLateInvoke` is a separate per-wrapper override used by `_DynamicDisplay`: it also bypasses the known ID and shared cache, without setting `IsPoly`.

For compatibility, `InvokeType`, `InvokeMode.Static`, and `IsLate` are aliases for `InvocationMode`, `InvokeMode.Early`, and `ForceLateInvoke`, respectively.

### Generate types before resolving calls at runtime

The generator indexes TLB names and dependencies before emitting wrappers, so it can resolve references to enums, interfaces, and types from other libraries consistently. A multi-library adapter emits per-library enums and wrappers plus separate shared `Infra.cs`, `BaseObjects.cs`, and `TypeMappings.cs` files. `BaseObjects.cs` contains types and behavior independent of the selected adapter libraries; library-specific enum conversions and wrapper-factory registrations are emitted in `TypeMappings.cs`. Wrapper factories prefer the combination of TLB GUID and type name, with a name-only fallback; the GUID prevents identically named interfaces from different libraries from being conflated when library identity is available.

For an optional enum parameter, an explicit TLB default takes precedence. If the TLB supplies no concrete default, the generated C# parameter uses the enum's first declared member (or `default` for an empty enum). Optional primitive value-type parameters without a concrete TLB default use the C# type's default (`false` or zero); explicit defaults take precedence. These are generator conventions, not defaults specified by the TLB; the chosen values are passed to COM when the arguments are omitted.

### Native AOT and trimming

`UIAGen` is a build-time tool, not a component the published application needs to run. The adapter's C# types and factory registrations are generated before compilation. The runtime calls native COM vtables directly and marshals supported Automation values explicitly, so it does not depend on the C# runtime binder or on runtime generation of interop wrappers. Looking up a `DISPID` by name is still possible in a Native AOT app: it is a COM operation, not C# dynamic binding or runtime code generation.

The generated factories reference wrapper constructors directly. Paths that use reflection, such as `As<TWrapper>()`, annotate the public constructors they need so the trimmer can preserve them. Generated projects enable the AOT and trimming analyzers, and the Office integration project enables `PublishAot` in Release. This is not a guarantee for every consumer dependency or COM server: review analyzer warnings in the consuming app, and remember that the target Windows machine still needs the relevant COM server installed and registered.

### Own native references and marshal values explicitly

`ComPointerLease` and `ComStub` track ownership of native references. Generated `Application.Create(progId)` initializes the application's `ComBehavior` and its `Tracker`; related wrappers can share that behavior. `Tracker.MoveCheckPoint` disposes wrappers registered after the checkpoint in reverse order. Generated `Application.Quit` methods that clear the tracker do so before invoking COM. `Dispose` releases a wrapper's reference; it does not shut down the application. Use the server's `Quit` or `Close` method when you own the application or document.

`ComInvoker` uses stack storage for `VARIANT` argument slots (up to 256 arguments) and the result. BSTRs, SAFEARRAYs, and managed conversions can still allocate. The runtime explicitly encodes and decodes supported `VARIANT` types and clears temporary variants; this gives it control over native ownership and over the behavior of values such as `VT_EMPTY` and `VT_NULL`.

### Distinguish missing objects from failed calls

Generated wrapper-returning members do not return CLR `null` for missing COM objects. The adapter checks the `HRESULT` separately from the returned value:

- **Successful call with a null object result** (for example, `hr == 0`, `S_OK`): the adapter returns a non-null `Missing` wrapper. This also applies to a null result from `Document.Selection`, even though the property is declared as `object`.
- **Failed call** (`hr < 0`): if the failure persists after any applicable retry, the adapter throws a `COMException`. A nonzero `HRESULT` is not necessarily a failure: positive values such as `S_FALSE` are success codes. Selected collection APIs can opt into returning `Missing` for known missing-item HRESULTs; this is not a general error-handling policy.

Check `IsMissing` rather than comparing the wrapper with `null`. `Application.GetMissing()` and `ComProxy.Missing<T>()` create typed sentinels without activating COM. Generated wrappers reserve `Missing` and `GetMissing`; conflicting COM members become `MissingValue` and `GetMissingValue` without changing the underlying COM name or `DISPID`.

## Code examples

### Advanced example: resolving InDesign's polymorphic selection

`Document.Selection` is a property that returns `object`. The helper below reads it once and handles both an `Objects` collection and a single `ComProxy`. It checks for `Objects` first because `Objects` inherits from `ComProxy`.

```csharp
using System;
using System.Collections.Generic;
using AdobeAdapter;
using AdobeAdapter.InDesign;

static List<Story> GetSelectedStories(Document document)
{
    List<Story> stories = [];
    if (document.IsMissing)
        return stories;

    var storyIds = new HashSet<string>(StringComparer.Ordinal);

    void AddSelectedItem(ComProxy item)
    {
        Story story = item switch
        {
            TextFrame { IsMissing: false } frame => frame.ParentStory,
            Text { IsMissing: false } text => text.ParentStory,
            Story selectedStory => selectedStory,
            _ => ComProxy.Missing<Story>()
        };

        if (!story.IsMissing && storyIds.Add(story.IDMLComponentName))
            stories.Add(story);
    }

    switch (document.Selection)
    {
        case Objects { IsMissing: false } items:
            foreach (ComProxy item in items)
                AddSelectedItem(item);
            break;
        case ComProxy item:
            AddSelectedItem(item);
            break;
    }

    return stories;
}
```

Call `GetSelectedStories(app.ActiveDocument)` from an STA thread with an InDesign application wrapper. Frames and text ranges can refer to the same story, so the helper adds each `IDMLComponentName` only once. An empty selection or an unsupported item type contributes no stories. See [`UtilsCC.GetSelectedStories`](../AdobeCCDispatcher/Common/UtilsCC.cs) for the application implementation.

The `TextFrame` pattern checks the wrapper type at runtime and gives `frame` a static type, so `frame.ParentStory` has IntelliSense and compile-time checking without `As<TextFrame>()` or C# `dynamic`. A polymorphic result does not by itself force late COM invocation: the runtime chooses how to resolve the `DISPID` independently of how the caller handles the result.

### Microsoft Word automation

Run this from an STA entry point after generating the `MSOfficeAdapter` sources:

```csharp
using MSOfficeAdapter.Word;
using UiaAdapter.ComRuntime;

using var app = Application.Create("Word.Application");
app.ComBehavior!.InvocationMode = InvokeMode.Early;
try {
    using var doc = app.Documents.Add();
    try {
        using var paragraph = doc.Content.Paragraphs.Add();
        paragraph.Range.Text = "COM automation with a generated .NET 10 adapter";
    }
    finally {
        doc.Close(WdSaveOptions.wdDoNotSaveChanges);
    }
}
finally {
    app.Quit(WdSaveOptions.wdDoNotSaveChanges);
}
```

## Building and generating adapters

Run the following commands from `CC\UIA` on Windows with the .NET 10 SDK:

```powershell
dotnet build .\UIA.slnx -c Debug -p:Platform=x64
```

The generator is a build-time tool. The current `UIA.slnx` builds it without running generation; invoke `UIAGen` explicitly to refresh adapter sources. The no-argument run probes the configured Office and Adobe libraries and can report failure if some are not installed. Use `--adapter` to generate only the adapter you need.

```powershell
$generator = '.\UIAGen\bin\x64\Debug\net10.0\UIAGen.dll'

# Generate the configured Office and Adobe adapters under CC\UIATests.
dotnet $generator

# Generate a standalone module from a single library.
dotnet $generator "$env:WINDIR\System32\stdole2.tlb" C:\Temp\UiaGeneration\OleUIA OleShared

# Generate an adapter from one or more libraries and discover their dependencies.
dotnet $generator Word.Application,Excel.Application,Access.Application,PowerPoint.Application C:\Temp\UiaGeneration\MSOfficeUIA MSOfficeAdapter

# Combine a file, a ProgID, and a registered class name in one adapter (PowerShell 7.3+).
dotnet $generator '"C:\Type Libraries\Custom, v2.tlb",Word.Application,"Microsoft Excel Application"' C:\Temp\CombinedUIA CombinedAdapter

# Scan registered libraries; optional -filter restricts paths and -list prints discovered servers without generating code.
dotnet $generator -all C:\Temp\UiaAll
dotnet $generator -all C:\Temp\UiaCombined CombinedAdapter
dotnet $generator -all -filter "C:\Program Files\Autodesk,C:\Program Files\Common Files\Autodesk Shared" -list
```

Combined-adapter syntax: `UIAGen Module1,Module2,Module3 outputFolder className`. Pass the list as a single command-line argument; it can contain one or more modules. A module can be a path to a `.tlb`, `.exe`, or `.dll` that contains a type library, a ProgID, or a registered name or GUID under `HKEY_CLASSES_ROOT\CLSID`, `TypeLib`, or `Interface`. Double quotes can enclose individual module names; if a name contains a comma, the shell must pass those double quotes through to `UIAGen` as shown above. The final `className` names the adapter's root namespace, not a COM class. The generator does not write output unless all listed modules resolve.

Single-library syntax: `tlbOrProgIdOrName className projectName prefixName [outputPath]`. The example writes `Generated.UIA.Ole.Infra.cs`, `Generated.UIA.Ole.Enums.cs`, `Generated.UIA.Ole.Wrap.cs`, the runtime sources, and a project file under `C:\Temp\UiaGeneration\Output`. Exit codes are `0` for success, `1` for generation failure, and `2` for invalid argument counts. Edit `UIAGen\Templates`, `UIAGen\Runtimes`, or the generator itself for changes that should survive regeneration.

### Why type-library discovery does not activate COM servers

Generation reads metadata, not live application objects. The resolver accepts existing file paths and tries ProgIDs before searching registered names or GUIDs. For ProgIDs backed by multi-resource binaries (such as `acad.exe`), the resolver consults `CLSID\TypeLib` before probing embedded `TYPELIB` resources directly so the exact registered type library is selected. Registry discovery examines both the 32-bit and 64-bit views; interface and class entries can resolve to a registered TLB. Multiple versions of one GUID are not ambiguous, but multiple loadable library GUIDs matching the same name are reported as an error rather than selected arbitrarily.

`LoadTypeLibEx` is called with `REGKIND_NONE` and returns an `HRESULT` to the resolver. A failed direct load is a normal probe result: the resolver can try embedded `TYPELIB` resources without creating a `COMException` for the failed load. `-all` deduplicates libraries by GUID, prefers ProgID sources, supports folder filtering (`-filter`) across both TLB and server binary paths (`InProcServer32` / `LocalServer32`), and can list matching servers without emitting files (`-list`). Adapter generation failures are reported separately.

## Collections

### Inferring collection element types

COM type libraries often declare `Item` as returning a generic COM object rather than naming the element type. `UIAGen` recovers the vast majority of these missing types at generation time using English plural-to-singular rules. For example, `Documents` yields `Document`, and `Galleries` yields `Gallery`. The generator checks the candidate against known types before using it. A small, application-specific exception table overrides the rules where the name alone is misleading: in Word, `ProofreadingErrors` contains `Range` objects, and `FontNames` contains strings.

When a type can be inferred, the generator emits a collection based on `ComProxies<TItem>` or, for Adobe, `Objects<TItem>`. The indexer and `foreach` then expose `TItem` directly, with IntelliSense and compile-time checks and without casts or `dynamic` for each element. If no suitable type is found, the generator keeps an untyped collection rather than guessing. This decision determines the managed element type, independently of how COM invocation resolves the `DISPID`.

### Enumeration and Adobe operations

Enumeration prefers native `_NewEnum` (`DISPID -4`) and `IEnumVARIANT`, with indexed access as a fallback when a native enumerator is unavailable. Adobe adapters use `Objects` and `Objects<TItem>` for their collection API; other adapters use `ComProxies` and `ComProxies<TItem>`. Adobe's `ItemByRange` returns the library's `Objects` type, and `Add` is emitted only where the TLB exposes it, with the corresponding signature.

## Inspecting wrappers in the Visual Studio debugger

At a breakpoint, expand a generated wrapper in **Locals** or **Watch**. The debugger proxy uses the generated property getters, so the **Name**, **Value**, and **Type** columns show the managed view of the COM object. For example, InDesign's `ActiveScriptUndoMode` is shown as an `idUndoModes` value instead of an integer. When you expand a collection, its debug view can show the count and individual items without requiring you to write a loop in your application.

Expansion can execute COM calls. The view therefore excludes selected properties from automatic evaluation and can limit or block collection enumeration. Internally, one indexed switch per wrapper dispatches property reads without allocating a delegate per property. Expand `_DynamicDisplay` explicitly when you need to inspect members returned by the COM server by name; this view can show raw Automation values, such as an `int` in place of `idUndoModes`. It is a debugger feature, not a requirement to use `dynamic` in application code.

The **Type** suffix is empty when a known `DISPID` was used, `[fallback]` when an early invocation was retried by name, and `[late]` when the invocation used name-based resolution from the start. These labels describe invocation, not the C# type of the wrapper. Use a debugger-enabled build for inspection; publishing with debugger support disabled is a separate choice from using Native AOT.

## Case Study: Autodesk AutoCAD COM Type Library Dependency Graph

Running `UIAGen.exe -all -filter` against an Autodesk AutoCAD 2026 installation demonstrates how the generator discovers and resolves cross-library `HREFTYPE` dependencies directly from binary TypeLibs without reflection or `tlbimp.exe`:

```text
Found 4 type libraries for 'AcAuthEntitiesComLibAdapter' (3 external - stdole, AXDBLib, MSForms).
Found 4 type libraries for 'ACSMCOMPONENTS25LibAdapter' (3 external - stdole, AXDBLib, MSForms).
Found 4 type libraries for 'AutoCADAdapter' (3 external - stdole, AXDBLib, MSForms).
Found 5 type libraries for 'axctextappLibAdapter' (4 external - stdole, AutoCAD, AXDBLib, MSForms).
Found 5 type libraries for 'axrtextLibAdapter' (4 external - stdole, AutoCAD, AXDBLib, MSForms).
Found 5 type libraries for 'CAOAdapter' (4 external - stdole, AutoCAD, MSForms, AXDBLib).
Found 4 type libraries for 'MPolygonComLibAdapter' (3 external - stdole, AXDBLib, MSForms).
Found 4 type libraries for 'ObjClassImpLibAdapter' (3 external - stdole, AXDBLib, MSForms).

Found 3 type libraries for 'ACCTRLLibAdapter' (2 external - stdole, MSForms).
Found 3 type libraries for 'AcDwgFilterImpLibAdapter' (2 external - stdole, MSForms).
Found 3 type libraries for 'ACFOCUSCTRLLibAdapter' (2 external - stdole, MSForms).
Found 3 type libraries for 'ACHELPLibAdapter' (2 external - stdole, MSForms).
Found 3 type libraries for 'ACINETENGINELibAdapter' (2 external - stdole, MSForms).
Found 3 type libraries for 'ACSIGNCORE16LibAdapter' (2 external - stdole, MSForms).
Found 3 type libraries for 'AdComFolderWatchLibAdapter' (2 external - stdole, MSForms).
Found 3 type libraries for 'AXDBLibAdapter' (2 external - stdole, MSForms).
Found 3 type libraries for 'TRANSMITTALLibAdapter' (2 external - stdole, MSForms).
Found 3 type libraries for 'WebServicesLibAdapter' (2 external - stdole, MSForms).
```

### Architectural insights from the resolved graph

1. **Clear separation between core/headless modules and the application UI layer**
   - **Standalone / Core Layer (`AXDBLib`, `ACCTRLLib`, `TRANSMITTALLib`, `AcSignCore16Lib`, etc.):** Depends solely on `stdole` and `MSForms`. Libraries such as ObjectDBX (`AXDBLib`) or eTransmit (`TRANSMITTALLib`) can be bound and used without pulling in the main `AutoCAD` application object model.
   - **Application & Express Tools Layer (`CAOAdapter`, `axctextappLib`, `axrtextLib`):** Explicitly references both `AutoCAD` and `AXDBLib`, operating directly on document windows, selection sets, and drawing collections.

2. **Acyclic dependency hierarchy (`AutoCAD` → `AXDBLib`)**
   - `AutoCADAdapter` depends on `AXDBLib`, whereas `AXDBLibAdapter` has no reverse dependency on `AutoCAD`. This acyclic structure allows generating either isolated per-library adapters or a single unified adapter sharing common base classes (`AcadEntity`, `AcadDatabase`, `AcCmColor`) and a single `TypeMappings` dispatch table.

3. **Automatic resolution of implicit `MSForms` (`FM20.DLL`) references**
   - AutoCAD COM libraries expose ActiveX control and data-transfer interfaces (`DataObject`, `Picture`, `Font`) that reference `Microsoft Forms 2.0 Object Library` (`MSForms`). `UIAGen` automatically follows these binary `HREFTYPE` links and emits the required external type definitions without manual `FM20.DLL` hunting.

## Source layout

| Source under `UIAGen` | Responsibility |
| --- | --- |
| `TlbResolver.cs`, `RegistryTypeLibScanner.cs`, `TypeLibDependencyScanner.cs` | Resolve input libraries and discover referenced type libraries. |
| `Wrapper.PreScan.cs`, `CanonicalTypeNames.cs`, `Wrapper.TypeResolver.cs` | Index type names and resolve their managed spelling and namespace. |
| `Wrapper.MemberReader.cs`, `TypeInfoDescriptor.cs` | Read COM members and release borrowed type information descriptors. |
| `Wrapper.WrapperGenerator.cs`, `Wrapper.Emit.Objects.cs` | Emit typed wrappers, shared infrastructure, and factory registrations. |
| `Templates\ComInvoker.cs`, `Templates\BaseObjects.cs` | Implement dispatch, wrapper binding, collection access, and debugger views. |
| `Runtimes\ComBehavior.cs`, `Runtimes\ComContracts.cs`, `Runtimes\ComStubOps*.cs` | Define invocation policy, reference tracking, native COM calls, and `VARIANT` conversion. |

## Verification

After generating the adapters, run these commands from `CC\UIA`:

```powershell
dotnet run --project ..\UIATests\InDesign\InDesign.csproj -c Debug -p:Platform=x64 -- --collections-only
dotnet run --project ..\UIATests\InDesign\InDesign.csproj -c Debug -p:Platform=x64 -- --debugger-only
dotnet run --project ..\UIATests\MSOffice\MSOffice.csproj -c Debug -p:Platform=x64
```

The first two InDesign modes do not launch InDesign. `--debugger-live` inspects properties on installed InDesign. The Office integration scenarios exercise Word, Excel, PowerPoint, Outlook, and Access with early invocation (including automatic fallback) and with late invocation. Run them in an interactive Windows session with the applications installed and an Outlook profile configured. The tests print the diagnostic TSV path under `%TEMP%\UiaOfficeTests`; a `START` record without a corresponding `END` or `ERROR` identifies the measured operation that did not return.