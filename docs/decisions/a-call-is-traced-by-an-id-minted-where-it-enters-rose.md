# A call is traced by an id minted where it enters Rose

**Decision.** Each tool call gets one correlation id, minted by the first Rose process it reaches and
repeated by every process after that. It travels in `_meta["rosemcp/correlationId"]`, is held in an
ambient for the length of the call, and is written by the file sink on every log line. Each child
host names the log file it writes, so a row in the tray opens the file that explains it.

**Why one id rather than the JSON-RPC request id.** The request id belongs to a hop, not to a call.
Each client chooses its own -- the relay's for the tray, the broker's for the worker -- so the same
call has a different number in each file, and logging every one of them still leaves the matching
to timestamps. An id of Rose's own, sent alongside the request, is the same string everywhere.

**Why minted where it enters, and only accepted in Rose's own shape.** The outermost Rose process is
the only one that sees the call before any hop, so its id is the one the rest can repeat. A process
with nothing in front of it -- a broker with no relay, a worker under a test -- is the outermost one
and mints for itself, which leaves no line without an id inside a call. An operator request is an
entry point too: an inspector's step or pick reaches a live-app host the way a tool call does, so the
operator API mints for each request it serves. A client may send anything in
`_meta`, and the id is written verbatim into every line of the call, so only lowercase hex of a
length a Rose process could mint is accepted: a value carrying a newline would otherwise forge lines
in the log, and one carrying a few kilobytes would bury every message under it.

**Why the ambient lives in `RoseMcp.Logging`, and the broker references it.** The sink renders the id
and the broker sends it on, so it has to be somewhere both can see. A `Microsoft.Extensions.Logging`
scope was the other candidate and lost on two counts: the broker would still need an ambient of its
own to read the id back for the next hop, and a scope cannot be ended for the work that inherited
it -- it is popped only on the execution context that pushed it. `RoseMcp.Contracts` was not a
candidate, since the ambient is state. The broker adds no sink by referencing Logging.

**Why the id ends with its call, and why long-lived work is also detached.** Anything a call starts
inherits its execution context. An id that simply stayed set would tag a refresh loop started by the
first call of the day with that call's id until the process exits, and a child's transport, started
inside one call, would file every later call's replies under it. So the call's scope marks its id
over when it closes, and everything that inherited it stops reporting it at that moment, without
anybody having to find every place work is started. Work that is never the call's -- a poll loop, a
sweep, a child's transport -- is started through `Detached` as well, so it does not carry the id even
while the call that happened to start it is still running. Work a call causes and waits on, such as
the load a first call starts, does carry it, because that is the call it belongs to -- including work
handed to a queue whose loop runs on a context of its own, like the worker's single writer: the item
captures the call when it is queued and the loop resumes it around the item, moving that id and no
other ambient state onto the loop.

**Why not the session id.** Which session made a call is already decided per call by `CallSession`
for owning a live-app session, and logging it would put a value that acts as a credential over http
into a file any process running as the user can read. The correlation id answers which call a line
belongs to, which is the question the logs could not answer; which agent made it is the origin
directory, already on the broker's forwarding line.
