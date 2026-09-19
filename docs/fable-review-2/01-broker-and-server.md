# Broker and Server

**Scope.** Every source file in `src/RoseMcp.Broker` (including `Tools/`) and `src/RoseMcp.Server`, read in full; all of `src/RoseMcp.Solutions`, `src/RoseMcp.Settings`, `src/RoseMcp.Logging`; from `src/RoseMcp.Contracts`: `ToolNames`, `WorkspaceScopedResult`, `WorkspaceSummary`, `WorkerActivity`, `OperatorRequests`, `LiveAppSessionSummary`, `HostVersion`, `WorkerInfo`. The Tray's `App.xaml.cs` and the Worker's and LiveApp's `ToolErrorReporting.cs`, read only to answer questions about the broker's contract with its hosts. Tests: `BrokerTests`, `RelayTests`, `WorkspaceRoutingTests`, `ResilienceTests`, `OperatorApiTests`, `ToolParityTests` (integration); `ToolSurfaceTests`, `SecurityModelTests`, `LoggingTests`, `RelayRetryPolicyTests` (unit). Docs: `CLAUDE.md`, the three invariant files named in the brief, `docs/debug/security-model.md`, `docs/decisions/the-inspector-is-a-client-of-the-broker.md`, `docs/decisions/a-session-detaches-before-its-host-is-closed.md`. GitHub issues #157, #213, #214.

**Verdict.** Adequate overall, with a strong core and fragile edges. The part of the broker that was designed rather than accreted -- `WorkspaceFor`'s single ordering, attribution in one place, the request filters that carry origin, session and error messages so no tool can forget them, and the tests that assert the tool surface against the registration and the security document -- is genuinely strong and is the thing to preserve through any refactor. The Roslyn broker is a clean layer: no Roslyn reference, routing and supervision only, and the tool classes are thin forwarders. The fragility is in lifetime and in the seams. One global semaphore serialises every call on every workspace behind any worker's start; a dead live-app session is never evicted and is polled forever; orderly teardown depends on each host disposing the DI container and the tray does not; and the two open bugs the brief names (#213, #214) are both in the relay-to-broker seam and both confirmed from the code. The live-app half of the broker is less clean than the Roslyn half: `LiveAppSession` carries XAML element-resolution logic that the decision record says belongs in the host, and `LiveAppDebugTools` carries the attach policy and session-lifecycle handling that belongs in the manager. Comments are excellent at recording *why* but a noticeable fraction record *what it used to be*, against the repository's own convention. The tests cover the routing core well and the resilience paths honestly (several say in their own remarks what they cannot assert); they do not cover Roslyn-half argument parity, eviction, or the tray's shutdown path.

## Strengths

- **One ordering decides routing, and it is public and cheap to test.** `WorkspaceManager.WorkspaceFor` (`src/RoseMcp.Broker/WorkspaceManager.cs:333-384`) is the whole routing rule, exposed so `WorkspaceRoutingTests` can exercise every branch without starting a worker. `WorkspaceHints` (`src/RoseMcp.Broker/WorkspaceHints.cs`) replaced seventeen hand-written `workspace ?? filePath` chains with a declaration. Keep both exactly as they are; every finding about routing below is a refinement of this design, not a replacement.
- **Attribution and cross-cutting facts live in one place each.** `Attribute<T>` (`WorkspaceManager.cs:188-197`) stamps every result; `WithCallOrigin`, `WithToolErrorMessages`, `WithLeanListing` (`src/RoseMcp.Broker/ServiceCollectionExtensions.cs:164-196`) are request filters, so a tool added tomorrow gets origin, session ownership, real error messages and a trimmed listing without anyone remembering. This is the pit-of-success pattern the rest of the review asks for, already in use.
- **The surface is asserted, not assumed.** `ToolSurfaceTests` pins the exact tool list per OS, the read-only set, the host-internal names that must never be advertised, and that every tool is routed by the instructions or exempted by name. `SecurityModelTests` fails if a tool ships without an entry in `docs/debug/security-model.md`. `ToolBudgetTests` bounds description length. This trio is the model for every inversion proposed below.
- **`SolutionResolver` refuses rather than guesses, and logs the contest.** Containment then pin (`src/RoseMcp.Broker/SolutionResolver.cs:157-187`), `SolutionChoice.Reason` logged only when contested (`WorkspaceManager.cs:413-428`), and `AmbiguousSolutionException` as an `McpException` so the candidates reach the caller. The invariant and the code agree.
- **Cancellation is propagated in the right order.** `CancellableToolCall` (`src/RoseMcp.Broker/CancellableToolCall.cs:61-95`) sends `notifications/cancelled` before abandoning the wait, with the http teardown race recorded as the reason. `BrokerTests.Cancelling_a_call_cancels_it_at_the_broker_and_leaves_the_worker_usable` covers the path and says honestly what it cannot time.
- **Worker state is derived, not asserted.** `WorkspaceWorker.State` (`src/RoseMcp.Broker/WorkspaceWorker.cs:98-111`) computes from `ExitReason`, `LastStatus` and `_loadFailure`; exit is observed by `Process.Exited` rather than discovered on the next call; memory is sampled from the process table so a wedged worker still reports. `A_crashed_worker_is_noticed_without_being_called` guards the exit watch with a remark explaining why the first draft passed against the un-fixed code.
- **`ActivityLog` is correct under concurrency.** Immutable snapshots leave the log, first outcome sticks, a missing total is null rather than zero, and the reasoning for each is recorded (`src/RoseMcp.Broker/ActivityLog.cs:126-138, 141-153, 214-222`).
- **`RelayRetryPolicy` learns from the tool list rather than hard-coding.** `src/RoseMcp.Server/RelayRetryPolicy.cs` reads `ReadOnlyHint` off the listing the relay forwards anyway; unknown is no. Unit-tested in four cases.
- **Logging has rules and tests for them.** Session files claimed with `FileMode.CreateNew` so two workers cannot share one (`src/RoseMcp.Logging/RoseLogFile.cs:96-122`), pruning grouped by session, UTC in name and line, and `Writes_nothing_at_all_to_stdout` guards the stdio invariant directly.
- **`OperatorApi` gates before binding and maps exception kinds to statuses deliberately.** Middleware on the path branch (`src/RoseMcp.Broker/OperatorApi.cs:37-62`), `Failures` filter (`OperatorApi.cs:319-352`), and `ForOperator` kept separate from `Find` with the reason written down (`src/RoseMcp.Broker/LiveAppSessionManager.cs:112-129`).
- **`PathCasing` puts the platform question in one place** (`src/RoseMcp.Solutions/PathCasing.cs`), with the four places that had assumed Windows named in its summary.

## Findings

### BRK-01 A relative path hint is resolved against the process directory, so a write lands in another checkout
- **Severity:** High
- **Effort:** M
- **Where:** `src/RoseMcp.Broker/WorkspaceManager.cs:345-365`, `src/RoseMcp.Broker/SolutionResolver.cs:36-38`
- **What:** `WorkspaceFor` tests each hint with `File.Exists(path)` (line 351) and hands it to `SolutionResolver.Choose`, which calls `Path.GetFullPath(path)` (line 38). Both resolve a relative path against `Environment.CurrentDirectory` of the *broker process*. The calling session's directory, `CallOrigin.Directory`, is read only at line 367, after the hints have already been tried. For a relayed session in a worktree, a relative `filePath` that also exists in the main checkout (which is where the tray happens to be running from, or any directory the broker started in) resolves by containment to the wrong solution and wins the ranking outright. This is issue #214 exactly: `rose_add_using` with `tests/.../LiveAppSessionTests.cs` edited the main checkout while `rose_add_file` with a not-yet-existing path fell through to the origin and landed correctly.
- **Why it matters:** A write to a repository the caller is not in, reported as success, with the path in the result as the only evidence. Every other failure in the issue list is a wrong answer; this one is a wrong side effect. Six worktrees of one repository is the ordinary case here.
- **Suggested change:** Rebase before ranking. A relative hint is a fact about where the caller stands, so make it absolute against `CallOrigin.Directory ?? _options.DefaultWorkspaceRoot` before the existence check and before `Choose`. Better, do it in the type: `WorkspaceHints.From` should produce absolute paths given an origin, so `WorkspaceFor` never sees a relative string (see inversion 6). Add the second guard the issue proposes: when a hint was inferred (not the `workspace` argument) and resolves to a solution that does not enclose the origin directory, refuse and name both, on the same reasoning as "loaded workspaces are named in the failure and never used as an answer". Test: two fixture copies, `CallOrigin.Use(worktree)`, a relative path present in both, assert the worktree wins.

### BRK-02 `ROSEMCP_TOKEN` silently defeats the relay, and the log says the tray is absent
- **Severity:** High
- **Effort:** S
- **Where:** `src/RoseMcp.Server/TrayRelay.cs:116-129`, `src/RoseMcp.Server/TrayRelay.cs:302-308`, `src/RoseMcp.Server/Program.cs:145`
- **What:** `IsListeningAsync` probes `GET /admin/workspaces` with a bare `HttpClient` and treats any non-success as "no tray". `ConnectAsync` builds `HttpClientTransportOptions { Endpoint = endpoint }` with no headers. When the http server was started with `ROSEMCP_TOKEN`, `RequireToken` gates the whole pipeline (Program.cs:145), the probe gets a 401, and the stdio session starts private workers with the log line `No tray at ...; this session will own its workers`, which is false. `ServerOptions.Validate` already reads the variable (ServerOptions.cs:83) so the process knows it exists; the relay never asks. Issue #213, confirmed.
- **Why it matters:** The user loses both things relaying exists for -- one warm worker per solution and a correct answer to "which workspace" -- and nothing names the token. A second Roslyn copy of a large solution is the exact cost the relay was built to avoid. The tokened http server is the *only* supported way to bind off loopback, so this is not an exotic configuration.
- **Suggested change:** Read `ROSEMCP_TOKEN` in `TrayRelay` (one static, shared with `OperatorToken.FromEnvironmentOrMint`), send `Authorization: Bearer` on the probe and via `AdditionalHeaders` on the transport. Separate the two outcomes in `IsListeningAsync`: return a three-state (`Absent`, `Refused`, `Listening`) and log "listening and refused this session (401); set ROSEMCP_TOKEN in the session's environment" for the middle one. A `RelayTests` case starting the tokened server through `RoseServerProcess.StartWith` (the `OperatorApiTests` fixture already does this) and asserting the tray holds the worker.

### BRK-03 One semaphore serialises every call on every workspace behind any worker's start
- **Severity:** Medium
- **Effort:** M
- **Where:** `src/RoseMcp.Broker/WorkspaceManager.cs:43`, `:83-114`, `:121-141`, `:259-277`
- **What:** `GetOrStartResolvedAsync` takes `_gate` before even checking whether an alive worker exists, and holds it across `StartAsync`, which spawns the process and awaits the MCP handshake -- bounded only by `WorkerHandshakeTimeout`, three minutes by default (`BrokerOptions.cs:40`), and documented as legitimately slow when several cold workers compete. Every `CallAsync` goes through `GetOrStartAsync`, so a `rose_find_references` on solution A waits behind solution B's handshake. `CloseResolvedAsync` holds the same gate across `worker.DisposeAsync()`. The comment at line 28-33 correctly explains why the dictionary is concurrent and what the gate makes atomic; it does not mention that the fast path pays for it too.
- **Why it matters:** In the tray -- the shared, multi-session host the relay exists for -- one agent opening a large solution stalls every other agent on the machine for the length of a handshake. It reads as Rose being slow on a solution that is already warm, which is the reflex-to-grep failure CLAUDE.md is most worried about.
- **Suggested change:** Per-key coordination. Replace `ConcurrentDictionary<string, WorkspaceWorker>` with `ConcurrentDictionary<string, Lazy<Task<WorkspaceWorker>>>` (or `AsyncLazy`), so the alive fast path is a lock-free `TryGetValue` and a start blocks only callers of that same solution. Keep a gate per solution for the check-dispose-replace sequence. If serialising *starts* across solutions is deliberate (to stop design-time builds competing), make that a separately named `SemaphoreSlim _starting` with that reason on it, and keep it off the fast path. `BrokerTests.Reuses_one_warm_worker_across_calls` should gain a sibling: open A, begin opening B with a slow handshake, assert a call on A completes before B's handshake does.

### BRK-04 An ended live-app session is never evicted and is polled forever
- **Severity:** Medium
- **Effort:** S
- **Where:** `src/RoseMcp.Broker/LiveAppSessionManager.cs:235-256`, `src/RoseMcp.Broker/LiveAppSession.cs:121-138`
- **What:** `RefreshInfoAsync` sets `_alive = false` on a transport failure and `Describe` reports `Ended`, but nothing removes the session from `_sessions` or disposes its `McpClient`. `RefreshLoopAsync` iterates `Sessions` every second and calls `RefreshInfoAsync` on the dead client, which fails the same way, indefinitely. Only `CloseAsync`/`CloseForOperatorAsync` (a caller's explicit act) or manager disposal removes anything. Issue #157 makes the same observation about workers ("a worker lives until its broker does"); for sessions it is worse because a dead one is also actively polled.
- **Why it matters:** A tray that has been up for a day carries every session any agent ever started, each costing a failed round trip per second and a row in `GET /admin/sessions` and the inspector. The `Ended` state is honest, but "ended and still here" is a state nothing acts on.
- **Suggested change:** Make eviction the manager's job. On the `_alive` transition, the manager removes the session after one more `Describe` cycle (so a window sees `Ended` once), disposes the client, and records the eviction in `Activities`. For workers, the same shape answers #157: an idle timer per worker, eviction said in the activity log, and `rose_workspace_list` so a session can see what is warm. Test: attach to a child process, kill the child's host, assert the session leaves `Describe()` within a few ticks and the poll stops.

### ~~BRK-05 `WorkerLauncher` still has the stale-binary trap the other two launchers fixed~~
**Done, PR #295.** One `RepositoryBuildOutput.Find`, used by all three launchers, carrying
configuration-then-architecture-then-recency once; the two duplicate `ConfigurationOf` copies and
the worker's recency-only search are gone. `RepositoryHostBuildTests` now stages a Release worker
against a Debug broker, which is the case the worker had no protection from and which every Roslyn
test drives. The reasoning is in `RepositoryBuildOutput`'s own summary, where the comments the two
launchers carried separately are now stated once.

### BRK-06 Three definitions of "the far side is gone", one by matching an assembly name string
- **Severity:** Medium
- **Effort:** M
- **Where:** `src/RoseMcp.Broker/WorkspaceWorker.cs:489-493`, `src/RoseMcp.Broker/LiveAppSession.cs:149-151`, `src/RoseMcp.Server/TrayRelay.cs:334-340`; and `src/RoseMcp.Broker/ToolErrorReporting.cs`, `src/RoseMcp.Worker/ToolErrorReporting.cs`, `src/RoseMcp.LiveApp/ToolErrorReporting.cs`
- **What:** Each `IsTransportFailure` differs: the worker's includes `ClientTransportClosedException` and `InvalidOperationException { Source: "ModelContextProtocol.Core" }`; the live-app session's checks `InnerException` and includes neither; the relay's adds `HttpRequestException` and `OperationCanceledException`. Two of them decide liveness by comparing `Exception.Source` against the SDK's assembly name, which changes when the package is restructured and would then silently reclassify a dead worker as a bad request (no retry, no replacement). `ToolErrorReporting` exists three times, nearly identical, with the LiveApp copy explaining that Contracts must stay package-free so nothing can be shared.
- **Why it matters:** The retry decision in `WorkspaceManager.CallAsync` and the reconnect decision in `TrayRelay.InvokeAsync` both turn on these predicates, and the invariant says the exception *type* is what carries meaning inward. Three hand-kept lists that disagree is how one boundary heals from a dropped socket and the next one shrugs -- which is precisely the shape of the bug `RelayTests.A_relayed_call_says_why_it_failed_every_time_the_tray_is_gone` was written for.
- **Suggested change:** A small `RoseMcp.Mcp` library referencing only `ModelContextProtocol`, holding `TransportFailure.Is(Exception)` (one predicate, inner-exception aware, unit-tested against constructed exceptions), `ToolErrorReporting`, `CancellableToolCall` and `ForwardedError`. Broker, Server, Worker and LiveApp all reference it; Contracts stays package-free. If the `Source` heuristic is genuinely needed, it lives in one place with a test that fails when the SDK's assembly name changes.

### BRK-07 Orderly teardown is the host's discipline, not the broker's guarantee
- **Severity:** Medium
- **Effort:** S
- **Where:** `src/RoseMcp.Broker/ServiceCollectionExtensions.cs:120-125`, `src/RoseMcp.Broker/LiveAppSession.cs:663-699`, `src/RoseMcp.Broker/WorkspaceManager.cs:430-440`; evidence in `src/RoseMcp.Tray/App.xaml.cs:161-166`
- **What:** `WorkspaceManager` and `LiveAppSessionManager` are plain singletons implementing `IAsyncDisposable`. Their disposal -- which for a live-app session is the detach-before-close ordering that `docs/decisions/a-session-detaches-before-its-host-is-closed.md` exists for -- runs only if the host disposes the container. The stdio server does (`Host.RunAsync` disposes on exit, and `A_stdio_server_exits_when_its_client_closes_stdin` proves the worker goes). The tray calls `_broker.StopAsync()` then `Exit()` and never `DisposeAsync()`, so on an orderly quit neither manager is disposed: no detach call is made, workers die only because process exit closes their stdin pipes, and whether an attached target survives depends on the LiveApp host's own stdin-close handling rather than on the broker's stated ordering. The decision record lists "a host killed outright, or a broker that crashes" as the cases it cannot cover; an orderly tray quit is a third case it does not mention.
- **Why it matters:** The decision is load-bearing -- a debuggee dies with a debugger that did not detach -- and it is enforced only on the host that was tested. The broker owns the sessions; it should own their end.
- **Suggested change:** Register an `IHostedService` in `AddRoseMcpBroker` whose `StopAsync` disposes both managers (or make the managers implement `IHostedService` themselves). Then `StopAsync` is sufficient in every host, and the ordering is the library's. The 05 reviewer should separately note the tray's missing `DisposeAsync`; this finding is about not needing it.

### BRK-08 The http pipeline is wired twice, once per host
- **Severity:** Medium
- **Effort:** S
- **Where:** `src/RoseMcp.Server/Program.cs:143-159`, `src/RoseMcp.Tray/App.xaml.cs:84-110`
- **What:** `AddRoseMcpBroker` is the one registration path for *services*, and the comment on it (ServiceCollectionExtensions.cs:13-19) says that is the reason the broker is a library. The *pipeline* is not unified: Origin refusal (inline lambda in the tray, `RefuseForeignOrigin` in the server), `MapMcp`, `/admin/workspaces`, `/admin/sessions` and `MapRoseOperatorApi` are each written out in both hosts, with the tray's comments saying "exactly as the http server maps them" and "two hosts serving the same broker should not answer different questions". Only `MapRoseOperatorApi` was pulled into the library.
- **Why it matters:** The next admin endpoint, or the next security middleware, is added to one host and forgotten in the other, and the tray -- which no test can host -- is the one that will be forgotten. `/admin/sessions` was evidently added to both by hand; the comments are the evidence that a person remembered.
- **Suggested change:** `app.MapRoseBroker(OperatorToken token, bool requireTokenEverywhere)` in the broker: Origin middleware, optional global token, `MapMcp`, the two admin routes, the operator API. Each host becomes one line plus its own logging and presenter. `OperatorApiTests` then covers the tray's pipeline by construction.

### BRK-09 `LiveAppDebugTools` carries session-lifecycle logic that belongs in the manager
- **Severity:** Medium
- **Effort:** S
- **Where:** `src/RoseMcp.Broker/Tools/LiveAppDebugTools.cs:45`, `:54-66`, `:122-133`, `:163-174`; `src/RoseMcp.Broker/LiveAppSessionManager.cs:143-182`
- **What:** `LocalAttachPolicy.EnsureAttachable` is called from the tool method and nowhere else (`rose_find_references` reports one caller). `LiveAppSessionManager.StartAsync` accepts an `AttachProcess` target without checking it, so any other caller -- the operator API if it grows an attach, or the fifty-odd test call sites -- bypasses the policy the security model describes as the gate on `rose_debug_attach`. The "started, but Faulted, so close it and throw the detail" block is written three times, once per launch kind, differing only in the fallback sentence. `ShowInspectorIfWanted` reads `RoseSettingsFile` from the tool layer.
- **Why it matters:** CLAUDE.md's rule for the broker is "routing and supervision only", and the Roslyn tools honour it: `BrokerAnalysisTools` is a pure forwarder. The debug tools are where policy has leaked into the presentation layer, which is the layer no unit test reaches.
- **Suggested change:** `StartAsync` applies `LocalAttachPolicy` when `target.Kind == AttachProcess`, and returns either the running session or throws with the faulted detail after reclaiming the host -- one implementation. The three tool methods become target construction plus one call. The inspector prompt moves to the manager too, or to a filter, so the operator API and the tools agree on it.

### BRK-10 `retryIfWorkerDied` duplicates the `ReadOnly` annotation by hand, and defaults to yes
- **Severity:** Medium
- **Effort:** S
- **Where:** `src/RoseMcp.Broker/Tools/BrokerAnalysisTools.cs:741-755` and every writing tool's trailing `retryIfWorkerDied: false`
- **What:** Whether a call may be replayed after a worker dies is decided by a boolean each tool passes, defaulting to `true` in `ForwardAsync`. The same fact is declared a second time on every tool as `ReadOnly = ...` in its `McpServerTool` attribute, and a third time in `ToolSurfaceTests.ReadOnly`. The relay derives the identical decision from the `ReadOnlyHint` annotation (`RelayRetryPolicy`). Today the two are consistent, by discipline: every write tool remembered to pass `false`.
- **Why it matters:** The failure is a new write tool that forgets the argument and is replayed after a mid-write crash -- a rename applied twice, which `WorkspaceManager.CallAsync`'s own summary names as the reason the flag exists. A default of "retry" is the wrong default for a flag whose omission is dangerous.
- **Suggested change:** Derive it. The filter in `WithCallOrigin` has `context.MatchedPrimitive as McpServerTool`, whose `ProtocolTool.Annotations.ReadOnlyHint` is the answer; put it in an ambient the way origin and session are, and drop the parameter. Failing that, remove the default so a new tool must say.

### BRK-11 `LiveAppSession` resolves and pages the visual tree itself, against the decision that the host does
- **Severity:** Medium
- **Effort:** M
- **Where:** `src/RoseMcp.Broker/LiveAppSession.cs:406-452`, `:520-552`; `docs/decisions/the-visual-tree-is-rooted-and-paged-in-the-host.md`
- **What:** `ReadXamlTreeAsync(root, offset, limit)` sends a name-rooted request to the host, but for a handle or an address it reads the *whole* tree over the pipe and cuts the subtree and the page in the broker (`Subtree`, `Addressed`). `ResolveElementAsync` reads the whole tree to turn an `x:Name` or address into a handle. `ToolParityTests.Compared` exempts `element`, `handle`, `root` and `rootName` from the broker/host argument-parity check precisely because the two ends take different things here. The comment at lines 426-430 knows the cost ("that costs the whole tree over the pipe") and accepts it.
- **Why it matters:** CLAUDE.md: "anything ... knowing what a tool does belongs in the host". This is the one place the broker has a model of a live app's structure. It also puts the same resolution in two surfaces -- `LiveAppDebugTools` and `OperatorApi` both call `ResolveElementAsync` -- and takes the broker out of the position of being a pure proxy for the debugger, which is the position the inspector decision relies on ("a process has one debugger").
- **Suggested change:** The host accepts a handle, `#name` or address wherever it takes an element, and roots and pages the tree for all three; the broker forwards. The parity exemption then disappears, which is the test telling you the leak is closed.

### ~~BRK-12 Attribution is by runtime type check with no compile-time constraint, and one tool returns nothing to attribute~~
**Done, PR #295.** `WorkspaceManager.CallAsync` and `Attribute` now constrain their result to
`WorkspaceScopedResult`, so a tool answering with anything else fails to build rather than answering
unattributed; the run-time `is not` check is gone because the compiler has already made it true.
`rose_workspace_close` answers with a new `WorkspaceClosed` record carrying the workspace, the key
and whether one was open. The enumerating guard is `ToolResultShapeTests`, which also asserts the
constraint itself, since a constraint is one word and deleting it breaks nothing that runs. The
reasoning for the close result's missing revision is in `WorkspaceClosed`'s own summary.

### BRK-13 `MarkStopped` and `WorkerExitReason.SolutionUnloaded` are dead in the broker
- **Severity:** Low
- **Effort:** S
- **Where:** `src/RoseMcp.Broker/WorkspaceWorker.cs:233`, `src/RoseMcp.Broker/WorkerExitReason.cs:8-9`; rendered at `src/RoseMcp.Tray/WorkspaceRow.cs:324`
- **What:** `MarkStopped` has zero references. `SolutionUnloaded` is never assigned anywhere in the broker; the worker has a `SolutionUnloadedException` path (`WorkspaceHost.cs:87`) but the broker sees that worker's exit as `Crashed`, because `WatchForExit` only knows "exited on its own". The tray has a display string for a state that cannot occur.
- **Why it matters:** A solution deleted past the grace period presents in the tray as a crash, which is the one exit the enum's own doc calls "expected, not a failure". Dead enum values invite a reader to believe a path exists.
- **Suggested change:** Either wire it -- the worker exits with a distinct code on unload and `WatchForExit` reads `process.ExitCode` -- or delete both members and the tray's mapping. The former is small and makes the tray honest.

### BRK-14 `OrderedProgress` claims to restore an order the invariant says is already lost
- **Severity:** Low
- **Effort:** S
- **Where:** `src/RoseMcp.Server/TrayRelay.cs:342-386`; `docs/invariants/transport-and-lifetime.md:44-50`
- **What:** The class summary says notifications "overtake each other, which was observed ... so notifications are queued and sent by one pump", promising "a percentage that only ever rises". The invariant, written later, says the SDK dispatches handlers concurrently on SSE so the values are "already unordered before any of our code sees it", and "no queue here can fix it". Both cannot be right; the code still does something useful (serialises the outbound stdio writes, swallows a dead client) but not what its comment says.
- **Why it matters:** This is the "fix layered on a fix" pattern the brief asks about: a queue added against an observed symptom, then an invariant written when the real cause was found, and the first comment never revisited. The next reader trusts the comment nearer the code.
- **Suggested change:** Rewrite the summary to what it guarantees (one writer on the stdio side, no failure propagated from a client that stopped listening) and link the invariant for why order is not one of them. If neither guarantee is needed, delete the class and pass the `IProgress` through.

### BRK-15 No correlation id crosses the broker-to-worker hop
- **Severity:** Low
- **Effort:** S
- **Where:** `src/RoseMcp.Broker/WorkspaceWorker.cs:202-206`, `src/RoseMcp.Broker/CancellableToolCall.cs:39-40`, `src/RoseMcp.Broker/LiveAppSession.cs:619-623`
- **What:** The broker logs `Forwarding {Tool} to {WorkspaceKey} for {Origin}` at Information; the worker logs into its own file under `Logs/Worker/`. `CancellableToolCall` mints a fresh JSON-RPC request id per call and never logs it; `CallSession.Id` is available in the filter and never logged. Matching a broker line to a worker line is by timestamp and tool name, which fails the moment two sessions ask the same worker the same thing.
- **Why it matters:** The transport invariant says a failure should be traceable end to end. With the tray serving several agents, "which call was this" is the first question and the logs cannot answer it.
- **Suggested change:** Log the request id in the Forwarding line, and have the worker's `ToolErrorReporting` filter (which already sees `context`) log the incoming JSON-RPC id at tool entry. Include `CallSession.Id` in the broker line. Cheap, and the id already exists.

### BRK-16 `WorkspaceKey` and the log file name compute the same hash independently, and nothing checks they agree
- **Severity:** Low
- **Effort:** S
- **Where:** `src/RoseMcp.Solutions/WorkspaceKey.cs:26-39`, `src/RoseMcp.Logging/RoseLogFile.cs:70-86`
- **What:** Both fold case per platform, SHA-256 the full path, and take four bytes lowercase. They agree today -- `RoseMcp-e5ce8a33` is the key and `rosemcp.slnx-e5ce8a33-...log` is the file -- which is what lets a person go from a result's `workspaceKey` to the right worker log. `RoseLogFile.cs:73-76` says the duplication is deliberate because Logging "references nothing"; `Solutions` also has no package references, so that reason does not apply to a project reference. No test asserts the two hashes match.
- **Why it matters:** The correlation is the most useful property of the key and it is accidental. Change either fold and the other keeps working while the link silently breaks.
- **Suggested change:** Let `RoseMcp.Logging` reference `RoseMcp.Solutions` and call `PathCasing.Fold` and a shared `PathHash.Short(path)`, or add `LoggingTests.The_log_file_hash_is_the_workspace_key_hash`.

### BRK-17 Small inconsistencies in the security plumbing
- **Severity:** Low
- **Effort:** S
- **Where:** `src/RoseMcp.Server/ServerOptions.cs:82` vs `src/RoseMcp.Broker/LoopbackOrigin.cs:25-28`; `src/RoseMcp.Server/Program.cs:204-212` vs `src/RoseMcp.Broker/OperatorApi.cs:41-62`
- **What:** Two lists of what counts as loopback (`ServerOptions` omits `[::1]`). Two 401 shapes: `RequireToken` writes an empty body and no `WWW-Authenticate`; the operator branch writes a JSON `OperatorError` with the header. The posture itself matches `docs/debug/security-model.md` in every case I checked: loopback default, Origin refused when present and foreign, token gates everything only when set, operator API always tokened, attach policy same-user, `/admin/*` open on loopback by design. The inconsistencies are cosmetic but they are the kind that make a reader doubt the rest.
- **Why it matters:** A future change to one list or one refusal will not reach the other, and a relay implementing #213 needs one answer to "what does a refusal look like".
- **Suggested change:** `LoopbackOrigin.Hosts` becomes the single list, used by `ServerOptions.Validate`. One `Unauthorized(HttpContext)` helper in the broker used by both middlewares (folds into BRK-08).

### BRK-18 Comments that carry history, against the repository's own convention
- **Severity:** Low
- **Effort:** S
- **Where:** `src/RoseMcp.Broker/CallOrigin.cs:9-13`, `src/RoseMcp.Broker/Tools/BrokerTools.cs:18-24`, `src/RoseMcp.Broker/WorkspaceHints.cs:5-11`, `src/RoseMcp.Broker/WorkspaceManager.cs:321-325`, `src/RoseMcp.Server/TrayRelay.cs:151-160`, `src/RoseMcp.Broker/LocalAttachPolicy.cs:9-17`, `src/RoseMcp.Broker/WorkerLauncher.cs:30`, `src/RoseMcp.Logging/RoseLogFile.cs:39`
- **What:** CLAUDE.md forbids "used to", "previously", issue tags on closed work, and schedule language. In scope: `CallOrigin` tells the story of the relay's former pre-emptive resolve; `BrokerTools.OpenAsync` says what it "used to be"; `WorkspaceHints` describes the seventeen chains it replaced; `TrayRelay.CallToolAsync` narrates three past failures; `LocalAttachPolicy` opens with "(issue #15, first step)" and closes with "the fuller model to build on"; `(#44)`, `(#101)`, `(#111)` tag closed work. The *why* in each is good and should stay; the *what it was* belongs in the commit.
- **Why it matters:** The convention exists because a comment that only makes sense against the old code stops making sense once nobody remembers the old code. The broker is where the most design happened, so it is where the most history accumulated.
- **Suggested change:** Rewrite each as the consequence of not having the code, present tense, per the `Nothing used to remove it` example in CLAUDE.md. Consider a lint: a test over `src/**/*.cs` that fails on `used to|previously|no longer|for now|until now` inside `///` or `//` -- a regex is crude but the convention is explicit enough to be checked.

### BRK-19 `CancellableToolCall` can cancel a disposed token source in a fire-and-forget continuation
- **Severity:** Low
- **Effort:** S
- **Where:** `src/RoseMcp.Broker/CancellableToolCall.cs:67-95`
- **What:** `abandon` is a `using var`; the registration callback runs `_ = NotifyThenAbandonAsync()`, which awaits `SendNotificationAsync` and then calls `abandon.CancelAsync()`. If the caller cancels at the moment the response arrives, `SendAsync` completes, the method returns and disposes `abandon`, and the still-running continuation then calls `CancelAsync` on a disposed source: `ObjectDisposedException` in an unobserved task.
- **Why it matters:** Unobserved exceptions are logged noise at best and, with `UnobservedTaskException` handlers, a crash at worst. The window is narrow but it is the exact moment (cancel racing completion) this class is about.
- **Suggested change:** Hold the continuation in a field, await it in a `finally` before disposal, or use `CancellationTokenSource.TryReset`-style guard: `if (!abandon.IsCancellationRequested) try { abandon.Cancel(); } catch (ObjectDisposedException) { }`. A `CancellableToolCallTests` unit test with a fake `McpClient` would be the first test this class has at the unit level.

### BRK-20 A warm worker holds its worktree's directory open, so `git worktree remove` fails until the tray is closed

- **Severity:** Medium
- **Effort:** S (the lock) / M (the relative-path half it is entangled with)
- **Where:** `src/RoseMcp.Broker/WorkspaceWorker.cs:156`
  (`WorkingDirectory = Path.GetDirectoryName(solutionPath)`),
  `src/RoseMcp.Worker/AddFileService.cs:50` (`Path.GetFullPath(request.FilePath)`)
- **What:** Every worker is started with its working directory set to its solution's directory. On
  Windows a process's working directory is held open with a handle that does not share delete, so
  that directory cannot be removed while the process lives. Verified directly: a child process
  started with a temp directory as its working directory made `Remove-Item` fail with "because it is
  being used by another process", and the same removal succeeded the moment the child exited.

  For this repository the solution sits at the worktree root, so the directory a worker pins **is**
  the worktree. Workers are kept warm and are never evicted (#157), so a solution opened once holds
  its worktree open for the life of the broker. `git worktree remove` on it fails, and so does any
  removal of a parent directory. Creating a worktree, opening it, and then removing it is an
  ordinary sequence here -- often inside a single session.
- **Why it matters:** Two things, and the second is the more interesting.

  1. The workflow breaks in a way that does not name Rose. Git reports a directory in use, the
     obvious suspects are an editor or a shell, and the actual holder is a background worker owned
     by a tray in the notification area.
  2. **The correct working directory is what makes #214 a silent success rather than a loud
     failure.** Trace the wrong-worktree write: the broker resolves a relative hint against its own
     working directory and picks the wrong workspace (BRK-01); it routes to that workspace's worker;
     that worker resolves the same relative path with `Path.GetFullPath`, against a working
     directory that is correctly its own solution's root; the file exists there, because it is a
     worktree of the same repository; the edit applies and reports success. Had the worker's working
     directory been inert, step four would have failed to find the file and the bug would have
     surfaced the first time instead of writing to the wrong checkout. A defensive setting is
     load-bearing in the failure.
- **Suggested change:** Separate the two, because only one is urgent.

  1. **Make the broker-to-worker hop absolute-only**, and have the worker *refuse* a relative path
     rather than resolve one. Relative paths are a convenience for the outside caller, and the
     broker is the only place that knows the origin to resolve them against (BRK-01). Once the hop
     is absolute, the worker's working directory stops being load-bearing for correctness, and a
     mis-routed call fails loudly instead of writing somewhere plausible.
  2. **Then move the working directory somewhere inert**, so a warm worker pins nothing a person
     might want to delete. The repository already has the concept:
     `tests/RoseMcp.TestSupport/NowhereDirectory` points at a drive that does not exist, precisely
     because "nowhere on a real disk can be promised clean". A worker wants the weaker version of
     the same idea: a directory whose removal nobody will ever attempt.
  3. **Measure before moving it.** MSBuild resolves project-relative paths against the project file
     rather than the working directory, but a repository's own custom targets or tasks may read
     relative paths against the process, and a design-time build runs the repository's code
     (`docs/debug/security-model.md`). Change it behind the fixture suite and watch the XAML and
     generator fixtures, which are the ones with non-trivial targets.

  Until then, #157's eviction work would shorten the exposure but not remove it, and the two should
  be cut as one card: an evicted worker releases the directory, which is a second reason to evict.

## Pit-of-success inversions

1. **Rule:** "Every result carries a `revision` and names the workspace that answered" (CLAUDE.md; `result-shapes.md`). Today a runtime `is` check in `Attribute<T>` and 21 hand-written `public required long Revision` properties. **Mechanism:** `where T : WorkspaceScopedResult` on `WorkspaceManager.CallAsync` and `StatusOfAsync`; move `Revision` onto a `WorkspaceReadResult : WorkspaceScopedResult` base so a result cannot omit it; a unit test that enumerates the advertised Roslyn tools via `McpServerTool` (as `ToolSurfaceTests.Advertised` already does), reads each method's return type, and asserts it derives from `WorkspaceScopedResult` -- the compiler for the broker's own tools, the test for anything registered another way.

2. **Rule:** "Retrying is only safe when the tool is read-only" (`WorkspaceManager.CallAsync` summary). Today a boolean per call defaulting to `true` (BRK-10). **Mechanism:** derive from `ProtocolTool.Annotations.ReadOnlyHint` in the call-tool filter and expose it ambiently like `CallOrigin`; delete the parameter. `ToolSurfaceTests.Only_the_listed_tools_call_themselves_read_only` then governs retry as well as the client's consent prompt, which is the same promise.

3. **Rule:** "A worker's tool takes the broker's arguments minus the workspace one, so routing is a straight pass-through" (`WorkspaceWorker.CallAsync` summary), which the same comment admits "nothing checks". Today every tool in `BrokerAnalysisTools` spells its argument dictionary by hand, and `ToolParityTests` covers only the live-app pairs. **Mechanism (either):** (a) forward `context.Params.Arguments` verbatim minus `workspace`, so the broker's tool methods declare schema only and cannot misspell what they never write; or (b) extend `ToolParityTests` to the Roslyn half by loading the worker assembly and asserting each broker tool's parameter names minus `workspace` equal the worker's. (a) removes the hazard; (b) detects it.

4. **Rule:** "One registration path, used by both hosts" (CLAUDE.md architecture table). Today true for services, false for the http pipeline (BRK-08). **Mechanism:** `MapRoseBroker` in the broker; the hosts call it. The security-model test's pattern applied to routes: a test that builds the server pipeline and asserts the route set, so the tray -- which cannot be hosted in a test -- is covered because it calls the same method.

5. **Rule:** "A debug session detaches before its host is closed" (decision) and "every stdio process ... takes what it owns" (`transport-and-lifetime.md`). Today enforced only when the host disposes the container (BRK-07). **Mechanism:** the managers implement `IHostedService` (or `AddRoseMcpBroker` registers one that owns them); `StopAsync` is the teardown. Then the decision holds in any host that stops, which every host does.

6. **Rule:** "A relative path is a fact about where the caller is standing" (issue #214; implicit in `solution-routing.md`). Today `WorkspaceFor` receives raw strings and resolves them against the process (BRK-01). **Mechanism:** `WorkspaceHints` carries absolute paths only -- `From(workspace, origin, params string?[] paths)` rebases relatives against the origin at construction -- so `WorkspaceFor` cannot see a relative path and `SolutionResolver.Choose` need never call `GetFullPath` on a caller's string. `WorkspaceRoutingTests` gains the two-checkout case.

7. **Rule:** "The `rose_worker_info` and `rose_live_app_*` names are host-internal: the broker calls them and declares none of them" (`ToolNames` summary). Today `ToolSurfaceTests.HostInternal` is a hand-kept list. **Mechanism:** reflect over `ToolNames`' public constants and require each to appear in exactly one of `Roslyn`, `LiveApp`, `HostInternal` or a named `Withheld` set (`XamlSelectMode`). A constant added to `ToolNames` and to nothing else then fails a test, which is the moment to decide what it is.

## Open questions for Steve

- Is holding `_gate` across the worker handshake deliberate -- to stop several design-time builds competing on a cold machine -- or incidental? The `WorkerHandshakeTimeout` comment describes several starting at once as a real case, so serialising starts may be wanted; the fast path paying for it almost certainly is not (BRK-03).
- The tray's `ShutdownAsync` calls `StopAsync` and never `DisposeAsync`. Is the intent that the LiveApp host detaches on its own stdin-close, making the broker's detach-first ordering a belt to the host's braces? If so, the decision record should say which one is load-bearing (BRK-07).
- `WorkerExitReason.SolutionUnloaded` -- was it ever wired, or was the tray's display string written ahead of a worker exit code that never arrived (BRK-13)?
- For #157, what should trigger eviction: idle time, memory pressure, or "no MCP session has touched this workspace since it disconnected"? The broker has no notion of which sessions use a workspace; adding one is the real cost of that issue.
- Is `OrderedProgress` still believed to help after the invariant was written, or is it a candidate for deletion (BRK-14)?
- Is there a Roslyn-half parity test anywhere I did not read? `ToolParityTests` covers `LiveAppPairs` only, and the `ToolNames` comment says the Roslyn half "needs no map" -- but I found no test that uses the absence of a map.
- The `Source: "ModelContextProtocol.Core"` match: which `InvalidOperationException` was it written for? If it is the SDK's "session disposed" case there is probably a typed exception or a state property to check instead (BRK-06).

## Rose dogfooding notes

The workspace was already loaded (revision 1, 20.7 s load) and reported **Degraded** for the reasons the brief's ground truth lists; navigation answers were unaffected.

| Tool | For | Outcome |
|---|---|---|
| `rose_workspace_status` | Confirm the workspace state before trusting answers | Worked. Degraded reasons and load diagnostics matched the README. |
| `rose_project_graph` (project=RoseMcp.Broker) | "Is the broker a clean layer" from the dependency graph | Worked, one call: references Contracts/Settings/Solutions only; referenced by Server, Tray and both test projects. Grep cannot answer this. |
| `rose_outline` WorkspaceManager | Member map before reading | Worked. Still read the whole file, because a review needs bodies; the outline was a preamble rather than a replacement. |
| `rose_outline` LiveAppSession (`includeSignatures=false`, `includeDocumentation=false`) | "What is in this 700-line class" | Worked, but the compact mode is not compact: every member still carries a full location record with preview, so the answer was ~60 entries of JSON. A name-and-line listing mode would make this the reach-for-first tool on a big type. |
| `rose_find_implementations` WorkspaceScopedResult | Which results are attributable (BRK-12, inversion 1) | Worked: 23 derived records in one call. The single best answer of the session; grep for `: WorkspaceScopedResult` would have missed the ones deriving via `WorkspaceMutationResult`. |
| `rose_find_references` CallOrigin.Directory | Where the origin is consumed (BRK-01) | Worked, 3 uses with containing members. |
| `rose_find_references` WorkspaceManager.Workers | Cross-project consumers | Worked; found the tray's `OnCloseAll` use I would not have grepped for. |
| `rose_find_references` WorkspaceWorker.MarkStopped | Is this dead | Worked: zero references (BRK-13). Grep finds the declaration and cannot say zero. |
| `rose_find_references` SolutionResolver.Resolve | Production callers | Worked: none in production, only `SolutionResolverTests`; `Choose` is what the broker uses. Not raised as a finding; noted here. |
| `rose_find_references` AddRoseMcpBroker | The two-hosts question (BRK-08) | Worked and matched grep exactly. Grep won on time only because I wanted seven symbols in one shot; Rose is one symbol per call. |
| `rose_search_symbols` IsTransportFailure | The three predicates (BRK-06) | Worked, all three. Tie with grep; I reached for grep first out of reflex, which is the loss the brief asks me to record. |
| `rose_symbol_info` `ModelContextProtocol.Server.McpServer.SessionId` | Verify the "null on stdio" claim in `CallSession` from the SDK's own docs | **Failed.** "Nothing is declared at ..." followed by a list of same-named *source* symbols. The description promises answers "about a type in a referenced assembly"; the by-name path evidently searches source declarations only. Worth filing under tool-surface. I fell back to trusting the code comment. |
| `rose_find_references` WorkspaceManager.CallAsync (`definitionsOnly=true`) | Count of callers | Worked (16), but the result says `truncated: true` with an empty `references` list. With `definitionsOnly` the list is suppressed, not truncated; the field misleads. Minor. |
| `rose_find_references` LocalAttachPolicy.EnsureAttachable | Where the policy is enforced (BRK-09) | Worked: exactly one caller, in the tool layer. |
| `rose_find_references` LiveAppSessionManager.StartAsync | Who can bypass the policy (BRK-09) | Worked but noisy: 53 hits, 50 in tests, each with a preview. I should have passed `project` or `includePreviews=false`; the defaults favour a first-time user over a reviewer. |

Not reached for, and whether it should have been: whole-file reads went through `cat -n` -- unavoidable for a line-by-line review, not a loss. Comment-text searches (issue tags, "used to") went through grep, which is the right tool; Rose has no comment search and should not. `rose_diagnostics` was not needed because nothing was edited. The one genuine loss to grep was the `IsTransportFailure` reflex above; the one genuine failure was `rose_symbol_info` on a metadata symbol.
