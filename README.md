# RoseMCP

An MCP server that gives coding agents real Roslyn semantics over a loaded C# solution:
diagnostics, navigation, source-generated code, and refactorings.

Agents are good at C# right up until the question needs a compiler. Then they fall back to grep,
which matches comments and strings, misses overrides and interface implementations, and cannot see
source-generated code at all. RoseMCP puts a live, warm Roslyn compilation behind a handful of MCP
tools so the agent can ask the compiler instead of guessing.

## Why another one

Three failures are common to the Roslyn MCP servers I tried, and each one is a design decision
here rather than a bug fix.

**Stale answers.** The agent edits files with its own tools, the workspace never learns, and the
next diagnostic comes from a snapshot several edits old. In RoseMCP every read is ordered behind
every pending mutation *and* behind a disk-reconciliation barrier, so a result can never describe
a world older than the files on disk. There is no refresh step to forget.

**Source generators silently producing nothing.** `Microsoft.CodeAnalysis.Workspaces.MSBuild` runs
a design-time build and reads `CscCommandLineArgs`, so analyzers and generators arrive exactly as
the real compiler sees them — but only if restore has run and any in-solution generator project's
output DLL already exists on disk. Miss that and you get an empty generator list and no error.
RoseMCP checks for it and reports a degraded load with the `dotnet build` that would fix it.

**Reloading the solution on every operation.** One warm worker process per solution. The first
call pays the load; every call after it is fast.

## Install

Grab a release, or build from source (below), then register it:

```
claude mcp add rose -- <path>/RoseMcp.Server.exe
```

That is the whole setup. There is no `workspace_open` to call first — every tool resolves its own
solution from a supplied path or from the working directory.

To make an agent actually reach for it, add this to the consuming repository's `CLAUDE.md`:

```markdown
## C# navigation and refactoring

Use the Roslyn-backed `rose_*` MCP tools rather than grep or find-and-replace for C# in this
repo: `rose_find_references` for usages, `rose_rename_symbol` for renames, `rose_diagnostics`
to check code compiles. Source-generated code is only readable via
`rose_list_generated_documents` / `rose_read_generated_document`.
```

## Tools

| Tool | What it does |
|---|---|
| `rose_diagnostics` | Compiler and (opt-in) analyzer diagnostics, from a warm compilation. No build required. |
| `rose_find_references` | Real references — overrides, interface implementations, aliases and `cref`s included. |
| `rose_symbol_info` | Resolved signature, accessibility, containing type, XML documentation. |
| `rose_search_symbols` | Pattern search across source declarations. |
| `rose_rename_symbol` | Solution-wide rename with conflict detection; returns a unified diff. `apply: false` previews. |
| `rose_move_type_to_file` | Moves one type out of a file that declares several, into a file named after it. Carries the doc comments across and fixes the usings in both halves. |
| `rose_list_generated_documents` | What the generators actually produced, per project. |
| `rose_read_generated_document` | The generated source itself. Nothing on disk to read — this is the only way to see it. |
| `rose_workspace_open` / `_status` / `_reload` / `_close` | Load state, per-project health, degraded-load detection. |

Every result carries a `revision`, so a caller can tell whether two answers describe the same
world.

## How it works

```
client --stdio or http--> RoseMcp.Server (broker, no Roslyn refs)
   or RoseMcp.Tray    -->    |  one child per solution, MCP over stdio
   (hosts it in-process)     +--> RoseMcp.Worker --solution D:\a\A.sln
                             +--> RoseMcp.Worker --solution D:\b\B.slnx
```

The broker owns tool schemas and routing and never references Roslyn, so it stays responsive while
a worker grinds through a design-time build. Each worker owns exactly one `MSBuildWorkspace`.

They are separate processes for a reason: analyzer and generator assemblies cannot be unloaded
once loaded, MSBuild resolution is per-process, and killing a worker is the only reliable way to
reclaim memory or pick up a generator you just rebuilt. Workers exit when their stdin closes, so
they die with the broker and never orphan.

Analyzer assemblies are shadow-copied before loading. A loaded assembly is held open for the life
of the process, and this process lives for hours — load them in place and your own next
`dotnet build` fails with MSB3021.

### XAML projects

WPF, UWP and WinUI code-behind is only half a class: the base type, the `x:Name` fields and
`InitializeComponent` come from a partial the markup compiler generates. That compiler runs only in
a real build — a design-time build reports no XAML items at all — so without help every code-behind
file in the solution reports errors that are not real. Measured on a 50-project UWP app mid-migration
to .NET 10: 2030 of them in one project.

So the markup is parsed and that partial is synthesised: base type, named fields with the
accessibility `x:FieldModifier` asked for, `InitializeComponent`, and the odds and ends real
generated code declares that hand-written code refers to. Element types are resolved out of the
Roslyn type universe, so third-party and in-house controls work like built-in ones; anything that
does not resolve is reported rather than guessed at. Nothing runnable is generated and nothing
reaches disk — the stubs are source-generated documents, readable with
`rose_read_generated_document` like any other.

Which flavour of XAML a project is written in is decided from what it references, since all three
use the same markup namespace: UWP is implemented today, and WPF and WinUI differ only in where
their types live and what the generated half declares.

It closed 2030 errors to 19, all of them a genuinely unreferenced assembly. Against the files a real
build had left in `obj`, all 450 synthesised classes agreed on the base type and on every field name
and type. `workspace_status` reports per project which dialect was chosen and on what evidence, how
many classes were stubbed, and any element type that could not be resolved. Pass `--no-xaml-stubs`
to a worker to see the workspace without them.

### Transports

```
RoseMcp.Server.exe                                   # stdio (default)
RoseMcp.Server.exe --transport http --port 5077      # http + GET /admin/workspaces
RoseMcp.Tray.exe                                     # tray UI, hosts the broker in-process
```

HTTP mode outlives any one client session, so a reconnecting agent reattaches to solutions that
are already warm. It binds `127.0.0.1` by default and refuses a non-loopback bind unless
`ROSEMCP_TOKEN` is set — this server reads and rewrites source anywhere it can reach.

The tray app (Windows only) hosts the same broker in-process and shows one row per workspace:
state, worker PID, and memory sampled from outside the worker so a hung one still reports real
numbers. Under each row is what that worker is doing right now — the tool being served, what it
is aimed at, how long it has been going, and how far through it is — plus the last few operations
to finish. A warm Roslyn host otherwise looks identical whether it is idle or two minutes into a
design-time build. The same rows come back from `GET /admin/workspaces`.

## Building from source

Requires the .NET 10 SDK.

```
dotnet build RoseMcp.slnx
dotnet test
dotnet format --verify-no-changes
```

Run a worker standalone against a fixture — the fastest way to see Roslyn behaviour without the
broker in the way:

```
dotnet run --project src/RoseMcp.Worker -- --solution tests/fixtures/WithGenerator/WithGenerator.sln
```

## Status

Early, and used daily against its own repository. The v1 tool surface above is stable. A code-action
engine (`list_code_actions` / `apply_code_action` / `fix_all` / `format`) is next.
