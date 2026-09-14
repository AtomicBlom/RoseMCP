# Solution resolution and routing

Read before changing how a call picks its workspace: `SolutionResolver`, `WorkspaceManager.WorkspaceFor`, `BuildProperties`, or `rosemcp.json`.

- **A directory with two solutions is never resolved by guessing.** `SolutionResolver` used to sort
  by name and take the first, which in `D:\Drawboard\Revit` is a one-project installer sitting beside
  the seventeen-project solution everyone means -- so every bare call answered from the wrong
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
  ambiguity error recommends: `D:\Drawboard\Windows\Windows.IntegrationFramework` holds three
  solutions at its root and the largest omits 23 projects under `Pdf/` and 5 under `Shared/`, so
  taking that advice pointed every question about those 28 projects at a compilation not containing
  the file. A pin is an ambient default for the directory; containment is evidence about the path in
  hand, and evidence wins. The pin still decides a bare directory, and still decides between several
  solutions that all compile the path.
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
  disk is passed over rather than resolved relative to the process directory.
- **Every result names the workspace that answered.** Attribution is added once, in
  `WorkspaceManager`, so a tool added later cannot forget. The key is derived from the path rather
  than minted per process -- workers are replaced routinely, and a key that died with one would tell
  a caller its workspace was gone when nothing had changed. Spelled *and* hashed, because six
  worktrees of one repository is the ordinary case and each holds a solution of the same name.
- **A solution is loaded under properties it declares.** MSBuild's `Debug|AnyCPU` default is not
  universal. Where `TargetFramework` is derived from the configuration name -- Drawboard's Revit
  add-in derives it from `Debug-2024` through `Debug-2027` -- the wrong configuration yields projects
  with no framework and no references, and thousands of diagnostics about `System.Object` being
  undefined. `BuildProperties` chooses: the caller wins, else the solution's declared list, else
  MSBuild's default untouched, and a `rosemcp.json` (or `A.slnx.rosemcp.json`) beside the solution
  pins it durably so no setup call is needed -- beside it and never up the tree, because two
  solutions in one directory routinely declare different configurations. Restore gets the same properties, because a repository that moves
  `BaseIntermediateOutputPath` per configuration moves its assets file with it.
