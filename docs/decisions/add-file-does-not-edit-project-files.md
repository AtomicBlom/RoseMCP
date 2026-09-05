# rose_add_file reports a project that lists its files; it does not edit one

**Decision.** `rose_add_file` writes a C# file and adds it to the Roslyn solution. Where the
owning project lists the files it compiles rather than globbing them, the file is not in the
build, and the tool says so in the result. It does not add a `Compile` item to the project file.

**Why the case exists.** `ProjectItemStyle.GlobsSourceFiles` reads the project's own text to
decide whether a file beside its siblings is in the build or merely near it. An SDK-style project
globs its directory, so a new file is compiled the moment it exists. A project that disables the
default items compiles nothing it has not named, and a file written into it is invisible to the
compiler. The read barrier already reports this for a file that appears on disk
(`WorkspaceSession.cs:224-229`); a tool that creates one should say it at the moment of creation,
where the caller is standing, rather than leaving it to the next read.

**Why not edit the project file.**

1. **It is not C#, and the guarantee that makes these tools worth using does not extend to it.**
   Every write tool parses the code and resolves the declaration before the file is opened, so a
   refusal costs nothing and nothing is ever left half-written. A project file is XML with an
   MSBuild evaluation behind it: conditional item groups, imported targets, globs excluded and
   re-included, `Compile Remove` followed by `Compile Include`. Deciding where an item belongs is
   an evaluation, and `ProjectItemStyle` is explicitly a parse and not a build, for the good
   reason that it runs on the read barrier.

2. **The failure modes are not comparable.** A file the build ignores is reported, twice, and
   fixed by one line in an editor. A project file written wrongly can break every project that
   imports it, and it breaks the build for everyone rather than for the caller.

3. **The case is rare and shrinking.** `GlobsSourceFiles` gives the benefit of the doubt to
   globbing precisely because that is what the overwhelming majority of projects do.

**What changes the answer.** Enough traffic on old-style UWP and WPF repositories to make the
manual step a real cost, plus a way to place an item that is a parse rather than an evaluation --
most likely "append to the last `ItemGroup` that already holds `Compile` items, or refuse".
