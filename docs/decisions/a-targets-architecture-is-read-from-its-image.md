# A target's architecture is read from its executable image

**Decision.** Which live-app host the broker starts for a running process -- x64, x86 or arm64 -- is
decided by reading the machine type from the PE header of the target's main module.
`IsWow64Process2` is the fallback, for when the image cannot be read.

**Why the host has to match.** ICorDebug has no cross-architecture path. The host loads the
`mscordbi` that matches the target's runtime, and an arm64 host cannot debug an x64 process.

**Why not `IsWow64Process2` first.** On ARM64 Windows it reports an x64 process running under
emulation as native ARM64, because x64 emulation there is not WOW64. Trusting it picks an arm64 host
for an x64 target, which then fails to load the x64 `mscordbi` with a `BadImageFormatException` -- an
error that reads like a broken install rather than a wrong choice. The PE header says what the binary
is, which is the question actually being asked.

**What it does not decide.** A UWP launch has no running image to read before the host exists, so it
is decided another way, and that way is wrong for a modern UWP app built x86 (#117).
