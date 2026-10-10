# Live-app tests

Read before adding or changing a test in `LiveAppSessionTests` or a live-app fixture.

**Which CI job runs a test is decided by what it needs, and the need is the fixture it takes.** A
class that asks for a probe app in its constructor wants a C++ toolset, the Windows App SDK,
developer mode and a machine-wide package registration, so it carries `[Category("ProbeApp")]`: the
integration job excludes it, and the probe-apps job, whose steps set all of that up first, runs it.
Everything else in this half drives `DebugProbeTarget`, an ordinary .NET child process, and any
hosted Windows runner attaches a real ICorDebug session to one, so it runs in the integration job
on every change rather than only when the probe apps' inputs do.

**Nothing in CI is allowed to skip, and each job carries what it takes not to.** Every skip in this
suite goes through `MachineLimit.Reached`, which skips on a developer machine and fails where
`ROSEMCP_TESTS_REQUIRE_TOOLCHAIN` is set -- and both integration jobs set it. So a runner that lost a
component fails naming it instead of reporting the same green as one that ran everything. The cost
is that each job has to install what its tests need: a runner has no x86 .NET runtime, so the
integration job installs one for `Attaches_to_an_x86_target` and points `DOTNET_ROOT_X86` at it; it
has no developer mode, Windows App Runtime or, sometimes, UWP tooling, so the probe-apps job sets
those up, each in a step that checks its result and says what it found. The UWP probes' debug
frameworks are the exception: the fixtures install them from their own build's recipe when a
registration is refused for one, so a developer machine gets them the same way (see
[the decision](../decisions/the-probe-app-tests-run-on-a-hosted-runner.md)). A skip is how a capability
stops being tested while the build stays green, so the answer to one is a runtime, a toolchain or an
exclusion that says out loud what is not covered -- never a skip left in place. A test that calls the
framework's skip directly is out of the switch's reach, and `ProbeAppCategoryTests` fails on one.

Do not apply the category by hand in either direction. `ProbeAppCategoryTests` decides from the
constructor and fails both ways, because a probe-app test without it is a red build on a runner that
was never set up to serve it, and a debugger test wearing it runs only when the probe apps' inputs
change. It also holds the two jobs' filters to the category and checks that both set the switch.

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

A sixth, from the same family and found the same way: **a wait starts from a cursor that means
"before I acted", and the answer to the call that acted is where that cursor comes from.** Every
live-app answer carries one, so `WaitForEventAsync` takes `startCursor: selected.Cursor` off the
select it is waiting on the consequences of -- never the default of zero, which is everything the
session ever recorded. The probe apps announce their ticks and their own state changes on a loop and
a shared session outlives every test in its class, so waiting from zero matches something from before
the test began, returns in a hundredth of a second having waited for nothing, and leaves every
assertion under it running against a state the app has not reached. The rule and the reasoning behind
it are in [an action hands back the event cursor](../decisions/an-action-hands-back-the-event-cursor.md).

This is the third costume of "passes alone, fails in company", and the cheapest to wear by accident,
because the margin is one turn of whatever loop the app is running. A session fresh enough to hold no
such event yet makes the wait real and the test pass; the same session five seconds older holds one,
and the test reads the app before it has done anything. Nothing about the test changes in between,
which is what makes it read as a flake rather than as the ordering bug it is.
