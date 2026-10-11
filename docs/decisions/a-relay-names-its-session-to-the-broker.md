# A relay names its session to the broker

**Decision.** Once its client has made its first request, the stdio relay posts what it knows of the
session it serves to `POST /admin/relays/client`: the name it already sends as
`_meta["rosemcp/session"]`, the directory the client started it in, the client's roots, the branch of
the first root, the client's process, the `clientInfo` name and version, `ROSE_AGENT_LABEL` from its
environment, a hash of the tools the client holds and whether that is what the broker offers. It
posts again after every reconnect and whenever any of it changes. The broker keeps one row per client
in `ConnectedClients`, read at `GET /admin/clients` and `GET /operator/clients`, with when the client
connected, its last call and how many it has made. A client that reached the broker over http
without a relay is listed unnamed.

**Why the session name joins the facts to the calls.** A relay already has two ids: the one in the
`Rose-Relay` header, which its watch is counted by, and the session name in every call's `_meta`.
The calls are what the table counts, and they carry only the session name, so the row is keyed by it;
the introduction carries both, which is how a row learns which watch says its client is connected.
A session another relay already named is refused, so one relay cannot write over another's facts.
The row's own id is minted by the broker and grants nothing: the session name decides whose
acknowledgements a call reaches, so no answer carries it.

**Why after the client's first request, and not at connect.** The relay connects to the broker before
its own stdio host exists, and the client's name and capabilities arrive with its requests -- on the
2026-07-28 revision with every request rather than once, so the request-scoped server is the one to
read them from. The first listing or call starts the introduction and returns at once; it runs
detached, bounded by a read budget and by the relay's lifetime, ordered behind any introduction
already under way, and it never throws. A request never waits on it and none fails for it.

**Roots are asked for, and the working directory is the fallback.** The 2026-07-28 revision, which
Claude Code speaks, deprecates roots, and the SDK marks every part of them obsolete. A client that
still declares them is asked, from outside any request, with a budget; one that declares none, or does
not answer, is named by its working directory -- which is the folder a client starts a stdio server
in, so the session is named either way. `roots/list_changed` has them asked for again.

**The branch is read from the checkout's files, not asked of git.** The relay is in front of a session
and must not hold it up or outlive it. A `HEAD` is a line long, a worktree's `.git` file names the
directory its `HEAD` is in, and reading them cannot prompt, hang or leave a child behind. Anything
unexpected is no branch.

**The client's process is the relay's parent, past any shell.** A client starts its stdio servers
itself; a configuration that starts the relay through `cmd /c` or a shell puts the shell between them,
so a few shells are walked past. A parent that started after its child is a reused id and ends the
walk.

**Whether the tools match is decided by the relay.** The relay holds both sides already: what the
client last listed through it, and what it read from the broker at connect or after a restart, each as
a `ToolSurface` fingerprint computed the same way in one process. The broker hashing its own surface
would compare a value computed from tool objects before serialisation in one process with one computed
after a round trip in another, and a difference in that round trip would show every session as stale
-- a confident wrong answer. So the relay sends the hash and the comparison, and sends them again when
a restart changes the tools and again when the client lists them. For an unnamed client the broker can
say only whether it has listed tools from this broker: one that calls without listing holds a list an
earlier broker gave it.

**Unnamed clients.** The broker's http transport serves each request without a session on the current
revision, so nothing tells one unnamed client from another: they share one row. A client on an older
revision has a transport session, and each such session is a row of its own. An unnamed row's
`clientInfo` is what its transport carried.

**When a row goes.** The broker outlives every client and nothing tells it an http client has gone. A
named row stays while its relay holds `/admin/relays/watch`, and goes once the relay has stopped and
the client has been quiet for two minutes, which covers a relay re-opening its watch. An unnamed row,
and a named one whose relay never introduced itself, goes after two hours quiet, the MCP transport's
own idle limit. The table holds at most 256 rows; a newcomer past that replaces the quietest row whose
relay is not watching.

**What the broker trusts.** Nothing. The body is limited to 64 KiB before it is read; every string is
stripped of control and formatting characters and cut to a length; at most sixteen roots are kept; a
process id must be positive and a hash must have a hash's shape. Each is shown to a person and may be
logged.

**The relay protocol moved to 2.** A broker of protocol 1 refuses the new route, which a relay says
once and lives with; a relay of protocol 1 never introduces itself, and its session shows as a row
with calls and no facts. Neither misreads the other, but a deploy replaces an installed relay only when
the protocol moves, so without the bump no installed relay would ever introduce its session.

**Subagents stay one client.** They share their session's one MCP connection, so one stdio process and
one relay, and every call they make carries its session name.
