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

The machine needs the .NET **SDK**, not only the runtime. The worker runs a design-time build
through `Microsoft.CodeAnalysis.Workspaces.MSBuild`, which locates MSBuild and the targets out of
an SDK installation — so with the runtime alone every project loads with no references and
reports thousands of errors about `System.Object` being undefined. True on Windows too; it is
only surprising on a server, where installing the runtime is the usual thing to do.

Windows gets the tray and the `rose_debug_*` live-app surface as well. Linux gets the stdio
broker and worker: the tray is WinUI and the debugger is ICorDebug, so neither has a Linux build
to ship, and the tools that would have nothing behind them are not advertised there.

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
| `rose_diagnostics` | Compiler and (opt-in) analyzer diagnostics, from a warm compilation, in milliseconds. The edit loop, not a substitute for a build. |
| `rose_find_references` | Real references — overrides, interface implementations, aliases and `cref`s included. |
| `rose_symbol_info` | Resolved signature, accessibility, containing type, XML documentation, declaration sites, and what the member overrides or implements. |
| `rose_find_implementations` | What implements, overrides, or derives from a symbol. Grep cannot answer this at all. |
| `rose_search_symbols` | Pattern search across source declarations. |
| `rose_rename_symbol` | Solution-wide rename with conflict detection; returns a unified diff, and reports XAML that still names the old identifier. `apply: false` previews. |
| `rose_move_type_to_file` | Moves one type out of a file that declares several, into a file named after it. Carries the doc comments across and fixes the usings in both halves. |
| `rose_format` | Applies the repository's own `.editorconfig`: indentation, braces, line endings, trailing whitespace, final newline. |
| `rose_list_code_fixes` | What the solution's own analyzers offer to fix in a file. |
| `rose_apply_code_fix` | Applies one, to a file, a project, or the whole solution, through Roslyn's fix-all. |
| `rose_list_generated_documents` | What the generators actually produced, per project. |
| `rose_read_generated_document` | The generated source itself. Nothing on disk to read — this is the only way to see it. |
| `rose_workspace_open` / `_status` / `_reload` / `_close` | Load state, per-project health, degraded-load detection, and the MSBuild properties in use. `_reload` can change them. |

Every result carries a `revision`, so a caller can tell whether two answers describe the same
world.

## Fixing what the analyzers report

`rose_list_code_fixes` says what can be fixed in a file; `rose_apply_code_fix` applies it to that
file, its project, or the whole solution through Roslyn's own fix-all.

No dependency was added for this. Analyzers ship their fixers in the same assemblies, so a solution
that reports a diagnostic already carries the code that repairs it — measured at **206 fix providers
over 186 diagnostic ids** for this repository and **143 over 120** for a Revit add-in, entirely from
packages those solutions already referenced. Roslyn exposes the analyzers through `AnalyzerReference`
but not the fixers, so those are found by reflecting over the same assembly — loaded through the
shadow-copy loader, never from `AnalyzerReference.FullPath`, which is deliberately still the original
file.

Only the analyzers that report the requested id are run. A full analyzer pass over a large project is
seconds to minutes; fixing one rule is a fraction of that, for the same answer.

The IDE catalogue is included, and needed no package either. The SDK ships
`Microsoft.CodeAnalysis.CSharp.CodeStyle.Fixes.dll` beside its code-style analyzers and passes both
to the build, so a project that sets `EnforceCodeStyleInBuild` already has all 140 of those fixers on
disk. Reaching them takes one thing: when `GetTypes` throws because a single type in an assembly will
not load, salvage the ones that did rather than discarding the assembly. That turned **186 fixable
ids into 433** for this repository — 112 `IDE*`, and 119 compiler `CS*` fixes, which are the "add the
missing using" and "remove the unreachable code" an editor offers on a red squiggle.

A project that does not turn code style on in the build has the `CA` rules and no `IDE` ones, because
the fixers follow the analyzers. That is visible rather than silent: `rose_list_code_fixes` reports
the ids it found no fix for.

## Formatting what you wrote

`rose_format` applies the repository's own `.editorconfig` to files you name: indentation, brace
placement, line endings, trailing whitespace and the final newline. Call it after writing C# by any
other means.

It exists because writing C# and getting its whitespace right are separate skills, and a caller good
at the first routinely fails the second — spaces where the repository wants tabs, LF where it wants
CRLF. Where IDE0055 is escalated to an error, each of those is a failed build rather than a tidiness
question, and the correct answer is not a judgement call: it is written down in a file the compiler
already reads.

Two passes, because one is not enough. Roslyn's formatter handles syntax and does honour
`.editorconfig` — measured — but it only rewrites the trivia it has reason to touch, so a
four-space, LF-terminated file comes out with tabs and CRLF on the lines it reindented and the
original endings everywhere else. The second pass fixes every line. Multi-line verbatim and raw
string literals are left exactly as they are, since a newline in one is part of the value and a raw
literal's indentation decides how much is stripped from it.

`apply=false` returns the diff without writing, which makes it a formatting check for a named set of
files.

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
use the same markup namespace. UWP, WinUI and WPF are all recognised, and the differences between
them are more than cosmetic: WPF's named fields default to `internal` where the Windows frameworks'
default to `private`, and WPF types a field for a named root element as the class it generates where
UWP types it as the element the markup writes. Both were read off real generated files rather than
reasoned about.

How much a project needs is a separate question from which dialect it is. WinUI's markup compiler
takes part in design-time builds, and WPF's runs its first pass in one, so a WPF project needs a stub
only for the files that pass cannot do: pass 1 resolves element types out of *assemblies on disk*,
and a design-time build compiles nothing, so any file whose markup names a type from a project
reference fails and gets no generated half. In a real WPF project that was 1 file of 26. Recognising
the dialect still matters for the other 25: without it the project is reported as having no XAML
framework, which is false and more misleading than silence.

Verified on net48 and net10 alike, which is worth doing rather than assuming — an SDK-style .NET
Framework project defaults to C# 7.3, where the `#nullable disable` a stub carries is itself three
errors. The language version is asked, not inferred from the framework.

Renaming knows about markup too, as far as anything can. `rose_rename_symbol` reports every place
XAML still names the old identifier — bindings, `x:Name`, event handlers, element names, `x:Class` —
and changes none of them. A binding path resolves at runtime against a DataContext only the running
application knows, so nothing can prove the mention refers to the symbol renamed; but a rename that
breaks forty bindings and says nothing is the worst outcome available, because the compiler will not
catch it either.

It closed 2030 errors to 19, all of them a genuinely unreferenced assembly. Against the files a real
UWP build had left in `obj`, all 450 synthesised classes agreed on the base type and on every field
name and type. `workspace_status` reports per project which dialect was chosen and on what evidence, how
many classes were stubbed, and any element type that could not be resolved. Pass `--no-xaml-stubs`
to a worker to see the workspace without them.

### Configurations and platforms

A design-time build is a build, so it obeys MSBuild properties. Most repositories declare
`Debug|AnyCPU` and never think about it, but a solution is free to declare neither — an add-in built
against four versions of a host API can name its configurations `Debug-2024` through `Debug-2027`,
`x64` only, and derive `TargetFramework` from the configuration name. Loading such a solution as
`Debug|AnyCPU` produces projects with no target framework and no references at all, and the
thousands of diagnostics that follow name everything except the cause.

So the configuration is chosen rather than assumed. What you ask for wins; otherwise the solution's
own declared list decides, and MSBuild's default is left alone unless the solution demonstrably does
not offer it. The choice, the alternatives and the reason are all reported by `workspace_status`.

```
RoseMcp.Worker.exe --solution A.slnx --configuration Debug-2027 --platform x64
RoseMcp.Worker.exe --solution A.slnx --configuration Release --property RevitVersion=2027
```

A solution whose answer is always the same can commit it, in a `rosemcp.json` beside the solution or
an `A.slnx.rosemcp.json` naming that one solution. That is the durable form of the same mechanism,
and the reason to prefer it is the rule that no tool needs a setup call first: an agent that has to
reload before its first useful answer has already lost to grep.

```json
{
  "configuration": "Debug-2027",
  "platform": "x64",
  "properties": { "RevitVersion": "2027" }
}
```

`rose_workspace_reload` takes the same three, for changing them without editing a file — it restarts
the worker, because MSBuild global properties are fixed when a workspace opens. They are remembered
per solution, so a worker replaced later comes back under the same ones rather than silently
reverting.

Scoped to one solution, and deliberately not found by walking up the tree. Configurations belong to a
solution and not to a repository: one real directory holds a Revit add-in solution declaring
Debug-2024 through Debug-2027 beside an installer solution declaring no build types at all, so a file
above them would be wrong for one of the two. The solution-specific name is what a `.DotSettings`
file next to them already does.

Where nothing is pinned and MSBuild's default is not on offer, the platform chosen is this machine's
own architecture if the solution declares it, and the first declared otherwise -- a solution build
takes the first configuration with the first platform, which is how a solution listing ARM64 first
comes to build ARM64 on an x64 machine.

Arbitrary properties are supported because the derivation is sometimes from neither configuration
nor platform. Restore gets the same properties as the load: a repository that moves
`BaseIntermediateOutputPath` per configuration also moves where restore writes
`project.assets.json`, and the two disagreeing leaves the assets file somewhere the load will not
look.

### Transports

```
RoseMcp.Server.exe                                   # stdio (default)
RoseMcp.Server.exe --transport http --port 5077      # http + GET /admin/workspaces
RoseMcp.Tray.exe                                     # tray UI, hosts the broker in-process
```

HTTP mode outlives any one client session, so a reconnecting agent reattaches to solutions that
are already warm. It binds `127.0.0.1` by default and refuses a non-loopback bind unless
`ROSEMCP_TOKEN` is set — this server reads and rewrites source anywhere it can reach.

Register it as stdio and the two stop being alternatives. With a tray already running, a stdio
server starts no workers of its own: it relays to the tray, which owns them. That is the
arrangement worth having, because each half supplies what the other cannot. A stdio process knows
the directory its client started it in, so a call naming no workspace resolves to the right
solution; an http broker serves every repository on the machine at once and, with two solutions
open, genuinely cannot tell which one a bare call means. Relaying gives both at the same time —
one warm worker per solution shared by every session, and a correct answer per session — and the
tray still sees every workspace, because it is still the one holding them. With no tray running,
the same stdio server starts its own workers and behaves exactly as it did before.

The tray app (Windows only) hosts the same broker in-process and shows one card per workspace:
whether it is loading, loaded, degraded or faulted; the MSBuild configuration it was loaded under,
its project count and how long the load took; the worker's PID and uptime; and memory sampled
from outside the worker so a hung one still reports real numbers. A degraded or faulted workspace
says why, with the fix. Under that is what the worker is doing right now — the tool being served,
what it is aimed at, how long it has been going, and how far through it is — plus the last few
operations to finish, failures called out with their reason. A warm Roslyn host otherwise looks
identical whether it is idle or two minutes into a design-time build. The same data comes back
from `GET /admin/workspaces`. Closing the window hides it; the tray icon brings it back, and Exit
in its menu is what stops the broker and, with it, every worker.

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

### Dogfooding

Work on this repository with this server running against it. Register it globally rather than per
project:

```
claude mcp add rose -s user -- <path>/RoseMcp.Server.exe
```

There is deliberately no `.mcp.json` here. Committing one would either pin every contributor to one
machine's install path or point at a build output that may not exist yet; a user-scope registration
covers this repository along with every other.

This is not ceremony. Two of the sharper bugs in this codebase were found by using it on itself and
not by its tests: a custom `AnalyzerReference` that made `find_references` and `rename` throw on any
member in a solution containing XAML, and a resolution failure whose message the MCP layer replaced
with "An error occurred". Both were invisible to a green suite and obvious within a minute of real
use. After a `tools/deploy.ps1` run the tray restarts, so the first call afterwards reloads the
solution and is slow; that is expected rather than a fault.

## Status

Early, and used daily against its own repository. The v1 tool surface above is stable. A code-action
engine (`list_code_actions` / `apply_code_action` / `fix_all` / `format`) is next.
