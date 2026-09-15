# Framework detection, hosts, and deploy

Read before changing `XamlStackModules`, architecture detection, `tools/deploy.ps1`, or which runtimes an install carries.

- **Which XAML framework a target is running is asked of the target, and the order of the asking is
  the trick.** The live half had no idea: it hard-coded UWP in four places -- the
  `Windows.UI.Xaml.dll` DllImport, the provider file name, the CLSID, and the AppContainer grants --
  while the stub half asked properly. The cost was not that WinUI 3 failed but *how*: twenty seconds
  waiting for an endpoint that could never appear, ending in a claim that the app was not packaged,
  which is wrong twice over since packaging is irrelevant and unpackaged WinUI 3 is ordinary. The
  answer was in the target's loaded modules the whole time, the way `RuntimeFlavour` already reads
  them for "is this even CoreCLR". One `XamlStack` names the framework and both halves use it, but
  they stay separate mechanisms on purpose -- a compilation is not available to a host holding a pid
  for an app it never built, and the process in front of you settles which of a solution's projects
  you are actually looking at.
  <br>
  `Windows.UI.Xaml.dll` is tested **first**, and that ordering is the whole rule. WinUI 2 is a UWP
  library that ships a `Microsoft.UI.Xaml.dll` of its own, so a UWP app using it loads both names --
  and it is a UWP app, whose diagnostics live in `Windows.UI.Xaml.dll`. Matching the Microsoft name
  first reports WinUI 3 and refuses a target the UWP tap serves perfectly well. The issue proposing
  this work named `Microsoft.UI.Xaml.dll` as the WinUI 3 signal, and a real WinUI 3 process does load
  it, so the claim survives inspection and fails on WinUI 2 -- which is why every name in
  `XamlStackModules` was read off a running process rather than reasoned about, and why the rule
  lives in `Contracts` where a test can reach it: the host is `net10.0-windows` and neither test
  project takes a compile reference on it.
- **UWP on modern .NET needed nothing, and running one is what established that.** A `UseUwp` app on
  `net10.0-windows` is `Windows.UI.Xaml` in an AppContainer behind an AUMID, so the UWP tap serves it
  unmodified -- same endpoint, same initialiser out of the framework itself, same `CoreDispatcher`
  seam, same grants -- and the workspace half stubs its markup and compiles it clean. `XamlStackModules`
  had *claimed* that since #74 while its own rule says every name in it was read off a running process,
  and no such process had ever been read. Doing it turned up two things reasoning had not. The process
  loads `Microsoft.Windows.UI.Xaml.dll` -- the CsWinRT projection, a managed assembly -- beside the
  framework's own `Windows.UI.Xaml.dll`: a third name in a family of three, the only one that is not a
  XAML framework, and the one that reads like the WinUI signal. It is passed over only because matching
  is by whole name, so do not relax that to a prefix or a substring. And the XAML markup compiler runs
  only under full MSBuild; under `dotnet build` it does not run and does not complain, so the build
  fails with `CS0103` on `InitializeComponent`, which reads like broken source rather than a missing
  toolchain and would be diagnosed as a bug in the app.
  <br>
  What it does change is architecture, and that is where an old shortcut became a wrong answer. Classic
  UWP is debuggable only as `Debug|x64` -- every other configuration forces .NET Native -- so pinning
  UWP launches to x64 cost nothing and said something true. A modern UWP app is CoreCLR in x86, x64 and
  ARM64 alike, and the standard template leads with x86, so `DetectArchitecture`'s
  `LaunchUwp => X64` now answers confidently and wrongly for a target it has already activated, and no
  `win-x86` host is built for it to have been right about (#117).
- **An install carries a debug host for every architecture its machine can execute, and that set is
  not symmetric.** ICorDebug has no cross-architecture path, so the host matches the *target* process
  rather than the broker -- but which targets can exist is a fact about the machine. ARM64 Windows
  runs ARM64 and emulates x64 and x86, so all three ship there. An x64 machine runs x64 and, through
  WOW64, x86; it cannot execute an ARM64 binary under any emulation, so an ARM64 host in an x64
  install is weight nothing can load. Publishing both architectures everywhere looked symmetrical and
  was half wrong: it made every x64 deploy build an ARM64 provider it had no cross-toolset for, and
  warn about a capability no target on that machine could have wanted -- a warning that then leaked a
  non-zero `$LASTEXITCODE` into a successful promote, so the deploy reported failure while the install
  was live. `Get-LiveAppRuntimes` is the one place that says which hosts an install needs.
  <br>
  x86 is in both lists because it is not a legacy case: it is the default platform of the modern UWP
  project template, so it is what an ordinary new UWP app is built and registered as. And each host
  gets **both** taps, because which one serves a target is decided by the framework that target runs
  and a provider built for one cannot serve the other. Shipping only the UWP tap left every WinUI 3
  target without XAML inspection, with nothing in the install to say why -- which is the failure
  `Assert-WindowsPackage` exists to make loud, so it checks both providers for every host.
  <br>
  A missing toolset is a warning on a laptop and a failure on CI, and those are the same rule rather
  than two: `build.ps1` exits 3 for "this machine has no toolset for it", which a developer machine
  may legitimately hit, while a build agent is the machine that is supposed to have every toolset. So
  `promote` warns, `package` refuses, and CI fails on any non-zero exit -- because nothing else in CI
  compiles a line of the C++, and the first thing to notice used to be a release failing to package,
  after the tag was already cut. Main builds all six combinations as Release. A pull request builds
  x64 Debug only, and only when a provider's inputs changed: the ARM64 cross-toolset is an installer
  run that costs more than every compile together, and a break that only one architecture or the
  optimiser sees, in headers all of them share, is rare enough to be caught on main, before any tag,
  rather than paid for on every PR.
