# The live-app suite is phased by what each test can share

**Decision.** The live-app tests share one launched probe app and ask for it in one of three ways.
`TakeAppAsync` launches an app of the test's own. `TakeSessionAsync` takes the shared app exclusively,
for state no owner smaller than the app holds. `TakeSlotAsync` shares the app and owns one named slot to
build elements in. Most tests take a slot.

**Why phase at all.** A launch of the UWP probe costs about 6.5 seconds and the XAML work in a test about
1.2. With a launch per test, each new test costs six times its own work, and every new probe and
framework adds tests. Phasing changes that slope, which is what decides how the suite grows.

**Why "isolation" is the wrong thing to argue about.** Each way of asking gives up something different.
A fresh app gives up nothing and costs a launch. An exclusive turn gives up running alongside anything,
and exists for the selection, select mode and the resource dictionary, which have no owner smaller than
the app. A slot gives up having the app to itself and keeps only what it builds.

**Why slots are named.** An element the markup never named is addressed by its position under its
nearest named ancestor, so two tests adding children to one container renumber each other's addresses.
Anchored on its own named slot, an address stays put however busy the app is.

**What a slot cannot give.** An element built in a slot has no source info, because nothing declared it,
and is not pristine, because creating it materialises its collection properties. A test about either
reads a dedicated element the probe's markup declares.

**Why state has to be handed back.** An exclusive turn fails if it leaves anything selected or armed, and
a slot fails if it is not returned empty. Both clean up as well, so one offender does not cascade, but the
offender still fails: residue is found by a different test somewhere else, which is the hardest failure
there is to trace.

The rules that keep this working are in [live-app-tests](../invariants/live-app-tests.md).
