# A reason is accepted by the person, per solution, kind and project

**Decision.** Every reason in a workspace's health has a kind (`ReasonKinds`), an effect -- it blocks
answers, it may affect some projects, or it is a note -- and the projects it covers. A person accepts a
kind of reason for a solution's projects, and chooses what agents read about it then: Rose's line, the
person's own note, or nothing. The acceptance, and when each kind was first seen for each project, are
kept per machine in `workspace-health.json` beside `settings.json`. An agent acknowledges a reason for its
own session, which lasts until the session ends, or proposes that the person accept it; only the person
accepts, on the operator API, behind the token.

**Why keyed on kind and project, never on the reason.** A reason's wording changes from one build to the
next and from one load to the next -- it counts failures, it names the first three projects -- so an
acceptance keyed on its text would stop matching without a word, and one keyed on its id would cover
whatever the id happened to name. A kind is named once in code and is the same at every load. And keying
on each project rather than on the reason means a reason covering one more project than was accepted is
not covered: a project newly failing in a kind the person accepted for two others still shows, which is
the case the person most needs to see. A reason naming no project is keyed on its subjects instead -- the assemblies that
would not load, the platform chosen, the restore's NuGet error codes -- and one with none of those on its
own opening sentence, so a different assembly failing, or a cause worded differently, is not hidden by an
acceptance of the one before it. An acceptance naming neither projects nor subjects is refused, since it
would match nothing while the person was told it had worked.

**Why there is no kind for a cause Rose cannot see.** Legacy test projects that reference UWP assemblies
by `HintPath` are the case that asked for acceptance, and nothing tells them apart from any other project
whose design-time build resolved nothing: they reach health as `project-not-loaded`, as the
`msbuild-load-failures` count covering them, or as `legacy-project-not-evaluated`, a note. Guessing at the
cause from MSBuild's wording would be a heuristic that names a cause it has not established, so they are
accepted under the kinds they arrive as, which already cover exactly those projects.

**Why the solution is named by its path, not its workspace key.** The key is four bytes of hash, made to
be echoed back in a call while a workspace is loaded; two solutions can share one, and a hash cannot be
read back into anything a person recognises. The path is exact, is what every other part of the broker
keys a workspace on, and is readable in the file. It does mean each worktree of a repository is a solution
of its own, and an acceptance made in one checkout is not made in the next; whether a repository's
worktrees should share acceptances is open.

**Why a file of its own, and why it is written the way it is.** Settings are one person toggling one
value in one window. This is written by every broker on the machine -- the tray's, and any stdio server's
beside it -- whenever a reason is first seen, so two writers meeting is the ordinary case. A change is a
read, a change and a write under a lock file held exclusively across processes, and the write goes to a
temporary file renamed over the real one, so neither a race nor a crash loses an acceptance. A file that
does not parse is moved aside rather than written over; one that cannot be read at all, because another
process holds it this moment, is left alone and the change fails, since that says nothing about what is in
it; and one written by a newer build is never written over. A reader opens it sharing everything and a
writer waits out a reader, since Windows refuses to replace a file anyone has open. A change that does not
land says what stopped it, and the in-memory record is never held while the file is waited on, since every
tool call reads it. The shape is versioned: a build reads older shapes and refuses to write over a newer one. A sighting is rewritten at most daily and forgotten a month after the reason was last seen, unless
an acceptance covers it, so the tray describing every workspace every two seconds is not a write every two
seconds and a removed worktree does not stay in the file for ever.

**Why the notice is filtered in the worker, with what the broker sends.** Whether a read says its
workspace is degraded is decided in the worker, which is the only process that sees a reload it made on
its own or an assembly a tool failed to load a call ago (see [result-shapes.md](../invariants/result-shapes.md)).
What the person accepted and what this session acknowledged is known only to the broker. So the broker
sends, with every call it forwards, how that call hears each kind and project (`ReasonHearing`, in
`_meta`), and the worker leaves out or rewords only what that list covers for every project a reason
names. Filtering in the broker instead would mean recognising and rewriting the worker's sentence, and the
broker cannot recompute it without the reasons it does not hold.

**Why an agent cannot accept a reason.** An acceptance outlives the session that made it and changes what
every other agent reads. An agent able to make one -- or to confirm its own proposal -- could silence a
reason for every session on the machine on the strength of one task's view of it. So the MCP tool
acknowledges for the calling session, or proposes; the proposal waits in the broker's memory for the
person, and goes when the session that made it does. A host with nothing a person could decide one on -- a
stdio server running alone, which maps no operator API -- refuses the proposal rather than hold it for
nobody.

**Why a relayed client is named by its relay, and a session ends by going quiet.** The tray's http
transport serves each request without a session, and over the current protocol revision `Mcp-Session-Id` is
gone altogether, so every client relayed to it would be one caller. The relay mints an opaque id for its
own life and sends it as `_meta["rosemcp/session"]`, and the broker's call filter takes it for the calling
client (`CallSession.Client`) where the transport has no session. It is a name, not a credential: it decides
which acknowledgements a call hears and grants nothing -- debug-session ownership does not read it, and stays
unscoped over stateless http until a client identity reaches it (#458) -- and it is read only inside a tool call, past the host's checks. Nothing
tells the broker a client has gone either. A session that has made no call for two hours -- the limit the MCP SDK's own http transport
holds an idle session to -- is taken to have ended, and its acknowledgements and proposals go with it;
`WorkspaceHealth.ForgetSession` drops one at once for a host that does learn of an end. A call carrying no
session id at all is the one client of a stdio broker, whose session lasts as long as the process however
quiet it is; on a broker serving several clients it is any of them, so an acknowledgement from one is
refused there rather than shared by all.

**Why one tool.** Acknowledging and proposing act on the same ids from the same status and differ only in
who decides. Two tools would be two descriptions explaining ids, kinds and projects on a surface held to a
budget, so `rose_workspace_acknowledge` takes `propose`.
