# A failed load stays failed until a reload

**Decision.** A worker whose solution load threw reports that failure for the life of the process.
`rose_workspace_reload` is the way out, and nothing retries the load on its own.

**What actually reaches this, which is narrower than it looks.** A failed restore is not a failed
load. `RestoreRunner` reports `Succeeded = false`, the status reporter turns that into a degraded
reason, and the workspace answers as `Degraded` with the restore report attached -- which is the
whole point of reporting restore separately. What faults a workspace is the load itself throwing:
the solution file unreadable or unparsable, `MSBuildWorkspace` failing to open it, the design-time
build dying. So the sticky state is not "NuGet was unreachable for a minute", it is "this solution
could not be opened at all".

**Stickiness is the load task, not a flag.** `WorkspaceHost` caches the faulted status report so a
status call gets an answer rather than an exception, but the durable part is the load task itself:
every read awaits the same `Task<WorkspaceSession>`, so once it faults every read re-throws it
whether or not the report was cached. Clearing a flag would change what status says while every read
went on failing, which is worse than either behaviour on its own. A retry means starting a second
load, and that is the thing being declined.

**Why not retry.** A load is a full design-time build -- seconds for a small solution, minutes for a
large one -- and a retry has to be serialised against the load already in flight or two concurrent
readers start two of them over the same solution and one result is discarded arbitrarily. That is
real cost, paid on every faulted workspace, against a class of failure nobody here has observed: the
transient shapes are a solution file being rewritten by a branch switch at the instant of the load,
or a file lock, and both are followed by a caller who can say so. The broker already replaces a
worker that *died* and retries the call, read-only tools only, because a dead process is unambiguous
and cheap to detect; a load that threw is neither.

**The middle ground, and why it is not taken.** Retrying only where the load threw `IOException` or
`UnauthorizedAccessException` -- the shapes that really are transient -- is defensible and would cost
nothing on a malformed solution, which would still fault once and stay faulted. It is left out
because the recovery it saves is one tool call on a failure that has not been seen, and because a
retry path that fires rarely is a path that is never exercised. If a repository is found where a
branch switch reliably faults a live worker, this is the shape to add, and only this shape.

**What it costs.** A caller whose load failed for a reason that has since gone away has to say so
with `rose_workspace_reload`. The error names what went wrong, so that call is an informed one
rather than a shot in the dark.
