# An outline is cheap by default

**Decision.** `rose_outline` answers with each member's name, kind, accessibility and line, and
nothing else, unless asked. `includeSignatures` and `includeDocumentation` default to off. What every
member of a type shares -- the file, the project, whether it is a test project -- is said once on the
type (`filePath` and `declarations`), and a member names a file only when it is declared in another
one. Flags a member does not have are left out rather than written as `false`. `members` filters by
name and `maxMembers` caps the listing across the whole answer, with each type's `totalMembers` and
the answer's `truncated` saying what the cap left out, and a notice when either narrowing changes
what is listed.

**Why the cheap form is the default.** The caller who does not know the switches pays whatever they
default to, and the type an outline is worth most on is a large one, where signatures and summaries
are most of the answer. Defaulting them on put the expensive form in front of exactly the caller who
had not read the description, and on this repository's larger types it overran what a client accepts,
at which point the caller reads the file instead and the outline has bought nothing. Choosing which
members to look at needs only their names and where they are; a caller that wants a signature or the
prose has just been told the names, and can ask about the ones it cares about with `members`,
`rose_symbol_info`, or the two switches.

**Why hoist rather than add a second, compact mode.** Most of the cost was not content but repetition:
a full `SourceLocation` on every member carried the absolute path, the source line, a containing
member equal to the member's own name, the project and its test-ness -- all constant across a type or
already said. Taking that out brings the default answer to roughly a tenth of what it was without a
new argument, and leaves one shape for a client to learn rather than two. `SourceLocation` itself is
unchanged, because references, symbol info and the write results do carry a different file per item.

**Why one cap across the answer.** A file outline is one answer, and a cap per type lets a file of
many types overrun it a type at a time. Types the cap ran out before are still listed, with their
totals, so a caller can see what is there and narrow to it.

**What the ratchet holds.** `ResultBudgetTests.PerOutlinedMember` measures the members alone, as the
MCP layer serialises them, so a field every member shares, added back per member, fails it.

**Why a referenced assembly's type is listed with signatures on.** A type from metadata has no file,
so `rose_outline` cannot reach it, and `rose_symbol_info` -- the tool that does resolve it -- lists its
members instead: its own public, protected and protected internal ones, through the same enumeration,
the same `members` filter and cap and the same notices. There the signature is not optional, because
a metadata member has no line: without one, overloads are a name repeated with nothing to tell them
apart, and which overloads exist is usually the question. Each member also says whether it is
obsolete, and whether as a warning or an error, since in a build that treats warnings as errors both
decide whether the call can be written. `ResultBudgetTests.PerMetadataMember` holds what that costs.
Inherited members are left out, as the outline leaves them out by default; a base type is a
`rose_symbol_info` call of its own.

**Why a file outline lists only what the file declares.** A file is asked about as a file, and the
type it declares is often bigger than it: a XAML code-behind's type is mostly its generated half --
every named element, `InitializeComponent`, the connection plumbing -- none of which is in the file or
can be edited, and listing it made a code-behind's outline cost more than reading the file. So a
member is listed only where some part of it is written in that file, and each type says in
`declaredElsewhere`, and a notice, how many of its own members were left out for being declared in
its other files, which `declarations` names. The type outlined by name lists them all, a partial
declared in several hand-written files included: a read has no reason to make the caller choose one
of the files, as a write must. Members inherited through `includeInherited` are listed whatever the
file, since they belong to no file of the type and asking for them was explicit.
