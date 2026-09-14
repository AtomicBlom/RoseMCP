# Toolchain builds happen once per test run, and are never merely found

**Decision.** The integration tests build what they need from outside the solution build -- the x64
live-app host and probe target, the native providers, the UWP probe app and its registration -- once per
test run, memoised and shared by every test that needs it. An output that already exists is rebuilt
once rather than trusted.

**Why rebuild rather than use what is there.** A RID-specific build such as `win-x64` is one that an
ordinary `dotnet build` of the solution never touches, so an existing executable is routinely a source
change out of date. A test that runs yesterday's host reports failures describing code that has since
been fixed, which reads as a bug in the change under test. MSBuild is incremental, so the check is
nearly free.

**Why once rather than per test.** Building the native provider takes about 23 seconds every time,
however warm the machine, because the cost is entering the MSVC toolchain rather than compiling
anything. Seventeen of those were more than half the live-app suite's wall clock.

**Why lazily.** An assembly fixture is constructed before any test runs, so eager work there would make
a filtered run of a single Roslyn test pay for an MSVC build it does not use.

The rules that keep the shared fixtures honest are in [live-app-tests](../invariants/live-app-tests.md).
