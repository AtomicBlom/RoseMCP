# Every call an agent makes is recorded and kept on disk

**Decision.** Every MCP tool call the broker answers is recorded in the activity record, one
`ActivityEntry` per call: the client that made it, the workspace or debug session it reached, the tool
and its target, when it started and finished, how it ended and what the caller was told, its arguments
as sent and its response as answered, and, for a write, each changed file with its own part of the
diff. The record is JSON lines under `Logs/Activity` beside every process's logs, one file per broker
run per UTC day, kept for `RoseSettings.ActivityRetentionDays` (7 by default), and read at
`GET /operator/activity` and `GET /operator/activity/{id}`. A broker starting writes a line saying so.

**Why a call filter, and an ambient the routing adds to.** The facts worth keeping are known at
different depths: the client and the arguments where the call arrives, the workspace where it is routed,
the diff only before the broker leaves it off for the caller. A filter sees every call, so no tool can
be added that the record misses -- the reasoning that already puts attribution and the origin in one
place each. It makes a `CallRecording` ambient for the length of the call, the way the correlation id
is, and the three places every call is routed through add to it: a worker's call, a live-app session's
call, and the step that names the answering workspace, which is where a write's diff is still whole.
Threading a parameter through every tool instead is how one tool added later would go unrecorded.

**Why the in-memory activity log stays beside it.** `ActivityLog` answers a different question: what
each worker is doing now, lifecycle steps included -- starting a worker, loading its solution, an
eviction -- with live progress, for the tray. It is per workspace, eight deep and forgotten when a
workspace closes, which is right for live state and wrong for a record. The record is calls, attributed,
and outlives everything. Deriving one from the other would make either the tray read a file every two
seconds or the record miss the steps no client asked for.

**Why only MCP calls, and not the windows' operator requests.** The card asks what each agent did. A
window's reads through `/operator` are the act of looking, and an inspector's step or pick forwarded to
a live-app host is a person's action, not an agent's; filing either under an agent would put words in
its mouth. Operator routes are not MCP calls and never pass the filter, and nothing records outside a
call's recording, so this holds without a list of exceptions. A relay's own `rose_debug_list` read,
marked `_meta["rosemcp/relayRead"]`, is not its session's call and is not recorded either. Should a
person's own actions want recording -- a session a person starts and steps through by hand -- that is a
record of the person, with its own author, not an entry here.

**Why on disk, and one file per broker run.** The tray and any number of stdio servers can run on one
machine and share the folder. A file per run, named for the run's instance id, means no two brokers
ever write one file and no lock sits on a call's path. Reading takes in every file in the folder, so a
reload, a restart or a deploy loses nothing; a broker starting writes a line saying so, so a restart
reads as a restart rather than as a quiet stretch. Files roll each UTC day so the retention prunes whole
files, and past a size so none is too large to read back.

**Why a queue that drops rather than waits.** A call is never held up by its record. An entry goes to a
bounded queue a detached loop writes from, and where the queue is full -- a disk that cannot keep up --
the entry is dropped, counted and logged. The host disposing the record writes what is queued first,
and the host stopping flushes it before that, for the tray, which stops its host without disposing its
services. A force-killed broker loses at most the entries of the batch it was writing, since every batch
is flushed to the file as it is written.

**Why bodies are cut, and to what.** An entry is one line, and a listing reads past every line, so a
migration's megabyte response would be paid for by every later read. Arguments and responses are kept to
32,000 characters each, twice what a caller is shown of a diff; an error to 4,000, since a listing carries
it; one file's diff to 64,000, and a call's diffs together to 256,000, past which a file is listed with
the length of its diff and none of it. A person reading what an agent wrote wants the code, where the
agent wanted only where its code landed, so the diff gets the most room. Whatever is cut says how long it
really was, in a field and in a note, for the reason [a cut value says how long it really
is](a-cut-value-says-how-long-it-really-is.md).

**Why nothing is redacted.** Arguments and responses are source, and source is what the record is for.
A redaction guessing what a secret looks like would hide code a person needed to read and pass the
secret it did not recognise. The record is as private as the solution it describes and the logs beside
it: in the user's profile, and served only under the operator token, never on the unauthenticated
`/admin` routes.

**Why the client is named by its row and by a hash.** The client table's row id is minted per broker
run, so it says which client within a run and nothing across one. The session name a relay sends outlives
a restart, but it scopes what that session acknowledges and no answer carries it. So an entry carries
the row's id and a short hash of the key the table files the client under, which is stable across a
restart and grants nothing.

**Why under the logs, and a variable for tests.** `Logs/Activity` sits where a deploy never touches and
moves with `ROSEMCP_LOG_ROOT`, so an integration run's brokers keep their records with the run's
evidence. The unit suite builds brokers that never say where to keep a record and cannot set
`ROSEMCP_LOG_ROOT` without moving the logs its own logging tests read, so `ROSEMCP_ACTIVITY_ROOT`
names the folder outright, set once for the run.
