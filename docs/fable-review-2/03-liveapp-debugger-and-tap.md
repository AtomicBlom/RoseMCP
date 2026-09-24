# LiveApp: the debugger, the XAML tap, Symbols and XamlDiff

**Scope.** `src/RoseMcp.LiveApp` read in full (`Debugging/CorDebugSession.cs`, `LiveAppSessionHost.cs`,
`Xaml/XamlDiagnosticsSession.cs`, `Debugging/CorDebugInspector.cs`, `Xaml/XamlProviderSession.cs`,
`Xaml/XamlProviderPipe.cs`, `Debugging/Uwp.cs`, `Debugging/DebugEventBuffer.cs`, `Debugging/ValueReader.cs`,
`Program.cs`, `LiveAppOptions.cs`, `ToolErrorReporting.cs`, `Tools/*`, `Xaml/XamlChannelBounds.cs`,
`Xaml/XamlTap.cs`, `Xaml/XamlTaps.cs`, `Xaml/XamlStackProbe.cs`, `Debugging/UwpStartupCoordinator.cs`,
`Debugging/UwpResumeStub.cs`, `Debugging/RuntimeDiscovery.cs`, `Debugging/RuntimeFlavour.cs`).
`src/RoseMcp.Symbols` in full. `src/RoseMcp.XamlDiff` in full. `src/RoseMcp.Xaml.Tap`: `tap_object.h`,
`tap_channel.h`, `tap_edits.h`, `tap_pick.h`, `tap_tree.h`, `tap_diagnostics.h`, `tap_surface.h`,
`tap_properties.h`, `tap_render.h` in full; `tap_overlay.h` skimmed (install, arming, pick, globals).
Both bindings (`RoseMcp.Xaml.Uwp.Tap.cpp`, `RoseMcp.Xaml.WinUi.Tap.cpp`) in full. Contracts: `Live*.cs`,
`XamlStackModules.cs`, `XamlProviderPath.cs`, `ValuePath.cs`, `BreakpointCondition.cs`, `SymbolLocation.cs`.
Broker side skimmed only where the host's lifetime depends on it (`LiveAppSession.DisposeAsync`,
`LiveAppSessionManager.RemoveAsync`, `TargetArchitectureProbe`). Tests: `LiveAppSessionTests`,
`LiveAppUwpTests`, `LiveAppInspectionTests`, `LiveAppWinUiTests`, `TestToolchain`, `UwpProbeApp`,
`ProbeConstraints`, `XamlProviderPipeTests`, plus test counts across the Symbols/XamlDiff unit tests.
Invariants and decisions as listed in the brief, plus `a-stopping-breakpoint-lets-go-on-its-own`,
`breakpoint-conditions-compare-a-value`, `a-session-detaches-before-its-host-is-closed`,
`sibling-removals-are-applied-last-first`, `a-live-edit-is-diffed-against-what-was-last-sent`,
`a-targets-architecture-is-read-from-its-image`, `the-live-app-suite-is-phased-by-what-tests-share`.
GitHub issues #68, #208, #225 read.

**Verdict.** Adequate, leaning strong, and better than "largely vibe-coded" suggests. The parts that
are hard to get right have had real care spent on them and it shows: the detach protocol, the stop
timers, the event buffer, the named-pipe channel, the never-unadvise tap lifetime and the tier split
of the C++ headers are each backed by a measured invariant and, mostly, by a test. `RoseMcp.Symbols`
is exactly what CLAUDE.md claims (a debugger-free metadata and PDB reader with correct cache
invalidation and a stale-PDB refusal) and `RoseMcp.XamlDiff` is a well-isolated pure library with 33
unit tests. Where it was fragile was in two places a refactor had to fix rather than tidy. **The
debugger half is now done** (PRs #265, #268, #270, #274 and #281, closing LIV-01, LIV-02 and LIV-03):
`CorDebugSession` is 879 lines from 2,396 across nine types, the stop state machine is one value, and
both High findings -- a dead target reporting as stopped, and a breakpoint hit attributed by method
token alone -- are fixed with a regression test each. **The XAML half is done too** (#317 and #323,
closing LIV-07 and LIV-08): a reply is matched to its request by an id it echoes, and a timed-out verb
that changes the app says the change may still land. On hot reload:
nothing exists beyond the launch, attach and module-load hooks a debugger has anyway, and the launch
path sets no environment on the target.

**A note on references in this file.** The live-app code has been restructured three times since the
review was written, and precise line numbers have gone stale each time. Open findings here now cite
**file plus symbol**; a line range survives only where the finding is genuinely about a span. The
symbol names are what a `rose_search_symbols` or a grep will find, and they have proved far more
durable than the numbers.

## Strengths

What must survive a refactor, with where it lives:

- **The "confident wrong answer" discipline is real and is written into code paths, not only docs.**
  `PdbState.Mismatched` refuses a stale PDB rather than reading it (`src/RoseMcp.Symbols/ModuleSymbols.cs:94-109`,
  `PdbState.cs:18-27`); `TapTree::ResolveName` refuses a duplicated `x:Name` rather than picking one
  (`src/RoseMcp.Xaml.Tap/tap_tree.h:420-434`); `TypeOwners` refuses a type two modules declare
  (`src/RoseMcp.Symbols/TypeOwners.cs:52-58`); `XamlApplyBaseline.Prepare` records and applies nothing on a
  first apply rather than diffing a file against itself (`src/RoseMcp.XamlDiff/XamlApplyBaseline.cs:54-75`);
  `XamlProviderWire.Outcome` reports an edit as applied only if every step of it was.
- **`CorDebugInspector` is the right cut, and it is the cut the rest followed.** A read of a stopped
  target is handed a `StoppedTarget` record (`src/RoseMcp.LiveApp/Debugging/CorDebugInspector.cs:20`)
  built only under the target's gate (`TargetInspection.Stopped`), knows nothing about how the stop
  happened, and runs no debuggee code. Its `WalkFrames` counts unrepresentable frames rather than
  dropping them (`CorDebugInspector.cs:222-252`). Eight more types were cut to this shape afterwards,
  so it is the house style for anything else the session accretes rather than one good example.
- **The detach protocol is hard-won and tested.** `CorDebugSession.TryDetachOnce`/`ReleaseForDetach`
  and `DetachProtocol.Settle` encode two facts that each cost a target: ICorDebug refuses to detach
  over active breakpoints, and removing a patch under a parked thread fail-fasts the debuggee.
  `DetachProtocol.IsRefusal` stops a deterministic refusal being retried as if transient.
  `CorDebugSession.Dispose` terminates the interface only when the detach succeeded. Three integration
  tests cover it, including the #219 case of detaching past a bound breakpoint
  (`LiveAppSessionTests.cs:247-338`).
- **Timers that know which stop they were armed for.** Both the safety timer and the hold timer carry
  the `StopRecord` they were armed for, and `CorDebugSession.ContinueInternal` resumes only while that
  is still the record the session holds. This is the correct answer to "Timer.Dispose does not wait for
  a running callback", and reference identity is what decides it, so there is no counter to keep in
  step across the methods that end a stop. `ResumeCause` makes the three ways a stop ends distinct in
  the event stream.
- **`DebuggedTarget` is one answer to two questions.** Five systems needed "is there still a target"
  and "is it stopped" together, and paired them up in six spellings. `TryHeld`/`TryLive` are the only
  two ways to ask, every transition is a named method, and the gate lives with the state it guards, so
  the "caller holds the gate" contracts on `BreakpointTable`, `TargetSymbols` and `StopRecord` can now
  name whose gate and what it covers.
- **`DebugEventBuffer` is small and right.** `WaitForAsync` checks for a match under the same gate
  `Append` takes so nothing lands between check and registration (`DebugEventBuffer.cs:154-163`), wakes
  waiters with `RunContinuationsAsynchronously` so no reader code runs on mscordbi's thread (`:122-126`),
  re-checks the buffer rather than the deadline token after the wait (`:174-181`), and advances the cursor
  over skipped kinds (`:244-246`).
- **The pipe channel.** Length-prefixed UTF-8 frames at both ends (`XamlProviderPipe.ReadFrameAsync`,
  `tap_channel.h`); one pump owns every read so a departed provider is noticed between requests
  (`XamlProviderPipe.PumpAsync`); a reply is taken by the id its request carried, and any other is
  dropped and logged (`TakeReply`); on hang-up the uncollected replies are discarded and the greeting
  source replaced, so a caller cannot be handed a dead tap's answer or its greeting (`HangUp`). The
  reconnect path is tested where it can be, in the one project that references `RoseMcp.LiveApp` as a
  library (`tests/RoseMcp.IntegrationTests.Windows/XamlProviderPipeTests.cs`).
- **Two XAML classes split by which invariant governs them.** `XamlProviderSession` (staging, grants,
  architecture, injection) knows no wire format; `XamlDiagnosticsSession` (verbs, parsing, apply) knows
  no staging (`XamlProviderSession.cs:25-36`). Every public entry takes `_requests` once and calls a
  `Core` (`XamlDiagnosticsSession.cs:61-66`). `XamlChannelBounds` puts every wait's bound and its
  sentence in one place, capped by one environment variable that can only shorten (`XamlChannelBounds.cs`).
- **The tap's tier split is enforced by the compiler.** Include order in both bindings
  (`RoseMcp.Xaml.Uwp.Tap.cpp:35-56`, `RoseMcp.Xaml.WinUi.Tap.cpp:43-67`) puts `tap_object.h` above the alias
  block, so an `xaml::` type reaching in fails to compile. `IRoseOverlay` speaks only handles, strings and
  bools (`tap_surface.h:57-103`). `TapTree` reaches nothing and is testable as arithmetic over rows
  (`tap_tree.h`). `FreePropertyChain` is the one place a chain is freed (`tap_diagnostics.h:77-101`).
- **The tap's lifetime design is honest about what it cannot do.** Standing a superseded tap down rather
  than unadvising (`tap_object.h:447-457`, `:505-510`) follows three measurements recorded in
  `xaml-tap-lifecycle.md`. `PipeReaderLoop` catches everything around `Serve` because an escaping
  exception is `std::terminate` in someone else's app (`tap_object.h:600-608`). `RoseTapMaxFrame` guards
  `payload.assign` for the same reason (`tap_channel.h:99-102`). The reader looks the active tap up per
  request and holds a reference for the length of it (`:587-611`).
- **`RoseMcp.Symbols` is what the README says.** Prefetch-and-close so no handle is held on a developer's
  output (`ModuleSymbols.cs:58-64`); `TryOpenAssociatedPortablePdb` for the identity check
  (`:94-109`); stamp-based invalidation on write time and length (`SymbolCache.cs:33-56`); `MethodRegion`
  as a pure closure over extents with the state-machine and containment rules stated separately
  (`MethodRegion.cs:42-91`); `PortablePdb.LocalNames` honouring scopes so a reused slot gets the inner
  block's name (`PortablePdb.cs:47-64`). Unit-tested against a real compiled module
  (`tests/RoseMcp.UnitTests/CompiledModule.cs`).
- **`RoseMcp.XamlDiff` is pure and pins the things that matter.** Last-first sibling removal is pinned
  by order, not outcome (`XamlDiff.cs:146-163`; decision `sibling-removals-are-applied-last-first.md`);
  property-element syntax is never a child (`:94-112`); resources are keyed and templates are refused by
  name (`:199-278`); `XamlMaterialiser` attaches last so nothing observes a half-built element
  (`XamlMaterialiser.cs:17-21`).
- **The one coupling between debugger and XAML is explicit.** `TargetXaml.WhyUnservable`
  refuses a XAML verb while the target is held, naming the hold, and
  `TargetXaml.WithHeartbeat` attaches the age of the last debug event so a wedged UI thread can
  be told from a slow one. Tested by `A_xaml_failure_reports_how_long_ago_the_target_last_ran` (`LiveAppSessionTests.cs:697`) with a latency assertion.
- **`EndLaunchedTarget` and the orphan test.** A launched target dies with a host whose client went
  away unless a detach was asked for (`LiveAppSessionHost.cs:496`), and the test drives the host
  binary over a hand-written JSON-RPC handshake precisely because `McpClient.DisposeAsync` would have
  masked the bug (`LiveAppSessionTests.cs:541-600`).
- **Pure rules live where a test can reach them.** `ValuePath`, `SymbolLocation`, `BreakpointCondition`,
  `XamlStackModules`, `XamlProviderPath` in Contracts each carry their reason for being there and each has
  a unit test file (8-12 tests apiece).

## Findings

### LIV-01 `CorDebugSession` owns six unrelated concerns — **closed**
`CorDebugSession` is **879 lines from 2,396**, and is the callback dispatcher that composes nine types:
`TargetSymbols`, `BreakpointTable`/`BreakpointBinding`, `RuntimeAttachment`, `DetachProtocol`,
`StopRecord`/`TargetExecution`, then `DebuggedTarget`, `StopNarrative`, `TargetBreakpoints` and
`TargetInspection`. Shipped across PRs #265, #268, #274 and #281. The reasoning for each seam —
including what each class deliberately does *not* own — is in its own class summary.

Three amendments, all argued in the code rather than here. The stop machine became a *value*
(`TargetExecution` + `StopRecord`, LIV-02) rather than the `StopController` the card asked for. Only
the detach *policy* moved; the transitions stayed, because they are writes to the one value that says
what the target is doing and a second writer is how that value starts disagreeing with itself
(`DetachProtocol.cs:1-24`). And the card's six concerns were not the final cut: `DebuggedTarget` — the
process and its execution state behind `TryHeld`/`TryLive`, replacing six spellings of the same pair of
questions — is a seam the review did not name, and is the one that owns `_gate`.

### LIV-02 The stop state machine is implicit in nine fields and five differently-spelled guards — **closed**
A target killed while held went on reporting itself stopped at a breakpoint, so a XAML verb answered a
dead process with "resume the target and ask again". Shipped as `TargetExecution` (the five states as
one value, swapped whole) and `StopRecord` (the stop itself, owning both timers) in PR #265 (`9d95b94`),
with `A_target_that_dies_while_held_is_no_longer_reported_as_stopped` in `LiveAppDebugTests.cs`. The
reasoning is in `TargetExecution.cs` and `StopRecord.cs`: why every guard is a pattern match, why the
value is swapped rather than mutated, and why the stop generation counter is gone. The two `Hold`s that
meant different things are `HoldAtStop` and `OperatorHold`.

HOT-06 builds on this and is **not** closed: the `Applying(ApplyRecord)` arm is still tier-6 work.

### LIV-03 A breakpoint hit is attributed by method token alone, so two bindings in one method misreport — **closed**
A tracepoint on `Program.Beat` and a stopping breakpoint at `Program.Beat@IL_0002` both claimed every
hit of either, and the first registered won — so the target was never held at all while the breakpoint
reported itself bound with a hit count of zero. Shipped in PR #270 (`5356bfb`): `BreakpointTable.Claim`
matches on the IL offset read from the callback's breakpoint, and
`A_tracepoint_and_a_breakpoint_in_one_method_each_fire_as_itself` covers it.

**The card's suggested fix was wrong and the code says why.** Matching on the breakpoint object's
identity is the obvious answer, but ClrDebug's interfaces are source-generated `ComWrappers` rather than
classic RCWs, so the same COM pointer is not promised to come back as the same managed object —
identity would have held until it quietly did not, which is the failure class this finding is about.
The reasoning is on `BreakpointTable.Claim`.

### LIV-04 A failing callback handler continues the target silently
- **Severity:** Medium
- **Effort:** S
- **Where:** `src/RoseMcp.LiveApp/Debugging/CorDebugSession.cs`, `OnEvent`
- **What:** `OnEvent` defaults `shouldContinue = true`, calls `Record(e)`, and on any exception logs at
  Debug and continues. A throw inside `RecordBreakpointHit` or `HoldAtStop` -- a metadata read failing, a
  `DebugException` from `EnumerateChains` -- means the stop never happens, the event is never buffered,
  and the agent waiting on `rose_debug_events` for a `BreakpointHit` waits out its whole window.
- **Why it matters:** The event stream is the agent's only view; a hit that leaves no trace in it is
  indistinguishable from a breakpoint that was never reached, and Debug-level logging is not read
  mid-session.
- **Suggested change:** In the catch, `buffer.Append(SessionNotice, $"A {e.Kind} callback could not be
  recorded: {exception.Message}; the target was continued.")` so the loss is in the stream the agent
  reads. Make `Record` return a `CallbackOutcome { Continue, Hold }` enum rather than a bool, so the
  exception arm has to choose one explicitly (see inversions).

### LIV-05 Every breakpoint set async-breaks the whole target to enumerate modules it could already know
- **Severity:** Medium
- **Effort:** M
- **Where:** `src/RoseMcp.LiveApp/Debugging/TargetBreakpoints.cs`, `AddBinding` and
  `BindAgainstLoadedModules`; `src/RoseMcp.LiveApp/Debugging/TargetSymbols.cs:65-80` (`Walk`)
- **Scope reduced** after PR #265. The card originally led with a contradiction in the lock discipline:
  `Break` documents stopping *outside* the gate while these two stop *inside* it, with nothing saying
  the stop-count behaviour was being relied on deliberately. `TargetSymbols.Walk` now states it where it
  is relied on -- "the stop and the continue are a pair, which is what makes this safe to call while the
  target is held at a breakpoint: the stop count goes up and back down and the target stays exactly as
  stopped as it was" -- so that half is answered. The cost half is not.
- **What:** `AddBinding` calls `BindAgainstLoadedModules` on every `rose_debug_set_breakpoint` and
  `rose_debug_add_tracepoint`, which takes the target's gate, calls `process.Stop(0)` and walks
  `AppDomains -> Assemblies -> Modules` to hand `BreakpointTable` the modules. `TargetSymbols.Remember`
  already sees every module as it loads, but keeps only the path -- the `CorDebugModule` is dropped, so
  the walk has to be retaken.
- **Why it matters:** A full stop of somebody's application per breakpoint set, for a list the session
  could have kept. It is also the cost that makes card 11d (a plural `rose_debug_*` call) worth more
  than it looks: six locations today is six stops.
- **Suggested change:** Keep the `CorDebugModule` objects alongside the paths in `TargetSymbols`, filled
  from `Remember` on load and from the one walk the attach already takes, so `AddBinding` binds against
  what is known without stopping the target. Hot reload wants the same map for a different reason: EnC
  needs the `ICorDebugModule` to call `ApplyChanges`.

### LIV-06 Two stack walkers disagree about honesty
- **Severity:** Medium
- **Effort:** S
- **Where:** `src/RoseMcp.LiveApp/Debugging/StopNarrative.cs` (`Frames`, `DescribeFrame`) against
  `src/RoseMcp.LiveApp/Debugging/CorDebugInspector.cs:222-252` (`WalkFrames`);
  `src/RoseMcp.Contracts/LiveDebugEvent.cs` (`Frames` is `IReadOnlyList<string>`)
- **Sharpened by PR #274.** The dishonest walker now has a type of its own, `StopNarrative`, whose
  stated job is "what an event says about where the target stopped". That makes the disagreement easier
  to state and no less real: two types answer one question two ways.
- **What:** The event-stream walker renders frames to strings and silently drops any frame whose
  function cannot be resolved (`StopNarrative.DescribeFrame` returns null on any exception). The
  inspector's walker counts them (`SkippedBefore`) with the comment "a stack silently missing three
  frames reads as a complete stack with a surprising caller". The stop event, which the decision
  `a-stop-captures-its-frame-when-it-happens.md` says is the agent's primary view, uses the dishonest one.
- **Why it matters:** The agent's captured stack can show `Main` calling `Beat` directly with the native
  transition and the runtime stub missing, and nothing says so. Two implementations of one walk will
  drift further.
- **Suggested change:** Build the event's frames from `WalkFrames` + `DescribeStackFrame` and carry
  `LiveStackFrame` (which already has `SkippedBefore`, `Location`, `Source`) on `LiveDebugEvent` in
  place of strings, or at least render `SkippedBefore` into the string (`"[2 native frames]"`).
  `StopNarrative` already holds its own `CorDebugInspector`, so it can reach the honest walk without
  being handed anything new; `Frames` and `DescribeFrame` then go.

### ~~LIV-07 A request the host has timed out on still runs in the app, and the pipe cannot tell whose reply is whose~~
**#317, #323.** A XAML request the host had timed out on could still run in the app, the caller was
told only that it failed, and its late reply was matched by position. A timed-out verb that changes the
app says the change may still land, and a reply is matched to its request by an id it echoes.

**Cancelling a request in flight is declined.** It needs an `abandon` the provider checks before
dispatching, on top of the id, and nothing has been observed to hit the hazard: a late reply is logged
as it is dropped, and none is in any kept log. Revisit it if one ever is. The reasoning is in
`docs/invariants/xaml-live-edit.md`.

### ~~LIV-08 The wire format is versioned by column count and the greeting carries no identity~~
**#323.** A stale provider's shorter rows were read as data with fields missing. The greeting names a
protocol version the host refuses on mismatch, so every parser requires the full row.

### LIV-09 `XamlDiff` hard-codes UWP type names in a framework-neutral library
- **Severity:** Medium
- **Effort:** S
- **Where:** `src/RoseMcp.XamlDiff/XamlDiff.cs:22-42` (`ValueTypes`), `:412-416` (`InferValueType`);
  `src/RoseMcp.Xaml.Tap/tap_edits.h:81-124` (`ApplySetProperty` fallback)
- **What:** Every inferred value type is spelled `Windows.UI.Xaml.*`. On WinUI 3 the provider's
  `CreateInstance("Windows.UI.Xaml.Media.SolidColorBrush", ...)` cannot resolve, and the fallback is the
  property's *declared* type, which for `Background` is `Microsoft.UI.Xaml.Media.Brush` -- abstract, so
  that `CreateInstance` fails too. `Thickness` and `CornerRadius` may survive on the fallback because the
  declared type is the concrete struct; brushes cannot. The one WinUI edit test edits `Text`, a string
  with an empty type hint (`LiveAppWinUiTests.cs:508-527`), so none of this path is exercised. The unit
  tests pin the UWP spelling (`XamlDiffTests.cs:28`, `:349`, `:374`).
- **Why it matters:** `rose_xaml_apply` of a colour on a WinUI 3 app most likely reports
  `CreateInstance(...) failed 0x80004005` for every brush edit, and the tap already has the right
  qualifier (`RoseTapXamlRoot`, used by `Construct` at `tap_edits.h:209-217`) but is not asked to use it.
- **Suggested change:** Emit framework-neutral hints from the diff (`SolidColorBrush`, `Thickness`,
  `Double`) and let the provider qualify them with `RoseTapXamlRoot` the way `Construct` already does, or
  pass the `XamlStack` into `XamlDiff.Compute`. Add a WinUI 3 `Background` edit to
  `Reads_and_edits_properties_on_a_winui_app`.

### LIV-10 The XAML tree is unreadable while the target is held, and it need not be (#225)
- **Severity:** Medium
- **Effort:** M
- **Where:** `src/RoseMcp.LiveApp/Xaml/TargetXaml.cs`, `WhyUnservable` and `ReadTree`;
  `src/RoseMcp.Xaml.Tap/tap_object.h:249-257` (`tree` verb dispatches to the UI thread)
- **What:** Is #225 structural? Half. In the *provider* it is: `Serve("tree")` runs `SnapshotRows` on the
  UI thread because `m_tree` is written there, and a held UI thread cannot run it. In the *host* it is
  not: the host throws the previous snapshot away after paging it, so the moment the debugger holds the
  target every XAML verb is refused, including the read-only one an inspector user most wants at a
  breakpoint.
- **Why it matters:** The two things wanted at a stop are the stack and the tree, and having one costs
  the other. The refusal is correct for a live read and wrong as the whole answer.
- **Suggested change:** In the host, keep the last `LiveXamlTree` per session with a `ReadAtUtc`, and
  while held return it with `Stale = true` and a detail saying when it was read; properties stay refused.
  Longer term, in the tap, publish a copy of the node list under a mutex after each `OnVisualTreeChange`
  so `tree` can be answered from the reader thread without touching XAML -- `TapTree` reaches nothing,
  which is what makes that possible.

### LIV-11 `DllCanUnloadNow` says yes to unloading a DLL the design says is never ejected
- **Severity:** Medium
- **Effort:** S
- **Where:** `src/RoseMcp.Xaml.Tap/tap_object.h:723-726`; contrast `xaml-tap-lifecycle.md` "The tap is
  never ejected from the target"
- **What:** `DllCanUnloadNow` returns `S_OK` when `g_lockCount == 0`, which is always: nothing calls
  `LockServer`. The DLL owns a detached `std::thread` (`:653`), a leaked overlay singleton with event
  handlers into the app's tree (`tap_overlay.h:1635`), and taps the framework still holds as advised
  callbacks. A `CoFreeUnusedLibraries` in the host process would unload it under all three.
- **Why it matters:** Unloading is a crash in an application that is not ours, which the invariant ranks
  below a leak, and the export currently invites it.
- **Suggested change:** Return `S_FALSE` unconditionally, with the invariant's sentence as the comment.
  Consider pinning with `GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_PIN)` in `DllMain` so the answer is
  structural rather than polite.

### LIV-12 Unbounded waits on the UI thread from the provider's reader, and one dangling capture
- **Severity:** Medium
- **Effort:** S
- **Where:** `src/RoseMcp.Xaml.WinUi.Tap/RoseMcp.Xaml.WinUi.Tap.cpp:321` (`WaitForSingleObject(done, INFINITE)`);
  `src/RoseMcp.Xaml.Uwp.Tap/RoseMcp.Xaml.Uwp.Tap.cpp:211-223` (`RunAsync(...).get()` with `[&work]`)
- **What:** `RoseTapRunOnUiThread` from the reader thread waits forever for the UI thread. When the UI
  thread is wedged -- the case `XamlChannelBounds` exists for -- the reader blocks holding a reference to
  the serving tap; every later frame queues behind it; a re-injection creates a new tap but
  `StartPipeReader` is a no-op because `g_pipeRunning` is true (`tap_object.h:648-655`); and the host's
  pump never observes a departure because the stuck reader never closes the handle, so `Residency`
  reports `Resident` while every request spends its bound. On UWP the lambda captures `work` by
  reference; the `catch (...)` returns false when `.get()` throws, but if the throw is a dispatcher
  failure that leaves the lambda queued, a later run dereferences a stack frame that has gone.
- **Why it matters:** This is the wedge `ProbeConstraints.LiveApp` serialises the whole suite around
  ("stays until injection stops wedging apps", `ProbeConstraints.cs:61-78`), and the design has no way to
  recover a session from it short of restarting the app.
- **Suggested change:** Bound the wait (the host's `Snapshot` bound minus a margin) and answer the frame
  with a distinguishable "UI thread did not run the work" reply so the host can say so; capture `work` by
  value (`std::function` copy) so a late run is safe; on a bounded-wait expiry, mark the reader as needing
  restart so a re-injection can start a fresh one.

### LIV-13 Comment-carried ICorDebug workarounds without a test that would notice their loss
- **Severity:** Medium
- **Effort:** M
- **Where:** `src/RoseMcp.LiveApp/Debugging/DetachProtocol.cs:60-99` (`Settle` and the 0xC0000409
  fail-fast); `src/RoseMcp.LiveApp/Debugging/CorDebugSession.cs`, `GiveBackStop` (a breakpoint arriving
  during `Break`), `ContinueInternal` with `StopRecord.cs` (a timer firing for a stop that has ended),
  and the detach-window arm at the top of `OnEvent`
- **What:** Each of these encodes a race that was found by a target dying and is now held only by a
  comment. The detach tests detach from an idle target or one held at a breakpoint
  (`LiveAppSessionTests.cs:247-338`); none detaches while the probe is hitting a breakpoint in a loop,
  which is the case `Settle` exists for. `Break` racing a breakpoint has no test. The stale-timer guard
  has no test that fires one.
- **Why it matters:** These are the parts of the debugger most likely to be "simplified" by someone
  reading the code without the history, and the failure mode is a dead application rather than a red
  test. PR #265 and PR #268 moved all four behind type boundaries, which makes the comments easier to
  find and does nothing about the absent tests.
- **Suggested change:** `DebugProbeTarget` already loops at 5 Hz; add a `--fast` flag that loops without
  the sleep, set a tracepoint on `Beat` and detach under load, asserting the target survives. Add a
  `Break` test that sets a breakpoint first so the two stops race. For the timer, drive
  `ContinueInternal(ResumeCause, StopRecord)` with a record the session no longer holds, from
  `RoseMcp.IntegrationTests.Windows`, which already references the host as a library.

### LIV-14 The shared-app suite is sound in shape; its retries hide product defects
- **Severity:** Medium
- **Effort:** S
- **Where:** `tests/RoseMcp.IntegrationTests/UwpProbeApp.cs:369-406` (`LaunchSharedAsync`, three attempts
  with backoff), `:315-366` (`UsableAsync`/`IsTickingAsync`), `:487-533` (`SessionTurn.DisposeAsync`);
  `tests/RoseMcp.IntegrationTests/ProbeConstraints.cs:26-78`; `LiveAppUwpOverlayTests.cs:332`
- **What:** The phased structure is a good design: constraint keys give the runner the queue rather than a
  lock, slots are named so addresses cannot renumber, and the hand-back check fails the offender rather
  than its successor. It found #208 and attributed it correctly. Three things weaken it. The fixture
  launches the UWP probe up to three times with backoff and reports nothing when the first attempt
  faults -- a from-birth launch that fails one time in three is a product race (the resume stub) being
  retried past. The `LiveApp` blast-radius key serialises every live-app test because injection
  sometimes wedges the app (LIV-12), so the suite's wall clock is paying for a provider defect.
  `SelectTransientAsync` retries a mutating verb twenty times against an element that cycles twice a
  second, which is the shape LIV-07 turns into a flake.
- **Why it matters:** "Green once is not green" is already an invariant here; retries that absorb a
  race make green a lie in the other direction. The decision file's cost model (6.5 s per launch) is
  also what the retries are silently multiplying.
- **Suggested change:** Count launch attempts in the fixture and fail the run if any launch needed more
  than one, with the faulted `Detail` in the message, so the resume-stub race is a red test with a
  reason. LIV-07 has landed; once LIV-12 does, narrow `ProbeKeys.LiveApp` and measure. Make
  `SelectTransientAsync` select through the tree once the element is present and assert on a single
  attempt, so a timed-out select is a failure rather than a retry.

### LIV-15 Ninety-nine `catch` sites, most of them `catch (Exception)` to null
- **Severity:** Low
- **Effort:** M
- **Where:** `grep -rn "catch (Exception" src/RoseMcp.LiveApp src/RoseMcp.Symbols src/RoseMcp.XamlDiff`
  is the list and is the right way to read it -- 99 today, concentrated in `CorDebugInspector.cs` (18),
  `CorDebugSession.cs` (17), `XamlProviderPipe.cs` (8), `XamlDiagnosticsSession.cs` (7),
  `BreakpointTable.cs` (6) and `MethodTokens.cs` (6). PR #265 redistributed these across the extracted
  classes without changing any of them, so line references here would go stale again for no gain.
- **What:** Most are justified per-value defensiveness (one unreadable local must not lose the frame)
  and each says so. But they catch everything, including `ArgumentException`s that are the caller's own
  mistake and `InvalidOperationException`s that mean the session's state is wrong, and they log nothing,
  so a systematic failure (every `frame.Function` throwing because the target moved) presents as a stack
  of `(unreadable)` with no line in the log naming a site.
- **Why it matters:** When ClrDebug's wrappers throw for a reason that is not "this one value is odd",
  the code hides it as if it were.
- **Suggested change:** Catch `DebugException` and `COMException` where a ClrDebug call is the thrower
  and let the rest propagate to the boundary that already converts them (`ToolErrorReporting`). Route the
  swallowing sites through one `Try<T>(Func<T>, [CallerMemberName])` that logs at Debug with the member
  name, so a pattern of failures is visible in the log once rather than never.

### LIV-16 Six XAML verbs and eight inspection verbs repeat the same preamble
- **Severity:** Low
- **Effort:** S
- **Where:** `src/RoseMcp.LiveApp/Xaml/TargetXaml.cs` (six copies of "read pid, `Ensure()`, refuse with
  no target, refuse if unservable, call, attach heartbeat");
  `src/RoseMcp.LiveApp/InspectorSurface.cs` (eight copies of "if not attached return an empty result
  with `NotAttachedDetail`")
- **Scope reduced** by PRs #281 and #291, which moved both groups out of `LiveAppSessionHost` into
  types of their own. That is worth having on its own terms — `InspectorSurface` turned out to be a
  real boundary, the one `ToolNames` and `LiveAppInspectionTools` had each described in prose — and it
  makes the repetition easy to see rather than scattered through a 1,244-line host. It does not remove
  it: the preamble is still typed out per verb, in one file instead of two.
- **What:** The refusal shape, the heartbeat attachment and `Ensure()` are typed out per verb. A new
  verb that forgets `WhyUnservable` reintroduces the twenty-second wait the guard was added to remove.
- **Why it matters:** This is the pattern that decides whether a future `rose_xaml_*` verb inherits the
  debugger coupling or not, and today it is inherited by copy.
- **Suggested change:** `TResult WithXaml<TResult>(XamlAsk ask, Func<XamlDiagnosticsSession, int, TResult> verb,
  Func<string, TResult> refused)` on `TargetXaml`, doing the preamble once and stamping the heartbeat;
  the same shape for `InspectorSurface`'s not-attached result. Each verb becomes one line, and the
  guard cannot be forgotten. Cheaper now than when it was filed: `XamlAsk` already gathers what the
  preamble reads, so the helper has one argument rather than four.

### LIV-17 `XamlStackProbe.Detect` runs on every poll for the life of a non-XAML session
- **Severity:** Low
- **Effort:** S
- **Where:** `src/RoseMcp.LiveApp/LiveAppSessionHost.cs`, `CurrentInfo` and `ResolveXamlStack`;
  `src/RoseMcp.LiveApp/Xaml/XamlStackProbe.cs:95-107`
- **What:** While the stack is `Unknown` every `CurrentInfo` re-probes by enumerating
  `Process.Modules`, which is a cross-process snapshot of every loaded module with file-name resolution,
  not the "microseconds" the comment claims. A console target's stack is `Unknown` forever, and the
  broker polls `LiveAppInfo` on a timer, so the probe runs for the life of the session.
- **Why it matters:** A steady cross-process cost on a session that gains nothing from it, on a path the
  status view is waiting on.
- **Suggested change:** Stop re-probing once the process has been running longer than any XAML framework
  takes to load (thirty seconds after `ProcessCreated`, say) or once two probes return the same module
  count; record `Unknown` as settled with the reason.

### LIV-18 `DebugEventBuffer.Newest()` walks the whole ring per call
- **Severity:** Low
- **Effort:** S
- **Where:** `src/RoseMcp.LiveApp/Debugging/DebugEventBuffer.cs:89-105`
- **What:** `_events.Last()` on a `Queue<T>` is LINQ's enumeration of all entries (up to 4,096) under the
  gate. It is called by every `CurrentInfo` poll and every XAML failure.
- **Why it matters:** Cheap to fix, and it holds the same gate mscordbi's callback thread needs for
  `Append`.
- **Suggested change:** Keep `_newest` beside `_observed` and return it.

### LIV-19 The resume stub's answer is never read
- **Severity:** Low
- **Effort:** S
- **Where:** `src/RoseMcp.LiveApp/Debugging/UwpStartupCoordinator.cs:73` (`Resume` writes and returns),
  `:76-93` (`CompleteActivation`); `src/RoseMcp.LiveApp/Debugging/UwpResumeStub.cs:74-76` (the stub replies
  `resumed` or `resume-failed`)
- **What:** The stub reports whether `ResumeThread` worked; the coordinator ignores the reply and waits
  on `ActivateApplication`, which never returns for an app that stayed suspended. The failure is then
  reported after thirty seconds as "ActivateApplication did not return after the app was resumed" -- a
  sentence that is false in exactly this case.
- **Why it matters:** A wrong sentence about a rare failure, thirty seconds late, on a path whose whole
  point is capturing startup.
- **Suggested change:** Read the stub's reply after `Resume()` (bounded, like the pid line) and fail
  with the stub's own word when it is `resume-failed`.

### LIV-20 Comments carry history and decision numbers the conventions forbid
- **Severity:** Low
- **Effort:** S
- **Where:** 45 hits for `used to|previously|no longer` across the three C# projects and the tap headers;
  representative: `XamlProviderSession.cs:141`, `:240`, `:365`, `:567`; `XamlStackProbe.cs:13`;
  `XamlApplyBaseline.cs:7`; `tap_object.h:489`, `:732`; `tap_overlay.h:615`, `:1089`;
  `XamlProviderPipe.cs:14` ("D14 chose files in an ACL'd folder", a decision number, and the only one
  left in `src`); `RoseMcp.Xaml.Uwp.Tap.cpp:7-20` (describes the file channel the pipe replaced)
- **What:** CLAUDE.md forbids "used to", "previously", "no longer" and decision numbers in comments. The
  rule is broken forty-five times in this scope -- three more than when this was written, which is the
  argument for the CI grep rather than a sweep -- and the UWP binding's header comment still describes
  `commands.tsv`/`tree.tsv`, a channel that does not exist.
- **Why it matters:** The comments are otherwise the best in the repository, which makes the stale ones
  more misleading, not less: a reader trusts them.
- **Suggested change:** Rewrite each as the failure the code prevents (the conventions give the recipe);
  replace `D14` with a link to the decision file; rewrite the UWP `.cpp` header for the pipe. Add the
  grep to CI (see inversions).

### LIV-21 `LiveAppOptions.Parse` accepts contradictory targets silently
- **Severity:** Low
- **Effort:** S
- **Where:** `src/RoseMcp.LiveApp/LiveAppOptions.cs:69-87`
- **What:** `--attach 123 --launch a.exe` parses and picks attach by the order of the `if`s. A broker
  bug that sent both would debug the wrong thing without a word.
- **Why it matters:** Small, but it is the boundary between two processes and the only check the host
  has on what it was told.
- **Suggested change:** Count the target flags given and refuse more than one with all of them named.

### LIV-22 `SymbolCache.For` stats the file and rescans the type table on every metadata question
- **Severity:** Low
- **Effort:** S
- **Where:** `src/RoseMcp.Symbols/SymbolCache.cs:33-56`, `MethodTokens.cs:51-72` (`DeclaresType`),
  `MethodTokens.cs:219` (`Read`); called per module per binding from
  `src/RoseMcp.LiveApp/Debugging/BreakpointTable.cs:128`, `:145` and `:316` on mscordbi's callback thread
- **What:** `For` does a `FileInfo` stat per call and `DeclaresType` scans `TypeDefinitions` per call.
  `BreakpointTable.BindNewModule` runs `DeclaresType` over every remembered module for every unbound
  binding on every module load, so a location written without its assembly costs O(modules squared)
  stats and type-table scans during the target's startup, with the target stopped.
- **Why it matters:** It is on the callback thread, so it is a startup slowdown in somebody's
  application proportional to the square of its module count. The cache invalidation is right; the
  granularity is per question rather than per module.
- **Suggested change:** Compute a `HashSet<string>` of declared full type names once per `ModuleSymbols`
  (it is already the unit of invalidation), and rate-limit the stamp check to once per second per path.

## Pit-of-success inversions

1. ~~Five differently-spelled guards over nine fields, each checking for the right combination.~~
   **#265** (LIV-02): one state, read by pattern match, whose exited arm has no stop to return.
2. **Rule today:** "a callback handler returns false to hold and true to continue; on exception,
   continue". **Mechanism:** `Record` returns `CallbackOutcome { Continue, Hold }` and the catch arm has
   to construct one, so the silent-continue path is a visible choice with a `SessionNotice` beside it.
   (LIV-04)
3. **Rule today:** "every public XAML entry takes `_requests` once and calls a `Core` that assumes it is
   held; a `Core` must never take the lock" (comment at `XamlDiagnosticsSession.cs:61-66`).
   **Mechanism:** the `Core` methods and every `XamlProviderSession` method take a `HeldRequests` token, a
   `readonly ref struct` only the lock wrapper can construct. The session already does this for the
   debugger side with `StoppedTarget`; make it the same shape on the XAML side.
4. ~~"A mutating request that timed out may still run", held by a reviewer remembering it.~~ **#317,
   #323.** Which verbs mutate is a classification a test holds against the provider's dispatch, and
   each request carries an id its reply echoes.
5. **Rule today:** "the tap's tier purity is checked by include order and by nothing else"
   (`tap-tiers.md`: "not currently checked by a test"). **Mechanism:** a compile-only translation unit per
   tier in each `build.ps1` (`tap_tier2_check.cpp` includes `tap_channel.h` through `tap_object.h` with no
   projection headers and no aliases defined), so a violation fails the build in a file named for the
   tier rather than being absorbed by moving an include.
6. ~~"An older provider writes fewer columns; check the length" at every parse site.~~ **#323.** A
   provider that greets with another protocol version is refused by name, and a test holds the
   provider's version and escape table against the host's.
7. **Rule today:** "comments are self-contained and present tense; no `used to`, no decision numbers".
   **Mechanism:** a CI step that greps `src` for `\b(used to|previously|no longer|for now|until now)\b|\bD[0-9]{1,2}\b|§`
   with an allowlist, failing on new hits. Forty-five today, up three since this was written. (LIV-20)
8. **Rule today:** "a launch that faults once is retried three times in the fixture" (`UwpProbeApp.cs:385`).
   **Mechanism:** the fixture counts attempts and the assembly-level teardown fails the run if any launch
   needed more than one, with the faulted detail, so a product race cannot be green. (LIV-14)

## Open questions for Steve

1. `Refuses_a_xaml_request_while_the_target_is_stopped` removes the breakpoint the target is held at and
   then continues (`LiveAppUwpTests.cs:384`), while `ReleaseForDetach`'s comment says removing a
   patch under a parked thread fail-fasts the debuggee (`CorDebugSession.cs`, `ReleaseForDetach`). Is
   `RemoveBreakpoint` on the held breakpoint known safe because `Continue` fixes the thread up and only
   `Detach` does not? A sentence reconciling the two would stop the next reader "fixing" one of them.
2. ~~Is the reliance on mscordbi treating `Stop` on a synchronised process as a stop-count increment
   deliberate?~~ **Answered, #265**: the contract is stated where it is relied on. The open half is
   whether one caller's `Stop(0)` under the gate has ever been seen to block. (LIV-05)
3. Has a WinUI 3 brush or margin live edit ever been observed to land? (LIV-09)
4. ~~For #208, was the host log of a failing run checked for a `selecthandle` that timed out?~~
   **Checked: no kept log holds one**, which is why cancelling in flight was declined. (LIV-07)
5. Is `DllCanUnloadNow` returning `S_OK` intentional? (LIV-11)
6. Were `SetDesiredNGENCompilerFlags` / `SetJITCompilerFlags(CORDEBUG_JIT_DISABLE_OPTIMIZATION)` left out
   deliberately? `DebugProbeTarget` compensates with `MethodImplOptions.NoOptimization` (`Program.cs:62-64`),
   which suggests optimised frames lose locals in real targets too.
7. ~~Is the `Ended` + `StoppedAtBreakpoint` pairing something the inspector has ever shown?~~
   **Moot, #265**: the pairing is no longer expressible (LIV-02). Session state and execution state
   remain orthogonal by design.

## Hot-reload relevant facts

What exists today, and what does not, with the lines the hot-reload reviewer can build on. Grep over
`src` for `ApplyChanges|SetJITCompilerFlags|SetDesiredNGENCompilerFlags|MODIFIABLE_ASSEMBLIES|CORDEBUG_JIT|MetadataUpdater|ApplyUpdate|STARTUP_HOOKS|StartupHook`
returns nothing in `RoseMcp.LiveApp`; the only hits are Roslyn's unrelated `ApplyChangesOperation` in
the worker.

**How the target is launched or attached.** All of this moved to `RuntimeAttachment` in PR #265; the
facts are unchanged and the lines below are the new ones.
- Plain executable: `src/RoseMcp.LiveApp/Debugging/RuntimeAttachment.cs:76-96`, `Launch` calls dbgshim's
  `CreateProcessForLaunch(commandLine, bSuspendProcess: true, IntPtr.Zero, workingDirectory)` (`:81`).
  The third argument is `lpEnvironment` and is **`IntPtr.Zero`**: the target inherits the host's
  environment unchanged. Nothing sets `DOTNET_MODIFIABLE_ASSEMBLIES`, `DOTNET_STARTUP_HOOKS` or anything
  else on the target. The host's own environment is the broker's (it is started by
  `StdioClientTransport`, `src/RoseMcp.Broker/LiveAppSession.cs:93-102`), so setting a variable on the
  broker would leak to every target, which is the wrong grain.
- Startup attach: `RuntimeAttachment.cs:131-175`, `AttachAtSuspendedStartup` arms
  `GetStartupNotificationEvent(pid)`, resumes, waits, then `FindRuntimeWithRetry` (`:216`) ->
  `CreateCorDebug` -> `DebugActiveProcess(pid, win32Attach: false)` -> sets the runtime's continue event.
  The target is under debug before its first managed instruction, which is early enough to observe every
  module load.
- Running-process attach: `RuntimeAttachment.cs:52-70`, `Attach` -> `DebugActiveProcess`. Modules already
  loaded are enumerated lazily by `TargetSymbols.Walk` (`TargetSymbols.cs:65`) or eagerly by
  `CorDebugSession.BindAgainstLoadedModules` (`TargetBreakpoints.cs:153`), both via
  `TargetSymbols.EnumerateModules` (`TargetSymbols.cs:240`), which walks
  `process.AppDomains -> Assemblies -> Modules`.
- UWP: activation goes through `IPackageDebugSettings::EnableDebugging(packageFullName,
  debuggerCommandLine, environment)` at `src/RoseMcp.LiveApp/Debugging/Uwp.cs:118-133`. The environment
  block **is** settable there and is already used: `ActivationEnvironment =
  "ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO=1\0\0"` (`Uwp.cs:106`). Adding `DOTNET_MODIFIABLE_ASSEMBLIES=debug`
  to that multi-string is a one-line change for UWP launches. The resume stub
  (`UwpResumeStub.cs`, `UwpStartupCoordinator.cs`) holds the app suspended until the debugger has armed
  its startup notification, so the debugger sees the runtime's first module load.
- Which runtime: `RuntimeFlavour.Describe` (`Debugging/RuntimeFlavour.cs:42-72`) tells CoreCLR from
  desktop CLR from .NET Native by module name. EnC through ICorDebug is CoreCLR-only; classic UWP Release
  is .NET Native and out; classic UWP Debug runs the UWP CoreCLR flavour and its EnC support should be
  measured, not assumed.

**How ICorDebug is created and what flags are set.**
- `CreateCorDebug` (`RuntimeAttachment.cs:265-290`): `CreateDebuggingInterfaceFromVersionEx(CorDebugVersion_4_0,
  version)` (or the by-hand `CoreCLRCreateCordbObject3` in `RuntimeDiscovery.cs:73-102` when dbgshim
  folds the failure), then `Initialize()`, then `SetManagedHandler(callback)`. There is **no**
  `ICorDebugProcess2::SetDesiredNGENCompilerFlags`, **no** `ICorDebugModule2::SetJITCompilerFlags`, and
  therefore no `CORDEBUG_JIT_ENABLE_ENC` or `CORDEBUG_JIT_DISABLE_OPTIMIZATION`. Targets JIT optimised
  code unless their own build says otherwise, which is why the probe uses `MethodImplOptions.NoOptimization`
  (`tests/DebugProbeTarget/Program.cs:62-64`) to keep a local alive for the tests.
- The only EnC reference anywhere is `HRESULT.CORDBG_E_DETACH_FAILED_ON_ENC` in
  `DetachProtocol.IsRefusal` (`DetachProtocol.cs:106`), so a detach after an applied edit is already
  classified as a refusal rather than retried; `ReleaseForDetach` (`CorDebugSession.cs`, `ReleaseForDetach`,
  `BreakpointTable.cs:212`) would need to learn how to end an EnC session.

**Module-load hook that could record baselines.**
- `Record` handles `LoadModuleCorDebugManagedCallbackEventArgs` at `CorDebugSession.cs:750`,
  appending a `ModuleLoaded` event and calling `BindModule(loaded.Module)` (`:1345`), which calls
  `TargetSymbols.Remember` (`TargetSymbols.cs:44`) and `BreakpointTable.BindNewModule`
  (`BreakpointTable.cs:115`). `Remember` keeps only the **path**; the `CorDebugModule` object is
  dropped. Hot reload needs the `ICorDebugModule` to call
  `ICorDebugModule2::ApplyChanges(metadataDelta, ilDelta)`, so this is where a
  `Dictionary<string, CorDebugModule>` would be filled (LIV-05 wants the same map for a different
  reason). The callback runs with the target stopped, so reading the module's metadata for a baseline
  is safe here.
- `TargetSymbols.FileOf` (`TargetSymbols.cs:225`) already filters dynamic and in-memory modules, which
  EnC cannot target.

**Stop and resume infrastructure an apply would reuse.**
- `Break(int?)` (`CorDebugSession.cs:380`) produces a stop of the same shape as a breakpoint's, on
  the safety timer; `ContinueInternal` (`:922-972`) resumes. An EnC apply needs the process synchronised
  and `Break` gives that. `TargetSymbols.Walk` (`TargetSymbols.cs:65`) is the worked example of the
  stop/continue pair an apply would follow.
- The `_gate` and callback discipline (`OnEvent`, `:974-1050`) means the apply would run on a tool
  thread under `_gate` with the target stopped, which is also where `BindAgainstLoadedModules` runs today.
- `TargetExecution` (`TargetExecution.cs`) is where an `Applying(ApplyRecord)` arm goes, and adding it
  makes the compiler enumerate every site that has to decide what "applying" means. This is what HOT-06
  was waiting on and it is now available.

**Symbols.**
- `RoseMcp.Symbols` reads metadata and portable PDBs off disk with stamp-based invalidation
  (`SymbolCache.cs:33-56`) and a PDB identity check (`ModuleSymbols.cs:94-109`). After a rebuild the
  cache re-reads the **new** file, while the debuggee still runs the **old** IL, so frame-to-line mapping
  and local names would describe the wrong build until an apply lands; `PdbState.Mismatched` fires only
  when the PDB does not match the module *on disk*, not the module *in the process*. Hot reload needs a
  per-process baseline (the loaded module's metadata as loaded, plus the accumulated deltas), which the
  cache does not model. `PortablePdb.Extents`/`SequencePointsOf` (`PortablePdb.cs:121-186`) are the readers
  a delta's PDB would need the same of.
- The host has no Roslyn reference (`rose_project_graph`: `RoseMcp.LiveApp` -> `Contracts`, `Logging`,
  `Symbols`, `XamlDiff`). Delta emission (`Compilation.EmitDifference` against an `EmitBaseline`) belongs
  in the worker; the deltas would cross broker -> host as bytes in a tool argument, the way XAML markup
  does today.

**The XAML apply as a precedent.**
- `XamlApplyBaseline` (`src/RoseMcp.XamlDiff/XamlApplyBaseline.cs`) is the "diff against what was last
  sent" model with an explicit first-apply-records-nothing rule and a baseline that advances even on
  partial failure because edits are not idempotent. That is the same shape as Roslyn's `EmitBaseline`
  chain and the same non-idempotency argument. It is XAML-only by nature: `XamlDiff.Compute`
  (`XamlDiff.cs:44-50`) parses `XElement`s and addresses by `#name`/`Type[index]`; nothing in it is
  reusable for C# except the baseline discipline and the result shape (`LiveXamlApplyResult` with
  per-edit `Status` and `Notes`), which a `LiveCodeApplyResult` should copy.
- The apply pipeline (`XamlApply.ApplyEditsCore`) is the model for "compute
  edits outside the target, send a batch, get per-edit outcomes, never retry a mutating batch".

**What has no precedent here.**
- There is no managed in-process agent path. The tap is a native DLL loaded by the framework's own
  `InitializeXamlDiagnosticsEx` (`XamlProviderSession.Inject`, `:171`), not by generic injection; there is no
  `CreateRemoteThread`, no startup hook, no `MetadataUpdater.ApplyUpdate` caller. A dotnet-watch-style
  agent would be new machinery; the ICorDebug `ApplyChanges` path needs none and fits the host as it is.
- Nothing disables JIT optimisation or enables EnC at launch, and nothing tracks which methods have
  been edited (needed to refuse a rude edit or to remap active frames).
- Architecture: the host is published per RID and must match the target (`RoseMcp.LiveApp.csproj:9-15`,
  `LiveAppSessionHost.Architecture`). Any per-process apply inherits that constraint automatically; a
  managed agent would need building per RID too.

## Rose dogfooding notes

Every reach for a `rose_*` tool in this review, what for, and how it went.

- `rose_workspace_status` -- to confirm the workspace before trusting answers. Worked; reported
  `Degraded` for the two analyzer load failures the README already lists, so semantic answers about
  `RoseMcp.LiveApp` were still trustworthy. Load took 20.7 s.
- `rose_project_graph` -- to learn what `RoseMcp.LiveApp` references and who references it (only
  `RoseMcp.IntegrationTests.Windows`), which fed the "no Roslyn in the host" fact and the test-coverage
  reasoning. Worked, and answered a question grep would have got wrong (transitive `referencedBy`).
- `rose_outline symbol=RoseMcp.LiveApp.Debugging.CorDebugSession includeDocumentation=false` -- to map
  the 2.4k-line class before reading it. **Lost.** The result was 70,649 characters on one line and was
  cut by the client's token cap, so it was unusable; I read the file instead. Each member carries a
  full `location` object (absolute path, preview, containingMember, project, isTestProject) plus the
  signature, which is roughly 600 bytes per member for a class with ~110 members. A compact mode (name,
  kind, line, one-line signature) or a `maxMembers`/paging argument would have made this the right tool
  for exactly the read CLAUDE.md says it exists for. Dogfooding finding: the tool loses to `Read` on the
  files it is most needed for.
- `rose_outline symbol=RoseMcp.LiveApp.LiveAppSessionHost includeSignatures=false includeDocumentation=false`
  -- worked (67 members, ~14 KB), but the per-member `location` object with the absolute path repeated
  is still most of the payload.
- `rose_find_references symbol=RoseMcp.Symbols.SymbolCache.Shared` -- to confirm the cache is used only
  by `LiveApp` and `Symbols`. Worked, six references each named by containing member. One oddity: the
  getter-only auto-property reported three `definitions` at line 27 (columns 28, 28 and 37), which looks
  like the property, its getter and its backing field each counted as a declaration.
- `rose_find_references symbol=RoseMcp.LiveApp.Debugging.CorDebugSession.Hold` -- ambiguous over the two
  overloads. The error named both, with file and line, and said how to disambiguate. That is the right
  behaviour; retried with the parameter list and got the three call sites (`Break`, `Record`,
  `RecordBreakpointHit`), which is what LIV-02's naming point rests on.
- `rose_find_references symbol=RoseMcp.LiveApp.Debugging.DebugEventBuffer.Append includePreviews=false`
  -- to list every place an event is appended, for the hot-reload module-load facts. Worked, 27 sites
  by containing member.
- `rose_symbol_info symbol=RoseMcp.LiveApp.Debugging.CorDebugSession.OnEvent` -- to get the declaration
  span without reading. Worked (`1497-1562`, 66 lines).
- `rose_find_implementations symbol=System.IDisposable` -- to list what in the host owns a resource.
  **Lost to grep.** 1,312 matches, truncated at 60 and again at 200, every one from `ClrDebug`,
  `WinRT.Runtime`, ASP.NET and Roslyn metadata; passing `workspace` as the LiveApp project path did not
  scope it. `rose_find_references` has a `project` argument; `rose_find_implementations` does not, and
  for a BCL interface that is the only useful shape of the question. Dogfooding finding.
- Not reached for, and should have been: `rose_symbol_info ... includeSource=true` on the individual
  `CorDebugSession` methods would have replaced two of my three `Read` chunks once the outline had failed;
  I fell back to `Read` by habit after the outline overflowed. `rose_search_symbols` was never needed
  because the brief named every file.
- Cannot be reached for, by design: everything in `src/RoseMcp.Xaml.Tap` and the two bindings (C++) was
  read with `Read`/`sed`/`grep`. The `tap_overlay.h` structure map was a `grep` for method signatures,
  which is the outline the C++ side has no tool for.
