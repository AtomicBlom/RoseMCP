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
- **A pin is the default for its directory and everything under it, and the nearest one governs.**
  Walking up from a path stops at the first directory holding a solution, so a repository that pins
  its everything-solution at the root and keeps smaller solutions in subfolders would have its
  default read for nothing under those subfolders: a file both compile would be answered by the
  nearer, smaller one. The nearest `rosemcp.json` naming a solution, at or above the path, decides it on the same
  terms as the tie-break above -- the pinned solution is chosen where it compiles the path, and the
  nearer one stands where it does not, because a solution without the file is no answer about it. A
  subfolder that pins its own solution is nearer, and governs everything under it.
- **One ordering decides which workspace a call means, in `WorkspaceManager.WorkspaceFor`.** The
  workspace argument or the workspace key, then paths the call carries, then the calling session's
  directory, then refuse. It was previously spread across three places that disagreed, and the worst
  of them was the last resort: with no other signal the broker answered from the single loaded
  worker, which is a fact about what another session did earlier rather than about the question, so
  a call in one repository could be answered plausibly and silently from another. Loaded workspaces
  are named in the failure, consulted only to look up a key the caller sent, and never used as an
  answer. Tools declare their inputs as `WorkspaceHints` and no longer spell the ranking out
  themselves -- seventeen hand-written `workspace ?? filePath` chains had already drifted to one
  `workspace ?? filePaths.FirstOrDefault()`, and one of those hints is not even a path
  (`rose_diagnostics`' `target` is a project name under project scope), so a hint naming nothing on
  disk where the caller is standing is passed over rather than followed somewhere arbitrary.
- **A workspace is named by path or by key, never both, and a key never falls through.** Every tool
  taking `workspace` takes `workspaceKey` but `rose_workspace_open`, whose job is to start a load and
  which a key could only name once nothing was left to start. The two are alternatives, so a call
  sending both is refused rather than having one win: a path and a key that disagree are a mistake
  nothing can settle. A key is as strict as a path and more limited, since it can only be looked up
  among the loaded workers -- a hash cannot be turned back into a path -- so one no loaded worker
  carries, as after a broker restart, is refused naming the keys that are loaded and saying to pass
  `workspace`; passing it over for the paths would answer from a workspace the caller did not name.
  Four bytes of hash can collide, so two loaded solutions sharing a key are refused naming both
  rather than settled by whichever the dictionary yields first. A key sent as `workspace` is refused
  too, saying to send it as `workspaceKey`: read as a path it names nothing under the session's
  directory, and resolution walks up from it to the session's own solution, which answers for a
  workspace the key did not name. Only a `workspace` naming nothing on disk whose last segment has
  the key's shape is refused, so a real path is honoured whatever it is called.
- **A path the call will create routes by its nearest existing ancestor, and only such a path.**
  `rose_add_file`'s `filePath` names nothing on disk by definition and is the one argument saying
  where the call belongs, so passing it over like any other hint sends every new file in another
  checkout to the session's own workspace -- whose worker then truthfully says the path is in none of
  its projects, about a file that sits inside a project of a solution open beside it. The directory
  the file will be placed under is a fact about the call, so `WorkspaceHints.Creating` is routed by
  it, in the paths stage after the ordinary hints. Widening that to every hint would follow
  `rose_diagnostics`' "Db.App" up to the session's directory and call it evidence, so the ancestor
  walk is reserved for a tool that creates its path. A failure answered by a solution that does not
  compile the call's path says which solution does, or which of several sharing its directory do,
  and to pass `workspace`: the worker can only describe its own solution, and only the broker chose
  it. It names only solutions that compile the path, and says nothing where none does, because
  advice naming one that does not sends the caller to the same refusal from the other side.
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
  another checkout -- inferring a path was the failure and accepting one never was. A workspace key
  changes which worker answers and not where a relative path is measured from. Results carry
  absolute paths, so a relative path a caller sends is one it wrote from where it stands, and the
  session's directory is what it means; measuring it from the key's workspace instead would be right
  only for a path the result had made relative to that workspace, and no result does.
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
  worktrees of one repository is the ordinary case and each holds a solution of the same name. The
  key is also accepted back as `workspaceKey`, because a fact on every result that no argument takes
  is one a caller can read and never use: it is the anchor an agent will actually echo, where the
  absolute path is what it drops. A failure that names the loaded workspaces gives each one's key.
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
