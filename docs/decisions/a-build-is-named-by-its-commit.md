# A build is named by its commit

**Decision.** Every assembly carries its full commit, whether the tree was dirty when it was compiled,
when that was and, for a local build, the checkout it was built from. `BuildIdentity` reads them back.
Every handshake between Rose processes compares commits rather than versions, and names both sides
when they differ. `GET /operator/hello` returns the broker's build, and for a local build how many
commits the checkout's `HEAD` and `origin/main` are past it. The tray's icon names the version and
short commit.

**Why the commit and not the version.** MinVer derives the version from the last tag and the height
above it, so two local builds of different code at the same height carry the same number. A stale
worker beside a rebuilt broker passes a version check as the same build, and the mismatch surfaces
as a missing field or an unknown tool, which sends a reader to the tool rather than to the binary.

**How each part is stamped, and why there.** All of it is in `Directory.Build.props`, and each part
degrades to absent rather than failing a build:

- *The commit* is the SDK's own `SourceRevisionId`, read by SourceLink without a process. It is also
  stamped as `RoseMcp.Commit` metadata, so reading it does not depend on the informational version's
  format. An archive build has none. A commit changes the informational version anyway, so stamping
  it as an attribute recompiles nothing that was not already going to be.
- *The checkout* is the git source root, as metadata, stamped only for a local build. A CI runner's
  path means nothing on the machine running the build, and the point of naming a checkout is that
  somebody can go and look at it.
- *The build time and the dirty flag* are one embedded resource, written only when the project's
  compile is about to run, and `git status --porcelain` -- untracked files included, because an
  untracked source file is compiled -- runs only then too. As attributes, both would be inputs to
  every project's compile: a time would recompile every project on every build, and the dirty flag
  would recompile every project, the WinUI ones included, whenever the tree went dirty or clean or an
  untracked file came or went -- several times a day, for every deploy. A resource is in neither the
  reference assembly nor the assembly-info inputs, and the stamp shares the compile's own inputs, so
  an up-to-date build leaves it alone. `--no-optional-locks`, because a parallel build can run it in
  many projects at once. No git, or a repository git refuses, leaves the flag unknown.

**What a stale dirty flag looks like, and why that is accepted.** The flag is the assembly's, as of
its own last compile, not the tree's now. A project that did not recompile after the tree went dirty
still says clean, which is true of the code it was compiled from, and one that did recompile says
dirty although its own files may be untouched. So a person can see the broker "with uncommitted
changes" beside a worker of the same commit that says nothing, after editing only the broker. That is
why the flag is shown -- in the tooltip, in a mismatch message, on hello -- and never compared: a
comparison would warn on every edit a person builds, about a difference that is usually the edit
they just made. The alternative, a flag that is always the tree's, costs a full recompile whenever
the tree changes state, and a flag gated to release or deploy builds would be absent from exactly the
local builds it is for.

It is in the props file rather than a `Directory.Build.targets` because the probe apps and the
fixtures shadow the props file with their own, and a targets file at the root would reach into both.

**How the commit travels.** Folded into `ServerInfo.Version` as SemVer build metadata --
`1.3.0+<commit>`, with `.dirty` after a dirty build's commit -- because that is the one field every
MCP handshake already carries, and a client that only wants a version still reads one. A host that
has no commit to send -- an older one, or an archive build -- sends a bare version, which reads as a
build whose commit is unknown: where either side has no commit the versions are compared instead,
and the message says the commit is unknown.

**Said, not refused.** A mismatch is logged and, where a person or an agent can see it, shown: a
worker's on its workspace's notices, a live-app host's on its session, the inspector's in its notice
bar. The relay only logs, because every tool it forwards is the tray's whichever build the relay is.
A half-updated install is a state somebody can be in without meaning to, and turning a working
session into a failure over it would cost more than the confusion it prevents.

**What it cannot tell apart.** Builds of one commit with different uncommitted changes carry the same
commit, and the dirty flag is not compared. Telling them apart would take a fingerprint of the
working tree on every build, which costs more than the case is worth: a rebuilt broker beside a
worker from an earlier state of the same dirty tree is the one confusion this leaves.

**Why the checkout distance spawns git, and only there.** Reading `HEAD` and `origin/main` and walking
the history between them is what git does; doing it from the `.git` files would be a second
implementation of the commit graph. So the broker runs `git rev-list --count`, but only for
`/operator/hello`, never on a path that answers an agent. Each command is bounded and killed with
whatever it started when the budget runs out, its output is redirected so nothing reaches a stdio
broker's stdout, and the answer is kept for half a minute so a window polling hello does not start a
process per poll. Hello waits a couple of seconds at most for the count and otherwise says it is
still being counted, because hello also carries the build, and a window whose read timed out would
lose the mismatch it was asking about. Disposing the reader cancels a count and waits, bounded, for
the git it kills. A failure -- no git, no `origin`, a commit rebased out of the checkout -- is a
sentence in the answer rather than an exception.
