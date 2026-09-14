# A stop captures its frame when it happens

**Decision.** When a breakpoint or a step stops the target, the host reads the call stack and the top
frame's arguments and locals inside the stop callback, and records them on the stop event. An agent
reads them from `rose_debug_events` and needs no second call while the target is still stopped.

**Why.** The stop callback is the one moment the target is certainly frozen. A later call races the
safety timeout: an agent that reads the event on its next turn may find the target already running,
and a frame read after the target has moved is invalid. Capturing at the stop means the answer exists
however slow the agent is.

**What it costs.** Every stop does the reading whether or not anybody wants it, which for a breakpoint
in a hot loop is repeated work. Tracepoints exist for that case: they log the hit and keep running.

**What sits beside it.** A person holding a stop in the inspector reads any frame on demand, because
the hold removes the race. An agent that needs a value the captured locals do not show uses
`rose_debug_evaluate` while the stop lasts.
