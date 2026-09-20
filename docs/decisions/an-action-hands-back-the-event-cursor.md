# An action hands back the event cursor, so nobody has to go and find one

**Decision.** Every answer the live-app host produces carries `cursor`: where that session's event
stream stood when the answer was written. A caller that acts and then wants to know what its action
caused passes that number back as `after`. Nobody computes a cursor, and nobody reads the stream in
order to find out where it is.

This extends [debug events are a buffer the agent reads](debug-events-are-a-buffer-the-agent-reads.md)
rather than changing it. The buffer and the cursor are as they were; what is new is where a caller
gets the cursor from.

**The failure it prevents.** `rose_debug_events` answers one question, whether it waits or not: is
there anything past this cursor. That makes `after: 0` mean "anything, ever" -- so an agent that sets
a breakpoint and then waits for `BreakpointHit` from the default cursor is handed a hit from before it
set the breakpoint, instantly, and cannot tell that from the hit it was waiting for. The page looks
identical either way. It is a confidently wrong answer rather than a failure, which is the class of
bug this repository spends its effort on.

It is not hypothetical, and it is not only an agent's problem. One of the live-app tests waited for a
probe's removal exception from cursor 0 on a session shared by its whole class. The probe announces
one every five seconds, so the wait matched a removal from before the test acted and returned in a
hundredth of a second, leaving every assertion under it to run against an app that had not done
anything yet. It passed alone, where the session was younger than its own first removal, and failed in
company. Twenty-two archived runs put the real rate at one pass in twenty-two, and the cost of finding
that out was a week and an issue whose diagnosis was wrong.

**Why not a default that follows the verb.** The obvious patch is to make `after` nullable and have it
mean "the beginning" for a read and "now" for a wait. It closes this instance and leaves the shape
that produced it: one argument, two meanings, chosen by a sibling argument. Nothing about the call
says which is in force, so the next person writing a caller still has to know -- and the value the
caller actually wants, the position *before* it acted, is one neither default supplies.

**Why not a separate wait verb.** `rose_debug_wait` would give each verb one meaning, which is worth
something, and would still need a cursor to be correct: an agent's action completes a turn before its
next call arrives, so a wait that starts when the call starts drops anything that happens in between.
The trap would survive the split. It also costs another entry in a tool surface whose size is a
standing constraint, for no capability that is not already there.

**Why a stamp rather than a line in each tool.** The number is written onto every serialized answer by
one call-tool filter in the host (`CursorStamp`), for the reason workspace attribution is added once
in `WorkspaceManager`: what a tool never has to remember, a tool added later cannot forget. The filter
cannot do it alone, because the broker deserializes the host's answer into the tool's own result type
and re-serializes that -- a property the type does not declare is dropped there, silently. So every
live-app result derives from `LiveResult`, and a test over the host's tools fails when one does not.

**Zero keeps one honest meaning.** It is the beginning of the stream, and it is the right cursor
exactly once: at a session's birth, where everything the target has ever done is also everything it
has done since you attached. A wait from zero that is answered out of history says so in the answer's
`notices`, which costs nothing on the calls that do not need it -- `ToolListing` already keeps output
schemas off the wire, so the field itself is free, and the sentence is paid for only by the caller who
tripped it.

**When to revisit.** If a live-app tool ever answers without going through the host -- session
creation does, today -- it has no cursor to stamp, and zero is correct there for the reason above. A
second host, or a tool that answers from the broker's own state about a running session, would need
the number to come from somewhere else before it could be trusted.
