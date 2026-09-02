# Live-app debugging: security model (#15)

Attaching a debugger, launching a process, and (later) injecting a provider are powerful. This is what
RoseMCP permits, how it is gated, and what a deployer should know. The minimal gate described here
ships in code today (`LocalAttachPolicy`); the injection sections describe the model the XAML track
will land against.

## Trust boundary

RoseMCP runs as the user who started it, and the agent drives it. Every capability here is bounded by
that user's own privileges: the agent can do what the user could already do at a shell. Debugging a
process the user owns, or launching a program the user could launch, is not a privilege escalation --
it is the user's own authority, exercised through the agent. The gate exists to keep the surface *at*
that boundary, not to grant anything past it, and to refuse the obviously-wrong target early and
clearly rather than deep inside ICorDebug.

Nothing here is a remote or cross-user capability. There is no network attach, no debugging another
user's processes, no elevation.

## Attach

`rose_debug_attach` is gated by `LocalAttachPolicy` before any ICorDebug call:

- **Local only.** The pid must resolve to a process on this machine.
- **Running.** A pid that is not a live process, or has exited, is refused with a plain reason.
- **Not a system process.** pids below the first user process (System, Idle) are refused.
- **Same user.** The target's owning SID is compared to the current user's; a mismatch is refused.
  When ownership cannot be read, the decision is deferred to the operating system.

The operating system is the real backstop: `DebugActiveProcess` fails unless the caller's token may
debug the target, which normally means the same user (or a holder of `SeDebugPrivilege`). The policy
is an early, legible refusal layered on top of that ACL, not a replacement for it.

## Launch

`rose_debug_launch` starts a local executable under the debugger. It runs as the current user, exactly
as if the user had launched it -- the same authority as attach, applied to a program the user names. A
missing file is refused; a non-.NET target fails cleanly when its runtime never signals startup.
Detaching leaves the launched process running; RoseMCP does not silently keep orphans beyond the
session's host, which dies with the broker.

## What the events expose

Debug events carry runtime data -- exception types and stack traces, `Debugger.Log` output, and, at a
stopping breakpoint, the top frame's locals and arguments. This can include sensitive values. It stays
local: captured in the per-target host, read by the broker over stdio, and surfaced only to the agent
session that asked. RoseMCP sends none of it to any external service. A deployer who treats agent
transcripts as sensitive should treat debug output the same way.

## Injection (XAML track, not yet shipped)

The XAML provider is injected into the target's process, and for a packaged (UWP) app that means its
AppContainer. The model the track will hold to:

- **Least-privilege staging.** The native provider is staged in a temporary directory granted only the
  access the AppContainer needs (read/execute to `ALL APPLICATION PACKAGES`), never a broad grant.
- **Deterministic teardown.** Every exit path removes the ACL grants, disables package debug mode, and
  unloads the provider. A crash must not leave a target with debugging enabled or a world-readable
  provider on disk.
- **Rebuildability.** The provider is shadow-copied so it never blocks the user rebuilding it, the same
  invariant the analyzer loader already holds.
- **Abuse history.** The XAML diagnostics API has been abused before (CVE-2023-36003); the safe path
  -- the well-known local endpoint, same-user, explicitly targeted -- is the default and the only one
  offered, with no remote or cross-user injection.

## Deliberate non-goals (for now)

- No authentication beyond the same-user boundary; RoseMCP trusts its own local session.
- No audit log of debug actions beyond the existing activity log and file logs.
- No per-tool capability configuration; the gate is uniform. A deployer wanting the debug surface off
  entirely can omit its registration.
