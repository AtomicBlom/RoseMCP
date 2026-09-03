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
The XAML track's foundation is in the repo. `src/RoseMcp.Xaml.Uwp.Tap` is the native diagnostics provider,
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
- **Stage the provider once per session.** The first injection loads the provider DLL into the target,
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

### D17 — Expression evaluation: safe field-access only; func-eval is a deliberate non-goal (#7, resolves D6)
D6 parked full expression evaluation as the plan's open question and the riskiest ICorDebug surface.
The resolution: ship the safe, bounded part and draw the line explicitly. `rose_debug_evaluate`, valid
only at a stop, evaluates a field-access chain -- an argument or local by name, then `.field` into the
object graph -- by reading fields directly from memory (metadata field token + `GetFieldValue`). It
runs **none of the debuggee's own code**, so it cannot hang or corrupt the target the way property
getters, method calls, or `ToString` would; those need `ICorDebugEval` func-eval, which stays a
deliberate non-goal -- for heavy inspection the agent attaches an external debugger. Stack and locals
at a stop (already shipped) plus this cover the common "drill into the stopped object graph" need.

Scope and limits: own (declared) fields, not inherited ones; arguments are always named, locals need a
PDB (indexed `local_N` otherwise); a missing field or a null in the chain is a returned error, not a
throw. Two gotchas: `GetFieldValue` wants the raw `ICorDebugClass`, and casting the ClrDebug
`CorDebugClass` wrapper to that interface throws -- pass `cls.Raw`; and `dotnet format` "helpfully"
auto-inserted that very (wrong) cast to make a CS1503 compile, so a green build is not proof the cast
is right. Proven by `Evaluates_a_field_access_expression_at_a_stop`: `state.Label` -> "beat" and
`state.Inner.Count` -> -1 off a stopped frame, a missing field reported cleanly.

### D18 — Visual-tree query: rooting and paging in the host; filtering and live are follow-ups (#9)
`rose_xaml_tree` gained `rootName`, `offset`, and `limit`, applied host-side over the provider's full
snapshot: root the result at a named element's subtree (walk the flat parent list), and page it, since a
real app's tree runs to thousands of elements -- `Total` reports how many matched so the caller knows
whether more remain. The provider still enumerates the whole tree each call; filtering there would save
nothing at our scale. Two acceptance items are deliberate follow-ups: framework-chrome **filtering**
depends on per-element source info, which is not populated for a from-birth UWP target yet (D15); and a
**resident, live-updating** provider is unnecessary while inject-per-query already returns a fresh
snapshot on every call. **Hit-test** (element at a point) is the selection primitive and lands with #18.
Proven by the rooting/paging assertions in `Reads_the_live_visual_tree_of_the_classic_uwp_probe`.

### D19 — Debug event stream: buffered is the mechanism; push stays deferred (#8, extends D1)
#8's useful half ships and is load-bearing: every debugger capability records into the bounded, sequenced
`DebugEventBuffer`, read via `rose_debug_events` with a cursor. The proactive-push half (MCP
notifications to the agent) stays deliberately deferred, for the reason D1 gave -- a turn-based agent
cannot act on a notification mid-turn, so the buffer, not a push, is what it actually uses. This is the
resolution of #8, not an omission: revisit only if a streaming/interactive (non-turn-based) client
becomes a target, at which point the push rides the same buffer.

### D20 — #15 threat model documented in full against the shipped surface
`docs/debug/security-model.md` was expanded from the minimal gate into a full threat model now that the
XAML injection, UWP from-birth launch, hot reload, and expression evaluation have shipped: assets, each
capability's gate, an explicit list of threats considered with their mitigations (cross-user reach,
leaving a package debuggable, DLL planting in the staging folder, injection as an escalation vector,
executing attacker code during inspection, secret exfiltration, wedging the target), and deployer
guidance. No new gate was needed -- the same-user boundary and OS backstops already held; this writes
down why.

### D21 — Interactive selection: an in-app overlay on the diagnostics UI layer (#18)
*Superseded in part by D22: the overlay is now resident and the person can arm select mode themselves.*
Chosen by the user over a host-side mouse hook, because it is what Visual Studio does and the click
does not leak through to the app. `rose_xaml_select_mode` injects the provider with a `select` request;
it puts a transparent, hit-testable `Grid` -- sized to the window -- on the diagnostics **UI layer**
(`IXamlDiagnostics::GetUiLayer`, the layer meant for adorners, so the app's own tree is never touched)
and stays resident. The next click lands on the overlay, whose handler marks the event handled,
hit-tests the element beneath with `VisualTreeHelper.FindElementsInHostCoordinates`, records it, and
tears the overlay down. `rose_xaml_selection` reads it and deliberately does **not** re-inject, since
that would tear down the very overlay it is waiting on.

This is the one place the provider needs C++/WinRT rather than raw `IVisualTreeService` COM (creating a
UIElement and wiring a pointer event), which set the build up: cppwinrt include path, `WindowsApp.lib`,
`/bigobj`, and `/std:c++20` (C++17 drags in the deprecated `experimental/coroutine`). Note that
`GetUiLayer`, `GetHandleFromIInspectable` and `HitTest` are on **`IXamlDiagnostics`**, not
`IVisualTreeService`.

The selection carries the element's type, `x:Name`, and the same stable handle the tree reports, so it
feeds `rose_xaml_properties` and `rose_xaml_apply` directly -- which is the point: a user clicks a
thing on screen and the agent can then read and change it. Hover-highlight remains the fast-follow.
Testing is split deliberately: the integration test drives it to the **armed** state and asserts the
selection is empty until someone clicks (auto-clicking a live desktop from a test suite is not
acceptable), and the click itself was verified once by hand -- clicking the probe's centre selected
`TextBlock` `Caption`, exactly what is there.

### D22 — The overlay is a resident, click-through toolbar the person drives (#18, supersedes half of D21)
D21 shipped select mode as something only the agent could arm: the agent called `rose_xaml_select_mode`,
the person clicked, the overlay came down. The user's own expectation was the other way round -- an
adorner they enter select mode from, mark an element with, and *then* talk to the agent about -- and
they were right that the agent-first ordering is the wrong default. Both are now the same act.

Two constraints shaped the mechanism. A modifier chord (Ctrl+Shift+Click) was rejected outright: apps
implement their own, and RoseMCP silently stealing one would be a collision nobody could diagnose. A
perpetual overlay was accepted **provided nothing has to be hacked to make it work** -- and it does not,
because XAML's hit testing already draws the line in the right place:

- A panel whose `Background` is **null** takes no part in hit testing. The root `Grid` and the `Canvas`
  inside it carry none, so they are invisible to input and every click reaches the app underneath.
- The toolbar itself does have a `Background`, so it takes input. That is the whole of the always-on
  case: a toolbar that works, over an app that still works.
- A `Background` that is merely **transparent** *does* hit-test. Select mode inserts a full-bleed,
  faintly tinted capture `Grid` at index 0 -- beneath the `Canvas`, so the toolbar's own buttons stay
  live while the rest of the window collects the pick -- and removing it restores click-through. The
  tint is deliberate: a layer that swallows every click while looking like nothing at all reads as the
  app having hung.

No input hooks, no window subclassing, nothing to collide with.

The toolbar is installed by the first injection of *any* XAML tool and then left alone, which makes it
outlive the `RoseTap` instance that built it -- so it is a leaked singleton holding its own
`IXamlDiagnostics` reference, not a member. Its handlers capture `this`, and a `this` that died at the
end of an injection would leave the app calling into freed memory; the kept reference is also what lets
a click resolve to a handle long after that injection is done.

It has the two views the user asked for: a full view (drag grip, name, Hide, `Idle` / `Select Element`,
and a status line naming the last pick) and a collapsed grip that drags to move and taps to expand.
Position is clamped to the window, so a toolbar dragged at the edge cannot be lost off-screen.

Two consequences follow from the person being in control:

- **The mode is read, not remembered.** It lives in `overlay.state` beside the other channel files,
  because someone can arm or cancel from the toolbar with the host not in the conversation at all;
  what this side last asked for proves nothing. `LiveXamlSelection.Armed` carries it back.
- **A selection outlives an injection.** Only arming a fresh pick clears the selection files; reading
  the tree must not throw away an element picked minutes ago.

The tree snapshot filters our own subtree out by the root's name (`__RoseMcpOverlay`), so the tool keeps
answering about the app's UI rather than RoseMCP's. On the versions tested the diagnostics UI layer is
not enumerated by `AdviseVisualTreeChange` at all -- the count is identical before and after the
toolbar goes up -- so that filter is a guard against a framework that does enumerate it, not a fix for
one that does. The test asserts it either way.

Rulers (pick one element, hover another, read the gap in pixels) and a nearest-neighbour zoom rendered
into the overlay are the user's next two asks; both are cards, not code.

### D23 — The provider is named for the XAML framework it binds to
`src/RoseXamlTap` is now `src/RoseMcp.Xaml.Uwp.Tap`, matching how everything else here is named. The
discriminator is the framework, not the app model: every line of it is `Windows.UI.Xaml`, which classic
and modern UWP both use, so `UwpClassic` would have been too narrow. WinUI 3 is `Microsoft.UI.Xaml` --
a different dll to initialise and a different set of projections -- so it earns a sibling,
`RoseMcp.Xaml.WinUI.Tap`, rather than a flag on this one. The `.def` drops its `LIBRARY` statement
along the way; the output name comes from `/Fe`, and a dotted name is not worth the quoting rules.

### D24 — RID-specific test builds are rebuilt once per run, never merely "found"
`EnsureX64Build` returned early when the exe already existed. That is the obvious optimisation and it
is wrong: `win-x64` is a separate RID build that a normal `dotnet build` of the solution never touches,
so an existing exe is routinely one source change out of date. It cost real time here -- a host change
was made, the test ran yesterday's host, and the failure it reported ("the provider was not found for
this host's architecture") described a rename that had already been done. It now builds once per test
run, memoised by output path. MSBuild is incremental, so the check is nearly free, and the failure mode
it removes is one that reads as a bug in the code under test.

### D25 — The toolbar, made by looking at it (#18, refines D22)
D22 built the toolbar; this is what a person using it changed. Worth writing down because almost none
of it was reachable by reasoning, and two of the bugs were invisible in exactly the way that wastes
an afternoon.

**Layout that does not stretch.** The capture layer was sized by `HorizontalAlignment::Stretch` and
came out **0x0**. That failed in the worst possible shape: the toolbar still drew, because a `Canvas`
does not clip children that hang outside it, and it still took input, because it has a size of its
own -- so buttons, dragging and collapsing all worked, `select.ready` was written, and
`LiveXamlSelection.Armed` came back true. Only the full-bleed layer collapsed, so select mode was on,
tintless, and never received a single pointer event. Nothing was in the log, because nothing had
failed. The original one-shot overlay had set `Width`/`Height` from `Window::Current().Bounds()`
explicitly and D22 dropped that when it moved to alignment. **Everything on the diagnostics UI layer
is now sized explicitly**, from the window bounds, with a `Window::SizeChanged` handler so it tracks
a resize rather than being right only at startup -- which also keeps the drag clamp honest.

The install log now records the layer's runtime type and its arranged size, because the first
explanation offered for the collapse -- that the layer measures children at their desired size -- was
wrong: the layer is a `Grid`, which does stretch its children. The real cause is upstream of that, and
the layer's own arranged extent is the fact that distinguishes them. It costs one line.

**Arming now reports an extent, and the host checks it.** `select.ready` carries
`armed <width>x<height>`, taken from the capture layer's size after arrange, and `EnterSelectMode`
fails when either is zero. The old check -- does the marker file exist -- passed happily throughout
the bug above. A capability that reports success while being unusable is worse than one that reports
failure, and this is the one place where the difference was demonstrated rather than imagined.

**Glyph coverage is checked, not assumed.** The grip was to be braille U+283F (six dots) and the mark
had a florette U+273F fallback. Neither is in Segoe UI. A missing glyph does not fail -- it renders as
a hollow box, or whatever font fallback happens to find -- so reading the font's own character map
(`GlyphTypeface.CharacterToGlyphMap`) before committing to a codepoint is the only way to know. Both
were caught before they were seen. The six dots are **drawn** now, as 2x3 ellipses: shapes cannot
miss, and the dot count is then exactly the one asked for rather than whatever a glyph contains.

**The mark is geometry, from the same curve as the app icon.** It was briefly an embedded RCDATA PNG,
which was a real improvement on staging a file into the sandbox, and then a worse idea than just
drawing it: `r = cos(3*theta/2)`, even-odd filled, rotated 90 degrees -- `tools/Rose.ps1`'s own curve,
so the toolbar cannot drift from the brand. That deleted the `.rc`, the asset, a generator script, the
`rc.exe` build step, `BitmapImage`, `InMemoryRandomAccessStream`, a coroutine and three includes, and
it is exact at every DPI. It is the small mark deliberately: above 32px the icon adds the stem and leg
that make the R, and at 16px those are sub-pixel. No tile behind it -- the toolbar is already a dark
panel, and a second rounded square inside one reads as a sticker.

**Everything else came from use, not from thinking about it.** The collapsed thumb could only be
grabbed by hitting one of its 2px dots, because its `Border` had padding but no `Background`, and a
null background does not hit-test -- the same rule that makes the overlay click-through, this time
working against us. The buttons were a few pixels wider than tall, sized by their padding, and are
now explicitly square. The active mode wears RoseMCP's accent, read from `Rose.ps1` rather than
matched by hand. The icons follow Visual Studio's toolbar -- a plain pointer for the neutral mode
(`E8B0`), a pointer inside a marquee for picking, a chevron to fold away (`E76B`) -- and the marquee
is composed from a dashed `Rectangle` plus that same pointer, because MDL2's nearest single glyph
(`E8B3`, SelectAll) is a dense grid that turns to mush at button size. Candidate glyphs were rendered
and looked at, at true size, before being chosen.

**The provider's log is UTF-8.** It was a `wofstream`, which narrows to the ANSI code page, so the
first badge caption with a `·` in it reached the log as a question mark. The snapshot writers already
knew this; the log had been left behind.

**How the invisible bug was actually found.** Three plausible causes, no way to choose between them,
so the provider was made to say what it saw: `Beneath` traces the first few pointer moves with the
element it picked, the rect it computed, and how many candidates it passed over, and `ShowBox` returns
whether it drew. One run answered it -- the trace was *empty*, which ruled out every hit-testing
theory at once and pointed straight at input never arriving. The tracing stayed in, bounded to six
lines, because the next question about this layer will be the same shape as the last one.
