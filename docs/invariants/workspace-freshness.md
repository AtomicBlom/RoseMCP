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
- **A branch switch is its files changing, and a write of ours is not somebody else's.** Both are ways
  of paying a full design-time build of every project for nothing. Nothing git does is a reason to
  reload on its own: a switch rewrites working-tree files, and the barrier reads those like any other
  change -- tracked documents by their stamps, new source files by walking, and build files by their
  stamps or by looking again at every place one was absent at load -- so a switch that touches only
  source is absorbed, and one that moves a project, an import or the solution reloads for that reason.
  The git directory itself is ignored, HEAD included. A background fetch writes
  `refs/remotes/origin/HEAD`, and a plain `git status` rewrites the index to refresh its stat cache --
  which every IDE git integration runs continuously *in reaction to file writes* -- so treating anything
  in there as a signal lets an agent editing C# drive its own reloads. The one thing waited for is
  `index.lock`, held while git writes the tree, because reconciling then reads half of one branch and
  half of another. `MERGE_HEAD` and `REBASE_HEAD` are not waited for: they describe a conflict being
  resolved rather than a write in progress, and git can leave `REBASE_HEAD` behind after the rebase
  finished. What git wrote during the wait is drained afterwards and read like any other change. A
  linked worktree's `.git` is a file naming the real directory, so testing only for a directory walks
  past it and leaves every worktree unable to say whether git is mid-operation.
  <br>
  The other half is our own writes coming back at us. One rewrite raises more than one watcher event,
  so suppression that forgets the path on the first leaks the rest back as somebody else's edits;
  it is held for a window instead, which costs nothing because the stat sweep is what makes a read
  correct and the watcher only decides how soon it hears. And the tracking table is restamped after a
  mutation writes, or the next barrier re-reads every file the mutation just wrote, advances the
  revision and calls it an external change -- which throws the compilation away and runs every source
  generator again, on a XAML project every stub included, for text the snapshot already holds.
- **A reload is decided by what changed, never by how much.** A reload is a design-time build of every
  project, so it is paid only when a project's evaluation inputs moved: its project file, the solution,
  or a file its evaluation imported. Roslyn's build host reports no import list, so the worker evaluates
  each loaded project itself, under the load's own properties, and tracks every file `Project.Imports`
  names -- which is what catches an edit to a file brought in with `<Import>`, or to a
  `Directory.Build.props` nearer a project than its solution, both of which otherwise leave the
  workspace answering from the evaluation before the edit. Evaluation lists only imports that exist, so
  a build file appearing still reloads by its name -- and that includes one that was there at load, went,
  and came back. An `.editorconfig` caught missing by one sweep, which an editor's delete-and-rename save
  or a checkout rewriting it will do, is removed from every project and then tracked by nothing, so
  unless its return is awaited the workspace carries on without it: new files in spaces and LF, and
  `rose_format` reading the same empty options and calling them formatted. The watcher remembers build files and nothing else:
  every read stats each tracked document and walks the project directories for new source files, so a
  thousand source files changing needs nothing from the event stream, and a list holding every event
  would need a cap that turns the number of events into a reason to reload. A project whose evaluation
  fails has no import list, and for that case alone any untracked `.props` or `.targets` changing
  reloads -- over-reloading is the only answer there that cannot be stale. A watcher that loses events,
  to an overflowed buffer or a vanished directory, loses nothing else, so lost events reload only in that
  same case.
- **Re-reading a file decides what a later write puts back.** The barrier's reader is where a file's
  encoding is settled, so the fallback it passes for a stream with no byte order mark has to be one
  that emits none -- `Encoding.UTF8` emits a preamble and would mark every mark-less file the sweep
  touched. The write side of that is in [writing-csharp.md](writing-csharp.md).
- **Every result carries a `revision`.** It is how callers detect that the world moved.
