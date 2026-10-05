# A name is resolved by the compilation, in one place, and a repeated segment is read both ways

**Decision.** Every tool that takes a symbol by name asks `SymbolResolver`, which resolves the
address against each project's compilation: the type path to source types, then the last segment
as a member of those types, under every reading the text allows. Referenced assemblies are its
metadata branch, searched only when nothing in source is at the address and only for a read. A
repeated last segment, `Namespace.Type.Type`, is read both as a type and as that type's
constructor; whatever the compilation finds under either reading is the answer, and a name that
reaches both is refused with the spelling for each.

**Why the compilation and not the declaration index.** The solution's declaration index lists what
syntax declares under a name. A property a record synthesises from a positional parameter has no
declaration of its name, so a resolver built on the index cannot reach `SymbolLocation.TypeName` at
all, while a metadata lookup run after it -- which goes through the compilation -- can, for the
reads allowed to ask it. Reads and writes then disagree about one address, and a rename, the tool
most insistent that callers address by name, is the one that cannot. Asking
the type for its members is how the language itself answers, so the two cannot diverge.

**Why one place.** Separate resolvers disagree in three ways at once: which symbols a name reaches,
which refusals let a read fall back to metadata, and how a repeated segment is read. Whichever tool
a caller meets first teaches it a grammar the next does not keep. `SymbolResolverTests`
fails any worker type other than the resolver and its metadata branch that asks a declaration index
or a compilation what a name is, so a new tool cannot grow a second.

**When metadata is asked.** When source has nothing *at the address*, rather than nothing carrying its
last segment. `System.Collections.Generic.List.Add` is reached whatever this solution happens to call
`Add`; a bare `Add` that source declares is answered from source, which keeps a bare name ambiguous
wherever it is ambiguous rather than letting a library member settle it. A constructor address that
reaches a source type keeps that type's answer -- "declares no constructor", or "no constructor takes
those parameter types" -- and referenced assemblies are not asked, because the caller means that
type; a type of the same name somewhere else is not at the address and does not stop a library's
constructor being reached. A refusal after a metadata search says it ran.

**Why both readings, and why refuse when both hit.** A member may not share the name of its type, but
a type may share the name of its namespace, so `RoseMcp.XamlDiff.XamlDiff` is a type and is also, as
C# spells one, the constructor of a type `XamlDiff` in `RoseMcp`. Read only as a constructor, every
`Foo.Bar/Bar.cs` layout is unreachable by name and the refusal advises adding a constructor. Read
only as a type, `Greeter.Greeter` -- the spelling C# itself uses -- would stop naming a constructor.
The two collide only for a type named for its namespace that declares a constructor, written partly
qualified -- or, where that namespace is at the root, written in full -- and there either answer is a guess: a wrong one writes correct code into the wrong
member. The refusal names both and how to write each, and every spelling it recommends resolves:
`Namespace.Type..ctor` for the constructor, and `global::Namespace.Type` for the type. `global::`
anchors a name at the root, so its path has to be the whole of the symbol's rather than the end of
it -- which is what separates a type named for a root namespace from a constructor even by its full
name, since `Gauge.Gauge` is still also the constructor of the type `Gauge`. A tool
that wants a type -- an outline, adding a member -- takes only the type reading, so it never meets
the collision.

**What it costs.** A compilation per project rather than one index lookup, which the worker holds
warm anyway, and a scan of the records for a bare name, since only a record declares members through
a parameter list.
