using RoseMcp.IntegrationTests;
using Xunit.Sdk;
using Xunit.v3;

// The UWP probe app is built, staged and registered once for the whole run rather than per test.
// An assembly fixture is what gives it an end-of-run teardown, so the package this suite registers
// on the machine is still removed -- which twenty per-test Remove-AppxPackage calls used to do.
[assembly: AssemblyFixture(typeof(UwpProbeApp))]

// The WinUI 3 probe gets a fixture of its own rather than sharing the UWP one, so the two sets
// of live-app tests overlap instead of queueing behind a single gate. They drive different
// processes and share no package, provider or window, so there is nothing to serialise between
// them -- only within each.
[assembly: AssemblyFixture(typeof(WinUiProbeApp))]

// Tests are scheduled across the threads, not collections. Every class here was its own collection
// and therefore ran its own tests one at a time, so a long class held a thread for its whole length
// while the others went idle: 1606s of test work came out as 770s of wall clock, a 2.09x return on
// twelve cores. Nothing here shares state that makes this unsafe -- no test touches an environment
// variable, there is no mutable static state, and every fixture copy is GUID-suffixed -- with the
// one exception that says so for itself, in LiveAppSessionTests.
[assembly: Parallelization(Mode = ParallelMode.All)]
