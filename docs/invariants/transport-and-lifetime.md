# Transport, relay, and process lifetime

Read before touching stdio or http transport, `TrayRelay`, progress reporting, cancellation, or anything that ends a process.

- **Nothing writes to stdout in stdio mode** except protocol frames. All logging goes to stderr,
  and to a file. A stray `Console.WriteLine` corrupts the stream, and the failure looks like a
  protocol bug. `StdoutRuleTests` holds the rule against the source of every project a stdio host
  loads -- a host is found by its `WithStdioServerTransport` call, and what it loads by following
  its project references -- and names the file and the line of each of three things: a stdout write
  in any spelling `Console` allows; console logging that leaves a level on stdout; and a host
  builder that registers the default providers, a stdout console logger among them, without those
  providers being cleared. A host builder is a `Create*Builder` on `Host`, `WebApplication` or
  `WebHost`, or a `HostApplicationBuilder` constructed directly, unless its arguments set
  `DisableDefaults`. Cleared means tied to that builder: `ConfigureLogging` on its own call chain or
  on the local it is assigned to, with a lambda that clears its parameter; or, in the same function,
  `local.Logging.ClearProviders()`, or `local.Logging` passed to a method of the same file in a
  parameter that method clears. A clear on another builder, in a lambda, or in a host a called method
  builds for itself does not count. Every branch of every `#if` is read. It reads syntax rather than
  binding, so a write that names none of these -- a stream opened some other way -- gets past it;
  that is what the logging test below is for. `RoseMcp.Logging` adds the file sink -- Serilog behind the existing
  `Microsoft.Extensions.Logging` call sites, never a console sink, and there is a regression test
  asserting the pipeline writes nothing to stdout at all. Logs land in
  `%LOCALAPPDATA%/BinaryVibrance/RoseMCP/Logs/{Server,Worker,LiveApp,Tray,App}/[{solution}-]{yyyyMMdd-HHmmss}.log`
  -- under their own `Logs` folder, separate from the install that shares the same vendor/product
  parent, so promoting a build never touches a session's log files. UTC in the name and UTC in
  every line so the two cannot disagree. A worker's file names the
  solution it owns, hashed as well as spelled, because two worktrees of one repository share a
  solution name. Twenty sessions are kept per component, pruned at startup -- Serilog's own
  retention cannot do it, since it only prunes within one rolling base name and every session
  here has its own.
- **A call is one id in every process it crosses, and only while it lasts.** The outermost Rose
  process to see a tool call mints a `CallCorrelation` id -- the stdio relay, else the broker, else a
  child driven directly -- and every later process takes the one it was sent. An operator API request
  is an entry point as well, and gets an id of its own, since an inspector's step is forwarded to a
  live-app host like any tool call. The first call filter in each pipeline sets it, and the operator
  group's first endpoint filter; `CancellableToolCall` sends it on in `_meta["rosemcp/correlationId"]`,
  read from the ambient, so a hop added later carries it without asking; the file sink writes it on
  every line, a dash outside any call. A line can then be matched across files by one search rather
  than by timestamp and tool name, which fails the moment two sessions ask one worker the same thing.
  An incoming id is accepted only in the shape a Rose process mints -- lowercase hex -- and anything
  else is replaced, because whatever is read here is written verbatim into every line of the call.
  The id ends when its call does: whatever inherited the call's execution context stops reporting it
  then, so a loop started by the first call of the day does not file its lines under that call until
  the process exits. Work that is not the call's at all -- a poll loop, a sweep, a child's transport,
  whose read loop carries every later call's replies -- is started through `Detached`, inheriting no
  ambient of the call that started it. Work a call queues and waits on is the opposite case: a queue
  whose loop runs on its own context -- the worker's `WorkspaceSession` writer, which runs every
  reload, restore and mutation -- carries `CallCorrelation.Capture()` on each item and resumes it
  around the item, or every line of that work is written as though no call had asked for it. Each
  child names the log file it writes (`WorkerInfo.LogPath`,
  `LiveAppInfo.HostLogPath`), so the row that shows it can open the right file rather than a folder
  of twenty. See [the decision](../decisions/a-call-is-traced-by-an-id-minted-where-it-enters-rose.md).
- **A stdio session relays to a tray when one is running.** `TrayRelay` forwards both listing and
  calling, declaring no tools of its own, so the surface cannot drift from the tray's. It sends the
  directory its client started it in as `_meta["rosemcp/originDirectory"]`, and an id it mints for its own
  life as `_meta["rosemcp/session"]`, and changes nothing else. The id is what tells relayed clients apart:
  the http transport serves each request without a session, so without it every relayed client is the same
  caller, and one agent's acknowledgement is every agent's. It scopes acknowledgements, and which session a
  person shared their debug session with: the share is matched on the key the client table files the relay's
  session under, so only that session's calls reach it. An agent's own debug session is owned by the
  transport's session alone, and so is unscoped over stateless http even though this id reaches the broker
  with every relayed call; a session a person started is reached by no call they did not share it with.
  It sends the same id with its own listings and reads, so the broker never takes them for an unnamed
  client, and marks its own reads `_meta["rosemcp/relayRead"]` so they are not counted as the session's
  calls. Once its client has made a request it also tells the broker whom it serves, at
  `/admin/relays/client` -- folder, roots, branch, process, `clientInfo`, `ROSE_AGENT_LABEL`, and whether
  the client holds the broker's tools -- detached from the request, bounded by a budget and the relay's
  lifetime, and never failing anything; again after every reconnect, since a restarted broker remembers
  nothing; and whenever any of it changes. See
  [the decision](../decisions/a-relay-names-its-session-to-the-broker.md).
  An http broker serves every repository on the machine and, with two solutions open, cannot know
  which one a bare call means; a stdio process cannot not know. Relaying buys both that and one warm
  worker per solution shared across sessions, and it keeps the RoseMCP windows reading one live
  `WorkspaceManager` rather than a pushed copy that could drift. With no tray, a relay running from an
  install's relay folder starts the install root's server as a child with `--no-relay` and relays to
  it, rather than hosting a broker from its own assemblies, which are as old as the last relay
  protocol change; it ends when that child does, which a deploy makes happen, rather than starting
  another while the deploy may still be copying over its files. An installed relay never hosts the
  broker, whatever fails: `RelayInstall.RoutesFor` sends a tray that refuses the session on to the
  child broker, and where that will not start the relay ends with the reason on stderr. A relay that
  hosted would hold workers a deploy waits for while keeping the relay running, and the deploy would
  abort. Anywhere else -- a build output, a Linux install -- the process owns its workers as before.
- **A relay outlives the broker it forwards to, and tells its client only what changed.** It holds
  `/admin/relays/watch` open, which a killed broker ends at once, and reconnects by itself, so an idle
  session hears of a deploy without making a call. A watch ending is a reason to look, not proof: the
  broker is asked first -- any answer counts, since the 2026-07-28 revision has no `ping` -- because
  reconnecting to a live broker opens a new MCP session, and the debug sessions belong to the old one.
  A watch that reached another broker instance is the exception, and `WatchEnd` says which ended it:
  the old connection reaches the new broker too, which answers it, so asking would keep the relay
  there for good, and that watch reconnects without asking. Calls and the watch reconnect through one gate, and the comparison of the new broker's tools and
  instructions with what the client last listed happens inside it, so one restart is one comparison
  and at most one `notifications/tools/list_changed`. That notification goes through `ToolListSignal`,
  the SDK's fan-out for a changed collection, never by hand: a 2026-07-28 client asks for it on a
  `subscriptions/listen` stream and must get it tagged with that subscription. The relay advertises
  `tools.listChanged` itself and gives the broker's instructions as its own, because the client never
  sees the broker's handshake. The restart line is claimed from `PendingNotice` only once a result is
  in hand, so exactly one result carries it and a failed call leaves it for the next. A reconnect
  publishes the new connection before it has read the broker behind it, so calls wait on that reading
  (`_adopted`) before taking the connection: one answered in between would carry no line, and the
  client would have an answer from a broker it was not told had restarted. The broker's
  instance id, not its build, says whether it restarted. The watch and the reconnect loop are
  `Detached`, bounded by the relay's lifetime, and disposing the relay cancels them and waits for them
  before closing the connection -- which, for a child broker, ends the child and its workers. See
  [the decision](../decisions/a-deploy-replaces-the-relay-only-when-its-protocol-changes.md).
- **The relay contributes a fact, never a conclusion.** It used to resolve that directory to a
  solution and write the answer into the `workspace` argument, which failed three ways at once. It
  matched the argument by name, and `rose_workspace_open` alone called its own `path` -- so an
  explicitly named solution was read as an omission and refused for an ambiguity the caller had
  already settled. It resolved before reading any argument, so a call carrying a `filePath`
  containment would have decided was refused too, killing every `rose_*` tool in a multi-solution
  root. And it ran for every tool, so `rose_debug_launch_uwp`, which takes no workspace and needs no
  solution, failed with a solution-ambiguity error. Resolution belongs where every path converges --
  the broker also serves clients with no relay in front of it -- so the relay sends what only it
  knows and the broker ranks it against everything else.
- **A cancelled call is cancelled all the way down, and the order is the trick.**
  `McpClient.CallToolAsync` honours a token by giving up locally and never telling the far side, so
  a cancelled analyzer run kept its worker busy five seconds longer -- which, because reads are
  ordered behind one another, is the delay before the caller's next question can start.
  `CancellableToolCall` builds the request itself, since the id it needs is one `CallToolAsync`
  never reveals. Send the cancellation *before* abandoning the wait: over http the request is a
  streaming POST, and tearing it down three milliseconds early was enough for the far side to treat
  the request as merely gone, never cancel its own token, and finish the work anyway.
- **A child's handshake never times out its `server/discover` probe on its own.** The SDK probes
  with `server/discover` and falls back to `initialize` after `DiscoverProbeTimeout`, and over stdio
  that timeout reaches the send, which writes a message and its newline as separate cancellable
  writes. Cancelled between them, the probe leaves its JSON on the child's stdin without a newline;
  the fallback `initialize` is appended, the child rejects the joined line, and the broker waits out
  the full handshake budget for a child that is alive and never received anything it could parse. A
  busy test run's thread pool delays a write past the five-second default. Every client the broker
  opens on a child takes its options from `ChildHostHandshake`, which leaves the probe bounded by the
  handshake budget alone -- the children ship with the broker and answer `server/discover`, so the
  fallback could only ever be reached by accident.
- **Progress can arrive out of order over http, and no queue here can fix it.** The SDK dispatches
  notification handlers concurrently on an SSE transport, so a four-project status was seen
  arriving 50, 75, 0, 25 -- already unordered before any of our code sees it, and MCP progress
  carries no sequence to sort on. Do not "fix" it by clamping to the highest value seen:
  `SharedWorkProgress` deliberately fans a reload's own scale into calls already in flight, so
  resets are legitimate and a clamp pins those bars at whatever the last operation reached. The
  stdio hop is ordered, being one reader.
- **Every slow path says where it has got to.** Workers report progress on the operations that take
  real time, the broker records every call it forwards in an `ActivityLog`, and `WorkspaceSummary`
  carries both the running and the recently finished ones. A RoseMCP window and
  `GET /admin/workspaces` read that same model, so they cannot disagree. A percentage is per
  operation and only ever rises; no percentage means "cannot say", which shows as an indeterminate
  bar rather than one frozen at a number that has stopped meaning anything. A worker tool reaches
  its workspace only through `WorkspaceCalls`, which keeps the call listening to the shared load and
  reload reports for the whole wait: a tool that let go early would answer correctly and show a
  client nothing through a cold load, which reads as a hang. No tool type is given the host or the
  shared progress, and `WorkspaceCallsTests` fails one that is.
- **A worker is asked for status the moment it connects.** Progress notifications only exist inside
  a request, but a worker starts loading when the process does. With no call in flight the first
  half-minute of a large solution is invisible -- which is exactly what a reload from the tray
  produces, since no client is waiting on it. The priming call pays for nothing the first real call
  would not have.
- **Every stdio process dies with its client, and takes what it owns.** A worker, a live-app host
  and a stdio `RoseMcp.Server` all exit when their stdin closes, and the rule is the same one three
  times: whatever the process owns goes with it, because nothing else knows it is there. A server
  ends its workers, a relay ends a broker it started as its child, and a host ends a target it
  *launched* -- never one it attached to, and never after a detach, which is the request to leave the
  app running. Orphaned Roslyn hosts holding a
  solution in memory are invisible until the machine is out of RAM; an orphaned probe app is worse
  than invisible, because the probe is single-instance and the next run finds an app it did not
  launch and treats it as its own.
- **A long-lived broker evicts an idle worker only when nothing can be about to call it, and
  watching a workspace is not using it.** `BrokerOptions.IdleEvictionAfter` is set by the tray and
  by the http server, which outlive every client, and left off over stdio, where the workers end
  with their one client anyway. A sweep stops a worker unused past that limit, and one whose
  solution file has been missing past `SolutionGoneGrace` -- a removed worktree, whose worker
  otherwise runs on against nothing for the life of the broker. Use is a tool call routed through
  `CallAsync`; `rose_workspace_status`, `rose_workspace_list`, the tray's `Describe` polling,
  opening a workspace that is already open and the priming status call never restart the idle
  clock, or a session that polls would keep every solution on the machine warm. The clock starts
  when the first load finishes -- not as use, but because that is when there was first something
  to use; counted from process start, a slow load would be evicted soon after it became ready, and
  one longer than the limit the moment it did. The worker holds itself for its own load and lets go
  only after the clock has restarted, because the report that ends the load lands before the clock
  can move, and a sweep in between would see a loaded worker idle since its process started. A
  caller takes its worker *held*, under the gate the sweep decides under, and the sweep reads
  everything again under that gate before acting, so a worker is never stopped between being handed to a call and
  being called -- for a write, which is not retried, that would be a failure with nothing wrong. A
  busy or loading worker is never evicted. An evicted worker stays registered, stopped as
  `Evicted`, with the reason filed in its activity history, so the tray, `GET /admin/workspaces`
  and `rose_workspace_list` can say why a workspace went cold; the next call replaces it as it
  replaces a crashed one, and the row goes once it has been stopped as long as the idle limit.
  Status on a stopped row answers from the row -- `Unloaded` (`Faulted` after a crash), revision 0, the
  reason as a degraded reason, and that nothing was started -- because starting a worker there would reload the solution and wipe the reason, and
  a session checking status now and then would keep it warm and never learn it was evicted. A
  stopped row is not *open*: anything saying which workspaces are open or loaded -- the routing
  failure's list, a change's sibling notice, the tray icon's tooltip -- reads `IsAlive`, not the
  registry. A stopped row reports no memory figures either: Windows hands a dead process's id to
  the next process it starts, so sampling it would show somebody else's memory as the solution's.
  The manager stops the sweep, and waits for it, before it disposes the gate the sweep
  takes. The sweep runs in every host once a worker has started, eviction or not, because it also
  replaces a worker holding a rebuilt analyzer when a person has asked for that -- a setting read
  when some worker holds one, so a sweep with nothing rebuilt reads no file. That reload follows the
  eviction's rules: decided again under the gate, never for a worker held, busy or loading, only
  once it has gone a minute unused since both its last call and the broker hearing of the rebuild,
  and never for a worker already replaced or whose solution file has gone. Eviction is decided
  first, since the call after an eviction starts a worker on the new build anyway. Only the manager
  stopping ends the sweep, decided by its token rather than by the exception's type: the sweep starts
  workers, a start that gives up can surface as a cancellation of its own, and a loop ended by one
  would stop eviction and the idle reload for the life of the broker with nothing to say so. The broker hears
  of a rebuild from the worker's info, refreshed after every call, and a call ending while a refresh
  is in flight asks for one more rather than being dropped, or a rebuild found by that call would
  never reach the sweep. The same holds for a key: `workspaceKey` still names a stopped row, whose path is known,
  but a refusal naming the workspace a key belongs to calls it loaded only while its worker serves,
  and promises a reload only while its solution file exists -- a removed worktree's row outlives
  the file, and loading it again would only fail.
- **What the broker holds for an agent session goes when the session does.** The broker outlives every
  client, so anything it keeps per session -- `WorkspaceHealth`'s acknowledgements and the proposals waiting
  for the person -- is a leak unless something ends it. Nothing tells an http broker a client has gone, so a
  session is taken to have ended when it has made no call for the MCP transport's own idle limit, two
  hours, and a host that does learn of an end calls `ForgetSession`. A relayed client is a session by the id
  its relay sends (`CallSession.Of`). A call with no session id is the one client of a stdio broker, whose
  session is the process's and never goes quiet; on a broker serving several it is any of them, and session
  state taken from it would be every client's, so it is refused rather than shared. `ConnectedClients`, the
  table of who is connected, holds a row per client under the same rule with a sooner end where the broker
  knows one: a relayed client's row stays while its relay holds the watch and goes two minutes after the
  later of the client's last word and the relay letting go -- counted from the stop, so a quiet session
  whose relay is only re-opening its watch keeps its row -- an unnamed client's after the two idle hours, and the table is capped, since every row is
  made at a client's say-so. Calls without a session share one unnamed row rather than being refused,
  because a row says only that somebody called. A note the person sent a session is the exception that outlives
  its row: it is the person's words rather than the session's state, so once its session has gone it reads as
  gone, is still delivered if that session calls again, and goes a day after the later of its session going and
  anything last happening to it, in a store bounded like the table.
- **Every tool call is recorded once, by the filter, and nothing else is.** `WithActivityRecord`, second
  in the broker's pipeline after the correlation id, makes a `CallRecording` ambient for the call and
  finishes it with the answer the client was given, the failure in the caller's words, or a
  cancellation; disposing an unfinished one records it as cancelled, so no path leaves a call listed as
  running. The routing adds to it only through `CallRecording.Current` -- a worker's call, a live-app
  session's call, the start of a debug session, and `WorkspaceManager.Attribute`, the one step every
  write's whole diff passes -- so whatever runs outside a call records nothing: an operator route, a poll,
  a sweep, a loop a call started, whose inherited recording ends when the call does. A relay's read
  marked `rosemcp/relayRead` is not recorded. Recording never fails a call and never waits on the disk:
  an entry goes to a bounded queue, dropped and logged where it is full, written by a `Detached` loop
  whose every disk step catches what it throws, since a loop that ended would leave every flush waiting
  for the life of the broker. A failed write appends to the same file on the next batch rather than
  starting another, or a full disk leaves an empty file per batch. Disposing the record completes the queue and waits, bounded, for the loop
  to write it; the host stopping flushes it first, for a host that stops without disposing its services.
  Each broker run writes files of its own, so brokers sharing the folder never share a file or a lock.
  See [the decision](../decisions/every-call-an-agent-makes-is-recorded-and-kept-on-disk.md).
- **A note rides on exactly one answer, and only on an answer.** `WaitingNotes`, inside the activity record's
  filter, claims the notes waiting for the calling session once the tool has answered -- under `AgentNotes`'s
  gate, so two calls answered together never both carry one -- and adds them to that answer's notices, marking
  each delivered with the call's activity entry. A refused, failed or cancelled call delivers nothing, since its
  error is written by the SDK after every filter has finished; a relay's own read carries nothing; and an answer
  the notices cannot be added to gives its notes back. So a note is never counted as delivered to an agent that
  could not have seen it. It is held under the key the client table files its session under rather than the row's
  id, because a row goes and is made again with a new id while the session is the same one, and a note to the one
  unnamed row is refused, since it could reach any client without a relay. See
  [the decision](../decisions/a-note-to-an-agent-rides-on-its-sessions-next-answer.md).
- **A window's long poll holds nothing past its request, and never holds up a stop.** The operator views
  a window reads -- clients, workspaces, activity, sessions, notes -- answer `after=<version>&waitSeconds=N` by
  `LongPoll` reading the view again every second, and on the activity record's, the client table's or the
  notes' `ChangeSignal`, until its version differs. A wait is the request's own linked token, a timer and a
  reference to the signal's current task, so it holds nothing on a singleton once the request ends.
  `IHostApplicationLifetime.ApplicationStopping` is linked into every wait and answers it with the view as
  it is, or a window's open poll would hold a deploy for the rest of its minute; the reader going away
  cancels it, and the operator filter answers nobody. A view's read stays broker-side -- no worker or
  live-app host is called, no load waited on, no workspace's idle clock touched -- because it runs every
  second for as long as a window is open. Every view goes inside the `/operator` group, where the token
  and the call id apply, and none on `/admin`, which nothing gates: memory and process ids included.
  See [the decision](../decisions/a-window-reads-the-broker-over-the-operator-api-and-waits-on-what-a-view-holds.md).
- **The RoseMCP app holds a poll only while a window can show what it reads, and never spins on a broker
  that is not there.** The RoseMCP window's `RoseFeed` is started when the window is on screen and stopped,
  cancelling every request it holds, when it is minimised or closed; a view not on screen does no work on
  what arrives, and the Activity view's own poll runs only while it is the view shown. Every long poll is a
  `ViewWatch`: a read that fails is reported, forgets its version and pauses before the next -- a refused
  connection answers at once, and without the pause a closed port is a core spinning for as long as the
  window stays open -- and `Kick` ends the pause, which is what a new token from the tray's next launch
  does through `OperatorClient.Reauthorise`. The client is the app's, shared by every window and disposed
  with none of them. An Inspector window still releases its hold before it closes, and its start page reads the
  running apps only while it is shown, cancelling that read and any attach or launch in flight when it is hidden
  or the window closes -- which the broker answers by ending a session that start made.
  See [the decision](../decisions/one-rosemcp-app-per-broker-holds-every-window.md).
- **A live-app session whose host has died is dropped by the session manager, and only that one.**
  A poll that finds the host's transport gone marks the session ended; nothing else would ever
  remove it -- no caller closes a session it can no longer reach -- so the registry would carry it
  and the poll would ask it how it is every second for the life of the broker. It is listed as
  `Ended`, with the reason filed on its row, for `BrokerOptions.EndedSessionGrace`, so the tray, an
  inspector and `rose_debug_list` show why before it goes; then it is taken out through the same
  teardown and gate as a close, its client disposed, and the drop said in the broker's log, since
  the row that would carry it is gone. Under that same gate it is remembered as dropped -- the
  latest few, scoped to each one's owners, the agent session a person shared it with among them -- so the next call naming it hears that its
  host died rather than that no such session is open, which reads as a wrong id or someone else's. A dead host is not polled again and not asked to detach --
  the debugger went with it. A host that is alive and reports its *target* as exited is a different
  thing and is kept: its event log is still readable, and closing it is the caller's act. The drop
  runs on the poll loop itself, before the tick's polls start, so a dropped session is never polled
  again by the tick that dropped it, and disposing the manager waits for a drop in progress. This
  applies over stdio as well as http: a dead host has nothing to read in either.
- **A debug session nobody will hold is ended where it was started, on every path.** Every start -- an agent's
  tool or a person's `/operator/debug` request -- goes through `LiveAppSessionManager`'s attach and launch, which
  end a host that started and was never registered, and take out through the close's own teardown a session
  that came up faulted or whose asker gave up before it was answered, with a token of their own rather than
  the one that was cancelled. A host left behind by any of those keeps its target under a debugger nobody can
  reach or detach. Two starts against one target at once are one start and one refusal, a process and the
  packaged app it runs as being one target under two names, and a target already being debugged -- by either
  name -- answers with its session wherever the asker may reach it. No tool call ends
  a session a person started: an agent it was shared with leaves it, and the debugger stays.
- **The one git the broker runs is bounded, contained and off every agent's path.**
  `CheckoutDistanceReader` counts how far a local build's checkout has moved on, for
  `/operator/hello` and nothing else. `GitCommand` redirects the child's output, so a stdio broker's
  stdout carries nothing of it; closes its stdin and turns prompting off, so it cannot wait for
  credentials; and kills it with its process tree when its budget runs out or its caller gives up.
  The count belongs to the reader rather than to the request that started it: a request that gives
  up stops waiting, and a read waits a couple of seconds at most before answering that the count is
  still running, so hello always answers inside a window's read budget. Disposing the reader, which
  the host does as it stops, cancels a git still running and waits, bounded, until it is gone, so a
  host exiting straight after does not orphan it. A failure is a sentence in the answer, never an
  exception. See
  [the decision](../decisions/a-build-is-named-by-its-commit.md).
