# A file's layout comes from what declares it, then from the file

**Decision.** A write works out one layout for each file it touches -- the line ending, the
indentation, and the two whitespace flags -- once, from the file as it was before the write. Every
pass that writes the file takes that one value, the formatter included. Each part comes from the
first of these that says anything:

1. **An .editorconfig covering the file.** That's Roslyn's reading of the files the project was
   given, or the files on disk where it was given none covering this path.
2. **For the line ending only, the repository's .gitattributes:** an `eol` that applies to the file,
   read the way git reads it.
3. **The file's own text:** the ending most of its lines use, and the indentation most of its
   indented lines begin with.
4. **For a file with nothing to read, the files nearest it** in its project. A new file is the usual
   case.
5. **Roslyn's defaults:** four spaces, and the platform's ending.

The code a caller supplied is never a source.

**Why one value, read before the write.** Each tool used to work the layout out for itself. The
answers disagreed, and the disagreement landed on lines nobody asked to change:

- The formatter, told nothing, used its own four spaces and set the whitespace in front of the next
  token too. So in a tab-indented file with no .editorconfig, an edit re-indented the member after
  it.
- One service took the file's dominant ending on one path and .editorconfig's on another.

Read after the write, the answer includes the payload: a forty-line member composed with bare LFs,
written into a ten-line CRLF file, would decide the file is LF.

**Why declared before observed.** A declaration is what every checkout of the repository converges
to, and the file's current text is one machine's state. .editorconfig already came first, and
.gitattributes is the same kind of statement: once git is told how a checkout's lines end, it writes
every file that way. For a file git treats as text, following the declaration never changes what is
committed, because git normalises the endings on the way in. The one visible effect is on the working
tree, which then matches what the next checkout will write. A file whose working copy disagrees with
its repository's declaration can come out of a narrowed write with both endings for a while, which is
what .editorconfig's `end_of_line` has always done too.

**Why .editorconfig outranks .gitattributes.** .editorconfig is what `dotnet format`, and so CI,
holds a file to. When the two disagree, the one the build checks wins.

**Why only `eol`.** `text` or `text=auto` without an `eol` leaves the ending to each machine's
`core.eol` and `core.autocrlf`. That's a setting nobody reviewing a pull request can see, and the
file's own endings already say what this machine did with it. `-text` leaves a file exactly as
committed, which again is what the file shows. The machine's own attribute files are left out for
the same reason.

**Why the attributes are read from the files rather than asked of git.** An edit asks about every
file it writes. Starting a process for each one is a poor price for three attributes, and git need
not be on the worker's path. The worker already reads git's own directory rather than starting it.
The cost is carrying the pattern rules and the order of precedence here. They were checked against
`git check-attr`, including a quirk of `core.ignorecase`: git folds the path but not a bracket
expression's members, so `[AB].cs` names no file on Windows.

**Why the .editorconfig on disk, and when.** Roslyn knows an .editorconfig only when the design-time
build listed it, and that build lists only the files above something it compiles. That leaves out a
project added after the workspace loaded, and a folder that held no source until now. `dotnet format`
reads the disk regardless, so an answer from Roslyn's defaults there is a file written with the
wrong layout and then certified as formatted. Where Roslyn *was* given one covering the file, its
reading stands and the disk isn't asked, because it read the same files.

**Why the file before its neighbours, and neighbours only when the file is silent.** A file's own
lines are the strongest evidence of how it's laid out, and following them keeps an edit's diff to
what the edit wrote. The neighbours are there for a file with no lines to read. Several of them are
read, not one, so a single odd file can't decide for a folder, and the build's own generated output
is left out.

**Why the formatter is told only what Roslyn wasn't.** Where Roslyn read an .editorconfig, it knows
its own reading best, including cases like `indent_size = tab` that a second reading could get
subtly different. The layout fills the gaps and changes nothing Roslyn already had.

**It is said when the answer is a guess.** `dotnet format` has no fallback. Finding no .editorconfig,
it indents with four spaces. So `rose_format` says when a file's indentation came from the file
rather than a declaration and isn't the four spaces `dotnet format` would want, since "already
formatted" alone would be contradicted by CI. `rose_add_file` says when it had nothing but Roslyn's
defaults to go on.

**Rejected:**

- **Asking `git check-attr` per file.** Exact, but a process per file written, and it makes a git
  install a dependency of the worker.
- **Reading `core.autocrlf` and `core.eol`.** They're a machine's settings, and the checkout already
  reflects them.
- **The file's own ending above .gitattributes.** Every file that disagreed with its repository would
  have its mistake kept by every edit. That's the opposite of what `end_of_line` does, for no reason
  the two sources give.
- **Handing the formatter a synthesised .editorconfig.** A document added to the project is a change
  the solution writer would put on disk.
