# Live-app debugging: security model & threat model (#15)

Attaching a debugger, launching and activating apps, injecting a provider, mutating a running UI, and
reading a stopped frame's memory are powerful. This is what RoseMCP permits, how each capability is
gated, the threats considered, and what a deployer should know. Everything described here is shipped
unless a line says otherwise.

## Trust boundary

RoseMCP runs as the user who started it, and the agent drives it. Every capability here is bounded by
that user's own privileges: the agent can do what the user could already do at a shell. Debugging a
process the user owns, launching a program the user could launch, injecting into an app the user is
developing -- none is a privilege escalation; each is the user's own authority, exercised through the
agent. The gates exist to hold the surface *at* that boundary, not to grant anything past it, and to
refuse an obviously-wrong target early and legibly rather than deep inside ICorDebug or the shell.

Nothing here is remote or cross-user: no network attach, no debugging or injecting into another user's
processes, no elevation. The operating system is the real backstop -- `DebugActiveProcess`, package
activation, and the AppContainer ACL all enforce the same-user boundary in the kernel; RoseMCP's own
checks are an early, clear refusal layered on top, never a replacement.

## Assets

- **The user's other processes and their data.** Only same-user, non-system targets are reachable.
- **The debuggee's integrity.** Inspection must not corrupt or wedge the target; mutation (hot reload)
  is in-memory and non-persistent.
- **Secrecy of runtime data.** Exception values, locals, arguments, and log output can be sensitive and
  stay local, surfaced only to the asking agent session.
- **The developer's ability to rebuild.** Loaded assemblies must not lock the user's own build outputs.

## Capabilities and their gates

### Attach (`rose_debug_attach`)
Gated by `LocalAttachPolicy` before any ICorDebug call: local pid, currently running, not a system
process (pid below the first user process), and same-user (owning SID compared to the current user's;
when ownership cannot be read the decision defers to the OS). `DebugActiveProcess` is the kernel
backstop -- it fails unless the caller's token may debug the target.

### Launch (`rose_debug_launch`)
Starts a local executable under the debugger, as the current user, exactly as if the user had launched
it. A missing file is refused; a non-.NET target fails cleanly when its runtime never signals startup.
Detaching leaves it running; the host that owns it dies with the broker, so no orphan outlives the
session.

### UWP activation, from birth (`rose_debug_launch_uwp`, #4/#5)
Puts the package into debug mode and activates it. From-birth capture registers a resume stub as the
package's debugger so the app is created suspended and attached before its first instruction. Security
properties: debug mode is set only on a package the user can activate; the resume stub is this same
host executable, launched by the OS, coordinating over a **per-session named pipe** the stub opens as
the same user; and debug mode is always lifted on detach or teardown (below), so the package is never
left debuggable.

### Injection (XAML provider, #2/#3)
The native provider is injected into the target -- for a packaged app, into its AppContainer -- via
`InitializeXamlDiagnosticsEx` at the well-known local endpoint. The model, as shipped:

- **Least-privilege, scoped staging.** The provider and the tab-separated exchange files live in a
  per-host-process working folder under the user's own temp. It is granted `ALL APPLICATION PACKAGES`
  (S-1-15-2-1) and `ALL RESTRICTED APPLICATION PACKAGES` (S-1-15-2-2) the access the sandboxed provider
  needs -- read/execute to load the DLL and read requests, write for its snapshot and log -- and that
  grant is scoped to that one folder, never a broad location. The folder sits under the user's
  per-user temp, not a world-writable path.
- **Deterministic teardown.** Every exit path (detach, stop, dispose) lifts package debug mode. The
  work folder is per-session and disposable.
- **Same-user, explicitly targeted, local.** The endpoint is the well-known local one, the target is
  named by pid, and there is no remote or cross-user injection offered. The XAML diagnostics API has
  been abused before (CVE-2023-36003); the safe path is the default and the only one exposed.
- **Rebuildability.** The provider is a native DLL built out-of-tree and staged by copy, so it never
  blocks the user rebuilding it -- the same intent as the analyzer loader's shadow-copy.

### Hot reload (`rose_xaml_apply`, #12)
Mutates a running app's live visual tree from a diff of two XAML versions. It changes only in-memory UI
state of the user's own app; nothing is written to the app's files and the change is lost on relaunch.
Only property edits on named elements are applied; structural edits are reported, not performed. This
is the user editing their own running UI, not a way to reach another process.

### Expression evaluation (`rose_debug_evaluate`, #7)
Reads field-access chains off a stopped frame directly from memory. It deliberately runs **none of the
debuggee's own code** -- no property getters, method calls, or `ToString` -- so it cannot execute
attacker-influenced code paths, re-enter the debugger, hang, or corrupt the target. Method-call
func-eval is a documented non-goal.

### What the events expose
Debug events carry runtime data -- exception types and stacks, `Debugger.Log` output, and at a stop the
top frame's locals and arguments -- which can include secrets. It stays local: captured in the
per-target host, read by the broker over stdio, surfaced only to the agent session that asked, and sent
to no external service. A deployer who treats agent transcripts as sensitive should treat debug output
the same way.

## Threats considered

- **Reaching another user's or a system process.** Mitigated by the same-user/non-system gate and the
  OS debug ACL; both must pass.
- **Leaving a package debuggable after a crash.** Mitigated by lifting debug mode on every teardown
  path; the host also dies with the broker, and a package left in debug mode by an abnormal exit is
  re-disabled on the next session over it.
- **Planting a malicious DLL in the staging folder.** The folder is under the user's per-user temp and
  owned by the user; the app-package grant is scoped to that folder and to the access the provider
  needs. A same-user attacker already has the user's authority (outside this trust boundary); a
  cross-user or lower-privilege one cannot write there.
- **Injection as an escalation vector.** Injection is same-user, local, well-known-endpoint, and
  explicitly pid-targeted; there is no remote or cross-user path, which is the class of abuse the
  historical CVE turned on.
- **Executing attacker-influenced code during inspection.** Evaluation reads memory only; func-eval is
  not offered, so inspecting a hostile object graph cannot run its code.
- **Exfiltrating runtime secrets.** All debug data stays on the machine and reaches only the asking
  agent session; RoseMCP originates no outbound traffic for it.
- **Wedging the target.** Stopping breakpoints carry an auto-continue safety timeout so an unattended
  stop cannot freeze the app indefinitely; detach always leaves the target running.

## Deployer guidance

- The debug/XAML surface is the user's own authority through the agent; treat the agent session and its
  transcript as you would a shell the user opened.
- To remove the surface entirely, omit its tool registration -- the gate is uniform, not per-tool.
- Treat captured debug output (locals, exceptions, logs) as potentially sensitive.

## Deliberate non-goals (for now)

- No authentication beyond the same-user boundary; RoseMCP trusts its own local session.
- No method-call/property expression evaluation (func-eval); field-access reads only.
- No audit log of debug actions beyond the existing activity log and file logs.
- No per-tool capability configuration; the gate is uniform.
