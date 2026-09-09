# A sole import candidate is applied whatever namespace it is in

**Decision.** A write tool goes on adding the import for an unresolved name that has exactly one
candidate, with no test of whether that candidate's namespace has anything to do with the project's
own. The proposal to report rather than apply when the two share no namespace segment is declined.
The one thing that changes is that the line reporting the import says the choice was forced -- "the
only namespace anything of that name is in" -- so a caller who meant a type they have not written
yet can see on what basis the import was picked.

**The finding this answers.** Writing a member that referenced `EnvironmentVariable`, a type the
author was about to add, imported `Microsoft.Testing.Platform.Extensions.TestHostControllers`. One
candidate, so nothing was ambiguous and nothing was reported as a choice -- just wrong. It is the
shape the "never pick" rule exists for: an unresolved name the author intends to add is
indistinguishable from one needing an import, and the compilation cannot tell them apart because
one of the two does not exist yet.

**Why the heuristic loses.** "Shares no segment with the project's own namespaces" catches the
reported case and catches almost every import worth adding along with it. A member in
`RoseMcp.Worker` that references `Encoding` wants `System.Text`, whose segments are `System` and
`Text`; a member that references `ImmutableArray` wants `System.Collections.Immutable`. Neither
shares a segment with anything the project declares, so both would be reported rather than applied
-- and importing a BCL namespace is the overwhelming majority of what this feature does. Exempting
`System.*` by name would fix those two and leave every other package a project references in the
same position: `Microsoft.CodeAnalysis`, `Serilog`, `TUnit`. The rule would then be a list of
allowed prefixes, which is a configuration surface standing in for a judgement nobody can write
down.

Nor does the heuristic remove the failure it is aimed at. A wrong sole candidate *inside* a
namespace that does share a segment is applied exactly as before, and in a repository whose own
namespaces are its product name that is where the collisions are.

**What makes the cost bearable.** The reported failure did not compile, and it was reported in the
same call: the write tool adds the import, compiles, and returns the errors the edit left. An
import that does not resolve the name it was fetched for leaves the `CS0103` in place, so the
result carries both the import it added and the error it did not fix. That is a loud outcome, one
round trip long, and the caller reads it before doing anything else.

Compare that with what the existing guard already prevents, which is the failure with no symptom:
a name that exists in two places, where the reachable one is not the one meant, imported silently
and bound to the wrong type. `docs/decisions/automatic-imports-need-a-name-to-be-unique-everywhere.md`
covers that, and it covers it by requiring uniqueness in the referenced set *and* in the solution.
The remaining exposure is a name that is genuinely unique and genuinely not what the author meant,
which cannot compile against the wrong type unless that type happens to have a compatible member at
the use site -- in which case it is the two-candidate case again, and already refused.

**What it costs.** An occasional import nobody wanted, in a file the caller has just written and is
already reading the result of, alongside the error that says it did not help. Deleting a using is
one call.

**What is left open.** The result carries the import it added and the error it did not fix, and
says nothing about the two being related. Saying it -- an import added for a name whose error is
still in the introduced list is the wrong import, which is decidable rather than a heuristic --
needs the name-to-namespace pairing kept beside the prose and read by five write tools, so it is
filed rather than done here.

**What it does not change.** The two-candidate refusal, the unreachable-namespace refusal, and
`rose_resolve_name`, which reports and never writes.
