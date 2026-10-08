# A plural intent is one call, and one bad entry never fails the rest

**Decision.** `rose_debug_add_tracepoint`, `rose_debug_set_breakpoint`, `rose_debug_remove_tracepoint`
and `rose_debug_remove_breakpoint` take a list, and only a list. Adding takes a list of entries, each
an object carrying its own location and its own options -- `{location, logMessage, logEveryNthHit,
condition}` for a tracepoint, `{location, autoContinueSeconds, condition}` for a breakpoint. Removing
takes a list of ids. The answer has one entry per request, in the order given, each with a `status`
that is the outcome or the reason there was none, a count of the ones that took, a `total`, and
`notes` for what is true of the batch rather than of one entry -- the shape `rose_xaml_apply` already
answers with.

**The failure it prevents.** In an agentic loop a round trip is not a network hop, it is a model turn.
Instrumenting a code path is never one tracepoint: it is the entry, the exit, the branch somebody
suspects and the loop nobody trusts. One location per call made that four or six turns, each a chance
to lose the thread, each a result envelope, and each a partial failure to reconcile by hand -- three
set, one refused, and nothing anywhere saying what the session now holds. The tools are pitched
against adding log statements and rebuilding, and that alternative is plural in a single edit. A tool
that loses to it on turn count is a tool nobody reaches for.

It cannot be fixed at the protocol. JSON-RPC batching would save round trips, but the model still has
to decide each call separately, and the decisions are the cost. Only an argument shape that takes the
set collapses them.

**Why entries rather than a list of locations sharing one set of options.** The options are not
shared in practice. A message interpolates the arguments and locals of the method it fires in, and a
condition names them, so the entry, exit and branch of one path each want their own: `qty={quantity}`
means something in `Cart.Add` and nothing in `Cart.Clear`. A list of locations with one message would
either be refused for most real requests or log a placeholder that cannot be read at most of its
locations. The entry is also exactly what the inspector already sends -- `AddTracepointRequest` and
`SetBreakpointRequest` are the items of the MCP lists as well as the operator API's bodies -- so a
person and an agent describe a breakpoint with the same fields, and one declaration keeps the two
schemas the same.

**Why one spelling.** The argument is plural, with no singular sibling beside it. Two spellings
of one idea -- `location` beside `locations` -- is the inconsistency the tool surface already pays
for elsewhere, and it doubles what a caller has to learn to get a single tracepoint. A caller who
sends the singular shape anyway is told which argument it got wrong: a bare entry where a list was
wanted, a bare location string inside the list, and an entry with no location are each named by
`ToolArgumentShape`, with an example entry, rather than reported as the binder's CLR type and JSON
path.

**Why one bad entry never fails the batch.** The entries are independent. A location that does not
parse says nothing about the five beside it, and refusing all six for it sends the caller back to
retry the good ones piece by piece -- the turn count the batch exists to save. So an entry's own
mistake is its `status` (`refused: ` and the reason), and only what makes the whole call impossible is
an error: no such session, no target attached, or a list with nothing in it. The last is refused
rather than answered, because an answer with no entries reads as a call that worked. What counts as
an entry's own mistake is what its parsers throw, `ArgumentException`; anything else is a fault in the
session and fails the call as it would have for one request.

**Not bound is a success, said.** An entry whose module has not loaded is added, waits, and binds on
the load -- that is how a breakpoint in a plugin is set before the plugin arrives. It is not a
refusal, and its status says `added, not bound yet` so that it does not read as bound either. The
reason it is waiting is on the entry's tracepoint or breakpoint, once.

**Removing answers each id, and refuses one of the other kind.** An id that is already gone is `not
found`, and does not stop the rest; cleaning up after a path should not need to know which of its ids
the caller already removed. An id of the other kind -- a breakpoint's id given to the tracepoint
removal -- is refused rather than removed. Both kinds share one table, so without the check the
breakpoint goes and the answer is a tracepoint list in which nothing shows that it went. The answer
carries what the session still holds, so the caller learns its state without a second call.

**One stop for the batch.** Binding stops the whole target to walk its loaded modules. The entries of
a batch are all recorded first and bound in one pass, so six locations cost the debuggee one stop,
not six.

**The inspector keeps its singular forms.** A person adds one breakpoint at a time, by clicking.
`LiveAppSession` keeps one-entry methods over the batch, which throw a refused entry's status rather
than returning it, since a caller asking for one has no other entry to keep. There is one way a
breakpoint is set; the singular form is a wrapper, not a second path.

**When to revisit.** The read tools that are plural by intent -- `rose_find_references`,
`rose_symbol_info`, `rose_outline` -- are singular on purpose: their per-item answers are
large, and batching a read whose answer is already large multiplies the payload as well as saving the
turns. They follow once their answers are small enough to carry several of.
