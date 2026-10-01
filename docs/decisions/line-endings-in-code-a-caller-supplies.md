# Line endings in supplied code are normalised only when the caller said nothing about them

**Decision.** Code arriving as a tool argument has its line endings rewritten to the destination
file's, string literals included -- but only when every ending in that code is a bare LF. Code
carrying even one CR LF is written exactly as it arrived. Line endings already in the file are
never touched by this.

**Why any of it.** The endings inside a verbatim or raw string literal are part of its value: a raw
literal written with CRLF and the same one written with LF are different strings, and the compiler
says so. That is why `Whitespace` leaves literals alone and why `MemberSyntax.Shift` keeps each
line's own ending rather than splitting on newlines and joining with one. All of it is right for a
literal being moved.

It leaves a hole for a literal being *written*. An agent composing C# for a JSON argument writes
LF, not because it decided to but because that is what composing a string does. In a CRLF
repository the result fails `dotnet format --verify-no-changes` -- which is what CI runs -- while
no build reports anything, and the tool's own notice can only say what happened, not fix it. The
tool surface of this repository is almost entirely raw string literals, so the tool that exists to
replace a text edit hands the work back to a text edit on the commonest edit there is.

**Why the condition rather than a parameter.** A caller *can* express CRLF: `"\r\n"` in a JSON
string is a carriage return and a line feed, and there is a test that writes one. So the presence
of a CR is a reliable signal that the caller is thinking about endings, and its absence is a
reliable signal that it is not. That gives the rule an escape hatch with no new argument on four
tools: to put a bare LF inside a literal in a CRLF file, write the rest of the code with CRLF, and
nothing is touched.

**It is reported where it changes a value, and only there.** Inside a multi-line literal a
rewritten ending changes what the string says, and a unified diff cannot show it, so silence is not
available: the result names each such literal by the line it is on in the code the caller sent,
and counts the endings. Everywhere else an ending is layout, and making it the file's is what the
whitespace pass does to every line it writes -- so saying so on every payload, which ends lines on
every payload, said nothing a caller could act on and taught them to skim the channel that carries
the notices that matter. What counts as inside is the value, not the delimiters: a raw literal drops
the break after its opening delimiter and the one in front of its closing line, and a break inside
an interpolation hole is code, so rewriting any of those is not reported either.

The line is the caller's, not the file's and not that of whatever was parsed. A body is parsed
behind a signature copied out of the file, sometimes inside braces the caller never wrote, and a
find-and-replace or an insertion puts the caller's text among statements that were already there,
so each counts from where the caller's own text begins, with the blank lines above it dropped
wherever it lands. A literal the file already held, whose endings change because it went through
the same pass -- a moved member, or the part of a body the caller did not write -- is named by its
line in the member as it stood, found there by its text, since the member that is parsed joins the
body to the signature and a line counted in it is not one anybody can find. Whether the escape
hatch is offered turns on whether there is code to write a CR LF into. A move has none, so it is
not. A body edit does, and the condition is asked of the whole body it rebuilds, the file's own
lines included, so one CR LF in the code or replacement the caller sends leaves the file's literals
as they were along with everything else; the sentence says so.

A replacement or an insertion is asked on its own, and given the ending the file's layout writes
where it splices in, before it meets the file's text. Asked afterwards, inside the rebuilt body, the
CR LFs the file already had answer for a caller who said nothing, and the literal they wrote keeps
its bare LFs in a file that then fails the formatting check. It is the layout's ending rather than
the body's, because a checkout can disagree with what the repository declares, and the lines
around the literal are given the declared one. The statements an insertion lands between are
joined with the block's own ending for the same reason in reverse: joins of another kind would make
the file's text look as though nobody had a view about endings, and a literal of its own holding a
deliberate bare LF would be rewritten by an insertion that never touched it.

Every branch of an `#if` is read for literals, whichever symbols happen to be defined -- by the
rewrite and its report, and by the whitespace pass that protects a literal's interior and warns
about one whose endings the file does not use. The text of an inactive branch is rewritten like any
other, and the build that defines the symbol compiles the literal it holds, so a literal found only
in the active branches is a changed value in exactly the build nobody looked at. `dotnet format`
reads those branches too, and fails a bare LF inside one.

**Raw literals also move with their surroundings.** A raw string literal's value is what remains
after the closing delimiter's indentation is stripped from every line, so shifting the content and
the delimiter together by the same amount leaves the value identical -- and it is what puts a
literal written at column zero at the indentation of the code around it. A verbatim literal has no
such delimiter rule, its interior whitespace being its value, so it is still never moved.
