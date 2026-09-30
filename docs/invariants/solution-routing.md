# Solution resolution and routing

Read before changing how a call picks its workspace: `SolutionResolver`, `WorkspaceManager.WorkspaceFor`, `BuildProperties`, or `rosemcp.json`.

- **A directory with two solutions is never resolved by guessing.** `SolutionResolver` used to sort
  by name and take the first, which in one real repository root is a one-project installer sitting
  beside the seventeen-project solution everyone means -- so every bare call answered from the wrong
  compilation and returned nothing, shaped exactly like a true negative. Containment decides it: the
  solution that compiles the path you named is the one that can answer about it, and reading a
  project list is a parse, not a build. A repository root encloses no project, so it reaches the tie
  with nothing to go on -- that is an error naming the candidates, and a `"solution"` entry in the
  directory's `rosemcp.json` settles it durably. Refusing is against the grain of everything else
  here, and earns it because guessing is not cheap: the wrong guess pays a full design-time build of
  a solution nobody asked for.
- **Containment narrows, then a pin breaks the tie -- in that order.** `Disambiguate` used to check
  the pin first, contradicting its own docstring and the name of the test covering it, which only
  ever resolved a bare directory and so passed either way. It matters because the pin is the fix the
  ambiguity error recommends: one real repository holds three solutions at its root, and the largest
  omits 28 projects in two subfolders, so taking that advice pointed every question about those 28
  projects at a compilation not containing the file. A pin is an ambient default for the directory;
  containment is evidence about the path in hand, and evidence wins. The pin still decides a bare
  directory, and still decides between several solutions that all compile the path.
- **One ordering decides which workspace a call means, in `WorkspaceManager.WorkspaceFor`.** The
  workspace argument, then paths the call carries, then the calling session's directory, then refuse.
  It was previously spread across three places that disagreed, and the worst of them was the last
  resort: with no other signal the broker answered from the single loaded worker, which is a fact
  about what another session did earlier rather than about the question, so a call in one repository
  could be answered plausibly and silently from another. Loaded workspaces are named in the failure
  and never used as an answer. Tools declare their inputs as `WorkspaceHints` and no longer spell the
  ranking out themselves -- seventeen hand-written `workspace ?? filePath` chains had already drifted
  to one `workspace ?? filePaths.FirstOrDefault()`, and one of those hints is not even a path
  (`rose_diagnostics`' `target` is a project name under project scope), so a hint naming nothing on
  disk where the caller is standing is passed over rather than followed somewhere arbitrary.
- **A relative path is measured from the calling session's directory, and from nowhere else.** The
  broker's own directory is no answer: in http mode it is the tray's install directory, and in stdio
  mode it is whichever checkout the process was started in. Six worktrees of one repository is the
  ordinary case here and every one of them holds the same relative paths, so `tests/Foo.cs` measured
  from the wrong checkout names a real file, resolves there by containment, and wins the ranking
  outright -- and the edit that follows applies, verifies and reports success into a repository whose
  own sessions are free to commit it, with the caller's `git status` clean throughout. It is the one
  failure on this surface that the working copy the caller can see does not show. `RootedPath` is
  what stops it recurring: there is no way to make one without naming the directory a relative path
  is measured from, and `WorkspaceHints` carries nothing else, so the resolution cannot ask the file
  system about a relative path. An absolute path is honoured wherever it points, including into
  another checkout -- inferring a path was the failure and accepting one never was.
- **The hop to a worker or a live-app host is absolute-only, and they refuse a relative path.** A
  worker resolves one against its own working directory, which is its solution's root: the right
  answer for the call it was given and the wrong one for a call it should never have received, since
  the file exists under that root too. Refusing turns a mis-route into a sentence naming the
  argument, instead of a write to a plausible file -- and it stops a defensive setting being
  load-bearing, since the correct working directory is otherwise the only reason the wrong route
  finds anything at all. `PathArguments` is the single list of which arguments this covers, read by
  the end that makes them absolute and by the ends that require them, so the two cannot drift. An
  argument with a base of its own stays off it: `rose_move_type_to_file`'s `targetPath` is measured
  from the file being split, which the worker knows and the broker does not.
- **Every result names the workspace that answered.** Attribution is added once, in
  `WorkspaceManager`, so a tool added later cannot forget. The key is derived from the path rather
  than minted per process -- workers are replaced routinely, and a key that died with one would tell
  a caller its workspace was gone when nothing had changed. Spelled *and* hashed, because six
  worktrees of one repository is the ordinary case and each holds a solution of the same name.
- **A solution is loaded under properties it declares.** MSBuild's `Debug|AnyCPU` default is not
  universal. Where `TargetFramework` is derived from the configuration name -- a Revit add-in built
  against four host versions derives it from `Debug-2024` through `Debug-2027` -- the wrong
  configuration yields projects with no framework and no references, and thousands of diagnostics
  about `System.Object` being undefined. `BuildProperties` chooses: the caller wins, else the
  solution's declared list, else MSBuild's default untouched, and a `rosemcp.json` (or
  `A.slnx.rosemcp.json`) beside the solution pins it durably so no setup call is needed -- beside it
  and never up the tree, because two solutions in one directory routinely declare different
  configurations. Restore gets the same properties, because a repository that moves
  `BaseIntermediateOutputPath` per configuration moves its assets file with it.
