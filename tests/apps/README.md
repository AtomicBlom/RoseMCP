# Live-app test target apps

Real applications the live-app integration tests drive: attach a debugger, walk the visual tree, read
and mutate properties, live-edit XAML. Keeping them in the repository means the XAML/UWP work is
tested against apps we own, not a machine-specific external one.

## Frameworks

Each XAML stack exposes the tree and diagnostics differently, so there is an app per stack. Built as
they are needed:

| Folder | Stack | Status |
| --- | --- | --- |
| `uwp-classic/` | Classic UWP (`uap10.0`, `Windows.UI.Xaml`) | present |
| `winui/` | WinUI 3 (`Microsoft.UI.Xaml`), packaged **and** unpackaged | present |
| `uwp-modern/` | UWP on modern .NET (`UseUwp`, `Windows.UI.Xaml`) | present |
| `wpf/` | WPF (`net10.0-windows`) | planned |

Classic UWP comes first because it is what the architecture shim and the `Windows.UI.Xaml` diagnostics
mechanism are built around.

## Why these are isolated from the repository build

These are foreign project types with their own frameworks and toolchains. The `Directory.Build.props`
and `Directory.Packages.props` here shadow the repository-root ones, so the apps are **not** forced
into `net10.0`, warnings-as-errors, or central package management. They are also **not** in
`RoseMcp.slnx`: classic UWP is an old-style MSBuild project that `dotnet build` cannot build, so it
would break a solution-wide `dotnet build`.

## Building the classic UWP app

It needs full MSBuild from a Visual Studio install with the UWP/Windows-SDK tooling (not `dotnet`).
`Debug|x64` uses CoreCLR and is debuggable; `ARM64` and `Release` force the .NET Native toolchain and
are not — which is why, on Windows-on-ARM, the app is debugged x64-emulated.

```
"<VS>\MSBuild\Current\Bin\MSBuild.exe" uwp-classic\Rose.ProbeApp.UwpClassic.csproj -t:Restore
"<VS>\MSBuild\Current\Bin\MSBuild.exe" uwp-classic\Rose.ProbeApp.UwpClassic.csproj -t:Build -p:Configuration=Debug -p:Platform=x64
```

Do **not** register `bin\x64\Debug\AppxManifest.xml` directly. A `Debug|x64` build produces two
executables — the managed app assembly in `bin\x64\Debug\`, and a native CoreCLR apphost in
`bin\x64\Debug\Core\` — and the build folder is not a runnable layout. Registering the root manifest
makes Windows host the managed exe under the desktop .NET Framework CLR, which cannot load CoreCLR's
`System.Private.CoreLib`, so the app dies at host init with a `BadImageFormatException` before any of
its code runs.

The runnable layout is the one Visual Studio's deploy stages from the build's `*.build.appxrecipe`
(into `bin\x64\Debug\AppX`): the native apphost becomes the package executable, the managed assembly
moves under `entrypoint\`, and the CoreCLR `System.Runtime.dll` (not the desktop-framework copy also
in the build folder) sits beside them. Register that staged layout without a certificate, then
activate it under the debugger:

```
Add-AppxPackage -Register bin\x64\Debug\AppX\AppxManifest.xml
```

The integration tests stage that layout from the recipe on demand (`StageUwpProbeLayout`) and **skip**
when the MSBuild/UWP toolchain is not present, so the rest of the suite stays green on a machine
without it.

## Building the WinUI 3 app

SDK-style, so `dotnet build` is enough — no full MSBuild, unlike classic UWP. It builds both shapes
from one project:

```
dotnet build winui\Rose.ProbeApp.WinUi.csproj -r win-x64                        # unpackaged
dotnet build winui\Rose.ProbeApp.WinUi.csproj -r win-x64 -p:ProbePackaged=true  # packaged
```

The unpackaged build is an ordinary exe: run it directly. The packaged build writes a registrable
loose layout into the same output folder — the exe *and* an `AppxManifest.xml` beside it, with no
`AppX` subfolder and no recipe staging, because WinUI 3 desktop has none of the split apphost problem
that makes the classic UWP layout so awkward:

```
Add-AppxPackage -Register bin\Debug\net10.0-windows10.0.19041.0\win-x64\AppxManifest.xml
```

Pass `-r win-arm64` on an ARM64 machine. Unlike classic UWP, WinUI 3 runs natively as ARM64, so
there is no emulation and the live-app host runs as ARM64 too.

### Why both shapes, and what they actually differ in

Measured on this machine rather than assumed, because the natural assumption is wrong:

| Target | Package identity | AppContainer |
| --- | --- | --- |
| Classic UWP | yes | **yes** |
| WinUI 3, packaged | yes | no |
| WinUI 3, unpackaged | no | no |

A packaged WinUI 3 app is a *packaged desktop* app: it has package identity and runs full trust
(`runFullTrust`, `Windows.FullTrustApplication`), so it is **not** in an AppContainer. Packaging and
sandboxing are separate things, and only classic UWP has both.

That matters because it is what decides whether the XAML provider's work folder needs granting to
ALL APPLICATION PACKAGES. Reading "packaged" as "sandboxed" would put grants on a WinUI 3 target that
needs none, leaving a world-readable directory in TEMP for every session — so `NeedsAppContainerGrants`
on the tap is genuinely per-stack, not per-packaging. Having both shapes here is what makes that
checkable rather than argued about.

## Building the modern UWP app

SDK-style, and **`dotnet build` is not enough** — which is the one thing about this project type that
most looks like it should work. The UWP XAML markup compiler runs only under full MSBuild. Under the
.NET SDK's it does not run and does not complain, so the build fails with `CS0103` on
`InitializeComponent` and `CS5001` for a missing entry point, which reads like broken source rather
than a wrong toolchain. That was measured against a known-good project, not inferred from ours.

Two installs are needed and they are separate: MSBuild from Visual Studio, and the markup compiler
from the Windows SDK, at `Windows Kits\10\bin\<version>\XamlCompiler\`. The classic probe checks for
the `WindowsXaml` *targets* under the VS install instead, which is right for a project that imports
them by path and says nothing about a `UseUwp` one.

```
"<VS>\MSBuild\Current\Bin\MSBuild.exe" uwp-modern\Rose.ProbeApp.UwpModern.csproj -t:Restore -p:Platform=x64 -p:RuntimeIdentifier=win-x64
"<VS>\MSBuild\Current\Bin\MSBuild.exe" uwp-modern\Rose.ProbeApp.UwpModern.csproj -t:Build -p:Platform=x64 -p:RuntimeIdentifier=win-x64
```

Unlike classic UWP there is no staging step at all. The build writes a flat, self-contained folder
with `AppxManifest.xml` beside a native apphost and `coreclr.dll`, and registering it works directly —
none of the split-apphost problem that makes the classic layout so awkward:

```
Add-AppxPackage -Register bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\AppxManifest.xml
```

### What it is for, given uwp-classic exists

The app model and the XAML framework are identical — `Windows.UI.Xaml`, an AppContainer, an AUMID —
and the UWP tap serves both unmodified. What differs is underneath, and none of it is reachable from
`uwp-classic`:

| | Classic UWP | UWP on modern .NET |
| --- | --- | --- |
| Runtime | CoreCLR (`Debug\|x64` only) | CoreCLR, every configuration |
| Release | .NET Native | NativeAOT |
| Architectures | x64 debuggable; ARM64 forces .NET Native | x86, x64, ARM64 all CoreCLR |
| Managed frames | direct | behind a CsWinRT ABI layer |
| Registration | stage `AppX` from a build recipe | register the build folder |

A modern UWP process also loads `Microsoft.Windows.UI.Xaml.dll` — the CsWinRT projection, a managed
assembly — beside the framework's own `Windows.UI.Xaml.dll`. That is a third name in a family whose
other two decide opposite answers, and it is not a XAML framework; `XamlStackModulesTests` covers it.

`<Platforms>` deliberately omits x86, which the project builds perfectly well. The live half cannot
debug it yet: UWP launches are pinned to x64 and no `win-x86` host is ever built (#117). Adding x86
would give the suite a target it could build, register and launch, and then fail to attach to for a
reason that has nothing to do with the app.

## What the apps contain

Each app has named, inspectable elements for the tree and property tests, and a method the debugger
tests can trace, break on, and read locals from — which also throws a distinctively named exception so
exception capture can be exercised. In the classic UWP app that is `MainPage.Tick`, throwing
`RoseUwpProbeException`, mirroring the console probe target's `Beat`.

The WinUI 3 app mirrors that one **element for element and name for name** — `RootGrid`, `Panel`,
`Pane`, `Caption`, `Counter`, `Transient`, the unnamed `Border` pair under `Pair`, `Rows`/`Attached`,
the `ProbeAccent` keyed brush and the `Themed` border referencing it — so an assertion written for
one stack becomes one for the other by changing the app it drives, and anything that has to differ
beyond that is a finding rather than a detail. Its exceptions are `RoseWinUiProbeException`,
`RoseWinUiTransientRemovedException` and `RoseWinUiStartupException`, named apart from UWP's so a
test cannot pass against the wrong app.

The one deliberate structural difference is above the root grid, and it is the substance of #75:
a UWP page is hosted in a `Frame` on an ambient `Window.Current`, while a WinUI 3 window is an
object the app constructs and holds, because `Window.Current` does not exist there.

The modern UWP app mirrors `uwp-classic` too, and there the markup is *generated from it* rather than
written to match — a rename of the namespace and the three exception types, and nothing else. It is
the same app model and the same XAML framework, so anything that differs between the two is a fact
about the runtime rather than about the app, which is the only reason it is worth having. Keeping it
a derivation rather than a copy is what makes that true: an assertion that passes against one and
fails against the other cannot be blamed on the markup having drifted. Its exceptions are
`RoseUwpModernProbeException`, `RoseUwpModernTransientRemovedException` and
`RoseUwpModernStartupException`.
