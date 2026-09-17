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
unit tests. Where it is fragile is in two places that a refactor has to fix rather than tidy:
`CorDebugSession` carries six unrelated concerns and an implicit stop state machine spread over nine
fields, so every new verb re-derives the guards; and the XAML pipe's "a reply is this request's answer
by construction" claim holds only while no request ever times out, which is the mechanism behind
#208. Two findings are graded High because they produce confident wrong answers: breakpoint-hit
attribution ignores the IL offset, and a target that dies while held is reported as still stopped. On
hot reload: nothing exists beyond the launch, attach and module-load hooks a debugger has anyway, and
the launch path sets no environment on the target; the facts the other reviewer needs are in the
"Hot-reload relevant facts" section with exact line references.

## Strengths

What must survive a refactor, with where it lives:

- **The "confident wrong answer" discipline is real and is written into code paths, not only docs.**
  `PdbState.Mismatched` refuses a stale PDB rather than reading it (`src/RoseMcp.Symbols/ModuleSymbols.cs:94-109`,
  `PdbState.cs:18-27`); `TapTree::ResolveName` refuses a duplicated `x:Name` rather than picking one
  (`src/RoseMcp.Xaml.Tap/tap_tree.h:1015-1020`); `TypeOwners` refuses a type two modules declare
  (`src/RoseMcp.Symbols/TypeOwners.cs:52-58`); `XamlApplyBaseline.Prepare` records and applies nothing on a
  first apply rather than diffing a file against itself (`src/RoseMcp.XamlDiff/XamlApplyBaseline.cs:54-75`);
  `XamlDiagnosticsSession.Outcome` reports an edit as applied only if every step of it was
  (`src/RoseMcp.LiveApp/Xaml/XamlDiagnosticsSession.cs:941-954`).
- **`CorDebugInspector` is the right cut.** A read of a stopped target is handed a `StoppedTarget`
  record (`src/RoseMcp.LiveApp/Debugging/CorDebugInspector.cs:20`) built only under the session's gate
  (`CorDebugSession.cs:1230`), knows nothing about how the stop happened, and runs no debuggee code. Its
  `WalkFrames` counts unrepresentable frames rather than dropping them (`CorDebugInspector.cs:222-252`).
  The extraction demonstrates that the rest of `CorDebugSession` can be cut the same way.
- **The detach protocol is hard-won and tested.** `TryDetachOnce`/`SettleForRelease`/`ReleaseForDetach`
  (`CorDebugSession.cs:378-535`) encode two facts that each cost a target: ICorDebug refuses to detach
  over active breakpoints, and removing a patch under a parked thread fail-fasts the debuggee.
  `IsRefusal` (`:544-549`) stops a deterministic refusal being retried as if transient. `Dispose`
  (`:1245-1265`) terminates the interface only when the detach succeeded. Three integration tests cover
  it, including the #219 case of detaching past a bound breakpoint (`LiveAppSessionTests.cs:242-338`).
- **Generation-guarded timers.** `_stopGeneration` (`CorDebugSession.cs:175`) captured by both the
  safety timer and the hold timer, checked in `ContinueInternal` (`:1336`), is the correct answer to
  "Timer.Dispose does not wait for a running callback". `ResumeCause` (`:824-834`) makes the three ways a
  stop ends distinct in the event stream.
- **`DebugEventBuffer` is small and right.** `WaitForAsync` checks for a match under the same gate
  `Append` takes so nothing lands between check and registration (`DebugEventBuffer.cs:154-163`), wakes
  waiters with `RunContinuationsAsynchronously` so no reader code runs on mscordbi's thread (`:122-126`),
  re-checks the buffer rather than the deadline token after the wait (`:174-181`), and advances the cursor
  over skipped kinds (`:244-246`).
- **The pipe channel.** Length-prefixed UTF-8 frames at both ends (`XamlProviderPipe.cs:354-371`,
  `tap_channel.h:114-207`); one pump owns every read so a departed provider is noticed between requests
  (`XamlProviderPipe.cs:274-319`); a stale reply is drained and logged before a new request
  (`:186-193`); the greeting source is replaced on hang-up so a caller cannot be handed a dead tap's
  greeting (`:326-346`). The reconnect path is tested where it can be, in the one project that references
  `RoseMcp.LiveApp` as a library (`tests/RoseMcp.IntegrationTests.Windows/XamlProviderPipeTests.cs`).
- **Two XAML classes split by which invariant governs them.** `XamlProviderSession` (staging, grants,
  architecture, injection) knows no wire format; `XamlDiagnosticsSession` (verbs, parsing, apply) knows
  no staging (`XamlProviderSession.cs:25-36`). Every public entry takes `_requests` once and calls a
  `Core` (`XamlDiagnosticsSession.cs:64-69`). `XamlChannelBounds` puts every wait's bound and its
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
- **The host makes the one coupling between debugger and XAML explicit.** `WhyXamlIsUnservable`
  (`LiveAppSessionHost.cs:402-413`) refuses a XAML verb while the target is held, naming the hold, and
  `WithTargetHeartbeat` (`:434-456`) attaches the age of the last debug event so a wedged UI thread can
  be told from a slow one. Tested at `LiveAppUwpTests.cs:1691-1766` with a latency assertion.
- **`EndLaunchedTarget` and the orphan test.** A launched target dies with a host whose client went
  away unless a detach was asked for (`LiveAppSessionHost.cs:888-917`), and the test drives the host
  binary over a hand-written JSON-RPC handshake precisely because `McpClient.DisposeAsync` would have
  masked the bug (`LiveAppSessionTests.cs:541-600`).
- **Pure rules live where a test can reach them.** `ValuePath`, `SymbolLocation`, `BreakpointCondition`,
  `XamlStackModules`, `XamlProviderPath` in Contracts each carry their reason for being there and each has
  a unit test file (8-12 tests apiece).

## Findings

### LIV-01 `CorDebugSession` owns six unrelated concerns
- **Severity:** Medium
- **Effort:** L
- **Where:** `src/RoseMcp.LiveApp/Debugging/CorDebugSession.cs` (2,396 lines)
- **What:** The class is long for a reason other than growth: it is six things. (1) Runtime discovery,
  attach and launch: `Attach`, `Launch`, `AttachUwpAtStartup`, `AttachAtSuspendedStartup`, `LoadDbgShim`,
  `ResolveDbgShimPath`, `FindRuntimeWithRetry`, `FindRuntime`, `CreateCorDebug`, `WrapEvent` (`:198-299`,
  `:1382-1495`, `:2176-2183`). (2) The detach protocol: `Detach`, `TryDetachOnce`, `SettleForRelease`,
  `ForgetStepper`, `ReleaseForDetach`, `IsRefusal` (`:301-549`). (3) Breakpoint and tracepoint bookkeeping
  and binding: `AddTracepoint`, `AddBreakpoint`, `List*`, `Remove*`, `AddBinding`, `RemoveBinding`,
  `BindAgainstLoadedModules`, `BindAmong`, `BindModule`, `TryBind`, `ExplainUnbound`, `BreakpointBinding`
  (`:551-596`, `:739-741`, `:1267-1314`, `:1790-2126`, `:2348-2391`). (4) The stop/resume/hold state machine
  and its timers: `Continue`, `Step`, `Break`, both `Hold`s, `SetHold`, `ReleaseHold`, `ContinueInternal`,
  `CurrentStop`, `ClearStopTimers`, `GiveBackStop`, `ThreadToPauseOn` (`:743-1100`, `:1316-1380`,
  `:1681-1720`, `:2158-2174`). (5) Callback dispatch and event recording: `OnEvent`, `Record`,
  `RecordException`, `RecordBreakpointHit`, `WalkStack`, `DescribeFrame`, `DescribeExceptionType`
  (`:1497-1788`, `:2322-2344`). (6) Symbol and source services that never touch the debuggee after one
  module walk: `SearchMethods`, `ReadMethodSource`, `WhyNoModule`, `PositionsIn`, `NoMethodSource`,
  `SourceAt`, `SearchDetail`, `DescribeMatch`, `ModulePaths`, `EnumerateLoadedModules`, `RememberModule`
  (`:598-737`, `:1912-1969`, `:2213-2320`). A seventh, the inspection facade (`ReadFrames`,
  `ReadFrameVariables`, `Expand`, `ReadThreads`, `Evaluate`, `:1102-1230`), is already thin delegation to
  `CorDebugInspector` and shows the shape the others want.
- **Why it matters:** Every one of these shares `_gate`, so a change to binding has to reason about
  the detach window, and a change to the stop timers has to reason about module enumeration. The
  comment-carried contracts ("called with the gate held and the process stopped", "the walk happens
  before the lock", "outside the gate") exist because the type boundary that would make them structural
  does not. It also blocks the hot-reload work: an EnC apply needs (1), (4) and (5) and none of (3) or (6).
- **Suggested change:** Extract along the seams already named in the comments, the way `CorDebugInspector`
  was: a `RuntimeAttachment` that yields a `CorDebugProcess` and owns dbgshim; a `DetachProtocol` that
  takes the process and the binding list; a `BreakpointTable` that owns `BreakpointBinding` and binding
  against modules and exposes `Match(hit)`; a `StopController` that owns the stop record and both timers
  (see LIV-02); and a `TargetSymbols` that owns `_modulePaths` and the search/source verbs and needs the
  debuggee for exactly one enumeration. `CorDebugSession` becomes the callback dispatcher that composes
  them under the one gate. Do LIV-02 first; it shrinks the surface every extraction has to carry.

### LIV-02 The stop state machine is implicit in nine fields and five differently-spelled guards
- **Severity:** High
- **Effort:** M
- **Where:** `src/RoseMcp.LiveApp/Debugging/CorDebugSession.cs:145-181` (fields); guards at `:386`, `:772`,
  `:874-879`, `:997`, `:1217`, `:1331`, `:1799`, `:1936`; `CurrentStop` at `:840-857`
- **What:** Whether the target is running, held, detaching, detached or gone is spread over
  `_stoppedAtBreakpoint`, `_stoppedBindingId`, `_stoppedThread`, `_stoppedAs`, `_stopEventSequence`,
  `_stoppedAtUtc`, `_holdUntilUtc`, `_autoContinueSeconds`, `_autoContinueAtUtc`, `_detached`,
  `_detaching`, `_exited` and `_process is null`. The guards that read them are spelled five ways:
  `_process is null || _detached || _exited` (`:386`, `:1799`, `:1936`),
  `!_stoppedAtBreakpoint || _stoppedThread is null || _process is null || _detached || _exited` (`:772`,
  `:1217`), `!_stoppedAtBreakpoint || _process is null || _detached || _exited` (`:1331`),
  `!_stoppedAtBreakpoint` alone (`:844`, `:997`, `:1036`, `:1075`). The one at `:844` is wrong: `CurrentStop`
  does not consult `_exited`, so a target killed while held keeps reporting `StoppedAtBreakpoint` after
  its `ExitProcess` callback set `_exited` (`:1540-1544`). `LiveAppSessionHost.CurrentInfo` then reports
  `State = Ended` beside `Execution = StoppedAtBreakpoint` (`LiveAppSessionHost.cs:103-114`), and
  `WhyXamlIsUnservable` tells the caller to "resume the target and ask again" about a dead process
  (`:404-412`). The two methods called `Hold` mean different things: the private one holds the target
  at a stop (`:1681`), the public one is an operator's hold on the safety timer (`:993`).
- **Why it matters:** Wrong answers of the confident kind (a dead target described as stopped), and
  every new verb has to rediscover which of the five spellings applies to it. The "stop identity"
  fields (`_stopEventSequence`, `_stoppedAtUtc`, the two deadlines) are only meaningful together with
  `_stoppedThread` and the timers, and nothing but discipline keeps them in step across `Hold`, `Step`,
  `ContinueInternal`, `SetHold`, `ReleaseHold` and `TryDetachOnce`, each of which writes a different subset.
- **Suggested change:** One discriminated union swapped atomically under `_gate`:
  `TargetExecution = Running | Stopped(StopRecord) | Detaching | Detached | Exited`, where
  `StopRecord` is a small class owning the thread, the binding id, the sequence, the timestamps, the
  generation, the operator hold and *both timers*, with a `Dispose` that is today's `ClearStopTimers`.
  Every guard becomes a pattern match, so `CurrentStop` cannot forget `Exited` because the `Exited` arm
  has nothing to return. Rename the private `Hold` to `HoldAtStop` and the public one to
  `OperatorHold`. Add one integration test: kill the probe while it is held and assert
  `Execution == Running` and `State == Ended`.

### LIV-03 A breakpoint hit is attributed by method token alone, so two bindings in one method misreport
- **Severity:** High
- **Effort:** S
- **Where:** `src/RoseMcp.LiveApp/Debugging/CorDebugSession.cs:1628-1646` (`RecordBreakpointHit`),
  `:2142-2156` (`TryFunctionIdentity`), `:2374-2375` (`BreakpointBinding.Token`)
- **What:** A hit is matched to a binding on `entry.Token == token` and module path. `TryFunctionIdentity`
  reads the function token and module name from the `CorDebugFunctionBreakpoint` and nothing else. An
  `ILCode.CreateBreakpoint(offset)` (`:2062`) is also an `ICorDebugFunctionBreakpoint`, so a tracepoint at
  `Program.Beat` and a breakpoint at `Program.Beat@IL_0010` -- which is exactly the pairing
  `ReadMethodSource`'s positions invite (`:2250-2287`) -- both match the first binding in the list on
  every hit. The second binding's hit count never moves, its condition is never evaluated, and a hit
  meant to stop logs as a tracepoint (or a hit meant to log holds the target), reported with the wrong
  id.
- **Why it matters:** This is a wrong answer reported as success, in the one feature that exists to
  let an agent stop on a line rather than a method. No test sets two bindings in one method.
- **Suggested change:** Match on the breakpoint object, not its function: keep the
  `CorDebugFunctionBreakpoint` in the binding (it already is, `:2386`) and compare `hit.Breakpoint.Raw`
  against `binding.Breakpoint.Raw` (ClrDebug wraps the same COM pointer, so identity holds), falling back
  to `ICorDebugFunctionBreakpoint::GetOffset` plus token if identity ever fails. Add an integration test
  with a tracepoint at `Beat` and a stopping breakpoint at a `Beat@IL_xxxx` position, asserting each fires
  as itself.

### LIV-04 A failing callback handler continues the target silently
- **Severity:** Medium
- **Effort:** S
- **Where:** `src/RoseMcp.LiveApp/Debugging/CorDebugSession.cs:1530-1547`
- **What:** `OnEvent` defaults `shouldContinue = true`, calls `Record(e)`, and on any exception logs at
  Debug and continues. A throw inside `RecordBreakpointHit` or `Hold` -- a metadata read failing, a
  `DebugException` from `EnumerateChains` -- means the stop never happens, the event is never buffered,
  and the agent waiting on `rose_debug_events` for a `BreakpointHit` waits out its whole window.
- **Why it matters:** The event stream is the agent's only view; a hit that leaves no trace in it is
  indistinguishable from a breakpoint that was never reached, and Debug-level logging is not read
  mid-session.
- **Suggested change:** In the catch, `buffer.Append(SessionNotice, $"A {e.Kind} callback could not be
  recorded: {exception.Message}; the target was continued.")` so the loss is in the stream the agent
  reads. Make `Record` return a `CallbackOutcome { Continue, Hold }` enum rather than a bool, so the
  exception arm has to choose one explicitly (see inversions).

### LIV-05 Two paths hold `_gate` across `ICorDebugProcess::Stop`; a third releases it deliberately
- **Severity:** Medium
- **Effort:** M
- **Where:** `src/RoseMcp.LiveApp/Debugging/CorDebugSession.cs:1795-1846` (`BindAgainstLoadedModules`),
  `:1916-1969` (`ModulePaths` -> `EnumerateLoadedModules`), against `:884-888` (`Break`)
- **What:** `Break` says, correctly, "Outside the gate: this waits on the runtime to reach a point it can
  be stopped at, and holding the gate through it would block the very callbacks that get it there."
  `BindAgainstLoadedModules` and `EnumerateLoadedModules` do the opposite: they take `_gate` and call
  `_process.Stop(0)` inside it. They do not deadlock today because mscordbi treats `Stop` on an
  already-synchronised process as a stop-count increment (a callback in flight has the process
  synchronised before it is dispatched), but nothing in the code or the invariants says that this is
  what is being relied on, and `Break` says the opposite. `AddBinding` also async-breaks the whole
  target on every `rose_debug_set_breakpoint` (`:1291`, `:1805`) to enumerate modules it could already
  know.
- **Why it matters:** Lock discipline that contradicts itself is one refactor away from the wedge the
  detach path exists to avoid, and a full stop of somebody's app per breakpoint set is a cost the design
  does not need to pay.
- **Suggested change:** Keep the `CorDebugModule` objects, not only their paths: `RememberModule` (`:1902`)
  already sees every module on load, so a `Dictionary<string, CorDebugModule>` filled there (and once at
  attach, from the single enumeration the attach already does) lets `AddBinding` bind without stopping
  the target. For the remaining stops, one `WithSynchronizedTarget(Action)` helper that documents the
  stop-count contract and is the only place `Stop`/`Continue` pairs live.

### LIV-06 Two stack walkers disagree about honesty
- **Severity:** Medium
- **Effort:** S
- **Where:** `src/RoseMcp.LiveApp/Debugging/CorDebugSession.cs:1753-1788` (`WalkStack`, `DescribeFrame`)
  against `src/RoseMcp.LiveApp/Debugging/CorDebugInspector.cs:214-252` (`WalkFrames`);
  `src/RoseMcp.Contracts/LiveDebugEvent.cs` (`Frames` is `IReadOnlyList<string>`)
- **What:** The event-stream walker renders frames to strings and silently drops any frame whose
  function cannot be resolved (`DescribeFrame` returns null on any exception, `:1784-1786`). The
  inspector's walker counts them (`SkippedBefore`) with the comment "a stack silently missing three
  frames reads as a complete stack with a surprising caller". The stop event, which the decision
  `a-stop-captures-its-frame-when-it-happens.md` says is the agent's primary view, uses the dishonest one.
- **Why it matters:** The agent's captured stack can show `Main` calling `Beat` directly with the native
  transition and the runtime stub missing, and nothing says so. Two implementations of one walk will
  drift further.
- **Suggested change:** Build the event's frames from `_inspector.WalkFrames` + `DescribeStackFrame`
  and carry `LiveStackFrame` (which already has `SkippedBefore`, `Location`, `Source`) on
  `LiveDebugEvent` in place of strings, or at least render `SkippedBefore` into the string
  (`"[2 native frames]"`). Delete `WalkStack`/`DescribeFrame`.

### LIV-07 A request the host has timed out on still runs in the app, and the pipe cannot tell whose reply is whose
- **Severity:** Medium
- **Effort:** M
- **Where:** `src/RoseMcp.LiveApp/Xaml/XamlProviderPipe.cs:176-221` (`Request`), `:186-193` (stale drain);
  `src/RoseMcp.Xaml.Tap/tap_object.h:582-613` (`PipeReaderLoop`); decision
  `the-xaml-provider-is-injected-by-the-host-and-answers-on-a-pipe.md` ("a reply read from the pipe a
  request went out on is that request's answer by construction")
- **What:** `Request` writes a frame, waits `Snapshot` (15 s) for a reply, and returns null on expiry.
  The frame is already in the pipe; the provider's reader will serve it whenever the UI thread frees,
  and `selecthandle`, `select`, `deselect` and `apply` all mutate. The late reply is then read by the
  *next* `Request` and discarded as "stale" by position (`:186`). Correctness therefore depends on
  strictly alternating request/reply with no expiries, which is the assumption the pipe decision made
  and #208 is the case where it fails: `SelectTransientAsync` retries `selecthandle` up to 20 times
  (`LiveAppUwpTests.cs:1653-1668`), a timed-out attempt executes after the removal cleared the pick,
  and the hand-back check reads a pick the test was told did not happen. The invariant "a batch may not
  be retried" (`xaml-live-edit.md`) protects against double-sending; nothing protects against
  single-sending-late.
- **Why it matters:** A state-changing call that reported failure can succeed afterwards, which is the
  exact class of wrong answer the pipe was adopted to remove. It is also the structural cause of the
  #208 flake, so the test-suite flakiness is a protocol property showing through the fixture rather than
  a fixture bug (see LIV-15).
- **Suggested change:** Four bytes: a request id in the frame header, echoed in the reply, so `Request`
  matches by id and a late reply is dropped by identity rather than position. For mutating verbs, have
  the provider check an "abandoned" set before dispatching to the UI thread (the host sends `abandon <id>`
  on expiry), so a request the host gave up on does not mutate the app. Say in the tool result of a
  timed-out mutating verb that it may still land, until that is done.

### LIV-08 The wire format is versioned by column count and the greeting carries no identity
- **Severity:** Medium
- **Effort:** S
- **Where:** `src/RoseMcp.LiveApp/Xaml/XamlDiagnosticsSession.cs:1014-1018`, `:1056-1062`, `:980-983`;
  `src/RoseMcp.Xaml.Tap/tap_channel.h:158` (`"hello from the provider"`); `tap_tree.h:110-141`
  (`SnapshotRows`), `tap_properties.h:181-185`, `tap_edits.h:63-64`
- **What:** The host reads column 9 of a tree row and column 11 of a property row only if present,
  with comments explaining that an older provider in a recycled sandbox writes fewer columns. The
  provider greets with a fixed string. Row layout is duplicated as positional literals at both ends
  (`Line`/`Key` in the session, string concatenation in three headers) with nothing checking they agree.
- **Why it matters:** `hosts-and-deploy.md` already worries about an install whose host and provider
  disagree; today that disagreement is absorbed silently as "no address" or "no unrenderable flag"
  rather than refused by name. The next added column has to remember the length check at every parse
  site.
- **Suggested change:** The greeting carries `RoseTap/<protocol version>/<CLSID>`; the host refuses a
  provider whose protocol version it does not speak, naming both versions in the detail. Put the version
  and the column names in one `XamlWireFormat` constant in Contracts and a generated or hand-mirrored
  `tap_wire.h`, with a unit test that parses a fixture row the provider is known to emit.

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
- **Where:** `src/RoseMcp.LiveApp/LiveAppSessionHost.cs:402-413` (`WhyXamlIsUnservable`), `:464-509`
  (`ReadXamlTree`); `src/RoseMcp.Xaml.Tap/tap_object.h:249-257` (`tree` verb dispatches to the UI thread)
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
- **Where:** `src/RoseMcp.LiveApp/Debugging/CorDebugSession.cs:436-474` (`SettleForRelease` and the
  0xC0000409 fail-fast), `:896-906` (a breakpoint arriving during `Break` -> `GiveBackStop`), `:171-175`
  and `:1316-1326` (the timer generation race), `:1497-1528` (callbacks during the detach window)
- **What:** Each of these encodes a race that was found by a target dying and is now held only by a
  comment. The detach tests detach from an idle target or one held at a breakpoint
  (`LiveAppSessionTests.cs:242-338`); none detaches while the probe is hitting a breakpoint in a loop,
  which is the case `SettleForRelease` exists for. `Break` racing a breakpoint has no test. The
  generation guard has no test that fires a stale timer.
- **Why it matters:** These are the parts of the debugger most likely to be "simplified" by someone
  reading the code without the history, and the failure mode is a dead application rather than a red
  test.
- **Suggested change:** `DebugProbeTarget` already loops at 5 Hz; add a `--fast` flag that loops without
  the sleep, set a tracepoint on `Beat` and detach under load, asserting the target survives. Add a
  `Break` test that sets a breakpoint first so the two stops race. For the timer, expose
  `ContinueInternal(ResumeCause, generation)` to an internal test through
  `RoseMcp.IntegrationTests.Windows`, which already references the host as a library.

### LIV-14 The shared-app suite is sound in shape; its retries hide product defects
- **Severity:** Medium
- **Effort:** S
- **Where:** `tests/RoseMcp.IntegrationTests/UwpProbeApp.cs:369-406` (`LaunchSharedAsync`, three attempts
  with backoff), `:315-366` (`UsableAsync`/`IsTickingAsync`), `:487-533` (`SessionTurn.DisposeAsync`);
  `tests/RoseMcp.IntegrationTests/ProbeConstraints.cs:26-78`; `LiveAppUwpTests.cs:1653-1668`
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
  reason. Once LIV-07 and LIV-12 land, narrow `ProbeKeys.LiveApp` and measure. Make
  `SelectTransientAsync` select through the tree once the element is present and assert on a single
  attempt, so a timed-out select is a failure rather than a retry.

### LIV-15 Ninety-eight `catch` sites, forty-two of them `catch (Exception)` to null
- **Severity:** Low
- **Effort:** M
- **Where:** `src/RoseMcp.LiveApp/Debugging/CorDebugInspector.cs:52`, `:301`, `:313`, `:335`, `:383`,
  `:677`, `:707`, `:719`, `:806`, `:836`, `:871`, `:883`; `CorDebugSession.cs:1784`, `:1892`, `:2039`, `:2340`;
  `ValueReader.cs:75`, `:87`, `:123`, `:148`; `src/RoseMcp.Symbols/MethodTokens.cs:68`, `:84`, `:114`, `:152`,
  `:182`, `:213`; and the rest as listed by `grep -rn "catch (Exception)"` over the three projects
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

### LIV-16 Six XAML verbs and eight inspection verbs repeat the same preamble in the host
- **Severity:** Low
- **Effort:** S
- **Where:** `src/RoseMcp.LiveApp/LiveAppSessionHost.cs:464-690` (six copies of "take the gate, read pid
  and `_xaml ??=`, refuse with no target, refuse if held, call, attach heartbeat"), `:248-373` (eight
  copies of "if not attached return an empty result with `NotAttachedDetail`")
- **What:** The refusal shape, the heartbeat attachment and the `_xaml ??= new` are typed out per verb.
  A new verb that forgets `WhyXamlIsUnservable` reintroduces the twenty-second wait the guard was added
  to remove.
- **Why it matters:** This is the pattern that decides whether a future `rose_xaml_*` verb inherits the
  debugger coupling or not, and today it is inherited by copy.
- **Suggested change:** `TResult WithXaml<TResult>(Func<XamlDiagnosticsSession, int, TResult> verb,
  Func<string, TResult> refused)` that does the preamble once and stamps the heartbeat; the same for
  `WithStoppedTarget`. Each verb becomes one line, and the guard cannot be forgotten.

### LIV-17 `XamlStackProbe.Detect` runs on every poll for the life of a non-XAML session
- **Severity:** Low
- **Effort:** S
- **Where:** `src/RoseMcp.LiveApp/LiveAppSessionHost.cs:81-145` (`CurrentInfo`, `ResolveXamlStack`);
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
- **Where:** 42 hits for `used to|previously|no longer` across the three C# projects and the tap headers;
  representative: `CorDebugSession.cs:304`, `:1237`; `XamlProviderSession.cs:141`, `:240`, `:365`, `:567`;
  `XamlStackProbe.cs:13`; `XamlApplyBaseline.cs:7`; `tap_object.h:489`, `:732`; `tap_overlay.h:615`, `:1089`;
  `XamlProviderPipe.cs:14` ("D14 chose files in an ACL'd folder", a decision number);
  `RoseMcp.Xaml.Uwp.Tap.cpp:7-20` (describes the file channel the pipe replaced)
- **What:** CLAUDE.md forbids "used to", "previously", "no longer" and decision numbers in comments. The
  rule is broken forty-two times in this scope, and the UWP binding's header comment still describes
  `commands.tsv`/`tree.tsv`, a channel that no longer exists.
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
  `MethodTokens.cs:219` (`Read`); called per module per binding from `CorDebugSession.BindModule`
  (`:2015-2018`) on mscordbi's callback thread
- **What:** `For` does a `FileInfo` stat per call and `DeclaresType` scans `TypeDefinitions` per call.
  `BindModule` runs `DeclaresType` over every remembered module for every unbound binding on every module
  load, so a location written without its assembly costs O(modules squared) stats and type-table scans
  during the target's startup, with the target stopped.
- **Why it matters:** It is on the callback thread, so it is a startup slowdown in somebody's
  application proportional to the square of its module count. The cache invalidation is right; the
  granularity is per question rather than per module.
- **Suggested change:** Compute a `HashSet<string>` of declared full type names once per `ModuleSymbols`
  (it is already the unit of invalidation), and rate-limit the stamp check to once per second per path.

## Pit-of-success inversions

1. **Rule today:** "every guard checks `_stoppedAtBreakpoint`, `_process`, `_detached`, `_exited` in the
   right combination" (five spellings). **Mechanism:** a `TargetExecution` union with a `StopRecord`
   arm owning the thread, sequence, hold and both timers; every read is a pattern match the compiler
   completes, and `Exited` cannot be forgotten because its arm has no stop to return. (LIV-02)
2. **Rule today:** "a callback handler returns false to hold and true to continue; on exception,
   continue". **Mechanism:** `Record` returns `CallbackOutcome { Continue, Hold }` and the catch arm has
   to construct one, so the silent-continue path is a visible choice with a `SessionNotice` beside it.
   (LIV-04)
3. **Rule today:** "every public XAML entry takes `_requests` once and calls a `Core` that assumes it is
   held; a `Core` must never take the lock" (comment at `XamlDiagnosticsSession.cs:64-68`).
   **Mechanism:** the `Core` methods and every `XamlProviderSession` method take a `HeldRequests` token, a
   `readonly ref struct` only the lock wrapper can construct. The session already does this for the
   debugger side with `StoppedTarget`; make it the same shape on the XAML side.
4. **Rule today:** "a read may fall back to the other channel and a batch may not" (asymmetry to preserve,
   `xaml-live-edit.md`); "a mutating request that timed out may still run" (LIV-07). **Mechanism:** split
   `XamlProviderPipe.Request` into `Query(verb)` and `Command(verb)`, where `Command` stamps an id, never
   retries, and on expiry sends `abandon <id>`; the verbs are an enum with a `Mutates` flag so a new verb
   has to say which it is.
5. **Rule today:** "the tap's tier purity is checked by include order and by nothing else"
   (`tap-tiers.md`: "not currently checked by a test"). **Mechanism:** a compile-only translation unit per
   tier in each `build.ps1` (`tap_tier2_check.cpp` includes `tap_channel.h` through `tap_object.h` with no
   projection headers and no aliases defined), so a violation fails the build in a file named for the
   tier rather than being absorbed by moving an include.
6. **Rule today:** "an older provider writes fewer columns; check the length" at every parse site.
   **Mechanism:** a protocol version in the greeting, refused by name on mismatch, and one
   `XamlWireFormat` that both ends are generated from or tested against. (LIV-08)
7. **Rule today:** "comments are self-contained and present tense; no `used to`, no decision numbers".
   **Mechanism:** a CI step that greps `src` for `\b(used to|previously|no longer|for now|until now)\b|\bD[0-9]{1,2}\b|§`
   with an allowlist, failing on new hits. Forty-two today. (LIV-20)
8. **Rule today:** "a launch that faults once is retried three times in the fixture" (`UwpProbeApp.cs:385`).
   **Mechanism:** the fixture counts attempts and the assembly-level teardown fails the run if any launch
   needed more than one, with the faulted detail, so a product race cannot be green. (LIV-14)

## Open questions for Steve

1. `Refuses_a_xaml_request_while_the_target_is_stopped` removes the breakpoint the target is held at and
   then continues (`LiveAppUwpTests.cs:1740-1741`), while `TryDetachOnce`'s comment says removing a patch
   under a parked thread fail-fasts the debuggee (`CorDebugSession.cs:372-375`). Is `RemoveBreakpoint` on
   the held breakpoint known safe because `Continue` fixes the thread up and only `Detach` does not? A
   sentence reconciling the two would stop the next reader "fixing" one of them.
2. Has `BindAgainstLoadedModules`' `Stop(0)` under `_gate` ever been seen to block? Is the reliance on
   mscordbi treating `Stop` on a synchronised process as a stop-count increment deliberate? (LIV-05)
3. Has a WinUI 3 brush or margin live edit ever been observed to land? (LIV-09)
4. For #208, was the host log of a failing run checked for a `selecthandle` that timed out, as the issue
   proposes? If so the LIV-07 mechanism is confirmed rather than inferred.
5. Is `DllCanUnloadNow` returning `S_OK` intentional? (LIV-11)
6. Were `SetDesiredNGENCompilerFlags` / `SetJITCompilerFlags(CORDEBUG_JIT_DISABLE_OPTIMIZATION)` left out
   deliberately? `DebugProbeTarget` compensates with `MethodImplOptions.NoOptimization` (`Program.cs:62-64`),
   which suggests optimised frames lose locals in real targets too.
7. `LiveAppSessionState` and `LiveExecutionState` are orthogonal by design (`LiveExecutionState.cs:4-6`).
   Is the `Ended` + `StoppedAtBreakpoint` pairing (LIV-02) something the inspector has ever shown?

## Hot-reload relevant facts

What exists today, and what does not, with the lines the hot-reload reviewer can build on. Grep over
`src` for `ApplyChanges|SetJITCompilerFlags|SetDesiredNGENCompilerFlags|MODIFIABLE_ASSEMBLIES|CORDEBUG_JIT|MetadataUpdater|ApplyUpdate|STARTUP_HOOKS|StartupHook`
returns nothing in `RoseMcp.LiveApp`; the only hits are Roslyn's unrelated `ApplyChangesOperation` in
the worker.

**How the target is launched or attached.**
- Plain executable: `src/RoseMcp.LiveApp/Debugging/CorDebugSession.cs:226-240`, `Launch` calls dbgshim's
  `CreateProcessForLaunch(commandLine, bSuspendProcess: true, IntPtr.Zero, workingDirectory)`. The third
  argument is `lpEnvironment` and is **`IntPtr.Zero`**: the target inherits the host's environment
  unchanged. Nothing sets `DOTNET_MODIFIABLE_ASSEMBLIES`, `DOTNET_STARTUP_HOOKS` or anything else on the
  target. The host's own environment is the broker's (it is started by `StdioClientTransport`,
  `src/RoseMcp.Broker/LiveAppSession.cs:93-102`), so setting a variable on the broker would leak to every
  target, which is the wrong grain.
- Startup attach: `:261-299`, `AttachAtSuspendedStartup` arms `GetStartupNotificationEvent(pid)`, resumes,
  waits, then `FindRuntimeWithRetry` -> `CreateCorDebug` -> `DebugActiveProcess(pid, win32Attach: false)`
  -> sets the runtime's continue event. The target is under debug before its first managed instruction,
  which is early enough to observe every module load.
- Running-process attach: `:202-219`, `Attach` -> `DebugActiveProcess`. Modules already loaded are
  enumerated lazily by `EnumerateLoadedModules` (`:1934-1969`) or eagerly by `BindAgainstLoadedModules`
  (`:1795-1846`), both via `process.AppDomains -> Assemblies -> Modules` (`:2128-2140`).
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
- `CreateCorDebug` (`CorDebugSession.cs:1465-1495`): `CreateDebuggingInterfaceFromVersionEx(CorDebugVersion_4_0,
  version)` (or the by-hand `CoreCLRCreateCordbObject3` in `RuntimeDiscovery.cs:73-102` when dbgshim
  folds the failure), then `Initialize()`, then `SetManagedHandler(callback)`. There is **no**
  `ICorDebugProcess2::SetDesiredNGENCompilerFlags`, **no** `ICorDebugModule2::SetJITCompilerFlags`, and
  therefore no `CORDEBUG_JIT_ENABLE_ENC` or `CORDEBUG_JIT_DISABLE_OPTIMIZATION`. Targets JIT optimised
  code unless their own build says otherwise, which is why the probe uses `MethodImplOptions.NoOptimization`
  (`tests/DebugProbeTarget/Program.cs:62-64`) to keep a local alive for the tests.
- The only EnC reference anywhere is `HRESULT.CORDBG_E_DETACH_FAILED_ON_ENC` in `IsRefusal`
  (`CorDebugSession.cs:549`), so a detach after an applied edit is already classified as a refusal rather
  than retried; `ReleaseForDetach` (`:500-535`) would need to learn how to end an EnC session.

**Module-load hook that could record baselines.**
- `Record` handles `LoadModuleCorDebugManagedCallbackEventArgs` at `CorDebugSession.cs:1575-1578`,
  appending a `ModuleLoaded` event and calling `BindModule(loaded.Module)` (`:1981-2029`), which calls
  `RememberModule` (`:1902-1910`). `RememberModule` keeps only the **path** in `_modulePaths`; the
  `CorDebugModule` object is dropped. Hot reload needs the `ICorDebugModule` to call
  `ICorDebugModule2::ApplyChanges(metadataDelta, ilDelta)`, so this is where a
  `Dictionary<string, CorDebugModule>` would be filled (LIV-05 wants the same map for a different
  reason). The callback runs with the target stopped, so reading the module's metadata for a baseline
  is safe here.
- `FileOf` (`:1884-1896`) already filters dynamic and in-memory modules, which EnC cannot target.

**Stop and resume infrastructure an apply would reuse.**
- `Break(int?)` (`:868-922`) produces a stop of the same shape as a breakpoint's, on the safety timer;
  `ContinueInternal` (`:1327-1380`) resumes. An EnC apply needs the process synchronised; `Break` gives
  that, and `WithSynchronizedTarget` (suggested in LIV-05) would be the natural wrapper.
- The `_gate` and callback discipline (`OnEvent`, `:1497-1562`) means the apply would run on a tool
  thread under `_gate` with the target stopped, which is also where `BindAgainstLoadedModules` runs today.

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
- The apply pipeline (`XamlDiagnosticsSession.ApplyEditsCore`, `:603-756`) is the model for "compute
  edits outside the target, send a batch, get per-edit outcomes, never retry a mutating batch".

**What has no precedent here.**
- There is no managed in-process agent path. The tap is a native DLL loaded by the framework's own
  `InitializeXamlDiagnosticsEx` (`XamlProviderSession.cs:292-311`), not by generic injection; there is no
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
