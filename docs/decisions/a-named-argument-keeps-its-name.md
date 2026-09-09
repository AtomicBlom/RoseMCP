# A named argument keeps its name when it keeps its parameter

**Decision.** A signature change leaves an argument's name colon alone where the argument still
belongs to the parameter it named. Names are only taken off an argument whose parameter has gone,
and only put on an argument that needs one to reach its slot. The previous rule -- an argument that
lands in its own slot is written positionally whatever it arrived as -- is replaced.

**Why this is the safe direction rather than the risky one.** Keeping the name is not an edit. The
call site compiled with that name against that parameter, so leaving the argument exactly as
written reproduces the text that is already there, character for character. Stripping the name is
the change, and it is a change to a call site that needed none: `CallSiteRewriter` builds each
argument from the caller's own `ArgumentSyntax` precisely so that a `ref`, an `out var` and a
comment written beside it survive a change that has nothing to do with them, and a name is the same
kind of thing. It also shrinks the diff, because a site whose arguments come out identical is no
longer counted as rewritten at all.

**Why it is worth changing at all.** The names carry the meaning of a literal, and stripping them
takes the meaning with them. Measured on this repository, in the session that made this decision:
`rose_change_signature` inserting one parameter rewrote `MemberSyntax.Parse(code, "class",
options: null, indent: "\t", lineEnding: "\r\n", ...)` to `MemberSyntax.Parse(code, "class", null,
"\t", "\r\n", ...)`, and `BodyEdit.Anchored(..., includeTrivia: true)` to `BodyEdit.Anchored(...,
true)`, five times across two test files. `null`, `"\t"` and `true` say nothing about which
parameter they are for; `options:`, `indent:` and `includeTrivia:` are the whole reason a reader can
follow the call. Nothing downstream reports the loss: it compiles, it means the same, and no
analyzer has an opinion -- so it accumulates.

**The condition, and why it is that one.** A name is kept when the argument's parameter is one the
member already had, which is `PlannedParameter.WasAt` being set. Three things follow from it.

A parameter that was *renamed* is not that: `ParameterPlan` matches by name, so a rename is a
removal and an addition, the old parameter is gone, and its argument goes with it -- there is no
name left to keep. A parameter that was only *retyped* keeps its name, so the argument's name is
still correct. And where a name has to be written rather than kept, it is still taken from the
method the call site binds to rather than from the declaration being changed, because an override
is free to call its parameters something else.

**What the language allows.** A named argument followed by positional ones is legal since C# 7.2
as long as it sits in its own parameter's position, which is exactly the case where a name is kept
-- so keeping one does not force every argument after it to be named. Reordering existing
parameters is refused anyway, so an argument that keeps its parameter keeps its position.

**What it costs.** A call site that named its arguments and a call site that did not now come out
differently from each other, where before both came out positional. That is the point: each comes
out the way it was written. `CallSiteShapeMatrixTests` records both.
