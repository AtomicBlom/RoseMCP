# RoseMCP

An MCP server that gives coding agents real Roslyn semantics over a loaded C# solution:
diagnostics, navigation, source-generated code, and refactorings.

It exists to fix three things that other Roslyn MCP servers get wrong:

1. **Stale diagnostics.** The agent edits files with its own tools, the workspace never learns,
   and answers come from an old snapshot. Here, every read is ordered behind every pending
   mutation *and* behind a disk-reconciliation barrier.
2. **Source generators silently producing nothing.** `Microsoft.CodeAnalysis.Workspaces.MSBuild`
   runs a design-time build and reads `CscCommandLineArgs`, so analyzers and generators arrive
   exactly as the real compiler sees them -- but only if restore has run and any in-solution
   generator project's output DLL already exists on disk. We check for that and report it loudly
   instead of returning an empty list.
3. **Reloading the solution on every operation.** One warm worker process per solution.

## Architecture

```
client --stdio or http--> RoseMcp.Server (broker, no Roslyn refs)
   or RoseMcp.Tray    -->    |  one child per solution, MCP over stdio
   (hosts it in-process)     +--> RoseMcp.Worker --solution D:\a\A.sln
                             +--> RoseMcp.Worker --solution D:\b\B.slnx
```

- **`RoseMcp.Contracts`** -- DTOs and tool-name constants shared by broker and worker.
- **`RoseMcp.Broker`** -- library. `WorkspaceManager`, worker supervision, the tool layer, and
  `AddRoseMcpBroker()`. One registration path, used by both hosts below.
- **`RoseMcp.Server`** -- console host. `--transport stdio` (default) or `--transport http`.
- **`RoseMcp.Worker`** -- owns exactly one `MSBuildWorkspace`. All Roslyn work happens here.
- **`RoseMcp.Tray`** -- WinUI 3 tray app for http mode. Hosts the broker in-process, so its
  window reads the live `WorkspaceManager` directly rather than through an API.

The worker is a separate process because analyzer and generator assemblies cannot be unloaded
once loaded, MSBuild resolution is per-process, and killing a worker is the only reliable way to
reclaim memory or pick up a rebuilt generator.

### Invariants worth protecting

- **Nothing writes to stdout in stdio mode** except protocol frames. All logging goes to stderr.
  A stray `Console.WriteLine` corrupts the stream, and the failure looks like a protocol bug.
- **Reads never observe a snapshot older than disk.** If you add a read path, it goes through the
  `WorkspaceSession` barrier. No exceptions.
- **Every result carries a `revision`.** It is how callers detect that the world moved.
- **Analyzer assemblies are never loaded from where they live.** They are shadow-copied first. A
  loaded assembly is held open for the life of the process, and this process lives for hours, so
  loading them in place means the user cannot rebuild their own generator -- `dotnet build` fails
  with MSB3021. There is a regression test for this; do not "simplify" it away.
- **Workers die with the broker.** A worker exits when its stdin closes. Orphaned Roslyn hosts
  holding a solution in memory are invisible until the machine is out of RAM.

## Commands

```
dotnet build RoseMcp.slnx
dotnet test
dotnet format                      # run before every commit
dotnet format --verify-no-changes  # what CI checks
```

Run it:

```
dotnet run --project src/RoseMcp.Server                                  # stdio (default)
dotnet run --project src/RoseMcp.Server -- --transport http --port 5077  # http + /admin/workspaces
dotnet run --project src/RoseMcp.Tray                                    # tray UI, hosts the broker
```

The broker finds the worker binary next to itself, then via `ROSEMCP_WORKER`, then in the sibling
project output, so running from source works without publishing first. Non-loopback http binds are
refused unless `ROSEMCP_TOKEN` is set.

Deploy over the running instance, or build release zips:

```
./tools/deploy.ps1                          # test, stop tray, publish, restart
./tools/deploy.ps1 -Mode package            # artifacts/rosemcp-win-{x64,arm64}.zip
```

`promote` installs to `-Destination`, else `ROSEMCP_DEPLOY_ROOT`, else `%LOCALAPPDATA%/RoseMcp`.
Where a machine keeps its install is that machine's business, so no path is committed here.

`dotnet test` needs the `global.json` opt-in already in the repo: xunit.v3 runs on
Microsoft.Testing.Platform, and the .NET 10 SDK no longer bridges that through VSTest.
Individual test projects are also executables, so running one directly works too.

Run a worker standalone against a fixture -- the fastest way to debug Roslyn behaviour without
the broker in the way:

```
dotnet run --project src/RoseMcp.Worker -- --solution tests/fixtures/WithGenerator/WithGenerator.sln
```

## Getting it actually used

An agent's default reflex for C# is grep plus manual editing, and a semantic tool only wins if
reaching for it is cheaper than that reflex. Three things carry that, in descending order of effect:

1. **Server instructions** (`ServiceCollectionExtensions.Instructions`). Sent during `initialize`,
   so the model reads them before choosing an approach -- unlike tool descriptions, which are only
   read once a tool is already under consideration. Keep them short and framed around what goes
   wrong with the default approach, not around what the tools do.
2. **No setup call.** Every tool finds its own solution, from a supplied path or from the working
   directory. A tool that needs `workspace_open` first loses to grep before it is ever tried.
3. **Registration in the consuming repo.** Add to that project's `CLAUDE.md`:

   ```markdown
   ## C# navigation and refactoring

   Use the Roslyn-backed `rose_*` MCP tools rather than grep or find-and-replace for C# in
   this repo: `rose_find_references` for usages, `rose_rename_symbol` for renames,
   `rose_diagnostics` to check code compiles. Source-generated code is only readable via
   `rose_list_generated_documents` / `rose_read_generated_document`.
   ```

Register the server with:

```
claude mcp add rose -- <path>/RoseMcp.Server.exe
```

## Conventions

Enforced by `.editorconfig` where the analyzer can express them, by review where it cannot.

- **Tabs**, not spaces.
- **File-scoped namespaces**, matching the folder they live in (IDE0130). A directory rename is
  otherwise invisible to the compiler.
- **Braces on their own line** -- Allman, everywhere.
- **Conditionals get braces**, with one exception: a simple control-flow body kept on the same
  line may go unbraced.

  ```csharp
  if (document is null) return null;      // fine -- return/continue/break/throw
  if (!TryResolve(path, out var project))
  {
      return WorkspaceResult.NotFound(path);   // anything else gets braces
  }
  ```

  `.editorconfig` can only express `csharp_prefer_braces = when_multiline`, which is close but
  not exact. The rule above is the intent.
- **Readable `if` statements.** Prefer an early-return guard over nesting; hoist a compound
  condition into a named local `bool` rather than packing three clauses into the `if`.

  ```csharp
  var isStructuralChange = projectFileChanged || solutionFileChanged;
  if (isStructuralChange)
  ```
- `nullable enable`, warnings as errors, latest language version.

Commit at every milestone boundary and whenever a self-contained piece works. Run `dotnet format`
first so formatting never shows up as diff noise.
