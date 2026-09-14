# A UWP app is debugged from its first instruction

**Decision.** `rose_debug_launch_uwp` registers a resume stub as the package's debugger, activates the
app so that Windows creates it suspended, attaches, and only then lets it run. Attaching after
activation is kept only as the fallback where the stub cannot be used.

**Why.** Attaching a moment after `ActivateApplication` misses the part of a UWP app most worth
debugging: the first `OnLaunched`, the startup module loads, and any exception thrown before the attach
lands.

**How it works, and the three facts that shaped it.**

- `IPackageDebugSettings::EnableDebugging` with a command line makes Windows create the app suspended
  on its next activation and start that command line as its debugger, with `-p <pid> -tid <tid>`
  appended. The command line is this same host executable in resume-stub mode, and the host and the
  stub meet on a named pipe.
- `ActivateApplication` does not return until the app is resumed. So activation runs on a background
  thread, and the stub sending the process id over the pipe breaks the circle: the startup notification
  needs the pid, and activation yields it only once the app has been resumed.
- The app is created suspended, not as a native debuggee, so the stub exiting does not kill it. The
  stub is a courier and a synchronisation point, not a debugger.

The host then arms dbgshim's startup notification for the pid, tells the stub to resume the app, and
attaches when the runtime signals -- the same sequence as launching a plain executable under the
debugger. The stub resumes the app even if the host never answers, so a crashed host leaves an app
running normally rather than suspended forever.

**Limits.** The debugger command line has to stay under about 256 characters, or `EnableDebugging`
returns `E_INVALIDARG`. The stub's command line is kept short for that reason, and the host falls back
to attaching after startup where even the short form would not fit. Debug mode is lifted from the
package when the session ends.
