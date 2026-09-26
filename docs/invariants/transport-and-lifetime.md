# Transport, relay, and process lifetime

Read before touching stdio or http transport, `TrayRelay`, progress reporting, cancellation, or anything that ends a process.

- **Nothing writes to stdout in stdio mode** except protocol frames. All logging goes to stderr,
  and to a file. A stray `Console.WriteLine` corrupts the stream, and the failure looks like a
  protocol bug. `RoseMcp.Logging` adds the file sink -- Serilog behind the existing
  `Microsoft.Extensions.Logging` call sites, never a console sink, and there is a regression test
  asserting the pipeline writes nothing to stdout at all. Logs land in
  `%LOCALAPPDATA%/BinaryVibrance/RoseMCP/Logs/{Server,Worker,Tray,Inspector}/[{solution}-]{yyyyMMdd-HHmmss}.log`
  -- under their own `Logs` folder, separate from the install that shares the same vendor/product
  parent, so promoting a build never touches a session's log files. UTC in the name and UTC in
  every line so the two cannot disagree. A worker's file names the
  solution it owns, hashed as well as spelled, because two worktrees of one repository share a
  solution name. Twenty sessions are kept per component, pruned at startup -- Serilog's own
  retention cannot do it, since it only prunes within one rolling base name and every session
  here has its own.
- **A stdio session relays to a tray when one is running.** `TrayRelay` forwards both listing and
  calling, declaring no tools of its own, so the surface cannot drift from the tray's. It sends the
  directory its client started it in as `_meta["rosemcp/originDirectory"]` and changes nothing else.
  An http broker serves every repository on the machine and, with two solutions open, cannot know
  which one a bare call means; a stdio process cannot not know. Relaying buys both that and one warm
  worker per solution shared across sessions, and it keeps the tray's window reading a live
  `WorkspaceManager` rather than a pushed copy that could drift. With no tray, the same process owns
  its workers as before.
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
  carries both the running and the recently finished ones. The tray window and
  `GET /admin/workspaces` read that same model, so they cannot disagree. A percentage is per
  operation and only ever rises; no percentage means "cannot say", which shows as an indeterminate
  bar rather than one frozen at a number that has stopped meaning anything.
- **A worker is asked for status the moment it connects.** Progress notifications only exist inside
  a request, but a worker starts loading when the process does. With no call in flight the first
  half-minute of a large solution is invisible -- which is exactly what a reload from the tray
  produces, since no client is waiting on it. The priming call pays for nothing the first real call
  would not have.
- **Every stdio process dies with its client, and takes what it owns.** A worker, a live-app host
  and a stdio `RoseMcp.Server` all exit when their stdin closes, and the rule is the same one three
  times: whatever the process owns goes with it, because nothing else knows it is there. A server
  ends its workers, and a host ends a target it *launched* -- never one it attached to, and never
  after a detach, which is the request to leave the app running. Orphaned Roslyn hosts holding a
  solution in memory are invisible until the machine is out of RAM; an orphaned probe app is worse
  than invisible, because the probe is single-instance and the next run finds an app it did not
  launch and treats it as its own.
