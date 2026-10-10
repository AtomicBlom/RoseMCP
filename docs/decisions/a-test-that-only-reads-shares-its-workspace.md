# A test that only reads shares its workspace

**Decision.** An integration test that only reads a fixture takes that fixture's shared workspace from
`SharedWorkspaces`, which TUnit hands to its class once for the whole assembly. Each fixture is copied
and loaded at most once a run, the first time a test asks, and every reading test after that reads the
same session. A test that writes -- through a tool, or to the fixture on disk -- or that needs a load
of its own shape (other options, a build first, an analyzer loader it controls) opens its own with
`TestSession.OpenAsync`.

**Why share at all.** A load is a design-time build. It costs seconds and competes for the same cores
as every other test running beside it, where the read a test makes afterwards costs a fraction of
that. Loaded per test, a read-only test spends most of its time getting to where it can begin, and a
dozen loading at once slow each other down by as much again, so the suite's wall clock is made of
contention rather than of work. Shared, a new reading test costs its read rather than a load.

**Why it is safe for readers.** A read takes the session as pure input. `WorkspaceSession` is built
for concurrent reads: every read is a barrier on the single writer that returns an immutable snapshot,
and the expensive part runs off the writer against it. Nothing a read does can change what the next
reader sees.

**Why a reader cannot write.** Sharing is only safe while nothing writes, and a rule that says so is a
rule somebody forgets, so three things make it structural. The session is not handed out: a shared
workspace offers a read and paths, and no tool can write through a read. The copy's files are read-only
on disk, so an edit behind the workspace's back fails in the test that made it, which is the test that
should fail. And a new file is the one write neither of those stops, so every read checks the workspace
is still at the revision it loaded at and refuses to answer from one that has moved. That last check
fails whichever test reads next rather than the one that wrote, which is why it is the backstop and
not the mechanism.

**Why lazy, and why one object for every fixture.** An object shared across the assembly is
constructed before any test runs, so work in its constructor would make a filtered run of one test pay
for every fixture. Nothing is copied or loaded until a test asks for that fixture, which is the same
choice the probe apps make. One holder with a property per fixture, rather than a type per fixture,
lets a class that reads several fixtures take one parameter and name the one each test wants.

**Why the load belongs to no test.** It runs on the shared workspace's own token, which only disposal
cancels, and without the first caller's execution context. A test that times out abandons its own wait
rather than the load every other reader is waiting on, and nothing the session does later is
attributed to whichever test happened to ask first. A load that fails is not tried again: every reader
is told what the one attempt hit, so a broken fixture costs one load rather than one per test.

**What does not share, and why.**

- **Tests that write**, by tool or on disk. They change what every other reader would see.
- **Tests about a load or about reconciling with disk** -- staleness, resilience, build freshness, a
  project appearing mid-session. The load or the change is the thing under test.
- **Tests that need the fixture in a state of their own**: the fresh-clone state with no `bin` or
  `obj`, which is where a source generator quietly produces nothing, or a project built before the
  load. A shared copy is in whatever state its one load left it.
- **A fixture only one test reads.** Sharing it saves nothing, so it is not on `SharedWorkspaces`.

A test that writes loads a fixture of its own. Sharing a warm copy or a pool of loaded workspaces
between writers is a different trade: what a writer hands back has to be
checked, the way a live-app slot is (see
[the-live-app-suite-is-phased-by-what-tests-share](the-live-app-suite-is-phased-by-what-tests-share.md)).
