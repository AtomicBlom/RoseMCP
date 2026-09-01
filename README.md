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

Fixes from the IDE catalogue — the `IDE####` rules, and refactorings like extract method — are *not*
here: they live in `Microsoft.CodeAnalysis.CSharp.Features`, which this does not reference. Measured
across both solutions above: zero `IDE*` ids among the fixers on disk.

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
