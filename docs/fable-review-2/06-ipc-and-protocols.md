# IPC and protocols

**Scope.** Every process boundary in RoseMCP and the technology crossing it. Read:
`src/RoseMcp.Server/{Program.cs,TrayRelay.cs,RelayRetryPolicy.cs,ServerOptions.cs}`;
`src/RoseMcp.Broker/{WorkspaceWorker.cs,WorkerLauncher.cs,CancellableToolCall.cs,LiveAppSession.cs,LiveAppHostLauncher.cs,OperatorApi.cs,OperatorToken.cs,InspectorLauncher.cs,InspectorPresenter.cs,ActivityLog.cs,ForwardedError.cs,LoopbackOrigin.cs}`;
`src/RoseMcp.Contracts/{ContractJson.cs,HostVersion.cs,OperatorRequests.cs,ToolNames.cs,WorkerInfo.cs,LiveAppInfo.cs,LiveHeartbeat.cs,XamlStubReportChannel.cs}`;
`src/RoseMcp.LiveApp/Xaml/{XamlProviderPipe.cs,XamlProviderSession.cs,XamlChannelBounds.cs}`;
`src/RoseMcp.LiveApp/Debugging/{UwpStartupCoordinator.cs,UwpResumeStub.cs}`;
`src/RoseMcp.Xaml.Tap/tap_channel.h`;
`src/RoseMcp.XamlStubs/XamlStubReportChannel.cs`, `src/RoseMcp.Worker/Xaml/XamlStubReportReader.cs`;
`src/RoseMcp.Ui.Core/{PollLoop.cs,Inspector/OperatorClient.cs,Inspector/HoldKeeper.cs,Inspector/CommandLine.cs}`;
`src/RoseMcp.Inspector/Program.cs`; `src/RoseMcp.Logging/*`.
Plus `docs/invariants/transport-and-lifetime.md`, `docs/debug/security-model.md`, and the three
decision records naming a boundary.

**Verdict.** **Adequate, tending to strong on the boundaries that were designed and fragile on the
two that accreted.** The technology choice is right at nine of twelve boundaries and defensible at a
tenth. MCP-over-stdio for the internal parent-child hops is the standout call: it is not the obvious
choice -- gRPC or a hand-rolled JSON-RPC would be the reflex -- and it pays for itself three times
over, because the worker and the live-app host are *also* standalone MCP servers that a person can
drive with any MCP client, the tests exploit exactly that, and progress and cancellation arrive for
free on a protocol the broker was already speaking outward. What it costs is real and mostly
unpriced here: results are JSON with no binary path, there is no correlation id, cancellation had to
be re-implemented by hand because the SDK does not send `notifications/cancelled`
(`CancellableToolCall`), and one SDK's quirks now show up in four processes. Where the design shows
its age was the XAML tap pipe, the only boundary where the framing was invented rather than adopted
and the only one whose protocol-level defects produced wrong answers rather than failures -- closed by
#PRNUM (IPC-01, IPC-03, LIV-07, LIV-08); and at the *edges* of otherwise sound boundaries -- no version
handshake anywhere despite `HostVersion` existing and being sent (IPC-02), the operator token
travelling on a command line the threat model says is readable (IPC-04), and three serializer
configurations on a single hop (IPC-05). None of this is a wrong-tool verdict. It is a system that
picked its protocols well and has not yet written down the handshakes that make a protocol safe to
change.

## Boundary inventory

| # | Boundary | Transport / protocol | Shape | Verdict |
|---|---|---|---|---|
| 1 | MCP client -> `RoseMcp.Server` | stdio, JSON-RPC, MCP SDK 2.2.0 | request/response + progress notifications | Appropriate |
| 2 | `RoseMcp.Server` -> Tray broker | http, MCP streamable-http (SSE) | request/response + progress; reconnecting | Appropriate with fixes (BRK-02, IPC-10) |
| 3 | Broker -> Worker | stdio to a child, MCP | request/response + progress; priming call | Appropriate with fixes (IPC-02, IPC-05, IPC-09) |
| 4 | Broker -> LiveApp host | stdio to a child, MCP | request/response + long-poll event read | Appropriate with fixes (IPC-02, IPC-08) |
| 5 | Inspector -> Tray operator API | http + bearer token, REST/JSON | polled reads + long-polled event tail | Appropriate (see IPC-04 for the token) |
| 6 | LiveApp host -> XAML tap | named pipe, 4-byte LE length + UTF-8 text payload | request/reply, each reply carrying its request's id | Appropriate (#PRNUM) |
| 7 | LiveApp host -> UWP resume stub | named pipe, newline-delimited text | two-message handshake, fail-safe | Appropriate with fixes (IPC-06) |
| 8 | Worker <-> XAML stub generator | a generated source document carrying JSON on a marked comment | one-way report, per compilation | Appropriate -- the only channel Roslyn offers |
| 9 | Tray -> Inspector | process start; args, then WinAppSDK activation redirection | one-shot launch + hand-over | Appropriate with fixes (IPC-04) |
| 10 | Logs | per-component rolling files on disk | one-way, out of band | Appropriate with fixes (IPC-07, BRK-15) |
| 11 | Tray window <-> broker | in-process object graph (`WorkspaceManager`, `ActivityLog`) | direct reads, polled by the window | Appropriate -- deliberately not IPC |
| 12 | Serialization everywhere | System.Text.Json, three option sets | DTOs in `RoseMcp.Contracts` | Appropriate with fixes (IPC-02, IPC-05) |

## Strengths

- **One protocol for every internal hop, and it is the same protocol served outward.** The broker
  speaks MCP to its clients and MCP to its children. A worker is a complete MCP server that
  `dotnet run --project src/RoseMcp.Worker -- --solution ...` drives standalone, which CLAUDE.md
  offers as the fastest way to debug Roslyn behaviour; `ToolNames` (`src/RoseMcp.Contracts/ToolNames.cs:3-12`)
  says out loud that the worker's surface is the broker's minus `workspace`, so routing is a
  pass-through. That is not a thing gRPC or a private wire format would have given, and it is why
  the integration suite can start a real worker and talk to it without the broker.
- **Cancellation crosses every internal hop, which the SDK does not do for you.**
  `CancellableToolCall` (`src/RoseMcp.Broker/CancellableToolCall.cs:12-27`) builds the JSON-RPC
  request by hand purely to own the request id, so it can send `notifications/cancelled` before
  abandoning the wait. The ordering comment at `:61-67` -- notify first, because tearing down a
  streaming POST three milliseconds early leaves the far side treating the request as merely gone --
  is the kind of thing that is only ever learned the expensive way, and it is written down.
- **`_meta` is used for what `_meta` is for.** The origin directory rides in
  `_meta["rosemcp/originDirectory"]` rather than being smuggled into a tool argument
  (`CancellableToolCall.Meta:113-140`), with the reasoning that it belongs to no tool's schema. The
  relay contributes a fact and the broker draws the conclusion, and the invariant records the three
  distinct bugs that the other arrangement produced.
- **Every long wait is bounded, and the bound says which wait it was.** `XamlChannelBounds`
  (`src/RoseMcp.LiveApp/Xaml/XamlChannelBounds.cs`) is the model: five named bounds in one record, a
  single environment ceiling that can only ever *shorten* a wait, and a `TimedOut(channel, bound)`
  that names the channel in the same words the log uses. A fifty-minute hang produced this, and it
  is the best piece of protocol engineering in the repository.
- **The framing on the tap pipe is length-prefixed, both ends, with a maximum frame size enforced
  on both** (`XamlProviderPipe.ReadFrameAsync:344-367`, `tap_channel.h:99-132,187-207`). The C++
  side's comment explains that without the cap, `payload.assign` of a garbage length is `bad_alloc`
  out of a `std::thread` function, which is `std::terminate` *inside somebody else's app*. Both
  sides also `ReadExactly` and treat a short read as broken rather than smaller.
- **The far end is detected by reading, not by asking.** `XamlProviderPipe.Connected` is
  deliberately not `NamedPipeServerStream.IsConnected`, with the reason spelled out at `:69-81`:
  the server's own state stays true after the provider's process has gone, so a channel that
  believed it would report a dead tap as present and time out every request. The pump reads
  continuously so a departure between requests is noticed.
- **Poll loops are written as loops, not timers.** `PollLoop` (`src/RoseMcp.Ui.Core/PollLoop.cs:3-14`)
  awaits each body before scheduling the next wait, so a slow tray costs a skipped interval rather
  than a backlog, and it yields unconditionally so a synchronous body cannot eat the message pump.
  It is plain `net10.0`, so the fast suite covers it.
- **The event tail is a long poll, not a spin.** `EventsPane` runs a `PollLoop` with a *zero*
  interval and a 30-second `waitSeconds` (`src/RoseMcp.Inspector/Panes/EventsPane.xaml.cs:26,49,83`),
  and `OperatorClient.PollBudget` gives the request its wait plus a 15-second margin
  (`OperatorClient.cs:39-49`). That is SSE latency with none of SSE's reconnection semantics to get
  wrong, and the comment says why a `Quick` budget there would silently drop events.
- **The resume-stub protocol fails safe.** `UwpResumeStub.Run` resumes the app's main thread in a
  `finally` whatever went wrong (`UwpResumeStub.cs:45-49`), so a missing or crashed host degrades to
  an ordinary post-startup attach rather than leaving somebody's app suspended forever.
- **The stub generator's report channel is honest about its constraint.** Roslyn instantiates a
  generator out of an analyzer assembly, so there is no callback; the report goes out as a generated
  document with JSON on a marked comment, and only two `const` strings cross -- declared twice on
  purpose so no assembly identity has to agree (`src/RoseMcp.Contracts/XamlStubReportChannel.cs:4-12`),
  with `XamlStubChannelTests` failing if the spellings part.

## The boundaries, one at a time

### 1. MCP client -> `RoseMcp.Server` (stdio, JSON-RPC, SDK 2.2.0)

**What crosses.** `initialize`, `tools/list`, `tools/call`, `notifications/progress`,
`notifications/cancelled`. Request/response plus a progress stream; no binary, no streaming result.
`src/RoseMcp.Server/Program.cs:38-56` (own broker) and `:62-105` (relay).

**Lifetime and failure.** The process dies with its client's stdin, and takes its workers with it
(`transport-and-lifetime.md`, and `WorkspaceWorker.DisposeAsync:497-515` closes each worker's stdin
in turn). The failure model is the strongest in the system precisely because it is the operating
system's: nothing has to notice, the handle closes.

**Security.** The process boundary. One client started it; nobody else can reach it.

**Versioning.** The SDK negotiates the MCP protocol version. `ServerInfo.Version` is
`HostVersion.Of(typeof(Program).Assembly)` -- and `Program.cs:86-88` is careful to say that a relay
reports *its own* version rather than the tray's, which is the right call and the only place in the
repository where the meaning of a version on the wire was thought about.

**Observability.** Everything to stderr and to `Logs/Server/`. No request id in any log line
(BRK-15).

**Verdict: appropriate.** This is the boundary the product is for; there is no alternative to weigh.

### 2. `RoseMcp.Server` -> Tray broker (MCP over streamable http)

**What crosses.** The same MCP calls, forwarded verbatim, plus `_meta["rosemcp/originDirectory"]`
added by `CancellableToolCall.Meta` (`:123-140`). `tools/list` is forwarded too, so the relay
declares nothing (`TrayRelay.cs:28-30`) and `RelayRetryPolicy` learns read-only-ness from the same
list it is passing through (`RelayRetryPolicy.cs:17-22`) -- a genuinely elegant piece of design: the
retry decision needs no new protocol because the answer is already on the wire.

**Lifetime and failure.** Three-layered and each layer is justified in place. `IsListeningAsync`
decides before anything is built (2 s, one round trip). `ConnectAsync` retries inside a window
(`:290-315`). `InvokeAsync` catches a transport failure, reconnects once behind a semaphore so
concurrent calls do not open several sockets (`:242-284`), and re-sends only where the tool is
annotated read-only; a write is reported with a message that says the tray *may* have applied it
(`:234-238`). That last sentence is the correct answer to "at-least-once versus at-most-once over a
protocol with no idempotency key", and it is the only place in the system that states which one it
has chosen.

**Security.** `LoopbackOrigin.IsAllowed` on the tray side; loopback bind. `ROSEMCP_TOKEN` is *not*
sent (BRK-02, #213).

**Versioning.** None beyond MCP's own. A relay built from a newer commit than the tray forwards a
call for a tool the tray does not have, and gets the tray's "unknown tool" -- which is survivable,
because the relay does not declare tools. That is a real benefit of forwarding `tools/list`: the
relay cannot advertise a surface the tray lacks. The reverse -- new Server relaying to old Tray --
degrades correctly for the same reason.

**Observability.** `_endpoint` is in every log line; nothing correlates a relay line to a tray line.

**Verdict: appropriate with fixes.** MCP-over-http here is right for the reason the class summary
gives: delegating needs no new protocol, because the destination already speaks the one the source
speaks. The alternative -- a private "please run this for me" RPC -- would have needed its own tool
list, its own progress, its own cancellation, and would have made `tools/list` drift possible. Fix
BRK-02 and IPC-10.

### 3. Broker -> Worker (MCP over stdio to a child)

**What crosses.** `StdioClientTransport` with `--solution`, `--no-restore`, `--configuration`,
`--platform`, repeated `--property` (`WorkspaceWorker.StartAsync:110-166`) -- build identity on the
command line because MSBuild properties are per process, which is the honest reason. Then MCP:
`rose_worker_info` on connect, a priming `rose_workspace_status` (`BeginLoading:377-407`) purely so
the load has a request to report progress against, and every forwarded tool call. Progress fans in
through `ActivityLog` as well as to the caller, so a reload nobody is waiting on still shows in the
tray.

**Lifetime and failure.** Two independent detectors, which is right: `Process.Exited` armed from the
pid the worker reports about itself (`WatchForExit:271-301`), *and* `IsTransportFailure` on the call
path. The comment at `:257-268` says why the first was added -- a worker that crashed while idle went
on describing itself as alive. The `HasExited` re-check after arming the event closes the race. Death
is ordinary rather than exceptional, and `WorkerExitReason` distinguishes `StoppedByBroker` from
`Crashed` so an orderly close is not relabelled a crash.

**Security.** Process boundary; the child is chosen by `WorkerLauncher.ResolveWorkerPath`, which
honours `ROSEMCP_WORKER` -- a code-execution setting, as `security-model.md` says.

**Versioning.** **None.** `HostVersion.Of` has seven references in the whole solution
(`rose_find_references`), four of which are hosts setting their own `ServerInfo.Version` and three of
which are its unit tests. Nothing reads `McpClient.ServerInfo`. See IPC-02.

**Observability.** `Forwarding {Tool} to {WorkspaceKey} for {Origin}` at Information
(`WorkspaceWorker.CallAsync:184-189`), and a separate log file per worker naming its solution. No id
ties the two (BRK-15).

**Verdict: appropriate with fixes.** The alternative worth naming is gRPC: it would give a schema,
generated clients, streaming and version negotiation. It would cost the thing that actually earns its
keep here -- a worker you can run standalone and drive with any MCP client, which is how Roslyn
behaviour gets debugged in this repository and how half the integration suite is written. A second
alternative, a JSON-RPC-lite of our own, is strictly worse than MCP: same JSON, same framing, none of
the tool metadata, and `SharedWorkProgress` / `CancellableToolCall` would have to be written anyway.
The costs that *are* real: results round-trip through JSON three times on the relayed path (IPC-09),
there is no binary channel at all (which matters for hot reload -- see `07`), and the SDK's quirks are
now load-bearing in four processes (`IsTransportFailure` matching on
`Source: "ModelContextProtocol.Core"`, BRK-06).

### 4. Broker -> LiveApp host (MCP over stdio to a child)

**What crosses.** The target on the command line (`BuildArguments:214-248`: `--attach`,
`--launch-uwp`, `--launch`, `--arguments`, `--description`), then `rose_live_app_info` on connect and
one method per debug verb (`LiveAppSession.cs:250-420`). Two things make this hop different from the
worker's: the host is chosen *by architecture* (`LiveAppHostLauncher.ResolveHostPath`), because an
ICorDebug host must match its target; and one call, `rose_live_app_events`, is a **long poll** --
`waitSeconds` is passed through and the host holds the request open (`ReadEventsAsync:260-282`). The
comment there is exactly right that the ordering in `CancellableToolCall` matters more here than
anywhere: a thirty-second wait abandoned locally would leave the host holding a reader for the rest of
it.

**Lifetime and failure.** `LiveHeartbeat` is not a transport keepalive and should not be mistaken for
one -- it is the age of the target's last debug *event*, carried because a job-object-frozen UWP app
is indistinguishable from a wedged one by CPU, thread state or window responsiveness
(`LiveHeartbeat.cs:6-12`). The record deliberately refuses to turn the age into a verdict. Liveness of
the *host* is `IsTransportFailure` on `RefreshInfoAsync`, and it is narrower than the worker's on
purpose (`:139-151`): only `IOException` / `ObjectDisposedException`, inner exception included,
because a poll that timed out means slow, not gone.

**Verdict: appropriate with fixes.** Same reasoning as the worker, plus the long poll is the right
shape for an event tail (see boundary 5). Fix IPC-02, and note that `LiveAppSession` and
`WorkspaceWorker` are the same class twice with three divergences -- serializer aside, the
`IsTransportFailure` predicates differ (BRK-06) and only the worker watches `Process.Exited` (IPC-08).

### 5. Inspector -> Tray operator API (http + bearer token, REST/JSON)

**What crosses.** Plain REST: `GET /operator/sessions`, `POST .../breakpoints`,
`DELETE .../breakpoints/{id}`, and so on (`OperatorApi.cs:73-290`), request bodies from
`RoseMcp.Contracts/OperatorRequests.cs` so both ends share the record and a new field is a compile
error rather than a silently ignored property. Answers go out through `ContractJson.Options` so the
operator surface, the admin endpoints and the tray window cannot disagree.

**Why not MCP here.** The decision record answers it and the code agrees: `CallSession.Id` is null
inside a minimal-API endpoint, so `LiveAppSessionManager.Find` -- owner-scoped, correctly -- refuses
every call from an http endpoint. `ForOperator` is owner-agnostic and the token is what authenticates
it. Reaching for MCP would have meant either weakening the ownership check or giving the inspector a
fake session identity, and both are worse.

**Polling versus SSE/WebSocket.** This is the question the brief asks, and the answer in the code is
better than the question implies: the event tail is **already a long poll**, not a spin. `EventsPane`
runs a `PollLoop` with a `TimeSpan.Zero` interval and `waitSeconds = 30`
(`EventsPane.xaml.cs:26,49,83`), and `OperatorClient.PollBudget` gives it the wait plus a 15 s margin.
Latency to a new event is therefore one round trip, the same as SSE. The other panes (session summary,
breakpoints, XAML selection) poll on an interval, which is correct for *state* rather than events:
they want the current value, not the history, and a re-read is naturally idempotent where a missed
push is not. What SSE or a WebSocket would buy is one connection instead of several and no
reconnection gap; what they would cost is a second failure model in a window whose whole design
principle is "say what you could not read rather than showing an empty list"
(`the-inspector-is-a-client-of-the-broker.md`). A dropped SSE stream has to be re-established and
re-synchronised from a cursor -- which is exactly what the long poll already does, for free, every
thirty seconds. **Polling is the right call here**, and the one thing worth changing is not the
transport but the token's delivery (IPC-04).

**Lifetime and failure.** `HoldKeeper` is the part that had to get the semantics right, and does:
readers declare what they want and the keeper reconciles, so `Want` is idempotent and safe to call
from a poll, and a pane that forgets to release is corrected by its own next poll rather than leaving
somebody's application stopped (`HoldKeeper.cs:11-29`). `OperatorClient` sets
`HttpClient.Timeout = InfiniteTimeSpan` and imposes a per-request budget with a linked token, so a
budget expiring and a caller giving up are distinguishable (`:60-66`) -- which matters because a pane
closed mid-request must not put "the tray timed out" on screen on its way out.

**Security.** Bearer token, fixed-time compare after a length check (`OperatorToken.Matches:69-77`),
minted per run, never persisted. The 401 carries `WWW-Authenticate` and a JSON body that says how to
get a token. Sound -- except for how the token gets to the inspector (IPC-04).

**Versioning.** None. A newer inspector against an older tray gets a 404 for a route that does not
exist and a `null` for a property the old tray does not send; `OperatorError` carries a message but
there is no negotiated version and no `/operator/version`.

**Verdict: appropriate.** REST for an operator surface a person's window drives, MCP for the agent
surface, and the reason they are different is written down. Fix IPC-04.

### 6. LiveApp host -> XAML tap (named pipe)

*The framing is good.* 4-byte little-endian length, UTF-8 payload, an exact read on both sides, and a
64 MiB cap enforced identically at both ends, with the C++ side explaining that without it a garbage
length is `std::terminate` inside somebody else's app (`RoseTapMaxFrame` in `tap_channel.h`). One
encoding decision for the whole channel, taken once, with the two bugs it retires named
(`XamlProviderPipe.ReadFrameAsync`).

*The payload was three positional sub-encodings, escaped in one direction only, with no request id and
a greeting that proved nothing.* **#PRNUM** made it one contract: every field escaped with one table in
both directions, each reply carrying its request's id, and a provider refused unless it greets with the
host's protocol version and the session's key. **Verdict: appropriate.**

### 7. LiveApp host -> UWP resume stub (named pipe)

**What crosses.** Two messages. The stub writes `"{pid} {tid}"`; the host writes `"resume"`; the stub
writes `"resumed"` or `"resume-failed"` (`UwpResumeStub.Coordinate:52-77`,
`UwpStartupCoordinator.WaitForStub:47-67`). Newline-delimited text over `StreamReader` /
`StreamWriter`.

**Lifetime and failure.** Every wait is bounded (20 s connect, 20 s resume), and the whole thing fails
*safe*: `Run`'s `finally` resumes the app's main thread whatever went wrong, so a missing or crashed
host degrades to an ordinary post-startup attach rather than leaving somebody's app suspended forever
(`:45-49`). The malformed-id case is a named exception rather than a silent default (`:61-64`). The
command line is kept under 255 characters because `EnableDebugging` refuses longer, with the fallback
path documented in the decision record.

**Security.** `new NamedPipeServerStream(...)` with no `PipeSecurity` -- the framework default -- and
a name built from `Random.Shared` (IPC-06).

**Verdict: appropriate.** Two messages, one direction each, with a fail-safe: a pipe is exactly right
and anything heavier would be ceremony. The protocol is so small that its text framing costs nothing.
Fix IPC-06 for consistency with its sibling pipe.

### 8. Worker <-> XAML stub generator (a generated document)

**What crosses.** One way: `XamlStubReportPayload` as JSON on a line prefixed
`// rosemcp-xaml-report:` inside a generated document named `__RoseMcpXamlStubReport.g.cs`
(`src/RoseMcp.XamlStubs/XamlStubReportChannel.cs:38-42`), read back by `XamlStubReportReader.ReadAsync`
out of `GetSourceGeneratedDocumentsAsync`.

**Why this shape.** Roslyn constructs a generator out of an analyzer assembly itself, so there is no
callback to hand it; source and diagnostics are the only outputs a generator has. Choosing source over
a diagnostic is right -- a diagnostic would appear in the user's build output.

**Is it sound under two workers or a reload?** Yes, and for a structural reason: this is not really
IPC at all, it is a value in a compilation. Two workers are two processes with two `Solution`s and two
generator instances; there is no shared state, no file, no name to collide. A reload produces a new
compilation and therefore a new document. The report is scoped to a `Project` by construction
(`ReadAsync(Project, ...)`), so it cannot be read against the wrong one. `rose_find_references` on
`XamlStubReportChannel.Marker` shows exactly three uses: the reader twice and the test that keeps the
two spellings in step.

**Versioning.** The two `const` strings are duplicated on purpose, so no assembly identity has to
agree across the analyzer load context -- which is the same version-matching problem the generator
exists to avoid, correctly identified. The *payload* shape is duplicated too, and is not covered by
`XamlStubChannelTests` beyond the marker and the hint name: a field added to `XamlStubReportPayload`
and not to `XamlStubReport` is silently dropped (`JsonSerializer` defaults, no
`UnmappedMemberHandling.Disallow`). That is the right failure mode here, though -- a status call has
to answer, and the reader already treats anything unparseable as "no report".

**Verdict: appropriate -- it is the only channel Roslyn offers**, and the reasoning for every part of
it is recorded where a reader will find it.

### 9. Tray -> Inspector (process start, then activation redirection)

**What crosses.** `--host`, `--port`, `--token <value>`, `--session`, `--target-pid`, as an argument
*list* with `UseShellExecute` off so nothing is re-parsed (`InspectorLauncher.Arguments:98-112`). A
second launch does not open a second window: `Program.Main` claims a single-instance key derived from
the target pid *before* `Application.Start`, and a non-primary launch hands its raw command line to
the running instance via `AppInstance.RedirectActivationToAsync`, waited on with
`CoWaitForMultipleObjects` so the STA pump keeps running (`Program.cs:56-100`). The receiving side
re-parses that raw line with `CommandLine.Split`, which implements the C runtime's backslash-quote
rule -- necessary, because a redirected activation arrives as one string rather than a parsed array.

**Lifetime and failure.** The hand-over is bounded at 5 s and falls through to becoming a second
window rather than exiting with none; single-instance registration failing means "carry on as the
primary". Both are the right default for a diagnostic window.

**Security.** The token is on a command line (IPC-04).

**Verdict: appropriate with fixes.** Launch-with-arguments is the right mechanism for a one-shot
hand-off, and the WinAppSDK redirection is the only way to get single-instance for an unpackaged app.
The `target-pid`-on-the-command-line reasoning -- the key has to be claimed before the broker has been
spoken to -- is a good example of a protocol detail existing for a lifetime reason. Move the token off
the command line.

### 10. Logs as an out-of-band channel

**What crosses.** Nothing between processes: each host writes
`%LOCALAPPDATA%/BinaryVibrance/RoseMCP/Logs/{Server,Worker,Tray,Inspector}/[{solution}-]{ts}.log`
(`RoseLogFile.DirectoryFor:43-52`), UTC in the name and in every line, twenty sessions kept per
component and pruned at startup because Serilog's own retention only prunes within one rolling base
name. A worker's file names its solution both readably and as a hash, because six worktrees of one
repository is the ordinary case here. Serilog is the sink and nothing more -- every call site keeps
its `ILogger<T>`, and `SelfLog` goes to stderr so a logging failure cannot corrupt stdio.

**As a channel, what it lacks.** Three things, in order of cost: no correlation id across the
broker/worker hop (BRK-15); no way for a reader of a workspace to find the worker's log file, though a
reader of a debug session gets `LiveAppInfo.HostLogPath` for exactly that reason (IPC-07); and no
shared notion of a session, so with the tray serving several agents "which call was this" is
unanswerable from the files.

**Verdict: appropriate with fixes.** Files are right -- a broker that shipped logs over its own IPC
would lose them exactly when the IPC is what failed. The gaps are all "add a field", not "change the
mechanism".

### 11. In-process: the tray window and the `ActivityLog`

**What crosses.** Nothing. `MainWindow` resolves `WorkspaceManager` and `LiveAppSessionManager` out of
the same container the broker was registered in (`MainWindow.xaml.cs:119,154-155`) and calls
`Describe()` on a poll. `App.xaml.cs:19` states the reason: there is no second copy of the state to
drift. `GET /admin/workspaces` serialises that *same* call, so the window and the endpoint cannot
disagree. `ActivityLog` lives in the broker because it is the only party that sees every call,
including the ones a worker never gets to answer, and the only one still around to say what happened
when a worker dies mid-call (`ActivityLog.cs:10-16`); it is explicitly live state for a UI and not an
audit trail, capped at eight recent per workspace.

**Verdict: appropriate -- and the most important non-decision in the file.** The obvious alternative,
a tray that pushes a copy of its state to a window, is what `the-inspector-is-a-client-of-the-broker`
rejects for the *inspector* and what the tray is spared by hosting the broker in-process. The two
windows in this product are deliberately different: the tray reads live objects because it is in the
same process, the inspector reads http because a debugger UI is a crash surface that must not take the
workspaces down. Both choices are recorded.

### 12. Serialization everywhere

**What crosses.** `RoseMcp.Contracts` records, with `required` members, on every boundary. No package
references at all on that assembly, which is what lets a WinUI app, an analyzer-adjacent worker and a
package-free test all reference it.

**How many serializers.** Three, on one hop. `CancellableToolCall` serialises arguments and
deserialises the `CallToolResult` envelope with `ContractJson.Options` (`:45-48`, `:104`, `:108`);
`WorkspaceWorker.SendAsync` and `LiveAppSession.SendAsync` then deserialise `StructuredContent` with
`McpJsonUtilities.DefaultOptions`; the stub generator uses bare `JsonSerializer` defaults. See IPC-05.

**DTO evolution.** No DTO carries a version, and `System.Text.Json` defaults mean an unknown property
is ignored and a missing one throws only when the member is `required`. So:

- *New Server relaying to old Tray:* safe, because the relay declares no tools and forwards
  `tools/list`. A call for a tool the tray lacks is refused by name.
- *New broker starting an old worker binary* (BRK-05 says this happens -- `ROSEMCP_WORKER`, or a stale
  `bin` found by `FindInRepository`, which picks the most recently written file it can see):
  **not** safe. A new `required` member on a result record is a deserialisation exception reported as
  "Could not read the worker's {tool} result", which names the consequence and not the cause. A removed
  argument is ignored silently and the worker does the old thing. See IPC-02.
- *New inspector against old tray:* a 404 per missing route; a missing `required` property throws
  inside `OperatorClient`.

**Verdict: appropriate with fixes.** Sharing the record between both ends (`OperatorRequests.cs:7-11`
states this as the reason the file exists) is the right instinct and should be the rule everywhere;
what is missing is the handshake that turns a shape mismatch into a sentence instead of an exception.

## Are the technologies and protocols appropriate? -- the short answer

Yes at eleven of twelve boundaries, and the twelfth is the *payload* of one of them rather than its
transport. Boundary by boundary, with the alternative weighed:

| Boundary | Right tool? | Alternative weighed, and why it loses |
|---|---|---|
| 1 client -> Server (stdio MCP) | Yes | None. This is the product's contract. |
| 2 Server -> Tray (http MCP) | Yes | A private relay RPC: needs its own tool list, progress, cancel, and lets the surface drift. MCP means the relay can declare nothing. |
| 3 Broker -> Worker (stdio MCP) | Yes | gRPC (schema + streaming, loses standalone-drivable workers and the test shape); named pipes + JSON-RPC-lite (same JSON, no tool metadata, must rewrite progress and cancel). |
| 4 Broker -> LiveApp (stdio MCP) | Yes | As above, plus the long poll needs nothing MCP does not already give. |
| 5 Inspector -> operator API (http REST, polled) | Yes | SSE/WebSocket: the event tail is already a 30 s long poll, so latency is identical; a push adds a second failure model and a resync to a window whose principle is "say what you could not read". State panes want the current value, which a re-read gives and a missed push does not. |
| 6 host -> tap (named pipe) | Yes | Files in a shared folder (the rejected predecessor) is worse and the record says why. The payload is one text contract in both directions since #PRNUM; why text rather than JSON or binary is in the same record. |
| 7 host -> resume stub (named pipe) | Yes | Nothing smaller exists for two messages, and the fail-safe is the design. |
| 8 worker <-> stub generator (generated document) | Yes | There is no alternative: Roslyn gives a generator source and diagnostics, and source is the right one. |
| 9 Tray -> Inspector (args + activation redirect) | Transport yes, **secret no** | The launch mechanism is right; a secret on a command line is not (IPC-04). |
| 10 logs (files) | Yes | Shipping logs over the IPC loses them exactly when the IPC is what failed. |
| 11 tray window (in-process) | Yes | A pushed copy of the state would drift; the decision to host the broker in-process is what buys this. |
| 12 serialization (System.Text.Json) | Yes | Protobuf/MessagePack would buy size and a schema, at the cost of the readable `/admin/workspaces` and `Contracts` having a package reference -- which is the thing that lets every host reference it. |

**On base64-in-a-tool-argument for a future hot-reload delta.** It would work and it is the wrong
shape. An EnC delta is three binary blobs (metadata, IL, PDB) that belong together, are sized in
hundreds of kilobytes, and are meaningless to a human reading a log or an activity row. Base64 in an
MCP argument inflates them by a third, puts them through three JSON round trips on the relayed path
(IPC-09), and makes every `ActivityLog` target string and every error message carry a wall of text.
The shape that fits what already exists: the worker writes the delta to a file under its own temp
area and the tool argument carries the *path* plus a hash -- the same move `XamlProviderSession`
already makes for the provider DLL, with the same sweep-on-start cleanup
(`SweepDeadSandboxFolders`). Worker and live-app host are on the same machine by construction (the
broker starts both), so a path is a legitimate reference. If they ever are not, that is the moment
to add a side channel, and the pipe framing in `tap_channel.h` is already the design for it.

## Findings

### ~~IPC-01 The tap's request side does not escape what its reply side unescapes~~
**#PRNUM.** A tab or a newline in a property value mis-framed the edit and mis-keyed its status, so an
edit that landed reported that it had not. Both directions share one escaping contract, and a test
holds the provider's half against the host's.

### ~~IPC-02 Nothing checks that a child process is the same build as its parent~~
**#295.** Four hosts reported a version and nothing read one, so a child from a stale build answered
as whatever it was and the mismatch surfaced as a missing field or an unknown tool. Both hops that
launch a child compare it now, and say so rather than refusing.

### ~~IPC-03 The tap pipe is reachable by every packaged app, and the greeting proves nothing~~
**#PRNUM.** Whatever reached the pipe first was accepted as the provider and could answer with rows of
its own. A provider is refused unless it greets with a key minted for its session.

### IPC-04 The operator token travels on a command line the threat model treats as readable
- **Severity:** Medium
- **Effort:** M
- **Where:** `src/RoseMcp.Broker/InspectorLauncher.cs:98-112` (`--token {token.Value}`), `:71-86`
  (`CommandLine`, for **Copy inspector command**); `src/RoseMcp.Inspector/Program.cs:34,56-71`
  (the raw line is handed to another process on redirection);
  `docs/decisions/the-inspector-is-a-client-of-the-broker.md` ("Why a token and not loopback-only")
- **What:** The decision record's argument for the token is precise and correct: "Loopback is not a
  boundary on a developer machine: every process on it, including the target being debugged, can
  reach a loopback port. The operator surface reads memory out of an attached process and can move
  it, so 'anything local' is the wrong audience." The token is then delivered as an argument on the
  inspector's command line, where any process running as the same user can read it -- `Win32_Process`
  exposes `CommandLine` for the caller's own processes without elevation. The audience the token
  excludes is therefore the same audience that can read it. `HandedOver` widens it slightly: a second
  launch passes its whole raw command line, token included, to the running instance.
- **Why it matters:** The argument for the token is the argument against this delivery, verbatim.
  The consequence is not theoretical -- the operator API can set a breakpoint, read frames, evaluate
  and drive the XAML provider in any session on the machine. It is a developer-machine surface and
  the severity reflects that, but the decision record makes a claim the code does not keep.
- **Suggested change:** Pass the token out of band. Cheapest correct option: the tray writes the
  token to a file under `%LOCALAPPDATA%/BinaryVibrance/RoseMCP` with a user-only DACL and passes the
  *path* on the command line; the inspector reads it and the tray deletes it on exit. An environment
  variable on the child's `ProcessStartInfo.Environment` is a smaller change and a smaller
  improvement (still readable with `PROCESS_VM_READ`, but not from a process listing). Either way,
  **Copy inspector command** should copy a command that names the file rather than the secret.
  Whichever is chosen, record it in the decision, because the current record argues for a property
  the implementation does not have.

### IPC-05 Three JSON serializer configurations on one hop
- **Severity:** Low
- **Effort:** S
- **Where:** `src/RoseMcp.Broker/CancellableToolCall.cs:45-48,104,108`;
  `src/RoseMcp.Broker/WorkspaceWorker.cs:29-34,432`; `src/RoseMcp.Broker/LiveAppSession.cs:19,648`;
  `src/RoseMcp.Contracts/ContractJson.cs`
- **What:** One broker-to-worker call touches three option sets. Arguments are serialised with
  `ContractJson.Options` (web casing, string enums); the `CallToolResult` envelope is deserialised
  with `ContractJson.Options`; `StructuredContent` is deserialised with
  `McpJsonUtilities.DefaultOptions`. Each has a comment defending itself and neither comment mentions
  the other: `ContractJson` says it exists so two hosts cannot disagree, `WorkspaceWorker` says the
  SDK's own options are used because "anything that differs -- string enums being the one that bit --
  turns a working call into a deserialisation failure at the boundary". Both are describing the same
  bug from opposite ends.
- **Why it matters:** It works today because the SDK's defaults and `JsonSerializerDefaults.Web`
  happen to agree on casing, and because enums appear in results (SDK options) rather than in
  arguments. The next enum-valued *argument*, or the next SDK release that changes a default, breaks
  one half of a hop that looks like one thing. The comment a reader trusts depends on which file
  they opened.
- **Suggested change:** One `RoseWire.Options` used for everything that crosses a Rose boundary,
  built from `McpJsonUtilities.DefaultOptions` with `JsonStringEnumConverter` added, and
  `ContractJson.Options` becoming an alias of it. A unit test that serialises a record carrying an
  enum with each option set and asserts the two strings are identical is the thing that keeps them
  from drifting again.

### IPC-06 The UWP coordination pipe takes the framework's default DACL and a non-cryptographic name
- **Severity:** Low
- **Effort:** S
- **Where:** `src/RoseMcp.LiveApp/Debugging/UwpStartupCoordinator.cs:25-27`
- **What:** `new NamedPipeServerStream(_pipeName, ...)` with no `PipeSecurity`, so it gets the
  default named-pipe descriptor; the name is `rose-uwp.{pid}.{Random.Shared.Next():x8}`.
  `Random.Shared` is not a cryptographic source and the pid is public, so the name is a 32-bit guess
  from a known prefix. Its sibling in the same process -- `XamlProviderPipe` -- writes an explicit
  DACL and uses `Guid.NewGuid()`, with a comment about why the name is shaped the way it is.
- **Why it matters:** A process that connects first sends `{pid} {tid}` and the host arms its
  runtime-startup notification against whatever it was told, then resumes a thread in a process it
  did not launch. The window is short (`ConnectTimeout` is 20 s, once per UWP launch) and the
  attacker must already run as this user, so the severity is low -- but the inconsistency is the
  finding: two pipes in one host, one of which had its security thought about.
- **Suggested change:** `NamedPipeServerStreamAcl.Create` with a current-user-only DACL, and
  `Guid.NewGuid()` for the name. Both are one line, and `XamlProviderPipe.Listen` is the template.

### IPC-07 A worker's log file is never named to anyone, though a live-app host's is
- **Severity:** Low
- **Effort:** S
- **Where:** `src/RoseMcp.Contracts/WorkspaceSummary.cs` (no log path);
  `src/RoseMcp.Contracts/LiveAppInfo.cs:70-74` (`HostLogPath`, with the reason);
  `src/RoseMcp.Logging/RoseFileLogging.cs:20-26` (`Destination`)
- **What:** `LiveAppInfo` carries `HostLogPath` precisely so "a reader looking at a session can open
  the log that explains it rather than guessing which of twenty files in the folder is the one".
  `WorkerInfo` and `WorkspaceSummary` carry nothing equivalent, although the worker has
  `RoseFileLogging.Destination` sitting in a static and `rose_worker_info` already exists as the
  cheap self-report the broker calls on connect.
- **Why it matters:** A worker's file name is `{solution}-{hash}-{timestamp}.log`, and twenty
  sessions are kept, so picking the right one for a worker that started forty minutes ago is exactly
  the guessing the live-app field exists to remove. The tray window has a workspace row and nothing
  to open.
- **Suggested change:** Add `LogPath` to `WorkerInfo`, populate it from `RoseFileLogging.Destination`,
  surface it on `WorkspaceSummary`, and make the tray's workspace row open it -- the session row
  already does the same thing.

### IPC-08 Only one of the two child-session types notices its child exiting
- **Severity:** Low
- **Effort:** S
- **Where:** `src/RoseMcp.Broker/WorkspaceWorker.cs:271-301` (`WatchForExit`) versus
  `src/RoseMcp.Broker/LiveAppSession.cs:113-137` (`RefreshInfoAsync` only)
- **What:** `WorkspaceWorker` arms `Process.Exited` from the pid the worker reports, with a comment
  explaining that without it a worker that crashed while idle went on describing itself as alive
  until somebody called it -- "exactly the situation a person is looking at that window in".
  `LiveAppSession` learns the host's pid on connect (`LiveAppInfo.HostProcessId`) and never opens a
  handle; liveness is only ever inferred from a failed call.
- **Why it matters:** The same reasoning applies and more strongly: a debug session is *usually*
  idle, because the target is running and nobody is asking it anything. A live-app host that crashed
  shows as `Starting` or with its last state until the next poll fails -- and `RefreshInfoAsync` is
  the poll, so it is noticed, but a session in a window with no pane open is polled by nothing.
- **Suggested change:** Lift `WatchForExit` into a small shared helper and arm it in
  `LiveAppSession.RefreshInfoAsync` from `HostProcessId`. It is the same eight lines, including the
  `HasExited` re-check for the arm-after-exit race. Naturally part of the "two classes, one shape"
  consolidation BRK-06 already proposes.

### IPC-09 A relayed result is serialised three times and deserialised twice
- **Severity:** Low
- **Effort:** M
- **Where:** `src/RoseMcp.Broker/WorkspaceWorker.cs:425-436`, `src/RoseMcp.Broker/WorkspaceManager.cs`
  (`Attribute<T>`), `src/RoseMcp.Server/TrayRelay.cs:167-182`
- **What:** On the relayed path a `rose_read_generated_document` or a 200-hit
  `rose_find_references` is: serialised by the worker, deserialised by the broker into `T`,
  re-serialised by the SDK for the http response, deserialised by the relay into a `CallToolResult`,
  and written to stdout. The broker's round trip through `T` exists only so `Attribute<T>` can stamp
  `Revision` and the workspace name onto it.
- **Why it matters:** It is the honest price of MCP-on-internal-hops and nothing in the repository
  names it. It is invisible on small results and is the reason a large one feels slower than the
  Roslyn work behind it; it is also the argument that decides the hot-reload delta question (see the
  note above) and so is worth having measured rather than assumed.
- **Suggested change:** Measure first -- one integration test timing a large `rose_outline` through
  the relay against the same call direct to a worker. If it is material, attribute by adding two
  properties to the `JsonElement` rather than round-tripping through `T`: the broker needs `T` only
  for the two tools that read a result's contents (`rose_workspace_status`), and everything else is
  a pass-through with two fields added. That also removes the `is WorkspaceStatusReport` runtime
  check BRK-12 objects to.

### IPC-10 The tray-liveness probe asks a different surface from the one it needs
- **Severity:** Low
- **Effort:** S
- **Where:** `src/RoseMcp.Server/TrayRelay.cs:116-130`
- **What:** `IsListeningAsync` decides whether to relay by issuing `GET /admin/workspaces` and
  treating any non-2xx as "no tray". That route is not the MCP endpoint, has different middleware
  (BRK-02 is one consequence of exactly that), and its handler serialises `WorkspaceManager.Describe()`
  -- every workspace, its configuration, its project failures, its running and its eight most recent
  activities -- purely to answer "are you there".
- **Why it matters:** Two distinct costs. Correctness: the probe can succeed while the MCP endpoint
  refuses, or fail while it would have answered, and the log line the user sees says "no tray" for
  both. Cost: on a machine with several large solutions loaded this is the most expensive GET the
  broker serves, issued at the start of every stdio session -- which for an agent host means every
  time it restarts a server.
- **Suggested change:** A dedicated `GET /admin/ping` returning `{ "version": ... }` and nothing
  else, mapped beside the MCP endpoint in the same `MapRoseBroker` (BRK-08) so the two cannot be
  mapped separately. The version in the response then also gives the relay the handshake IPC-02 asks
  for, at no extra round trip.

## Pit-of-success inversions

1. ~~Whether a field is escaped depends on which side wrote it.~~ **#PRNUM.** One text contract owns
   escaping, rows, the request id and the greeting, in both directions, and a test holds the provider's
   half against it.

2. **Rule today:** "the child is the same build as the parent", believed rather than checked.
   **Mechanism:** a `HostHandshake.Require(McpClient, Assembly, string resolvedFrom)` called by
   `WorkspaceWorker.StartAsync` and `LiveAppSession.StartAsync` -- there are exactly two places, and
   a third child added later cannot connect without passing through one of them if the client is
   only ever constructed inside that helper. `HostVersion` stops being a value nobody reads.

3. **Rule today:** a call can be traced across a hop by reading two timestamps and hoping.
   **Mechanism:** a correlation id minted at the outermost boundary, carried in `_meta` next to
   `rosemcp/originDirectory` -- `CancellableToolCall.Meta` is the one place that writes `_meta`, so
   one line there puts it on every internal hop -- and pushed into a logging scope at both ends, so
   every line in every file carries it without any call site remembering. `CallOrigin` is the
   pattern to copy: an ambient value set by the filter, read where it is needed. This is BRK-15's
   suggestion made structural rather than per-log-line.

4. **Rule today:** "loopback plus a token" is the policy, spelled out four times (`ServerOptions`,
   `LoopbackOrigin`, `Program.RequireToken`, `OperatorApi`'s branch) with two loopback lists and two
   401 shapes (BRK-17). **Mechanism:** one `LocalHttpPolicy` type owning the host list, the Origin
   decision, the 401 body and the `WWW-Authenticate` header, consumed by the single `MapRoseBroker`
   that BRK-08 asks for. A new endpoint is then gated by being mapped, not by being remembered.

5. **Rule today:** a secret is passed wherever is convenient -- a command line here, a log line there
   (`Program.cs:164-171`), a clipboard entry from the tray. **Mechanism:** an `OperatorToken.Handoff`
   type with exactly two members: `WriteForChild(ProcessStartInfo)` and `ReadFromParent()`. Nothing
   else can obtain `Value`, so a future surface that wants the token has to add a hand-off shape
   rather than interpolate it into a string. `Value` staying public is what made IPC-04 easy to
   write.

6. **Rule today:** every boundary decides for itself what "the far side is gone" means, and two of
   the three do it by matching an assembly-name string (BRK-06). **Mechanism:** as BRK-06 says, one
   `RoseMcp.Mcp` library -- and this review adds one member to its proposed contents:
   `ChildProcessWatch`, so IPC-08 cannot recur when a fourth child type is added.

7. **Rule today:** a result record can grow a `required` member and break an older peer with a
   message about deserialisation. **Mechanism:** either the handshake in (2) makes the mismatch
   impossible to reach, or `Contracts` adopts the rule that a member added after a type ships is
   never `required` -- enforceable by an analyzer, or by an approval test over the public shape of
   `RoseMcp.Contracts` that has to be re-approved deliberately.

## Open questions for Steve

- The AppContainer grants on the XAML pipe are for "the only identity a provider inside a packaged
  app has". Is there a narrower one available -- the target's own package SID, say, which the host
  knows because it activated the app? If there is, IPC-03's nonce becomes belt to that braces rather
  than the only control.
- Was the operator token's command-line delivery a considered trade (the inspector must be usable
  from a copied command, so the secret has to be typeable) or just the first thing that worked? The
  answer decides whether IPC-04's fix is a file path or an environment variable.
- `ROSEMCP_TOKEN` currently gates the whole http server including MCP, and the operator API shares
  it. If #213 is fixed by having the relay send it, does the relay then hold a secret that also
  unlocks the operator surface -- and is that intended, or should the two separate?
- ~~Is there an appetite for JSON inside the tap frame?~~ **Declined, #PRNUM.** Measured, it would buy no
  speed and cost the one property a lock-step pair needs -- a stale copy refused rather than read. The
  reasoning is in the pipe's decision record.
- `rose_live_app_events` is a long poll through two hops (client -> broker -> host). Over http with
  several agents, does a thirty-second wait hold anything the broker needs? `WorkspaceManager`'s
  `_gate` is per workspace and a debug session is not a workspace, so I believe not -- but BRK-03
  makes me want it confirmed rather than assumed.

## Rose dogfooding notes

The workspace was already warm (revision 1) and reported Degraded for the reasons in the brief;
nothing in these answers was affected by it.

| Tool | For | Outcome |
|---|---|---|
| `rose_find_references` `HostVersion.Of` (`includePreviews=false`) | Whether any code *reads* a host's reported version, which is the whole of IPC-02 | **The finding, in one call.** Seven hits: four hosts setting their own, three tests. Grep for `HostVersion` gives the same five files but cannot distinguish "sets" from "reads", and I would have had to open each one. `includePreviews=false` made the answer scannable. |
| `rose_find_references` `ContractJson.Options` (`includePreviews=false`) | How many boundaries share one serializer, for IPC-05 | Worked, 29 hits across six projects, which is what showed that `WorkspaceWorker` and `LiveAppSession` are the two production files *not* in the list -- an absence a grep cannot report. Minor defect: the `definitions` array lists the same property three times (`:19:38` twice and `:19:48`), presumably the property, its getter and its initialiser. Cosmetic, but it made me re-read the file to check there was not a second `Options`. |
| `rose_find_references` `CancellableToolCall.InvokeAsync` | Whether all three internal hops really cancel downstream | Worked, exactly three call sites, one per hop. This is the call that turned a claim in a comment into a verified strength. |
| `rose_find_references` `XamlStubReportChannel.Marker` | Whether the generator's report channel has readers I had not found | Worked, three hits, and the third was the test that keeps the two spellings in step -- which is the evidence for the "appropriate" verdict on boundary 8. |
| `rose_find_implementations` `System.IAsyncDisposable` | "Which Rose types own a connection that has to be closed" | **Worked but was unusable for the question.** 116 matches, truncated at 40, and the first 40 are all ASP.NET, Blazor, JSInterop and the MCP SDK -- not one Rose type made the page. `rose_find_references` has a `project` filter; `rose_find_implementations` has none, and no way to say "source only". For a framework interface the only interesting question is what *this solution* implements, and the tool cannot be asked it. Worth filing under tool-surface: add `project`, or an `inSourceOnly` flag, or rank source declarations ahead of metadata ones. I fell back to `grep -rn "IAsyncDisposable" src/`. |
| `rose_symbol_info` `XamlProviderPipe.Request` | The documented contract of the tap request, to quote it accurately | Worked; `declarationSpans` giving the true first and last line is genuinely better than a preview. Two nits: with `includeSource` left off the result still carries `"source":[]`, which reads as "no source" rather than "not asked for"; and `documentation` comes back as the raw XML element with escaped entities and literal `\r\n`, so quoting a sentence from it means un-escaping by hand. |
| `rose_search_symbols`, `rose_outline` | Not reached for | Both would have lost here. This review needed *comments and reasoning*, not shapes -- almost every verdict above rests on a doc comment explaining why a protocol is the way it is, and `sed -n` ranges over the file are the only way to read those in context. That is not a defect: it is the honest boundary of what a semantic index is for. |
| `rose_diagnostics` | Not reached for | Nothing was edited. |

One process note for the next reviewer: `rose_find_references` with `includePreviews=false` is the
right default for a review and is not the tool's default. The first call I made without it returned
previews I did not read for hits I only wanted counted.
