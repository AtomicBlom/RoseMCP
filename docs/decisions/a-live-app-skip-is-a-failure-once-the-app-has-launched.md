# A live-app skip is a failure once that fixture's app has launched

**Decision.** A skip in the live-app fixtures is a failure once the fixture it belongs to has
launched its app successfully at least once in the run. Before that first success a skip stands, and
the skips that decide whether the machine can run these tests at all are untouched.

**The two kinds of skip, which is what the rule is drawn between.** One kind is a fact about the
machine: no Visual Studio MSBuild with the UWP tooling, no C++ toolset, no Windows App SDK, developer
mode off, no x86 runtime for an x86 target. Each is decided once per fixture, before anything is
launched, and each is a correct reason not to run. The other kind is a fact about this attempt: the
app was started and exited before it could be attached to, or never opened a window inside thirty
seconds. That second kind is the one that reads as green while the test did not run, and it is the
one an acceptance test cannot afford, because the whole assertion is about an app that is up.

**Why the first success is the condition, rather than failing outright.** Failing outright is the
obvious rule and it is wrong on a machine where the Windows App Runtime does not bootstrap for the
unpackaged WinUI probe (#180): every launch dies, and a suite that is permanently red says nothing
about the change under test. The first successful launch is the evidence that separates the two --
a machine that cannot run these tests never produces one, and a machine that can produces one in the
first few seconds. So the rule reddens exactly the case it exists for, an app that was up a moment
ago and is not now, and leaves a machine that was never able alone.

**Per fixture, not per run.** The classic UWP probe, the modern UWP probe and the unpackaged WinUI
probe are three apps, three toolchains and three ways to fail. The WinUI probe coming up says nothing
about whether the modern UWP package registered, so a flag shared between them would let one
fixture's success arm a failure in another that has never worked on this machine.

**What the rule does not recover, and why the fixture says so directly.** A fixture that never
launches at all skips every one of its tests and is untouched by this. The remedy there is that a
fixture explains itself: where registering the package fails, the failure the registration reported
is what the skip says, rather than a guess. Inventing a cause -- naming developer mode for an error
that was captured and discarded -- is worse than a skip, because it sends the reader to a setting
that is already correct and closes the question.

**What it costs.** A machine that intermittently fails to launch the probe now fails the suite
instead of quietly shrinking it. That is the intended trade: an acceptance test that did not run is
the one outcome this suite must not report as success.
