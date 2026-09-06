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

**It is reported.** The result says how many endings were rewritten. This changes what a string
says, and a unified diff cannot show it, so silence is not available.

**Raw literals also move with their surroundings.** A raw string literal's value is what remains
after the closing delimiter's indentation is stripped from every line, so shifting the content and
the delimiter together by the same amount leaves the value identical -- and it is what puts a
literal written at column zero at the indentation of the code around it. A verbatim literal has no
such delimiter rule, its interior whitespace being its value, so it is still never moved.
