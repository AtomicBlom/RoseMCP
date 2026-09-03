namespace RoseMcp.Contracts;

/// <summary>
/// What each tool says about itself, shared by the broker facade and the worker so the two cannot
/// disagree.
/// <para>
/// They did disagree. Both hosts carried their own copy, five of them had drifted, and in every case
/// it was the broker's -- the one an MCP client actually reads -- that said less. A description is
/// the only thing that gets read when a tool is already under consideration, so a stale copy is a
/// tool that loses to grep for a reason nobody can see.
/// </para>
/// <para>
/// Each one says what the tool does, why to prefer it over the obvious alternative, and what it
/// costs or will not do. The second of those is the part that matters: an agent reaching for grep is
/// not choosing badly between known options, it is not aware there was a choice.
/// </para>
/// </summary>
public static class ToolDescriptions
{
	public const string WorkspaceOpen = """
        Loads a solution into a warm Roslyn host and keeps it loaded, so later calls cost nothing to
        set up. Rarely needed on its own: every other tool opens the enclosing solution itself. Call
        it to pay the load deliberately rather than inside the first real question, or to read the
        per-project load state up front -- including any reason the workspace is degraded, most often
        a source generator whose assembly has not been built, which otherwise produces no generated
        code and no error.
        """;

	public const string WorkspaceStatus = """
        Reports what is loaded and whether its answers can be trusted: per-project load state,
        document and source-generated document counts, what restore did, and any reason the workspace
        is degraded. Check this first when answers look wrong, because a degraded workspace returns
        plausible but incomplete results rather than errors. It also reports the MSBuild
        configuration and platform in use and the ones the solution declares -- worth checking when a
        whole solution looks broken, since a configuration the solution does not define resolves no
        references at all and reports thousands of errors about System.Object being undefined instead
        of about the cause.
        """;

	public const string WorkspaceReload = """
        Restarts the worker process for a workspace. Ordinary edits are picked up automatically and
        need no reload; this exists for the two cases that cannot be handled any other way. One is
        rebuilding an analyzer or source generator, since assembly loading is one-way and a process
        that loaded the old build can never see the new one. The other is loading under different
        MSBuild properties -- a configuration or platform is fixed when the workspace opens, so
        changing it is a restart. rose_workspace_status reports which are in use and what else the
        solution declares.
        """;

	public const string WorkspaceClose = """
        Stops the worker for a workspace and releases the memory its solution was holding. A loaded
        solution costs a gigabyte or more, so this is worth doing when moving off one for good.
        """;

	public const string Diagnostics = """
        Compiler (and optionally analyzer) diagnostics from a live Roslyn compilation, in
        milliseconds, always computed against the current state of disk -- edits made by other tools
        are absorbed before the analysis runs, so results are never stale. Diagnostics inside
        source-generated code are included and tagged with the hint name that reads that code back.
        Use it as the edit loop, in place of building after every change; it is not a substitute for
        a build. It compiles but does not emit, and runs no MSBuild targets, so it cannot see
        emit-time errors, anything a build step generates or repacks, or a failure in a project
        reference's own build. Analyzers are opt-in here and can be build errors there. Build before
        concluding the work is done. To repair what it reports, ask rose_list_code_fixes.
        """;

	public const string SymbolInfo = """
        What the symbol at a file position actually is: full signature, kind, accessibility,
        containing type, XML documentation, every declaration site, and what the member overrides or
        implements -- which is usually where an override's documentation actually lives. Resolved
        from the compilation rather than read off the declaration text, and works from a use site as
        well as a declaration. isFromSource being false means it lives in metadata and cannot be
        renamed or edited.
        """;

	public const string FindReferences = """
        Every reference to the symbol at a file position, resolved semantically across the whole
        solution. Unlike a text search this follows overrides, interface implementations and aliases,
        and will not match comments, strings, or unrelated identifiers that happen to share a name.
        For the opposite direction -- what implements or overrides this -- use
        rose_find_implementations.
        """;

	public const string FindImplementations = """
        What implements, overrides, or derives from the symbol at a file position -- derived types for
        a class, implementing types for an interface, overriding members for a virtual or abstract
        one. Grep cannot answer this at all: an implementation need not mention the interface's name
        anywhere near the member. The answer says which of those questions was actually answered,
        since that depends on what the symbol turns out to be.
        """;

	public const string SearchSymbols = """
        Finds declarations across the solution by name pattern. Understands the abbreviations people
        actually type, so SLoader matches SolutionLoader. Use this to locate a type or member before
        asking for its references, its implementations, or renaming it.
        """;

	public const string ListGeneratedDocuments = """
        Lists the documents this solution's source generators produce. These exist only inside the
        compilation and are never written to disk, so no file search or directory listing will ever
        find them. If the list is empty the notices say whether the project has no generators or has
        generators that failed to load -- which is the difference between nothing to see and a
        broken workspace.
        """;

	public const string ReadGeneratedDocument = """
        Returns the full text of one source-generated document, by the hint name from
        rose_list_generated_documents or from a diagnostic's generatedHintName. Use this whenever a
        diagnostic points at a file that does not exist on disk; there is no other way to read it.
        """;

	public const string RenameSymbol = """
        Renames the symbol at a file position everywhere it is used, using Roslyn's renamer, so
        overrides, interface implementations, partial declarations and cref references all move
        together -- none of which find-and-replace gets right. Conflicts, where the new name would
        bind to something else or shadow an existing member, are reported rather than silently
        applied. Also reports XAML that still names the old identifier and does not change it, since
        markup is text to the compiler and a broken binding builds and runs. Returns a unified diff
        of every file changed; pass apply=false to preview.
        """;

	public const string MoveTypeToFile = """
        Moves one top-level type out of a file that declares several, into a file named after it. The
        declaration goes across with its doc comments and attributes, indented and spaced exactly as
        it was, and using directives the split makes unnecessary are dropped from both files -- which
        is what stops the result from failing a build that treats unused usings as errors. Use this
        rather than reading a file and writing two. Returns a unified diff of both files; pass
        apply=false to preview. Declines rather than guessing when the type is the only one in its
        file, when the target already exists, or when preprocessor directives are involved.
        """;

	public const string FormatDocuments = """
        Formats C# files to their own repository's .editorconfig: indentation, brace placement, line
        endings, trailing whitespace and the final newline. Call this after writing or editing a C#
        file by any other means. Hand-written C# routinely lands with spaces where the repository
        wants tabs and LF where it wants CRLF, and in a repository that treats IDE0055 as an error
        that is a failed build rather than untidiness. Returns a unified diff; pass apply=false to
        check formatting without writing. Multi-line string literals are left alone, since a newline
        inside one is content rather than layout.
        """;

	public const string ListCodeFixes = """
        What the solution's own analyzers offer to fix in one file: the diagnostic, the titles of the
        fixes available for it, and whether that fix can be applied to a whole project or solution at
        once. Diagnostic ids nothing can fix are listed separately, so an empty answer is not
        mistaken for clean code. Apply one with rose_apply_code_fix rather than editing by hand.
        """;

	public const string ApplyCodeFix = """
        Applies the fix an analyzer ships for one diagnostic id, to a file, a project, or the whole
        solution at once, through Roslyn's own fix-all. Use this rather than editing each occurrence
        by hand: the same rule across fifty files is where hand-fixing and find-and-replace go wrong.
        Only the analyzers that report the requested id are run, so fixing one rule costs a fraction
        of a full analyzer pass. Returns a unified diff; pass apply=false to preview. Ask
        rose_list_code_fixes what is available first.
        """;

	public const string ReplaceMember = """
        Writes over one member -- a method, property, field, constructor, or a whole type --
        addressed by name rather than by line and column. Use this instead of a text edit: the code
        is parsed as a declaration first and the call refuses without touching the file if it does
        not parse, so an unbalanced brace, a dropped access modifier or an escape that leaked into
        the source cannot reach disk. What it writes is formatted to the repository's own
        .editorconfig, so the indentation and line endings cannot be wrong either. A name also does
        not go stale the way a line number does the moment an earlier edit lands. The documentation
        comment above the declaration is kept unless the code supplies one. It then compiles the
        projects holding the file and returns the errors the edit introduced -- so edit and check is
        one call, not an edit followed by a build.
        """;

	public const string ReplaceBody = """
        Replaces a member's body and nothing else: the signature that comes out is the one that was
        there, copied rather than rewritten, so it cannot drift. Takes statements, a block in
        braces, or => expression;, and a member can switch between the last two without saying so.
        Use this rather than a line-range edit, which is the usual way a member gets broken --
        splicing a body against line numbers that have moved drops a brace or a modifier, and the
        damage is found at the next build. Refuses if the code does not parse, formats what it
        writes, and returns the errors the edit introduced.
        """;

	public const string AddMember = """
        Adds one or more members to a type, addressed by name, placed with after or before rather
        than appended blindly. Use this rather than finding the closing brace and inserting text:
        the code is parsed first and refused if it does not parse, it lands with a blank line around
        it and the repository's own indentation, and a member the type already declares is refused
        instead of written as a duplicate the compiler would reject. It returns the errors the
        addition introduced, so there is no build in the loop. A using directive is not a member and
        is not added; add one yourself if the new code needs an import.
        """;
}
