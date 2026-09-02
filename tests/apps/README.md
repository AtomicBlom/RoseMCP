# Live-app test target apps

Real applications the live-app integration tests drive: attach a debugger, walk the visual tree, read
and mutate properties, hot-reload XAML. Keeping them in the repository means the XAML/UWP work is
tested against apps we own, not a machine-specific external one.

## Frameworks

Each XAML stack exposes the tree and diagnostics differently, so there is an app per stack. Built as
they are needed:

| Folder | Stack | Status |
| --- | --- | --- |
| `uwp-classic/` | Classic UWP (`uap10.0`, `Windows.UI.Xaml`) | present |
| `uwp-modern/` | UWP on modern .NET | planned |
| `winui/` | WinUI 3 (`Microsoft.UI.Xaml`) | planned |
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

The build output under `bin\x64\Debug\` is a loose package layout (it contains `AppxManifest.xml`).
Register it without a certificate, then activate it under the debugger:

```
Add-AppxPackage -Register bin\x64\Debug\AppxManifest.xml
```

The integration tests do this on demand and **skip** when the MSBuild/UWP toolchain is not present, so
the rest of the suite stays green on a machine without it.

## What the apps contain

Each app has named, inspectable elements for the tree and property tests, and a method the debugger
tests can trace, break on, and read locals from — which also throws a distinctively named exception so
exception capture can be exercised. In the classic UWP app that is `MainPage.Tick`, throwing
`RoseUwpProbeException`, mirroring the console probe target's `Beat`.
