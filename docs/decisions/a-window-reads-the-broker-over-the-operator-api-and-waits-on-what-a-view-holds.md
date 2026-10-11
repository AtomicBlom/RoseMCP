# A window reads the broker over the operator API, and waits on what a view holds

**Decision.** Everything a person's window shows of the broker is read at `/operator`, behind the
operator token: `GET /operator/clients`, `/operator/workspaces`, `/operator/activity`,
`/operator/sessions` and `/operator/memory`, each made by `OperatorViews` from the broker's own objects,
and each read through a typed method on `OperatorClient`. Every view but memory answers with a
`version`, a hash of what it holds; the same read with `after=<version>&waitSeconds=N` is held open by
`LongPoll` until the view would answer with another version, until the wait -- at most a minute -- runs
out, or until the host starts stopping, and is answered with the view as it then is. The activity view
is paged by a cursor, `next` passed back as `before`, and filtered by client, workspace, debug session,
tool, time, failures and text. Memory is sampled on every read and has no version.

**Why the windows read nothing else.** A window that reads `WorkspaceManager` directly has to live in
the broker's process, so a window that crashes takes every agent's workspace with it, and a test can
reach what it shows only by standing up the tray. A window that reads http can be its own process,
started when wanted, and a test drives it by driving the broker. The operator group already had the
token gate and the call id, so every view is added inside it, and none on `/admin`, which nothing
gates.

**Why a version of what the view holds, and not a counter.** The events route waits on a sequence its
host keeps, because the host appends every event in one place. A window's views are not like that: a
workspace row is the worker's state, its activity log's and the health record's; a client row changes
on every call and when a relay lets go; the activity listing changes when another broker writes to the
shared folder. A counter would have to be bumped at every place any of those changes, and the one place
missed is a window that silently stops updating. Reading the view again cannot miss anything, so a
wait reads it every second, and sooner when the activity record or the client table pulses a
`ChangeSignal` -- which they do on every call, so a call shows at once. The cost is a broker-side read
per open wait per second, of state already in memory.

**What a version leaves out.** Anything that differs on every read -- an uptime, an idle time, how long
a running operation has taken, a session's report age, and every memory figure -- is taken out before
the hash, or every wait would end at once. A window counts idle time on from the row's last-use time,
and reads memory on a timer. The activity page's version is worked out without its bodies, so a wait
compares the cheap read and a page of bodies is read once, when the wait ends.

**Why a cursor and not an offset for activity.** Calls keep arriving while a person pages back, and an
offset counted from the newest would push the second page onto entries the first already showed. The
cursor is the oldest entry's start and id, the order a listing keeps, so the next page starts exactly
where the last ended.

**Why the workspace row wraps the summary.** `WorkspaceSummary` is also what `rose_workspace_open`
answers an agent. The folder, the configuration and platform, the build properties, the last use, the
close time and the rebuilt analyzers are a person's questions, so `OperatorWorkspace` carries them
beside the summary rather than every agent paying for them. Configuration and platform are what the
worker reported loading, and what it was started with only until it reports.

**What a stop does to a wait.** A wait is one request's linked token, a timer and a reference to a
signal's current task, all released when the request ends; nothing is held on a singleton. The host
stopping ends every open wait with an answer, so a window's poll never holds a deploy for its minute;
the reader going away cancels it, and the request filter answers nobody.
