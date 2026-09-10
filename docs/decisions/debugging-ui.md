# The debugging and XAML inspection UI is a separate process that reads the broker over http

**Decision.** A person inspecting a debug session gets `RoseMcp.Inspector`, a WinUI 3 app of its own,
launched from the tray and talking to the tray's in-process broker over a loopback http surface
mounted at `/operator`. The tray grows a debug-sessions section beside its workspace cards; the
inspector holds everything else -- the event tail, breakpoints, the call stack, locals, threads, and
the live visual tree.

**Why a second process rather than another tray window.** The tray *is* the broker. Restarting it
drops every loaded solution and detaches every debug session, so iterating on a tree viewer inside it
costs a full reload each time. A tree over several thousand nodes with a property grid is also a much
larger crash surface than a status panel, and an unhandled binding exception there would take the
workers with it. What the inspector may never be is a debugger of its own: a process has one
debugger, so the inspector is always a client of a session some agent started.

**Why it requires the tray.** A stdio broker has no listener to connect to, and its session lives and
dies with one client. The inspector says so plainly when no tray answers rather than starting
anything itself.

## The operator surface is owner-agnostic and holds a token

**Decision.** `/operator/*` resolves sessions through a new `LiveAppSessionManager.ForOperator`, which
skips the ownership check `Find` applies, and every request must carry a bearer token the tray mints
per run. The tray passes it to the inspector it launches and offers it as a copyable command line.

**Why not reuse `Find`.** `CallSession.Id` comes from a call-tool filter reading the MCP transport's
session id, so inside an ASP.NET endpoint it is null. `Find` compares that null against the owner
recorded when the session started, so an operator endpoint would be refused for every session an
agent started -- which is all of them.

**Why a token rather than the loopback and Origin checks alone.** A session id is eight hex
characters and the ownership check exists precisely because guessing one reaches somebody else's
debugger. An endpoint that bypasses that check reintroduces the hole for any process running as this
user, so it gets a secret that agents never see. Minted per run rather than persisted: an inspector
holding yesterday's token should be told to relaunch from the tray, not silently authorised.

## Execution state is orthogonal to session lifecycle

**Decision.** `LiveAppSessionState` keeps meaning lifecycle -- `Starting`, `Ready`, `Faulted`,
`Ended`. A new `LiveExecutionState` (`Running`, `StoppedAtBreakpoint`, `StoppedAtStep`) sits beside
it, with a `LiveStop` carrying the thread, the breakpoint, the stop's own event sequence, and who
will let the target go (`LiveStopResume.AutoContinue` or `HeldByOperator`) with a deadline.

**Why the stop's event sequence is on it.** It is the stop's identity. A UI that re-reads frames
needs to know it is looking at a *new* stop rather than the same one polled again, and the sequence
of the `BreakpointHit` or `StepComplete` event that announced it is the only number both ends already
agree on.

**Why the hold is a resume mode rather than a fourth execution state.** A held target is still
stopped at a breakpoint or a step, and the pill a reader wants says which. Folding the hold into the
state loses the cause; putting it on `Resume` keeps both and makes the deadline mean one thing.

## What the inspector reports about XAML is the provider's residency, not a channel

**Decision.** The session reports `LiveXamlProvider` -- `None`, `Resident` or `Lost`.

**Why not a channel.** A channel is what served one read, and there is only one channel now: the
provider connects back on a named pipe and every request is a message on it. `LiveXamlTree.Channel`
still answers "which channel served this read" for a caller who wants to see the fast path is there.
What a status view needs is different and cheaper to act on: whether a provider is resident, so a
read is a message, or absent, so the next read pays an injection. `Lost` is a real third state rather
than a tidy-up -- a pipe that drops sends the next call back through injection, which the host already
warns about, and a session doing it repeatedly is a channel failing quietly.

## The shared UI is two projects, split by what a test can reach

**Decision.** `RoseMcp.Ui.Core` is plain `net10.0` and holds everything testable: the rows a window
binds to, the formatting they carry, the merge that keeps them alive across a refresh, and the poll
loop. `RoseMcp.Ui` is `net10.0-windows` with `UseWinUI` and holds the resource dictionaries, the
window chrome and the crash handler. Both apps reference both.

**Why not one project.** A `net10.0` test project cannot reference a `net10.0-windows` assembly at
all, so anything in the WinUI half is code no unit test can see. This repository has already settled
that question twice, in the same direction: `XamlStackModules` lives in Contracts and markup parsing
lives in `RoseMcp.XamlDiff`, both because the host that owns the behaviour cannot be referenced from
a test. The split is what makes the rows and the formatting testable, and the 489-test unit suite
covers them on Linux as a result.

**What stays in the tray.** `WorkspaceRow`, because it reaches for `InfoBarSeverity`;
`StartupRegistration`, because an inspector does not start with Windows; and `TrayOptions`.

## A referenced project's assets land under its project name, and the library owns that path

**Decision.** The icon lives once, in `RoseMcp.Ui/Assets/`, and `RoseUiAssets` is the single place
that spells where it ends up at runtime: `RoseMcp.Ui/Assets/` beside the consuming app's exe, not
`Assets/`.

**Why not what it looks like.** WinUI copies a referenced project's `Content` into each referencing
app's output under a folder named for the project. Neither `Link` nor `TargetPath` on the app's own
item moves it, which cost three build cycles to establish. Two ways out were rejected: duplicating
the files per app makes the pixels exist twice for no gain, and letting each app spell the path
encodes the surprise once per app. The chosen shape has the same structure as the `ms-appx` URI the
XAML already uses, so both halves agree.

**One thing that follows.** A shared resource dictionary is merged as
`ms-appx:///RoseMcp.Ui/Themes/Rose.xaml`, and it must be merged *after* `XamlControlsResources`,
because every style in it is `BasedOn` a stock WinUI one and that resolves at parse time up the merge
chain. Getting the order wrong fails to parse, and the only symptom is the process disappearing
during the `App` constructor -- which is why the crash handler landed in the same change.

## Only UWP and WinUI targets get a XAML tab

**Decision.** The host probes the target's loaded modules for its `XamlStack` when it establishes the
session, re-probing while the answer is `Unknown`, and reports the stack with the sentence that
decided it. The inspector shows a XAML tab only for `Uwp` and `WinUi`, and otherwise puts one line in
the header naming what the target is and why it cannot be inspected.

**Why probe at establish rather than at the first XAML call.** The probe is a module-list read
costing microseconds and needing nothing but a pid, and until now it only ran inside the first
injection -- so a status view could not say whether a target had XAML without paying for an injection
to find out. Re-probing while `Unknown` matters because frameworks load late: a target attached at
startup has not loaded `Windows.UI.Xaml.dll` yet, and a permanent `Unknown` from one early look would
be a wrong answer rather than an unknown one.

## Local names and lines come from the portable PDB, and a stale one is refused

**Decision.** `RoseMcp.Symbols` reads a module's metadata and its portable PDB. A stopped frame's
locals carry the names the source declares, resolved for the scopes covering that frame's own IL
offset, and an IL offset resolves to a file and a line. A module with no symbols leaves locals as
`local_0` upwards by slot, which is what they all were before.

**Why the IL offset is not optional.** A slot is reused by locals in sibling blocks, so which name a
slot has depends on where execution is. Naming every slot from every scope in the method at once
hands back two names for one slot and picks between them arbitrarily -- the shape of failure this
whole reader exists to remove, since the caller cannot tell a wrong name from a right one.

**Why a mismatched PDB is refused rather than read.** A PDB left over from an earlier build reads
perfectly well and answers with names and line numbers out of code that is not running: confident,
plausible, and wrong. `PEReader.TryOpenAssociatedPortablePdb` checks the PDB's id against the
module's debug directory, which is why symbols are opened through it rather than by opening the file
next door. `PdbState` then separates "no symbols" from "somebody else's symbols", because the two
send a reader to different places, and a null would say only that something was unknown.

**Why a hidden sequence point is an answer.** A hidden point says the IL from here maps to no source
at all -- compiler prologue, an `await`'s state-machine bookkeeping, an iterator's closing machinery.
So it stops the search rather than being skipped: skipping to the last real point before it puts a
frame on a line whose code is not executing.

**Why the module file is prefetched and closed.** This runs in a process that lives for hours beside
a developer who is rebuilding. A held handle on their output fails their next build with MSB3021,
which is the same failure the analyzer loader shadow-copies to avoid.

**Why the evaluator matches the name it reported.** `rose_debug_evaluate` resolves a root against the
same names the stop's recorded frame reported, so a caller passes back what they were shown. A slot
number still resolves, since a module without symbols reports slots and a caller reading an older
event may hold one.

**Why the tests compile their own module.** Read against the test assembly's own PDB, what these
tests assert would depend on how the suite was built: optimised code loses a local to a stack temp
and merges the scope it was declared in, and CI runs the unit suite in Release as well as Debug. So
`CompiledModule` emits a small assembly at `OptimizationLevel.Debug` with the source beside the
assertions about its lines, and the fixture the live-app test stops on carries `NoOptimization` for
the same reason.