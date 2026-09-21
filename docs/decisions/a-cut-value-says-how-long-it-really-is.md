# A cut value says how long it really is

**Decision.** Every string value a read reports is cut to 200 characters, and a value that was cut
carries `fullLength`: how long it really is. `rose_debug_evaluate` takes a `maxLength` to raise the
cap for one value, up to 65536 characters. The whole string is read off the target either way -- the
cap is on what is reported, not on what is fetched.

**Why a cap at all.** A frame can hold twenty locals and each of them a document. Reading a frame
would otherwise be a transfer of the target's heap into an answer that goes into a model's context
whole, and nothing downstream can recover from that once it has happened.

**Why the ellipsis was not enough.** A string is allowed to end in an ellipsis, so `"…"` at the end
of a value is not evidence of anything. A 431-character URL and a 200-character one that happens to
end in one were the same answer. Worse, the caller most likely to be holding a cut value is the one
forwarding it somewhere -- into a bug report, a diff, a message to another team -- which is exactly
where a fragment passed off as the whole does damage. `fullLength` is the field, rather than a
`truncated` flag, because "how much am I missing" is the question a caller actually has and a flag
does not answer it.

**Why an opt-in rather than a bigger default.** The values that hit the cap are the ones somebody
set a breakpoint to read: a consent URL, a request body, a connection string, a SQL statement. They
are read one at a time and deliberately. Raising the default would pay for that on every variable of
every frame, for the one in a hundred that anybody wanted.

**Why a ceiling on the opt-in.** The answer is not paged and goes into a context whole, so an
unbounded read is a call that cannot be taken back. 65536 clears every value of the kind above by a
wide margin. Past it, `fullLength` still reports, so a caller is never guessing at what is missing
even where it cannot have it.

**Why not expansion or a substring.** A string has no children to expand into -- it is one value --
and reading past the first slice would mean `Substring`, a method call on the target, which nothing
here will run for the reason
[expression evaluation reads memory and runs no code](expression-evaluation-reads-memory-and-runs-no-code.md)
gives. Raising the cap on a read that already has the whole string in hand costs nothing and needs
no such thing.

**Why a tracepoint cannot raise it.** A tracepoint is for watching something happen many times, and
a per-hit cap of 64KB on a hot method is the heap transfer this exists to prevent, arriving by
another door. Its logged values report `fullLength` like everything else; reading one whole means
stopping on it. See
[a tracepoint's message interpolates a value path](a-tracepoint-message-interpolates-a-value-path.md).
