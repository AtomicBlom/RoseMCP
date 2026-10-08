# A path a read returns is relative to where the caller stands

**Decision.** `rose_find_references` gives each file, and each definition, relative to the directory
the calling session runs in, with forward slashes, and names that directory once as `relativeTo`. A
file outside that directory keeps its absolute path, and so does a generated document, whose hint
name is what reads it back. The broker does it, after the worker has answered; the worker answers
with absolute paths as it always has. The routing rule is unchanged: a relative path a caller sends
is measured from the calling session's directory, and from nowhere else.

**The failure it prevents.** Every file of a reference search repeated the workspace root, under a
result that already named the workspace, and on an answer spanning many files the root was most of
each path -- the one part of it the caller had before it asked.

**Why the session's directory and not the workspace's.** The point of a path in a result is that the
caller can hand it to the next call, and a relative path the caller sends is measured from where it
stands -- the rule that stops a path written in one worktree from landing in another. Relative to the
session's directory, a returned path names the same file when it comes back, by construction, with
nothing else sent beside it. Relative to the workspace root it would do so only where the session
happens to run in the solution's own directory; everywhere else -- a solution under `src/`, a session
started in a parent folder -- the path a result handed over would be measured from somewhere it was
not made, and name a different file or none.

The alternative was to keep workspace-relative paths and change the routing rule, measuring a
relative path that arrives with a `workspaceKey` from that key's workspace. It loses on both halves.
It makes a round trip depend on the caller sending a second argument with the path, which is exactly
the kind of anchor agents drop. And it gives one string two meanings depending on what travels with
it, so `tests/Foo.cs` would name different files in two calls from the same place -- the ambiguity the
rule exists to remove.

**Why the broker does it.** Only the broker knows where the caller is standing: a relay says so on
every call, and a session with no relay is the broker's own directory. A worker answers for whoever
asked and cannot know. So the shortening is a step on the broker's side of the hop, and the hop to the
worker stays absolute-only.

**Why a path outside the directory stays absolute.** A path that climbs out with `..` is longer than
the one it replaces, depends on the depth of the caller's directory, and is the shape a mistyped path
takes. An absolute one is honoured wherever it points, which is all the caller needs of it. An http
client with no relay in front of it is standing in the broker's own directory, which holds none of
its files, so it gets absolute paths throughout.

**What it does not cover.** Only `rose_find_references` lists enough files for the root to matter.
An outline says its file once per type and symbol info once per declaration. The write results,
where the repetition costs most, can take this same step at the same place rather than changing the
routing rule.
