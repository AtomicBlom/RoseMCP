# Workspace freshness

Read before adding or changing a read path, a reload trigger, or the file watcher.

- **Reads never observe a snapshot older than disk.** If you add a read path, it goes through the
  `WorkspaceSession` barrier. No exceptions.
- **A file that appears is absorbed on the next read, not waited for.** A file created on disk is
  part of the state of disk, and the stat sweep cannot find one -- it checks the documents it already
  knows. So a new `.cs` file used to be invisible until something forced a reload, and every
  reference to the new type reported `CS0103` against perfectly good code: not an error but a
  confident answer about a file that is not there, which is the worst shape of failure here and the
  one that became likely the moment an agent could write C# rather than only read it. The barrier
  walks the project directories, pruned of `bin`, `obj` and dot directories, rather than trusting the
  watcher -- a watcher event lands some milliseconds after the write and an agent asks immediately,
  so trusting it would make the answer depend on a race, which is worse than a slow answer because it
  is intermittent and still confident. Containment in a project's directory is the attribution,
  because that is exactly what the SDK's default globs compile; a project whose own text lists its
  files instead has nothing claimed for it and the file is reported as not being in the build. The
  watcher's list of appearances is still used for one thing: a project or build file appearing, which
  no snapshot can represent and which sends the session round a reload.
- **A fetch is not a checkout, and a write of ours is not somebody else's.** Both are ways of paying
  a full design-time build of every project for nothing, and an idle checkout that nobody was editing
  was paying it twice a minute. `.git/HEAD` is the whole tree-replaced signal, matched as a path:
  matching the file name instead fires on `refs/remotes/origin/HEAD` and `logs/refs/remotes/origin/HEAD`,
  which a background fetch writes without touching a line of source. The index is worse than
  imprecise, it is the wrong file -- a plain `git status` rewrites it to refresh its stat cache, and
  every IDE git integration runs that continuously *in reaction to file writes*, so counting it lets
  an agent editing C# drive its own reloads. Nothing wider is needed, because a project file added,
  removed or edited by any git operation reaches the structural sweep and file contents reach the
  stat sweep. A linked worktree's `.git` is a file naming the real directory, so testing only for a
  directory walks past it and leaves every worktree unable to say whether git is mid-operation.
  <br>
  The other half is our own writes coming back at us. One rewrite raises more than one watcher event,
  so suppression that forgets the path on the first leaks the rest into the bulk-change threshold;
  it is held for a window instead, which costs nothing because the stat sweep is what makes a read
  correct and the watcher only decides how soon it hears. And the tracking table is restamped after a
  mutation writes, or the next barrier re-reads every file the mutation just wrote, advances the
  revision and calls it an external change -- which throws the compilation away and runs every source
  generator again, on a XAML project every stub included, for text the snapshot already holds.
- **Every result carries a `revision`.** It is how callers detect that the world moved.
