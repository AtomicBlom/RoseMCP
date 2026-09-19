# Security model

RoseMCP loads and builds solutions, rewrites source, attaches a debugger to running programs, injects a
provider into them and edits their user interface while they run. This is what it permits, who can
reach each part of it, what is enforced and where, the threats considered, and what a deployer should
do about the ones it does not stop.

Everything here describes what ships. Where a control is the operating system's rather than
RoseMCP's, it says so. `SecurityModelTests` fails when a tool a client is offered has no entry below.

## Trust boundary

RoseMCP runs as the user who started it, and an agent drives it. Every capability is bounded by that
user's own privileges: an agent can do through RoseMCP what the user could already do at a shell.
Debugging a process the user owns, launching a program the user could launch, and rewriting files the
user can write are not escalations; they are the user's authority, exercised by the agent. Treat an
agent session with RoseMCP registered the way you would treat a shell the user left open.

Nothing is remote or cross-user by design: no network attach, no debugging another user's processes, no
elevation. The operating system is the real backstop -- `DebugActiveProcess`, package activation and
object DACLs enforce the user boundary in the kernel -- and RoseMCP's own checks are an early, legible
refusal on top of those, never a replacement for them.

**Opening a solution runs that repository's code.** A workspace load is a design-time build: restore
downloads packages, MSBuild evaluates the repository's own targets and tasks, and the analyzers and
source generators it references run inside the worker. Pointing RoseMCP at a repository is as trusting
as building it, and nothing here makes that safer. Only open repositories you would build.

## Who can reach it

| Surface | Reachable by | Enforced by |
|---|---|---|
| stdio server | The one client that started the process | The process boundary |
| http MCP endpoint (tray, or `RoseMcp.Server --transport http`) | Any process on the machine that can open a loopback socket | Loopback bind, the `Origin` check, and `ROSEMCP_TOKEN` where it is set |
| `/operator/*` (the inspector's API) | Whoever holds the operator token | A bearer token compared in fixed time |
| `GET /admin/workspaces`, `GET /admin/sessions` | Same as the MCP endpoint | Same as the MCP endpoint |
| XAML provider pipe | The host's user, and packaged apps running as that user | The pipe's DACL |
| The Rose panel | Anybody using the debugged app's window | Nothing -- see below |

- **Loopback is not a boundary on a developer machine.** Every local process can reach a loopback port,
  including the app being debugged. An http broker binds `127.0.0.1` by default and refuses a
  non-loopback bind unless `ROSEMCP_TOKEN` is set. When `RoseMcp.Server --transport http` runs with
  `ROSEMCP_TOKEN`, every request -- the MCP endpoint included -- must carry it. Without it, the MCP
  endpoint answers any local process.
- **A browser cannot drive it.** The MCP specification asks a local http server to validate `Origin`,
  against DNS rebinding: a page resolves a name it controls to `127.0.0.1` and sends requests from the
  user's own network position. A request whose `Origin` is present and names anywhere but this machine
  is refused, and so is an `Origin` that does not parse. An absent `Origin` is allowed, because MCP
  clients are not browsers and send none.
- **One client cannot drive another's debug session.** Over http, a live-app session belongs to the MCP
  session that started it, and every `rose_debug_*` and `rose_xaml_*` call on a session it does not own
  is answered as though the session did not exist. `GET /admin/sessions` still lists every session
  (#160).
- **The operator API is owner-agnostic, so it has a secret.** It exists for the person running the
  broker, who needs to see every session, including ones agents started. The tray mints a token per run
  and gives it only to an inspector it launches, or on **Copy inspector command**. An http server takes
  `ROSEMCP_TOKEN` when it is set, and otherwise mints one and writes it to its own log once. A token that
  outlives its process has to be revoked, so none is persisted.
- **A stdio session relays to a running tray** on loopback, sending the directory its client started in
  and nothing else. Setting `ROSEMCP_TOKEN` currently stops that relay without saying so (#213).

## What decides which code runs

The broker starts executables and the live-app host injects a DLL. Each is resolved from an environment
variable first, then from the published layout, then from a build in the repository:

| Variable | Chooses |
|---|---|
| `ROSEMCP_WORKER` | The worker executable started for every solution |
| `ROSEMCP_LIVEAPP_HOST` | The live-app host started for every debug session |
| `ROSEMCP_XAML_PROVIDER` | The native DLL injected into every inspected app |
| `ROSEMCP_INSPECTOR` | The inspector executable the tray starts, and hands its token to |

Whoever controls the broker's environment chooses what runs as the user and what is loaded into the
user's applications. That is the user's authority again, but it means these variables are code
execution settings rather than preferences, and a broker running from source runs whatever was last
built in that checkout.

## Tools

One entry per tool a client is offered. Every tool runs as the user; the column says what else bounds it.

### Workspace

| Tool | What it does | Bounded by |
|---|---|---|
| `rose_workspace_open` | Starts a worker for a solution, which restores and runs a design-time build | Runs the repository's build logic, analyzers and generators -- see the trust boundary |
| `rose_workspace_status` | Reports load state, health and the properties in use | Reads only |
| `rose_workspace_reload` | Restarts the worker, optionally under a different configuration, platform or properties | As `rose_workspace_open`, with the global properties the caller supplies |
| `rose_workspace_close` | Stops a worker | Only affects RoseMCP's own process |

### Reading and navigation

These read the loaded compilation and files the solution holds, and write nothing.

| Tool | What it does | Bounded by |
|---|---|---|
| `rose_diagnostics` | Compiles, and optionally runs analyzers | Analyzers are third-party code running in the worker |
| `rose_find_references` | Finds usages of a symbol | Reads only |
| `rose_find_implementations` | Finds implementations and overrides | Reads only |
| `rose_symbol_info` | Describes a symbol, optionally with its source | Reads only |
| `rose_search_symbols` | Searches declarations by name | Reads only |
| `rose_outline` | Lists what a type or file contains | Reads only |
| `rose_find_split_options` | Reports where a type could be split | Reads only |
| `rose_project_graph` | Reports project references | Reads only |
| `rose_resolve_name` | Finds the namespace a type name lives in | Reads only |
| `rose_list_generated_documents` | Lists source-generated documents | Reads what generators produced in the worker |
| `rose_read_generated_document` | Reads one source-generated document | As above |
| `rose_build_freshness` | Compares build outputs with the source they were built from | Reads file timestamps and outputs |
| `rose_list_code_fixes` | Lists the fixes the solution's analyzers offer | Loads fixer types from analyzer assemblies and runs them to compute fixes |

### Writing C#

These change files on disk. Declarations are addressed by name, so an edit lands in a document of the
loaded solution, and every tool refuses code that does not parse before any file is opened.

| Tool | What it does | Bounded by |
|---|---|---|
| `rose_add_file` | Creates a `.cs` file | Refuses a path that already exists or is not `.cs`; the project is the one whose directory contains the path |
| `rose_add_member` | Adds a member to a type | Writes the file declaring that type |
| `rose_replace_member` | Replaces a member | Writes the file declaring that member |
| `rose_replace_body` | Replaces or edits a member's body | Writes the file declaring that member |
| `rose_delete_member` | Deletes a member | Writes the file declaring that member |
| `rose_replace_doc_comment` | Replaces a declaration's documentation comment | Writes the file declaring it |
| `rose_set_attribute` | Adds or changes an attribute | Writes the file declaring it |
| `rose_add_using` | Adds an import | Writes the named file |
| `rose_change_signature` | Changes a member's parameters with its overrides, implementations and call sites | Writes every document in the solution those touch |
| `rose_rename_symbol` | Renames a symbol and its references | Writes every document in the solution that names it; reports XAML mentions and changes none |
| `rose_move_member` | Moves a member between types, with its call sites | Writes the documents involved |
| `rose_move_type_to_file` | Moves a type to a file of its own | Writes the source file and the new one |
| `rose_apply_code_fix` | Applies an analyzer's fix to a file, project or solution | Runs third-party fixer code, and writes every document in the scope |
| `rose_format` | Applies `.editorconfig` whitespace rules | Writes the files named; `apply: false` returns the diff instead |

A change that reaches files another solution also compiles is reported, not acted on.

### Live-app debugging (Windows)

| Tool | What it does | Bounded by |
|---|---|---|
| `rose_debug_attach` | Attaches a debugger to a running process | `LocalAttachPolicy`: a real pid, running, and owned by the same user, deferring to the OS where ownership cannot be read. `DebugActiveProcess` is the kernel backstop |
| `rose_debug_launch` | Starts an executable under the debugger | Runs as the user, exactly as launching it by hand; it ends with its host |
| `rose_debug_launch_uwp` | Puts a package in debug mode and activates it under the debugger | Only a package the user can activate. Debug mode is lifted when the session ends; a host killed outright cannot lift it, and the package stays debuggable until a later session over it ends |
| `rose_debug_events` | Reads captured events | Returns exception messages, stacks, log output and locals, which can hold secrets |
| `rose_debug_add_tracepoint` | Logs each hit on a method | Conditions compare a value with a literal and run no code |
| `rose_debug_list_tracepoints` | Lists tracepoints | Reads only |
| `rose_debug_remove_tracepoint` | Removes a tracepoint | The caller's own session |
| `rose_debug_set_breakpoint` | Stops the target on a method | Auto-continues after a timeout, 30 seconds by default, so an unattended stop cannot wedge the app |
| `rose_debug_list_breakpoints` | Lists breakpoints | Reads only |
| `rose_debug_remove_breakpoint` | Removes a breakpoint | The caller's own session |
| `rose_debug_continue` | Resumes a stopped target | Releases an operator's hold if there is one, and says so |
| `rose_debug_step` | Steps in, over or out | As a stop |
| `rose_debug_evaluate` | Reads a value at a stop | Memory reads only: no getter, method or `ToString` runs, so inspecting a hostile object graph cannot run its code |
| `rose_debug_list` | Lists debug sessions | Only the caller's own over http |
| `rose_debug_detach` | Ends a session and leaves the target running | Fails, rather than reporting success, when the debugger could not be detached (#84) |

### Live XAML (Windows)

| Tool | What it does | Bounded by |
|---|---|---|
| `rose_xaml_tree` | Reads the visual tree; the first XAML call in a session injects the provider | See injection below |
| `rose_xaml_properties` | Reads an element's properties and where they were set | Returns values the app holds, including text on screen |
| `rose_xaml_selection` | Reads the element a person picked in the app | Returns content from the app's UI -- see the Rose panel |
| `rose_xaml_select_element` | Selects and outlines an element | Draws in the app; changes nothing the app owns |
| `rose_xaml_deselect` | Clears the selection | As above |
| `rose_xaml_apply` | Applies a XAML file's changes to the running app | Reads the named file in the host, as the user, and sends only the edits derived from it. Changes are in memory and end with the app; nothing is written to the app's files |

## Injection

The live-app host injects the native provider with `InitializeXamlDiagnosticsEx`, targeting an app by
process id at its framework's local diagnostics endpoint. There is no remote or cross-user path. The
XAML diagnostics interface has been abused before (CVE-2023-36003), which is the class of problem these
controls exist for.

- **The provider is staged per host.** It is copied into `%TEMP%\RoseMcpXaml\{host pid}` under the
  user's own temp. The folder is deleted and recreated and the DLL copied afresh for every host, so a
  file left behind is never loaded, and folders belonging to hosts that have exited are deleted when
  the next host starts.
- **The AppContainer grant is scoped and conditional.** For a packaged target, that folder -- and only
  that folder -- grants ALL APPLICATION PACKAGES (`S-1-15-2-1`) and ALL RESTRICTED APPLICATION PACKAGES
  (`S-1-15-2-2`) modify access, because the sandboxed provider has no other identity to grant. An
  unpackaged WinUI 3 target gets no grant.
- **The pipe is created before injection and locked down.** Its name carries the host's pid and a GUID,
  it allows one instance, and its DACL grants the user full control and the two AppContainer SIDs read
  and write. An AppContainer access check needs the user's SID as well, so another user's packaged apps
  are refused; a packaged app of the same user would need the GUID to find it.
- **The provider is never ejected.** Unloading it while its overlay is in the app's tree and its reader
  thread is running would crash an application that is not ours, so it stays loaded until the app
  exits. Detaching gives its framework interfaces back and leaves the DLL loaded.

## The Rose panel

The provider draws a toolbar inside the debugged app. Anybody using that window can arm select mode and
pick an element, and `rose_xaml_selection` returns that pick to the agent: its type, name and address,
and the elements under it. That is content from the app's user interface reaching an agent, and it is
as untrusted as anything else the app displays. The panel changes nothing in the app's own tree, and it
stays in the window until the app exits.

## Threats considered

- **Reaching another user's or a system process.** The attach policy refuses a different user's
  process, and the OS's debug ACL refuses independently. Both must pass.
- **One agent driving another's debugger.** Sessions are owned by the MCP session that started them, and
  the operator surface that bypasses ownership requires a token no client is given.
- **A web page driving a local broker.** Refused by the `Origin` check.
- **A local process driving an http broker.** Not stopped on loopback without `ROSEMCP_TOKEN`. Set it, or
  use stdio, where only the client that started the process can reach it.
- **A malicious repository.** Not stopped: loading a solution runs its build, analyzers and generators.
- **Planting a DLL for injection.** The staging folder is under the user's temp, recreated per host, and
  the provider copied into it each time. A same-user attacker already holds the user's authority; a
  different user cannot write there.
- **Squatting or joining the provider pipe.** The host creates the pipe before injecting, as its only
  instance, under a name with a GUID, with a DACL limited to the user and that user's packaged apps.
- **Replacing what runs.** The `ROSEMCP_*` variables choose executables and the injected DLL. Anyone who
  can set the broker's environment already runs as the user.
- **Leaving a package debuggable.** Lifted on every orderly teardown; not after a host is killed outright.
- **Wedging the target.** Stops auto-continue. A person's hold from the inspector is capped at ten minutes
  and is not available to agents, and detaching leaves the target running.
- **Running hostile code during inspection.** Evaluation and conditions read memory only; func-eval is
  not offered.
- **Runtime secrets leaving the machine.** RoseMCP sends nothing anywhere itself. But everything a tool
  returns -- source, locals, exception messages, log output, text from a running app -- goes into the
  agent's context, and from there to wherever the agent sends its context.
- **Untrusted content steering the agent.** Debug events, XAML property values, a person's pick and a
  repository's own source are all text an attacker can influence. RoseMCP returns them as data; whether
  an agent treats data as instructions is the agent's concern.

## Deployer guidance

- Treat an agent session with RoseMCP registered as a shell the user opened, and its transcript as
  holding whatever that shell could print.
- Open only repositories you would build.
- Prefer stdio. If you run an http broker, set `ROSEMCP_TOKEN`, and treat it as a credential.
- To withhold the debugging or XAML tools, deny them in the MCP client's own permission settings.
  RoseMCP has no per-tool switch; the live-app tools are simply not offered off Windows.
- Treat logs under `%LOCALAPPDATA%\BinaryVibrance\RoseMCP\Logs` as sensitive. They name paths and
  targets, and an http server that minted its own operator token wrote it there.

## Deliberate non-goals

- No authentication beyond the same-user boundary, except the token on http.
- No func-eval: no property getters, method calls or `ToString` at a stop.
- No per-tool capability configuration inside RoseMCP.
- No audit log beyond the activity log and the file logs.
- No unloading of an injected provider.
