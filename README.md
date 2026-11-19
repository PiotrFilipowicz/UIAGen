![Universal Interop Assembly Banner](https://raw.githubusercontent.com/PiotrFilipowicz/UIAGen/main/Assets/Banner.png)

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![OS](https://img.shields.io/badge/Platform-Windows-0078D6?logo=windows)](https://microsoft.com/windows)
[![License](https://img.shields.io/badge/License-LGPLv3-blue.svg)](LICENSE)

# Universal Interop Assembly (UIA) Code Generator

**Build-time code generator (`UIAGen.exe`) turns any Windows COM Type Library into a clean, statically typed, **Native AOT-compatible C# adapter** for .NET 10.

Say goodbye to Primary Interop Assemblies (PIA), GAC deployment nightmares, and the slow C# `dynamic` binder.

---

## What is it for?

Windows runs on COM — from Microsoft Office and Adobe Creative Cloud to industrial CAD, ERP systems, and core OS automation. Yet modern .NET left developers behind without a modern `tlbimp.exe`, forcing teams to either stay stuck on .NET Framework 4.8 or write painful, error-prone interop wrappers by hand.

**UIA bridges that gap.** Point `UIAGen` at any `.tlb`, `.dll`, `.exe`, or registered `ProgID` (like `Word.Application` or `InDesign.Application`), and get ready-to-compile C# static typed classes with full Visual Studio IntelliSense, full debugger support and compile-time type safety.

---

## Why is UIA unique?

* **True Native AOT & Trimming Support:** Unlike legacy RCW/CCW interop, UIA generates static C# wrappers that call native COM vtables directly—compiling cleanly into standalone Native AOT executables.
* **Zero PIA & Zero `dynamic`:** Ship your application without hunting for version-specific Primary Interop Assemblies or sacrificing performance and type safety to `dynamic`.
* **Proven Compilation Across 300+ COM Servers:** Tested at scale against more than 300 registered Windows system COM libraries, as well as massive, real-world object models like Microsoft Office and Adobe InDesign.
* **Smart Static Typing:** Infers strongly typed collection elements (`Stories` $\rightarrow$ `Story`), provides runtime polymorphic casting (`As<T>()`), and cleanly separates missing COM objects from failed `HRESULT`s.
* **Deterministic Memory Control:** Built-in reference lifecycle tracking (`Tracker` / `Dispose`) ensures COM servers actually close when your code finishes—no lingering zombie processes in Task Manager.

---

## Quick Start

```bash
# 1. Install the generator
dotnet tool install -g UIAGen

# 2. Generate a C# adapter from any ProgID or TypeLib
uiagen Word.Application ./WordUIA WordAdapter
uiagen InDesign.Application,InCopy.Application ./AdobeUIA AdobeAdapter
uiagen -mso ./MSOfficeUIA MSOfficeAdapter
```

```csharp
// 3. Automate with full IntelliSense, debugger application tree full view and Native AOT support
using MSOfficeAdapter.Word;

using var app = Application.Create("Word.Application");
using var doc = app.Documents.Add();
doc.Content.Paragraphs.Add().Range.Text = "Hello from Native AOT!";
doc.Close(WdSaveOptions.wdDoNotSaveChanges);
app.Quit(WdSaveOptions.wdDoNotSaveChanges);
```

---

## Deep Dive

* **Technical Architecture & CLI Reference:** Ready to see how vtable binding, `VARIANT` stack marshaling, and debugger proxies work under the hood? Read **[ARCHITECTURE.md](https://raw.githubusercontent.com/PiotrFilipowicz/UIAGen/main/ARCHITECTURE.md)**.

## License

- **Generator (`UIAGen`):** GNU Lesser General Public License v3. See [LICENSE](https://raw.githubusercontent.com/PiotrFilipowicz/UIAGen/main/LICENSE) and [LICENSE.GPL](https://raw.githubusercontent.com/PiotrFilipowicz/UIAGen/main/LICENSE.GPL).
- **Generated output:** The [Code Generation Exception](https://raw.githubusercontent.com/PiotrFilipowicz/UIAGen/main/EXCEPTION) permits distribution of the generated output under terms of your choice, subject to the exception's scope. It does not relicense the generator itself.
