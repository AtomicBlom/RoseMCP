# Debug events are a buffer the agent reads, not a stream pushed at it

**Decision.** Everything a debug session observes -- exceptions with their stacks, log output, module
loads, breakpoint and tracepoint hits, pauses -- goes into a bounded, sequenced buffer in the live-app
host. `rose_debug_events` reads it from a cursor. Nothing is pushed to the agent as an MCP
notification.

**Why a buffer.** An agent works in turns. A notification that arrives mid-turn has nowhere to go: the
model cannot act on it before its next turn, and on its next turn it reads what happened anyway. The
buffer is the mechanism the agent actually uses, so a push would be a second path to the same data
with nothing consuming it.

**Why sequenced.** A cursor lets a caller ask for what it has not seen without re-reading what it has.
The sequence is also what every reader of a stop agrees on: a stop's identity is the sequence number
of the event that announced it, which is how the inspector tells a new stop from the same one polled
again.

**Why bounded.** A target that throws in a loop would otherwise grow the host without limit. A reader
that falls far enough behind loses the oldest events rather than the host running out of memory inside
somebody's debugging session.

**When to revisit.** If a client that is not turn-based becomes a target -- a streaming UI, say -- a
push can ride the same buffer rather than replace it.
