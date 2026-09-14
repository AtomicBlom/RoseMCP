# A live edit is diffed against what was last sent to the app

**Decision.** `rose_xaml_apply` takes a file path. The host diffs the file's current markup against
the markup it last applied to that app for that file, applies the resulting edits, and moves the
baseline on. The session holds the baselines, one per file, in memory, for as long as it lasts. Both the
diff and the apply happen in the host, through `RoseMcp.XamlDiff`.

**Why the session holds the baseline.** Asking the caller for both versions reads reasonably and is
close to unusable: an agent that has just written a file does not have what was in it. The session is
the one party in a position to remember, and a baseline means nothing without the running app it
describes, so it ends with the session.

**Why the first apply records and applies nothing.** What the running app was built from is not on disk
once the file has been edited. Diffing the file against itself would find nothing and report success,
silently skipping the caller's first edit. So the first apply records the baseline and says that is what
it did, and `oldXaml` is available for that one call.

**Why the baseline moves even when an edit fails.** A structural edit is not idempotent. Re-sending an
`AddChild` because something else in the batch failed adds a second copy of the element on the attempt
that works. Failures are reported and belong to the caller.

**Why markup that does not parse is refused rather than recorded.** Otherwise a half-written file becomes
the baseline, and every later apply reports a parse error about a file the caller has since fixed.

**Why the file's age is three-valued.** Unchanged since the app started, changed since, or unknown. A
process that will not report its start time is no evidence either way, and calling that "changed" would
be a claim about the file with nothing behind it.

**Why there is no file watcher.** Applying on every save would change a running app from a keystroke,
including the saves mid-edit that do not parse, and an MCP tool has nowhere to push the outcome. The
agent asks, and is told.
