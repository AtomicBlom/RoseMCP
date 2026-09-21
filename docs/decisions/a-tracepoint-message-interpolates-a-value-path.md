# A tracepoint's message interpolates a value path

**Decision.** A tracepoint's log message may contain `{path}` placeholders, where `path` is the same
value path grammar `rose_debug_evaluate` and value expansion take: an argument or local by name, or
`arg:0` / `local:2`, then `.field` and `[3]` into the object graph.
`{{` and `}}` are literal braces. Each placeholder is read from memory on the callback the hit
arrived on, and the hit is logged with the values substituted into the line **and** carried out on
the event as data.

**Why the same grammar rather than a new one.** A value a caller was shown -- in a stop's variables,
in an expansion, in an evaluation -- carries the path that addresses it, so pasting that path into a
message works with no translation. A second grammar would have to be learnt, and the two would drift
about what `a.b[0]` means.

**Why no func-eval.** A placeholder cannot read a property, call a method or run anything else in the
target, for the reason
[expression evaluation reads memory and runs no code](expression-evaluation-reads-memory-and-runs-no-code.md)
gives, multiplied by how often a tracepoint fires: a message on a hot method renders thousands of
times, and each render would be a func-eval on a stopped thread. A tracepoint exists because it
cannot freeze the app, and interpolation must not be the thing that makes it able to.

**Why the values are on the event as well as in the message.** A value inside a sentence cannot be
read back. A page of hits is long enough that the client displaying it truncates, and the way out of
that is to ask for one event by its sequence and read its fields -- which needs the fields to hold
the values. They go in `logged` rather than in `variables`, which is the whole top frame at a
stopping breakpoint: one field meaning "everything in scope" on one event and "the four things
asked for" on another is a field that cannot be read without first knowing which kind of event
carried it.

**Why a malformed message is refused at the call that wrote it.** An unmatched brace or a path that
does not parse throws when the tracepoint is added. Accepted, it would log the same wrong line on
every hit, and a message reading `count={count` reads as interpolation not being supported at all
rather than as a typo in the one message that has it.

**Why an unresolved path still logs.** A path that parses but resolves to nothing -- a local out of
scope, a null in the middle of a chain -- renders as `<path: reason>` in the line and as the same
text in `logged`, with no type. The hit did happen, and a gap where a value should be reads as the
value having been empty. It is the same reasoning as
[a breakpoint condition compares a value](breakpoint-conditions-compare-a-value.md) arriving at the
opposite answer, and for a reason: a condition that cannot read its variable has a decision to make
and declines to fire, while a message has already been triggered and can only choose what to say.
