# A debug session detaches before its host is closed

**Decision.** Ending a session asks the live-app host to detach from its target while the host is
still running, and only then closes the host's stdin.

**Why.** A process debugged through ICorDebug dies when its debugger process dies without detaching.
Closing the host first would take the target with it, so ending a session over an app somebody
attached to -- to watch it, not to stop it -- would kill the thing the session was only observing.

**Why a failed detach is said rather than swallowed.** A detach that fails leaves a debugger attached
to somebody's process, which is the one outcome where the target is at risk. `rose_debug_detach` fails
in that case and says why, instead of reporting the session closed as though the target were safe.
#84 tracks a path where the report still claims more than it knows.

**What it cannot cover.** A host killed outright, or a broker that crashes, gets no chance to detach.
A target the host launched itself goes with it by design, because nothing else knows it is there. One
it attached to goes with it too, and there is nothing a process that has already died can do about
that.
