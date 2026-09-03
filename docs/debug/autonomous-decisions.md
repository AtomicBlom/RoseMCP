# Live-app initiative: autonomous decisions log

This file records decisions taken while completing the live-app debugging & XAML epic
(`AtomicBlom/RoseMCP` #16) without stopping for input, per the standing instruction to
"complete as much of the epic as you can that doesn't need input, make sane decisions, and
log them." Each entry says what was decided, why, and what a human might want to revisit.

The repository is public, so nothing here names internal targets, paths, or package identifiers.

## Conventions

- **Tools ship per feature, surface is unified later.** New capabilities land with their own
  `rose_*` tools rather than waiting for a batch; issue #13 is the cohesion pass. This is what
  the productionization plan's sequencing already calls for.
- **Every slice is tested and pushed on its own.** Build clean (0 warnings, `TreatWarningsAsErrors`),
  `dotnet format --verify-no-changes` clean, unit + integration green, a public-content leakage
  scan, then commit and push to `feat/live-app-session`.

## Decisions

### D1 — Turn-based agent reads a buffer; push (#8) is deferred, not dropped
Debug events go into a bounded, sequenced ring the agent reads between turns (`rose_debug_events`).
Proactively pushing events to the agent (the notification half of #8) is deferred: a turn-based
agent cannot act on a notification mid-turn, so the buffer is the load-bearing mechanism and push
is a later enhancement. Revisit if a streaming/interactive client (not a turn-based agent) becomes
a target.

### D2 — Detach is an explicit handshake before the host is closed
An ICorDebug debuggee whose debugger process simply dies is taken down with it, so the broker asks
the host to detach while it is still alive, before closing its stdin. This is what lets a session
end with the target still running (e.g. attaching to a warm worker and detaching must not kill it).

### D3 — Stopping breakpoints carry an auto-continue safety timeout (default 30s)
A stopping breakpoint freezes the target. For an unattended agent that could wedge the app, so a
hit is held only until continue or a safety timeout, whichever comes first. 30s is a sane default;
it is per-breakpoint configurable. Revisit the default once there is real usage.

### D4 — Locals are captured eagerly at the stop, not via a later tool call
Reading the top frame's variables happens in the stop callback (target definitely frozen) and rides
the stop event. This avoids a race against the auto-continue window and suits a turn-based reader.
An on-demand "inspect an arbitrary frame while stopped" tool can be added later if wanted.

### D5 — Conditional breakpoints use a cheap value-compare, not expression eval
A condition is `name OP literal` (OP one of == != < <= > >=), evaluated on each hit against the top
frame's already-readable arguments and locals: numeric when both sides parse as numbers, boolean for
true/false, else string equality. This is the plan's "cheap read-and-compare" for simple cases and
needs no func-eval. Full expression conditions (method calls, property chains, `this.X`) wait for
eval (D6). A condition whose variable is not in the top frame simply does not fire.

### D6 — Full expression evaluation (ICorDebugEval) is PARKED, needs a decision
`ICorDebugEval` (arbitrary expressions, calling an object's ToString, property chains) is the plan's
own open question — "how much of the debugger to build before it earns its place vs. leaning on an
external debugger for heavy inspection." It is also the riskiest ICorDebug surface (must run on the
stopped thread, re-enters the callback via EvalComplete, can corrupt debuggee state). It is not
started, so there is nothing to stash; it is left for an explicit decision. Everything that would
build on it (interpolated tracepoint messages, expression conditions, object value rendering) is
scoped around its absence for now.

### D7 — Tool surface kept coherent, not renamed; #13 done as instructions + snippet
The `rose_debug_*` surface reads coherently as shipped (attach / launch / events / detach / list, plus
tracepoint and breakpoint verbs), so #13's cohesion pass did not rename anything. The high-leverage
part of #13 was delivered: a debugging section in the server instructions (read before the model picks
an approach) and a consuming-repo CLAUDE.md snippet in docs/debug/using-the-debug-tools.md. The one
cosmetic wrinkle -- tracepoints use "add_" while breakpoints use "set_" -- is left as is; not worth a
rename. RoseMCP's own CLAUDE.md registration section was left untouched to avoid conflicting with the
main branch, which another session owns.

### D8 — Process architecture is read from the image, not IsWow64Process2
Validating the compatibility shim (attach a matching-arch host) with a plain x64 target on this ARM64
box exposed that `IsWow64Process2` reports a genuinely-x64 process as native-ARM64 (processMachine=
UNKNOWN) for x64-on-ARM64 emulation -- so the broker picked an ARM64 host and it BadImageFormat'd
loading x64 mscordbi. Fixed: `TargetArchitectureProbe.ForProcess` now reads the target's main-module
PE machine first (the reliable signal), falling back to IsWow64Process2. With that, an ARM64 broker
launches an x64 host that attaches to an x64 target end to end -- the exact classic-UWP case -- proven
by an integration test (skipped where the x64 .NET runtime is absent).

### D9 — #1 cross-arch is proven; the deploy-layout wiring is documented, not scripted
The cross-arch host resolution works from the dev build (the launcher prefers a RID-matching build
output) and is validated by the test above. The production layout the launcher also supports is a
per-RID publish under `live-app/<rid>` beside the broker. Wiring that into tools/deploy.ps1 (a publish
of RoseMcp.LiveApp for win-x64 and win-arm64 into that layout) is left to the user: the deploy script
has machine side effects that cannot be verified here, and it may be owned by the main branch. This is
the one remaining step for #1 in a deployed install.

### D10 — Repo-owned test apps under tests/apps, isolated from the repo build
The XAML/UWP track needs a live target to test against; depending on a machine-specific external app
is fragile, so the repo now carries its own. First is a minimal classic UWP app (`tests/apps/
uwp-classic`, `Rose.ProbeApp.UwpClassic`) with named inspectable elements and a `Tick` method that
throws a marker exception, mirroring the console probe. Structure anticipates siblings (WinUI, WPF,
modern UWP) per stack, since each exposes its tree/diagnostics differently.

These are foreign project types: `tests/apps/Directory.Build.props` and `Directory.Packages.props`
shadow the repo-root ones so the apps are not forced into net10.0 / warnings-as-errors / central
package management, and the classic UWP project is kept out of RoseMcp.slnx because it is an old-style
MSBuild project that `dotnet build` cannot build. The tests build it on demand with full MSBuild and
skip where that toolchain is absent, so the suite stays green without it. Verified end to end: it
restores, builds Debug|x64 (CoreCLR, debuggable) with 0 warnings, registers as a loose package, and
resolves to a real AUMID.

### D11 — The live-app host is Windows-only; the rest stays cross-platform
The live-app host (`RoseMcp.LiveApp`) targets `net10.0-windows` because it is Windows-only by nature:
it loads the target's mscordbi (ICorDebug is a Windows debugging API) and uses the UWP shell COM
interfaces. That satisfies the platform-compatibility analyzer with no extra runtime dependency (no
WinForms/WPF). Deliberately, nothing else moved: RoseMcp.Contracts, .Broker, .Worker, .Solutions and
.XamlDiff stay `net10.0` and cross-platform, so a Linux/Mac user keeps the full Roslyn surface; the
broker launches the host as a separate process and never references it. A future Linux/Mac debugging
backend would be a different host behind the same broker tools; for now the debug/UWP tools are
Windows-gated by the host's availability, and the tray is already Windows-only. The integration test's
reference to the host is build-only (ReferenceOutputAssembly=false, SkipGetTargetFrameworkProperties)
so a net10.0 test project can still trigger its build.

### D12 — Classic UWP launch/attach is built and the app now debugs end to end (RESOLVED)
The full UWP debugger path is implemented: host `EstablishUwp` (IPackageDebugSettings.EnableDebugging
+ IApplicationActivationManager.ActivateApplication + attach, with debug-mode teardown on detach),
`Uwp.cs` COM interop, per-target x64 host selection, and the `rose_debug_launch_uwp` tool. The
cross-architecture attach it relies on (ARM64 broker -> x64 host -> x64 target) is proven by
`Attaches_to_a_target_of_a_different_architecture`, and the whole path end to end by
`Launches_and_debugs_the_classic_uwp_probe_app` (no longer skipped).

What was parked, and the root cause: the probe app **crashed at CoreCLR host init when activated**
(WER MoAppCrash, managed exception 0xe0434352), before any of its own code ran. It was never the app,
the frameworks, `EnableTypeInfoReflection`, or the debugger. A WER crash dump (obtained via
`C:\UserModeDumps`, read with an **x64** `dotnet-dump` running under emulation — ARM64 SOS cannot read
an x64 dump) showed a `System.BadImageFormatException` on `System.Private.CoreLib` with the
.NET Framework desktop GAC (`mscorlib.dll`, `System.Runtime.WindowsRuntime.dll`) loaded. The process
was being hosted by the **desktop .NET Framework CLR**, which cannot load CoreCLR's CoreLib.

Why: a classic-UWP CoreCLR *Debug|x64* build emits two executables -- the **managed** app assembly in
`bin\x64\Debug\`, and a **native CoreCLR apphost** in `bin\x64\Debug\Core\`. Visual Studio's deploy
does not register either build folder directly; it stages the layout its `*.build.appxrecipe`
describes (into `bin\x64\Debug\AppX`), where the native apphost becomes the package executable, the
managed assembly moves under `entrypoint\`, and the CoreCLR `System.Runtime.dll` (from the UWP
CoreCLR runtime NuGet package, not the desktop-framework `System.Runtime.dll` also present in the
build folder) sits beside them, plus `WinMetadata\Windows.winmd`, the `.xbf`, and `resources.pri`.
The test had been registering the **root** `AppxManifest.xml`, whose `Executable` is the managed exe,
so Windows hosted it under the desktop CLR and it died at host init.

The fix (all in the test harness -- `StageUwpProbeLayout`): parse the `*.build.appxrecipe`, copy each
`AppXManifest`/`AppxPackagedFile` from its MSBuild-escaped `Include` to its `PackagePath` under the
recipe's `LayoutDir`, and register that staged `AppX` layout. No change to the app, the host, or the
debugger. MSBuild's `Build` target emits the recipe but never stages the layout (these old-style
projects have no `Deploy` target), which is why the staging is done in the test.

### D13 — UWP startup is captured from birth via a resume stub (#5)
Attaching a beat after `ActivateApplication` misses the earliest window -- the first `OnLaunched`, the
startup module loads, any exception thrown before the attach lands. The from-birth path (issue #5)
closes it, and is now the default for UWP; the post-startup attach remains only as a fallback.

The mechanism, verified empirically on this machine before it was built:
`IPackageDebugSettings::EnableDebugging(pfn, debuggerCommandLine, env)` with a non-null command line
makes the system, on the next activation, create the app **suspended** and launch that command line as
the app's debugger with `-p <pid> -tid <tid>` appended. The command line relaunches this same host in
resume-stub mode (`--uwp-resume-stub --pipe <name>`); host and stub meet on a named pipe. Three facts
decided the design:
- `ActivateApplication` does not return until the app is resumed, so it must run on a background thread
  while the main flow arms its runtime-startup notification. The stub reporting the pid over the pipe is
  what breaks the chicken-and-egg (the notification needs the pid, which activation only yields once the
  app has been resumed).
- The app is created `CREATE_SUSPENDED` (one thread, suspend count 1), not as a native debuggee: the
  stub exiting does not kill it, and `ResumeThread(tid)` releases it. So the stub is a courier and a
  synchronisation point, not a debugger.
- The debugger command line has a length limit around 256 characters; over it, `EnableDebugging`
  returns `E_INVALIDARG`. So the stub command line is kept short (no long log paths), and the host
  falls back to the post-startup attach when even the minimal command line would not fit.

Once the stub reports the ids, the session arms dbgshim's `GetStartupNotificationEvent(pid)`, tells the
stub to resume, waits for the runtime to signal, and attaches -- the same startup dance
`CorDebugSession.Launch` already used for a plain executable, now shared as `AttachAtSuspendedStartup`.
The stub resumes the app even if the host never answers, so a missing or crashed host degrades to a
normal run rather than a wedged, suspended process. Proven by
`Captures_the_classic_uwp_probe_apps_startup_from_birth`, which catches a one-time exception the probe
throws inside `OnLaunched` -- unreachable by a post-startup attach. The broker and the tool surface are
unchanged; from-birth is internal to the host.

### D14 — The XAML provider is injected by the host, over an ACL'd folder channel (#2/#3/#9)
The XAML track's foundation is in the repo. `src/RoseXamlTap` is the native diagnostics provider,
ported from the hot-reload spike and extended to emit a full tree snapshot; the live-app **host**
injects it (there is no separate injector process), since the host already runs in the target's
architecture and holds its pid. Injection is `InitializeXamlDiagnosticsEx` from Windows.UI.Xaml.dll to
the well-known endpoint `VisualDiagConnection1`; the provider loads into the app's AppContainer, so
the two ends exchange tab-separated files through a working folder the host stages and grants
`ALL APPLICATION PACKAGES` (S-1-15-2-1) and `ALL RESTRICTED APPLICATION PACKAGES` (S-1-15-2-2) rights
to. That folder is the seed of the session channel (#2): a snapshot request/response today.

Decisions taken:
- **File-over-ACL'd-folder, not a named pipe, for the channel.** The spike proved a folder the
  AppContainer can read and write works across the sandbox boundary; a named pipe from an AppContainer
  needs a capability-aware ACL and is finicky. A snapshot is one file today; a longer-lived
  request/response protocol (for live updates and selection, #18) can layer on the same folder.
- **The snapshot is UTF-8, and the host reads UTF-8.** `std::wofstream` narrows wide text to the ANSI
  code page, which the host was reading as UTF-16 -- so the tree parsed as zero elements even though
  the provider had enumerated it. The provider now encodes each row with `WideCharToMultiByte(CP_UTF8)`
  and writes bytes; one fixed encoding regardless of the app's locale, and non-ASCII names survive.
- **The provider is resolved by the host's architecture** (x64 provider for a classic UWP app emulated
  on ARM64), from an override, a published `xaml-provider/<rid>` layout, or the repo build output --
  the same shape as the dbgshim and host resolvers.
- **A target with no XAML UI is a detail, not a fault.** `rose_xaml_tree` returns an empty tree with a
  reason when the target is not a XAML app or the provider is not built, rather than throwing.

`rose_xaml_tree` is proven end to end by `Reads_the_live_visual_tree_of_the_classic_uwp_probe`: launch
the probe from birth, inject, and read back its five named elements (RootGrid, Panel, Pane, Counter,
Caption) through the host to the broker. The provider's SetProperty/ClearProperty apply path is kept
for the hot-reload loop (#12); properties (#10) and interactive selection (#18) build on this snapshot.

### D15 — XAML properties: provenance and source mapping over an inject-per-query request (#10)
`rose_xaml_properties` reads one element's property chain by the handle a tree snapshot reported. Each
effective (non-overridden) value comes back with its type and **provenance** -- `Local`, `Style`,
`Inherited`, `Animation`, `Default`, ... from `BaseValueSource` -- and, when the app carries XAML
source info, the file/line/column that set it. Decisions:

- **Inject per query, with a request file.** The provider does all its work on the app's UI thread at
  `SetSite`, so rather than keep it resident with a UI-thread marshal, each call re-injects. The host
  leaves a `request.txt` (`tree`, or `properties <handle>`); the provider serves it and writes the
  matching output. This was validated first: re-injection succeeds repeatedly, and -- the load-bearing
  fact -- **an InstanceHandle is stable across injections**, so a handle from a tree call is valid in a
  later properties call.
- **Stage the provider once per session.** The first injection loads `RoseXamlTap.dll` into the target,
  which holds the file open; a later injection cannot overwrite it and need not, since it is the same
  provider. The host copies it once into the per-host work folder and reuses it.
- **Default values are filtered out unless asked for.** An element has hundreds of properties, almost
  all framework defaults; the provider drops `Default`-provenance values (keeping the set from being
  pushed past the row cap) unless the request ends in ` all`, surfaced as `includeDefaults`.
- **Source info degrades gracefully.** It needs the app built with XBF line info (UWP:
  `DisableXbfLineInfo=false`, the classic-UWP default) and launched with
  `ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO=1`. When absent, the file/line fields are simply null and the
  agent still gets values and provenance. Populating source info for a from-birth UWP target means
  getting that variable into the app's own environment (its activation env, not the host's) -- a
  documented follow-up; the plumbing carries it through the moment it is present.

Proven by `Reads_the_properties_of_a_xaml_element`: RootGrid's `Background` reads back `Local`,
framework defaults are excluded until requested, and the caption's `Text` comes through as the exact
string the XAML sets. The provenance is the bridge #18 (selection) and the Roslyn side build on.

### D16 — XAML hot reload: diff in the host, apply named-element property edits live (#12)
`rose_xaml_apply` takes two XAML versions, diffs them with `RoseMcp.XamlDiff` (#11), and applies the
edits to the live tree, reporting each edit's outcome. Decisions:

- **The host diffs and applies.** `RoseMcp.XamlDiff` is a pure, cross-platform, unit-tested library the
  host now references; keeping the diff and apply in one place is one broker round trip and lets the
  host translate an edit's addressing to what the provider understands.
- **Property edits on named elements apply; the rest are reported, not dropped.** The provider
  addresses elements by `x:Name`, so a diff target of `#name` maps straight to a `SetProperty` /
  `ClearProperty` command. Structural edits (`AddChild`/`RemoveChild`) and unnamed-element path targets
  come back with an `unsupported: ...` status rather than silently vanishing -- honest about the live
  applier's current reach while the diff engine already detects them.
- **Per-command results flow back.** The provider writes an `apply.tsv` (op, target, property, outcome)
  beside its other files; the host joins it to the computed edits so the agent sees exactly what took
  and what did not (`applied`, `target not found`, `property not found`, a failure code).
- **The command file is UTF-8 without a BOM.** The provider reads `commands.tsv` with a narrow stream;
  `File.WriteAllLines` with the default encoding is BOM-less UTF-8, where `Encoding.UTF8` would prepend
  a BOM and corrupt the first op.

Value types go through the provider's `CreateInstance`: brushes (from a hex colour), doubles, thickness
and boolean are the reliable cases; a value the diff cannot type (a bare string) comes back as a
`CreateInstance` failure rather than applying, which is the honest current limit. Proven by
`Hot_reloads_a_property_on_the_live_uwp_probe`: it changes the caption's font size, applies it, and
reads the live element's font size back as the new value -- the edit-to-live loop, end to end.
