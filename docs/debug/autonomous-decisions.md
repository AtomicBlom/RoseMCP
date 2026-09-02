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

### D12 — Classic UWP launch/attach is built; the app's own startup crash is parked
The full UWP debugger path is implemented: host `EstablishUwp` (IPackageDebugSettings.EnableDebugging
+ IApplicationActivationManager.ActivateApplication + attach, with debug-mode teardown on detach),
`Uwp.cs` COM interop, per-target x64 host selection, and the `rose_debug_launch_uwp` tool. The
cross-architecture attach it relies on (ARM64 broker -> x64 host -> x64 target) is proven by
`Attaches_to_a_target_of_a_different_architecture`.

What is parked: the classic UWP probe app, though it builds, registers, and resolves to a real AUMID,
**crashes at CoreCLR host init when activated** (WER MoAppCrash, managed exception 0xe0434352),
before any of its own code runs. Diagnosis so far: it is not the debugger path (activation itself
fails); the missing `Microsoft.NET.CoreFramework.Debug.2.2` x64 framework was installed (no change);
`EnableTypeInfoReflection=false` matched to the known-good gallery (no change); a custom `Main`
wrapping `Application.Start` plus `LocalFolder` step-logging produced no log at all, so the throw is at
or before framework startup. Getting the managed exception needs one of: admin (WER LocalDumps are
denied here), a from-birth UWP debugger (issue #5, the resume-stub / debugger-command-line path), or
knowledge of this machine's x64-emulated UWP CoreCLR setup. The integration test
`Launches_and_debugs_the_classic_uwp_probe_app` is wired and **skips** with this reason; remove the
skip once the app starts. This likely wants the user's input, since they build classic UWP here in VS.
