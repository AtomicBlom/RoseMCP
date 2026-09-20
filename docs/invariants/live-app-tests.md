# Live-app tests

Read before adding or changing a test in `LiveAppSessionTests` or a live-app fixture.

**What CI runs is decided by what a test needs, and the need is the fixture it takes.** A class that
asks for a probe app in its constructor wants a C++ toolset, the Windows App SDK, developer mode and
a machine-wide package registration, so it carries `[Category("ProbeApp")]` and the hosted runner
skips it -- and an absent toolchain there answers by skipping, which reads as a pass, so excluding
the class is what says the coverage is not there. Everything else in this half drives
`DebugProbeTarget`, an ordinary .NET child process, and a hosted Windows runner attaches a real
ICorDebug session to one without complaint. Thirty-three debugger tests run there on that basis,
eleven of which proved it by running for weeks after a file split dropped their category.

Do not apply the category by hand in either direction. `ProbeAppCategoryTests` decides from the
constructor and fails both ways, because a probe-app test without it is a red build on a runner that
could never have served it, and a debugger test wearing it is coverage nobody knows they have lost.

The live-app tests are the expensive part, and they are phased by what each one can share (D33, D35).
A launch of the UWP probe costs about 6.5 seconds and the XAML work in a test costs about 1.2, so the
question that decides the suite's growth is what a *new* test costs. Three ways to ask for the app,
and what each gives up is different -- "isolation" as one property is the wrong thing to reason
about:

- `TakeAppAsync` launches its own app. For tests where a fresh process is the subject.
- `TakeSessionAsync` takes the shared app to itself, for state with no owner smaller than the app:
  the pick, select mode, the resource dictionary. **It must hand the app back unselected and
  unarmed, and the turn fails the test that does not.**
- `TakeSlotAsync` shares the app and owns one named slot to build elements in. Most tests.

Slots are *named* because an unnamed element is addressed by position under its nearest named
ancestor, so two tests filling one container renumber each other's addresses. Two things they cannot
do: a slot-built element has no source info, because nothing declared it, and it is not pristine,
because creating it materialises collection properties.

**A slot has to be given back empty, and the turn fails the test that cannot do it** -- the same rule
as a phase B turn, for the same reason. Slots come off a stack, so the one just released is the next
one handed out: residue is picked up immediately by a different test, which counts elements it never
added and fails somewhere else entirely. That is how the last-first removal ordering was found (D36),
and it is worth knowing that the bug wore three costumes -- a removal reported applied that did not
happen, an element count off by one, and a test that passed alone and failed in company. Checking the
cleanup is what collapsed them into one.

Four rules hold the whole thing up, each of which cost a run to learn. **Toolchain work goes in the
assembly fixture, once, never per test** -- the native provider is 23 seconds every time however warm
it is. **Serialise the resource, not the class**: `[TestClass(DisableParallelization = true)]` reads
as "not in parallel with each other" and means "not in parallel with *anything*", which made the
suite's two halves add up instead of overlapping; `MaxThreads` does not bound async tests either.
**One gate for everything that touches the app** -- two locks over one single-instance app is not two
locks, it is none, and that rule keeps being applied one layer too high. Sequencing the *tests*
correctly is not enough if the thing they queue for can be rebuilt by several of them at once:
phase C tests overlap by design and each asks for the shared session, so after a phase A test ends
the app they all arrive together, all correctly see no app, and all start by ending the app before
launching it. Bringing the app up is the one thing a reader does that is not read-only, so it takes a
lock of its own (D36). And **a fixture check that can hang is worse than the bug it looks for**: bound
it, or a failing test becomes a wedged suite.

A fifth, learned later and the hard way: **green once is not green.** This suite was reported passing
off a single run and was in fact failing one or two live-app tests a run, from three unrelated causes
that only repeats made visible (D36). A flake rate is a measurement like any other and needs more
than one sample. One of those three is worth stating as its own rule, because it is easy to write
again:
**a fixture's timer has to outlast the whole suite, not one test.** `DebugProbeTarget` self-terminated
after 120 seconds, which was ample when a live-app test had the machine to itself and became wrong
the moment they shared it -- the tests that end by asserting their target is still running failed on
it having correctly done what it was told. It is ten minutes now.
