# Only a missing toolchain skips a test

**Decision.** When a test builds a native provider or a probe app, only `build.ps1`'s exit code 3 -- no
MSVC toolset, or no Windows SDK -- skips it. Any other non-zero exit fails the test with the build's
output, and so does a build that reports success without producing its output.

**Why.** Treating any failed build as "no toolchain here" turns a compile error in the provider into
every XAML test quietly skipping with the suite green. A capability not being tested is worse than a red
build, and from the outside it looks exactly like a machine that genuinely cannot build it.
`build.ps1` already tells the two apart, so the tests believe it.

**The rule beside it.** A skip for a missing toolchain is a fact about the machine and stands.
[A live-app skip is a failure once the app has launched](a-live-app-skip-is-a-failure-once-the-app-has-launched.md)
draws the same line for launches: a skip that is about this attempt rather than this machine is a
failure.

**A consequence worth knowing.** The integration suite builds the provider itself, so building it by
hand while the suite runs races over the same outputs and breaks both. Do one at a time.
