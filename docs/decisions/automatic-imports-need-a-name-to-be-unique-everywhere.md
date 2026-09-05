# An import is added only when the name is unique in the referenced set and in the solution

**Decision.** A write tool resolves the names its edit left unresolved and adds the imports for
them, but only where the name has exactly one candidate across the assemblies the project
references *and* nothing of that name exists elsewhere in the solution. Anything else is reported
as a choice with the candidates named, and nothing is written.

**Why the second half is not obvious.** `NameResolver` searches the referenced assemblies, and only
searches the rest of the solution when the referenced set found nothing. That is right for
*reporting*: the useful answer to "what is `Widget`" is the reachable one, and the unreachable one
is a footnote about a missing project reference.

It is wrong for *adding*. Take a solution with its own `Logger`, in a project this one does not
reference, and Serilog on the reference list. An agent writing `Logger` almost certainly means the
first. The referenced search finds Serilog's, one candidate, no ambiguity reported -- and the
import is added, the file compiles, and every call binds to the wrong type. That is the failure
this whole surface names as its worst: not an error, a confident answer about something else, with
no symptom until behaviour is wrong at runtime.

So the uniqueness that licenses an automatic import is uniqueness *everywhere*, and the search runs
over the solution whether or not the referenced set answered.

**What it costs.** Fewer automatic imports. A name that collides with something in an unreferenced
project is reported rather than added, and the caller passes the namespace explicitly. That is a
round trip in the rare case, against a wrong binding in the rare case, and the round trip is
visible while the wrong binding is not.

**What it does not change.** `rose_resolve_name` still answers the way it always did. It reports
rather than writes, so the reachable answer with the unreachable one as a footnote is exactly what
its caller wants.
