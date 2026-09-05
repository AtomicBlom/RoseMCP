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

### D26 — A value's type comes from the property, not from the look of the string (#12)
Found by dogfooding rather than by testing: asked to set a `Border`'s `CornerRadius` to 0 through hot
reload, the apply came back `SetProperty failed 0x80004005`. The diff engine infers a value's type
from the property name and the shape of the string, `"0"` parses as a number, so the edit went out as
a `Double` -- and `CreateInstance` built that quite happily. Only `SetProperty` objected, with a bare
`E_FAIL` naming neither the property nor the type, one layer below where the mistake was made.

Two fixes, because there were two problems. `CornerRadius` joins the name-keyed table, so the common
case takes no round trip. More importantly the provider no longer depends on that table being
complete: `PropertyIndex` also reports the property's own **declared** value type, from the live
property chain, and `ApplySetProperty` tries the host's hint first and falls back to that.

The order is deliberate. The hint carries intent the runtime does not have -- a colour string is meant
as a `SolidColorBrush` even where the live value is some other `Brush`, so preferring the declared
type would break brush edits on gradient-valued properties. The declared type is the safety net
because it is a fact rather than a guess. Failure messages now name the type attempted
(`SetProperty(Windows.Foundation.Double) failed 0x...`), which is the sentence that was missing.

The table was only ever going to be as complete as the properties someone had tried, so the fallback
is the part that matters; the table is now an optimisation.

Tested at both levels, because the two failure modes are different: `XamlDiffTests` asserts the hint
for a one-number `CornerRadius`, plus the two cases either side of it -- a genuine `Double` property
stays a `Double`, and a four-part `CornerRadius` is not mistaken for the `Thickness` it looks exactly
like. The integration test then applies a `Double` edit and a struct edit in one reload against the
live app, which is the guard for the fallback itself.

The struct edit is asserted through its status rather than by reading the value back, and that
asymmetry is not laziness: the framework returns an **empty string** for a `CornerRadius` value while
stringifying `Thickness`, `GridLength`, `Size`, `Point` and `Vector3` perfectly well. The provider
passes `PropertyChainValue.Value` straight through, so this is a gap in what XAML diagnostics chooses
to render, not a formatting bug here -- issue #21, which also notes that the honest first move is to
stop reporting an empty string as though the property were unset.

### D27 — Select what the framework would hit, and prefer the app's own markup (#18)
Two findings from a session driving this against a real app, and the first is the one that mattered.

**The selector asked the framework to ignore its own hit testing.** `FindElementsInHostCoordinates`
was called with `includeAllElements: true`, which returns elements no click would ever reach. On the
app under test that made click-to-select useless: an empty `Grid` with no `Background`, stretched
across the window as a dialog host, sat topmost over everything, so *every* click resolved to it.
Input passes straight through such a panel, which is why the app itself was perfectly usable while
the selector insisted that was the thing being clicked.

The irony is complete. A panel with a null `Background` taking no part in hit testing is the rule
this whole overlay is built on -- it is why the toolbar is click-through, and it is the first thing
D22 says -- and then the selector opted out of it. "Click an element to select it" has to mean the
element the app's own input system would route that click to, or it means nothing. It is `false` now,
with `true` kept as an explicit opt-in, because inspecting an invisible host is occasionally the
goal and never the default.

**One element was never enough anyway.** A click on a button lands on some part of its template; a
click meant for a container lands on the content inside it. The pick now comes back with the whole
ordered stack beneath it, topmost first, capped at sixteen -- the enumeration is already ordered, so
it costs a few rows in a file written once per click, and it saves a round trip every time the
wanted element is one step up or down.

**And "just my XAML" turned out to be exact rather than heuristic**, which was a surprise.
`VisualElement` -- the struct the tree callback hands us per element -- carries a
`SourceInfo { FileName, LineNumber, ColumnNumber, CharPosition, Hash }` that this code had been
reading three fields out of and discarding. It is the discriminator Visual Studio's own Just My XAML
uses, and the values are unambiguous: the app's markup resolves to `ms-appx:///Page.xaml` with a real
line, a control template's parts to `ms-resource:///...themes/generic.xaml`. So the filter is a URI
scheme comparison, not a guess about namespaces or names.

It is on by default, because "the element I clicked" means the button the developer wrote rather than
whichever templated child is on top, and it falls back to the framework's own pick when nothing under
the click came from the app -- so an app without source info degrades to the previous behaviour
instead of selecting nothing. Absent source info is deliberately not read as "framework": treating it
that way would quietly empty the filter on exactly those apps. Both the agent's parameter and the
toolbar's toggle set the same switch.

### D28 — The source-info gap was an unused argument (#10, resolves a D15 limitation)
Every element and every property had been coming back with an empty file and line, and that was
carried from D15 onwards as a limitation of from-birth UWP activation. It was not. It was the third
argument to `IPackageDebugSettings::EnableDebugging(pfn, debuggerCommandLine, environment)` being
passed as `IntPtr.Zero`. Putting `ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO=1` in that environment block
turns the whole thing on, and it was measured before and after on the probe: empty for all thirteen
elements, then `ms-appx:///MainPage.xaml` with correct line numbers.

Three things stop being limitations at once: property provenance can name a file and line, the tree
can say where each element was declared, and "just my XAML" becomes exact (D27). It also settles an
ambiguity a field report raised -- an empty file could not be told from "not set in source", and now
an empty one means genuinely absent rather than never asked for.

Worth stating plainly because it is the second time in this work that a capability was written off as
unavailable when the truth was that nothing had asked for it: the first was the debug host's per-RID
publish, which had never been wired into deploy at all (D9).

### D29 — A capability that cannot be built fails the suite; only a missing toolchain skips it
`BuildXamlProvider` returned false for *any* non-zero exit from `build.ps1`, and the caller skipped
with "no C++ toolset". So a compile error in the provider -- or, as actually happened, two builds
racing over one PDB -- silently skipped every XAML test and left the suite green at 143 passed.

A capability quietly not being tested is worse than a red build, and from the outside it is
indistinguishable from a machine that genuinely cannot build it. `build.ps1` already separates the
two: its own `Fail` exits 3 for a missing MSVC toolset or Windows SDK, and every other non-zero exit
is a real failure. So 3 skips, anything else throws with the build output, and a build that reports
success but produces no DLL throws too.

The race that exposed it is worth remembering on its own: the integration suite builds the native
provider itself, so building it by hand while the suite is running corrupts both. Do one at a time.

### D30 — A hot reload is diffed against what was last sent, not against what is on disk (#12, supersedes half of D16)
D16 shipped `rose_xaml_apply` taking two versions of the markup, and recorded as a limitation that
structural edits and unnamed-element addressing came back `unsupported`. #11 removed that half:
properties, additions, removals, attached properties and keyed resources all apply, on named elements
and unnamed ones alike. This is the other half. Taking both versions reads reasonably and is close to
unusable in the loop the tool exists for, because an agent that has just written a file no longer
holds what was in it -- so the one piece of state the session is in a position to keep was being
asked of the caller.

- **The session keeps the baseline, per file, and the caller passes a path.** It lives in
  `XamlDiagnosticsSession` because that is the only place that can tell whether an apply reached the
  provider, and it dies with the session, which is right: a baseline means nothing without the
  running app it describes. The decision logic is a separate pure type
  (`RoseMcp.XamlDiff.XamlReloadBaseline`) for the same reason the diff and the materialiser are --
  the host cannot be unit tested.
- **A first apply records and applies nothing.** What the running app was built from is not on disk
  once the file has been edited, and this side will not reconstruct it. The alternative was to diff
  the file against itself, which finds nothing and reports success, silently skipping the caller's
  first edit -- the exact shape of failure this milestone is gated on. So it records, says which
  reason it was, and `oldXaml` remains available for that one call.
- **The baseline advances whether or not every edit took.** A structural edit is not idempotent:
  re-sending an `AddChild` because something else in the batch failed puts a second copy of the
  element in on the attempt that works. Failures are reported and belong to the caller. The one case
  that does *not* advance is the provider failing to report at all -- the commands were injected and
  may have run, so the message says that a retry could double what this batch was adding.
- **Markup that does not parse is refused, not recorded.** Otherwise the first apply of a
  half-written file becomes the baseline, and every apply after it reports a parse error about a file
  the caller has since fixed, with nothing it can do to say so. One parser decides, via
  `XamlDiff.Parses`, so the gate and the diff cannot disagree about what is diffable.
- **The file's age is three-valued.** Unchanged since the app started, changed since, or unknown.
  A process that will not give its start time is no evidence either way, and reporting that as
  "changed" would be a claim about the file with nothing behind it.
- **No file watcher, though the card offered one.** Applying on every save would mutate a running app
  from a keystroke, including the saves mid-edit that do not parse, and an MCP tool has nowhere to
  push the result: the agent would have to ask what happened anyway. The agent asks, and is told.

Proven by `Applies_successive_file_edits_to_the_running_app`: two first-apply cases (a file untouched
since launch, and one changed since), then two edits applied in one session with nothing passed but
the path, each read back off the running app, then an apply with nothing to apply. The assertion that
earns it is the count on the second edit -- it touches a different property from the first, so a
baseline still sitting on the original would come back with two edits rather than one.

### D31 — The stamp that proves a marker answers *this* request had to be read before the tree was written (#12, extends #57, closes most of #89)
#57 gave the overlay's markers a generation: the host stamps a number on the request, the provider
echoes it onto what it writes, and a marker carrying a different number is rejected as the previous
request's. #89 recorded that the tree, properties and apply handshakes still answered on existence
alone and called extending it mechanical. It is not, in two ways, and continuous apply is what made
finding out worthwhile -- a stale `apply.tsv` reports the previous apply's per-edit outcomes as this
one's, and in a loop that edits the same property repeatedly the keys line up, so it reads as success.

- **The tree snapshot is written before the request is read.** `ReadRequest` is also what learns the
  generation, so stamping the snapshot's marker without hoisting that read stamps it with the
  *previous* number and the host rejects a perfectly good tree. It presented as a tree read that
  timed out and blamed the app's diagnostics layer, and the continuous-apply test caught it within
  the hour.
- **`selection.ready` deliberately carries no generation.** It records a click, which outlives the
  injection that armed select mode by design, so stamping it would have the read that goes looking
  for it reject its own answer as stale. One file cannot both answer a request and survive one; what
  #89 has left is splitting `selecthandle`'s confirmation onto its own marker.

One `WriteMarker` writes all of them now, so the next marker added cannot forget the stamp -- which
is exactly how this came to be half done.

### D32 — It is a live edit, not a hot reload, and the difference is what it promises (#12, renames the vocabulary of D16, D26, D30 and D31)
Everything above this entry calls `rose_xaml_apply` hot reload, and that is not quite a misuse --
**XAML Hot Reload** is Microsoft's own name for this mechanism, renamed from "XAML Edit and Continue"
in VS 2019, and it runs through the same XAML diagnostics channel this provider attaches to. Nor does
Microsoft's version read from disk: it pushes the editor's buffer into the live tree, so the term
carries no disk-reload meaning in this ecosystem either.

It is still the wrong word here, for a reason worth stating because it is a claim about behaviour
rather than about taste. **"Reload" says the app's markup is now the new markup.** It is not. Every
edit is a `SetProperty` or an `AddChild` against the element objects that exist at that instant. The
app's compiled markup is untouched, so anything that rebuilds that part of the UI -- a relaunch, or
navigating away from an uncached page and back -- produces the original. A reader who took "hot
reload" at face value would expect elements created later to carry the change, and they do not. The
docs said "lost on relaunch", which is true and much weaker than the real limit.

The second reason is local: "reload" was already spoken for. `WorkspaceReload`, `ReloadAsync` and
`mustReload` are the *solution* reload, which genuinely reloads something. One word for both meant
the word carried no information.

So three tiers, because "edit" was already taken for the smallest one:

| Tier | Word | Where |
|---|---|---|
| The capability | **live edit** | prose, docs, tool titles and descriptions |
| The operation | **apply** | `ApplyXamlAsync`, `LiveXamlApplyResult`, `XamlApplyBaseline` |
| The unit | **edit** | `XamlEdit`, `XamlEditKind`, `LiveXamlEditResult` |

No MCP tool name changes -- they were already `rose_xaml_apply` and `rose_live_app_xaml_apply` -- and
a result record's *type* name never appears in the JSON, so nothing client-visible moved. The eight
identifier renames went through `rose_rename_symbol`, which is what it is for.

One deliberate exception: the tool description and the server instructions both say "what Visual
Studio calls XAML Hot Reload". Not as this project's vocabulary -- as the search term a reader
already has. The whole point of the instructions text is that it is read before an approach is
chosen, and an agent that knows the VS feature should recognise this as the same thing.

Earlier entries keep their original wording, as D21 did when D22 superseded half of it. They record
what was decided when, and the term they used is part of that.

### D33 — The suite's wall clock was one incremental C++ build, run seventeen times (extends D24)
The integration suite took **770s** on a machine with the UWP and C++ toolchains -- not the "four to
five minutes" CLAUDE.md claimed, which was measured somewhere the live-app tests skip. Where it went
was not where reasoning put it. The obvious suspect is the Roslyn side: 162 `FixtureSolution.Copy`
calls, each paying a real `dotnet restore` and a real design-time build, none of it shared. That
work is real -- 877s of it -- and it costs **zero wall clock**, because it runs in parallel
underneath something longer.

`LiveAppSessionTests` was 728.9s of the 770s, and its span was also 728.9s: one class is one xUnit
collection, so its 31 tests ran strictly serially and everything else finished inside them. 94.7% of
the suite was one class. Measured, then, per test, with the phases timed on their own:

| repeated every test | cost | calls | total |
|---|---|---|---|
| `build.ps1`, the native provider | **23.0s** | 17 | **391s** |
| `msbuild -t:Build`, the UWP app | 8.1s | 20 | 162s |
| `msbuild -t:Restore`, the UWP app | 1.4s | 20 | 28s |
| `Add-AppxPackage` + `Remove-AppxPackage` | 1.1s | 20 | 22s |
| vswhere, layout stage | 0.2s | 20 | 4s |

`build.ps1` was run three times back to back -- 24.0s, 23.0s, 22.8s. It is 23 seconds *every* time,
fully warm and incremental, because the cost is re-entering the MSVC toolchain rather than compiling
anything. Seventeen of those is over half the suite.

D24 had already decided this for the RID builds: once per run, memoised, never merely "found". It was
never extended to the app build, the provider build, or the registration, and those were the wall
clock. `UwpProbeApp` is an assembly fixture that does all of it once, lazily -- lazily because an
assembly fixture is constructed before any test runs, so eager work would make a filtered run of one
Roslyn test pay for an MSVC build. That alone took the class from 728.9s to **272.7s**.

Three things the numbers corrected, each of which reasoning had wrong.

**`DisableParallelization` on the class is the wrong tool, and it costs 109s.** It reads as "these
tests do not run in parallel with each other". It means "this class does not run in parallel with
*anything*": 1 of 210 non-live tests overlapped a live one, so the two halves added -- 268s + 109s --
rather than overlapping. What actually cannot overlap is two tests driving one app, so that is what
is serialised, by a `SemaphoreSlim(1,1)` lease in the fixture. The eleven tests here that debug an
ordinary .NET child process take no lease and join the pool. 205 of 210 now overlap, and the suite
went to **251s**.

**`MaxThreads` does not bound asynchronous tests.** Dropping it from 12 to 8 made the suite *slower*
(259s), which is the wrong direction for a contention fix and was the clue. Sampling in-flight tests
showed 194 running at t=25s against a nominal 12 -- a test that awaits I/O yields its slot, so
`ParallelMode.All` is effectively unbounded here. The bound that matters is the lease, which is on
the resource rather than on the scheduler.

**A lease makes reported durations meaningless, and the span is the measure.** A test blocked on the
lease has already started as far as xUnit is concerned, so the live tests report 88-108s each and
their sum is nonsense. Run alone, the chain is **186s**, and that is the number to reason about.

What is left is not waste. Twenty app launches at ~7.7s of genuine launch, attach, inject and close
cannot be removed by tidying, and they cannot overlap while the app is single-instance. The
suite is now 251s of which 186s is that chain and 65s is contention against it. Getting under two
minutes needs the per-request re-injection to go, which is #50: every XAML request re-injects the
provider today because the provider works on the app's UI thread at `SetSite`, and a persistent
channel makes a tree read a message instead of an injection.

### D34 — The provider channel is a named pipe, and D14's reason for it not being one was wrong (#50, supersedes half of D14)
D14 chose tab-separated files in an ACL'd folder over a named pipe, because "a named pipe from an
AppContainer needs a capability-aware ACL and is finicky". The first clause is true. The conclusion
does not follow, and this settles it by doing it: the host creates the pipe with the same two SIDs it
already puts on the work folder (`S-1-15-2-1`, `S-1-15-2-2`), passes the name through
`wszInitializationData` -- the slot that already carried the folder path, so no new plumbing -- and
the provider opens it with a plain `CreateFileW`. It connected in 37ms on the first attempt.

Two things that make pipes look harder than they are do not apply. The AppContainer loopback
restriction is about *sockets*; a pipe lives in `\Device\NamedPipe` and is gated by its DACL. And
"UWP cannot do named pipes" is a certification rule about submitted packages -- an injected
diagnostics DLL is in nobody's package. The direction is what makes it easy: creating a pipe from
inside the sandbox is the finicky case, and connecting to one that already grants your SID is not.

**The crux is not the channel, and the card does not mention it.** Every request re-injected because
the provider does its work inside `SetSite`, and `SetSite` runs on the app's UI thread, which is the
only thread XAML can be touched from. A pipe alone would not have changed that. What changes it is
`IXamlDiagnostics::GetDispatcher`: the provider keeps the `CoreDispatcher`, runs a background reader
on the pipe, and marshals each request back onto the UI thread. A read becomes a message.

**The provider is built afresh on every injection, and a resident reader has to know that.** Found by
a failing test rather than by reading: an element removed by an apply was still in the tree a
following read returned. The apply had run in a *new* provider instance and correctly forgotten the
node from its own list, while the reader thread went on answering from the first instance's list. The
same bug is a use-after-free the moment an old instance is released, which is worse and was only
luck. The reader resolves the current instance per request, under a lock, holding a reference.

**No generation number.** Every handshake through the folder was "does this file exist", so the host
had to stamp a number on the request and have the provider echo it back to tell this answer from the
last one (#57, #89, D31). A reply read from the pipe the request went out on is this request's answer
by construction. That whole mechanism, and the class of bug behind it, deletes itself.

**What it is worth, measured, because the estimate was wrong.** A tree read over the pipe is 14-23ms
against about 115ms for a warm read through the files -- five to eight times, per call, which is what
an interactive session feels. It was also expected to take most of the live-app suite's time out, and
it does not: the whole read path on the pipe moved the suite from 186s to 180s. The reason is in the
numbers that were already there. The three UWP tests that make no XAML calls at all take 6.2-7.3s and
the ones making several take 7.6-7.9s, so the channel is about 1.2s of a 7.7s test and the other
6.5s is launching the app, the resume-stub handshake and the ICorDebug attach. "Re-injection makes
each request slow" is true; "re-injection makes each test slow" was an unexamined substitution for
it. Getting the suite under two minutes means launching the app fewer times, not talking to it
faster.

Landed here: the channel, and the read path (`tree`, `properties`) with the files still in place as a
fallback, so nothing depends on the pipe that cannot fall back to what worked. The write path
(`apply`), the selection verbs, injecting once per session and deleting the file channel are the rest
of #50; CLAUDE.md gets its invariant when the migration is finished rather than while two channels
are live.

### D35 — The live-app suite is phased by what each test can share (extends D33)
D33 made the twenty UWP tests stop rebuilding the toolchain, and left the thing underneath it: each
test still launched its own app. A launch costs about 6.5s and the XAML work in a test costs about
1.2, so a new test cost six times what its own work cost, and the initiative is about to grow probes
for WinUI, WPF and modern UWP. The point of phasing is the slope, not the intercept.

One app, shared, with three ways to ask for it. What each phase gives up is different, which is why
"isolation" as a single property was the wrong thing to argue about:

| | asks for | gives up | for |
|---|---|---|---|
| A | `TakeAppAsync` | nothing; launches its own | tests where a fresh process *is* the subject |
| B | `TakeSessionAsync` | the app to itself, serially | state with no owner smaller than the app: the pick, select mode, the resource dictionary |
| C | `TakeSlotAsync` | shares the app, owns one slot | everything else |

**Slots are named, and that is the whole of why they work.** An element the markup never named is
addressed by position under its nearest *named* ancestor, so two tests adding children to one
container renumber each other -- `#Scratch/Border[1]` would mean different elements depending on who
else was mid-test. Anchored on its own slot, `#Slot3/Border[0]` is stable however busy the app is.

**Phase C turned out to be three kinds, not one**, and the second and third were found by tests
failing rather than by design:

- *Build what you test.* The majority.
- *Read what the markup declares.* `Reads_the_properties_of_a_xaml_element` asserts `SourceFile` and
  `SourceLine`. Nothing declared a slot-built element, so it has no source info at all, and that test
  can never own what it reads.
- *Read a dedicated declared element.* The #97 pin asserts what the **first** properties read of an
  element returns. Sharing the app's Caption made it depend on running before every other test that
  reads a TextBlock. Building its own did not fix it either: an element created through
  `CreateInstance` and `AddChild` arrives with `Inlines` already materialised, so it was never
  pristine. Only markup declares an element nothing has touched, so the probe now carries
  `PristineText`, owned by that one test.

**Serialised state has to be handed back, and the test that fails to is the one that fails.** A phase
B turn reads the selection back on release and fails if anything was left selected or armed. It also
clears up, so one offending test does not cascade -- but it still fails, because cleaning up after a
test is not the same as it having been clean. That check found, within a minute of being switched on,
that **select mode could not be disarmed at all**: `EndSelect` existed in the provider but was wired
only to the toolbar's Idle button and the click path, so an agent that armed select mode had put a
pointer-capturing layer over the app with nothing but a human click to lift it.
`rose_xaml_select_mode(arm: false)` is that hole closed.

Three things about the mechanics that cost a run each to learn.

**A fragment is not a document.** The diff parses what it is given as XML, and a fragment lifted out
of MainPage.xaml carries none of its namespace declarations, so `x:Name` is an undeclared prefix and
the apply is refused before it starts. The declarations go on the slot fragment's root, where a
caller cannot forget them.

**Launching a packaged app under a debugger is not reliable on the first attempt** when the previous
instance has only just gone, and it fails as a `Faulted` session saying the app "may not have
activated under the debugger" -- the symptom, not the race. The shared session waits for the process
to actually be gone and retries three times. Killing a process and its package being launchable again
are different moments.

**One gate, or it is not a gate.** During the migration the unconverted tests kept their own
semaphore while the converted ones used the phase gate: two independent locks over one
single-instance app, so an old-style test could launch its own instance while a phase B test was
using the shared one. A run wedged for fifteen minutes with the host alive and the app gone. Both go
through one gate now, which the design needs *during* a migration and not only at the end of one.

Left open, deliberately. `Removes_an_element_from_the_live_tree` passes alone and failed in company,
reporting the removal applied while both elements were still in the tree, so it takes the app
exclusively as well as owning a slot. Owning a slot was not enough for it and the reason is not yet
established -- the index being asked of the live collection rather than taken from the diff is the
obvious suspect. Exclusivity costs the overlap, which was worth almost nothing here because every
XAML request serialises behind one lock and one UI thread anyway, and keeps the shared launch, which
is where the time was. That is a fix pending an explanation, not an explanation.

### D36 - A batch of sibling removals has to go out last-first (#11, and it explains D35's loose end)
D35 shipped a test taking the app exclusively with the note "a fix pending an explanation, not an
explanation". This is the explanation, and the fix turned out to belong in the product rather than in
the test.

An unnamed element is addressed by its position among its siblings, and the apply resolves that
position against the *live* collection at the moment the edit runs. That is deliberate, and the
existing note says why: an add earlier in the same batch has already moved everything after it. What
that reasoning missed is that a **removal** moves things too, and moves them the other way. Emitted
in document order, deleting two adjacent children removed the first and then refused the second:

    RemoveChild #Slot0/Border[0] => applied
    RemoveChild #Slot0/Border[1] => target not found: no Border[1] here, among 1 element(s) of type Border

Correct arithmetic about a tree that had already moved. `XamlDiff` now emits removals last-first, so
nothing a later edit names can shift under it, and a unit test pins the order rather than the
behaviour, because the ordering is the whole of the fix.

**How it hid is the part worth keeping.** The fixture emptied each slot between tests and never
looked at what the apply reported, so a slot that had held two elements was handed on holding one.
Slots come off a stack, so the slot just released is the next one out: the residue was picked up
immediately, by a different test, which counted elements it had not added and failed somewhere else
entirely. Three symptoms that looked unrelated - a removal reported applied that did not happen, an
element count off by one, a test that passed alone and failed in company - were one bug seen from
three places. The fixture now checks that its cleanup worked, off the statuses the apply already
returns rather than a confirming read, because the same rule that governs a phase B turn governs a
slot: a resource handed back wrong is a failing test, not a tidiness problem.

**And a suite green once is not a green suite.** D35 was reported on a single run of each. Run twice
more, main failed 1 and then 2 of its 31 live-app tests - this bug, and two others it had been
masking. One was a test asserting a whole-tree element count against the probe's Transient pair,
which leaves the visual tree for one second in every five *by design*, so that #51 has a removal to
watch; a fixed count over ten reads spanning seconds was a coin flip, and what that test actually
protects - that a concurrent read is not silently truncated - is said just as well by counting the
stable elements. The other was `DebugProbeTarget` self-terminating on a 120-second deadline while the
suite runs longer than that, so the tests asserting their target is still alive were failing on it
having correctly done what it was told. A timer shorter than the run it bounds fails exactly when the
machine is busiest, which is when a suite is least able to explain itself. The deadline has to be
longer than the whole suite rather than longer than one test, and is now ten minutes.

That is the second answer. The first was to delete the deadline and have the target die when its
stdin closes, the way a worker dies with the broker, so the bound would be the parent's lifetime
rather than a number guessed in advance -- which is the right shape, and does not work here. Each
target passes on its own; the nine debugger tests hang as a group, reproducibly, with not one test
completing. The interaction between a redirected stdin, inherited handles across nine concurrently
launched children and an `ICorDebug` attach is not understood, and is recorded unexplained rather
than left out, because the idea is good enough that somebody will have it again. A fixture that can
hang the suite is worse than the deadline it was replacing -- which is a rule this initiative had
already written down, and then had to be taught twice.

**A fourth cause, and the one that took the longest to see, because it was the design's own shape.**
With the other three fixed, a run still failed two slot tests together on "the target's XAML
diagnostics endpoint did not appear within 20s". Phase C tests overlap on purpose, and each asks the
fixture for the shared session. After a phase A test has ended the app, several of them arrive at
that request at once, each correctly observes no app running, and each begins by ending the app
before launching it -- so the second kills what the first has just launched, and whoever loses holds
a session whose process is gone. Twenty seconds later the injection reports a missing diagnostics
endpoint, which describes the corpse and not the killing, and points at the app rather than at the
fixture. Bringing the app up is the one thing readers do that is not read-only, and it now takes a
lock of its own, with the readiness question asked again inside it so that the queue behind the first
launch observes the app rather than tearing it down to build the same one again.

That is the third time in this initiative that one resource has been reached through two paths that
did not know about each other: two locks over a single-instance app (D35), a fixture and a test both
emptying one slot (above), and now overlapping readers each relaunching one app. The rule already
written down -- one gate for everything that touches the app -- is right and keeps being applied one
layer too high. It is not enough for the *tests* to be sequenced correctly if the thing they queue for
can be rebuilt by several of them at once.

The method note: a flake rate is a measurement, and measurements need repeats. Reporting a suite
green off one run is the same error as reasoning about a slowdown instead of profiling it, and it was
made in the same initiative that had just been corrected for the latter. Four causes, and the first
run of the day showed one of them.
