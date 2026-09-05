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
	/// <summary>
	/// How the four tools that address one symbol describe their arguments. Shared so the naming
	/// half cannot be explained on one tool and left off another, which is how a caller learns that
	/// a position is the only way in.
	/// </summary>
	public const string SymbolArgument =
		"The symbol by name, as Namespace.Type.Member. Add a parameter list to pick an overload, and "
			+ "Type.Type or Type..ctor for a constructor. Preferred over a position: it needs no grep "
			+ "first and does not go stale when an earlier edit moves the line.";

	public const string FilePathArgument =
		"Absolute or solution-relative path to the file. Give it with line and column to point at a "
			+ "symbol, or on its own to say which file a name is declared in.";

	public const string LineArgument =
		"One-based line number. Only needed when pointing at a position rather than naming a symbol -- "
			+ "which is the way to reach a local variable or a parameter, since neither is declared "
			+ "under a name this can find.";

	public const string ColumnArgument = "One-based column, pointing at the identifier itself.";

	public const string VerifyScopeArgument =
		"How much to compile when verifying: auto, file, dependents, or solution. Defaults to auto, "
			+ "which compiles the file's own projects for a body change or an effectively private "
			+ "member and their dependents otherwise -- a public member added, reshaped or removed "
			+ "breaks its dependents by construction. Narrowing it to file is faster and says in the "
			+ "result which dependents nobody looked at.";

	public const string WorkspaceOpen = """
        Starts loading a solution into a warm Roslyn host and returns at once, without waiting for the
        load. Call it when you are about to ask questions about a large one and have something else to
        do first -- read the files you are going to ask about, check out a branch, run the part of a
        build that needs no Roslyn -- then call it again to see how far the load has got. It answers
        from the broker's own view (state, the solution it resolved, project count once known, and any
        running operation with its percentage), so polling it never waits on the load it is reporting
        on, and calling it on a solution already loading or loaded changes nothing.

        Never required: every other tool opens the enclosing solution itself, and every one of them
        blocks until the workspace can answer, so asking a real question early is safe -- it waits
        exactly as it would have. Use rose_workspace_status when you want the loaded detail and are
        content to wait for it.
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
        What a symbol actually is: full signature, kind, accessibility, containing type, XML
        documentation, every declaration site, and what the member overrides or implements -- which
        is usually where an override's documentation actually lives. Name it as Namespace.Type.Member,
        or point at a file position; naming needs no grep first and does not go stale when an earlier
        edit moves the line. It also returns each declaration's first and last line, so where a
        member stops is known rather than approximated -- which is what a text edit has to guess and
        what it gets wrong. Resolved from the compilation rather than read off the declaration text,
        and works from a use site as well as a declaration. isFromSource being false means it lives
        in metadata and cannot be renamed or edited.
        """;

	public const string FindReferences = """
        Every reference to a symbol, resolved semantically across the whole solution. Unlike a
        text search this follows overrides, interface implementations and aliases, and will not
        match comments, strings, or unrelated identifiers that happen to share a name. Name the
        symbol as Namespace.Type.Member -- no grep for a line and column first, and no stale
        position after an edit. A position still reaches a local or a parameter, which is not
        declared under a name; count the column carefully, because one that lands on a neighbouring
        identifier answers completely and correctly about a different symbol. For the opposite
        direction -- what implements or overrides this -- use rose_find_implementations.
        """;

	public const string FindImplementations = """
        What implements, overrides, or derives from a symbol -- derived types for a class,
        implementing types for an interface, overriding members for a virtual or abstract one.
        Grep cannot answer this at all: an implementation need not mention the interface's name
        anywhere near the member. Name the symbol as Namespace.Type.Member; a position works too,
        but finding one for a type means a rose_search_symbols call first, which is two calls where
        a name is one. The answer says which of those questions was actually answered, since that
        depends on what the symbol turns out to be.
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
        Renames a symbol everywhere it is used, using Roslyn's renamer, so overrides, interface
        implementations, partial declarations and cref references all move together -- none of which
        find-and-replace gets right. Name the symbol as Namespace.Type.Member: renames arrive in
        batches more than any other edit, and a line and column found by reading the file is wrong
        the moment an earlier rename in the same batch lands. A position still reaches a local or a
        parameter. Conflicts, where the new name would bind to something else or shadow an existing
        member, are reported rather than silently applied. Also reports XAML that still names the old
        identifier and does not change it, since markup is text to the compiler and a broken binding
        builds and runs. Returns a unified diff of every file changed; pass apply=false to preview.
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
        inside one is content rather than layout -- and reported, since dotnet format will still
        ask for the endings inside one while no build complains.
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
        there, copied rather than rewritten, so it cannot drift. Three ways to say what the body
        becomes, and exactly one of them per call. code takes the whole body -- statements, a block
        in braces, or => expression;, and a member can switch between the last two without saying
        so. find and replace change part of it, matched on the tokens inside this one member, so
        indentation and line endings cannot cause a miss and a one-line change costs one line rather
        than the whole body; nothing or more than one match is refused. position (start or end) with
        code inserts instead of replacing, and end means before a closing return or throw, since
        anything after one is unreachable. Use any of them rather than a line-range edit, which is
        the usual way a member gets broken -- splicing against line numbers that have moved drops a
        brace or a modifier, and the damage is found at the next build. Whichever payload arrives,
        what is written is a whole body: it is parsed first, refused if it does not parse, formatted,
        and the errors it introduced come back.
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

	public const string ChangeSignature = """
        Changes a member's parameters and everything that has to change with them: the declaration
        you named, the declaration it overrides or implements, every override and implementation of
        that, and the arguments at every call site. Say what the parameters should be -- as you would
        write them between the parentheses -- and what changed is worked out from it. Use this rather
        than grep and an edit per layer: threading one optional parameter through a stack of
        forwarders is where a call site gets missed, and the missed one compiles at some layers and
        not others, so a build tells you about the wrong half. It also keeps the param tags in the
        documentation comment in step, which is a build error where documentation is generated.
        Parameters that already exist may not be reordered, since an argument's meaning at a call
        site is not always recoverable from its position; new ones can go anywhere. A new parameter
        needs a default or an argument to pass, and it reports every call site it did not change --
        including the ones that still compile, because a forwarder still passing the old default is
        the bug that hides. Verified against the whole solution, since a call site it missed is by
        definition somewhere it did not look.
        """;

	public const string BuildFreshness = """
        Whether each project's build output is newer than the sources it was built from -- the
        question a green build does not answer. Ask this before running anything out of bin or obj:
        a test, a debug host, a generator, a tool. Taking an artefact's existence for its currency is
        how a test comes to run last week's binary and report a failure describing a change that was
        already made, and in that case the solution compiled perfectly, so nothing about a build
        would have said so. It needs no build of its own: the design-time build already knows every
        project's output path and every file it compiles, so this is a file timestamp comparison and
        answers immediately. It reports the output path, when it was written, the newest source and
        how many are newer -- and says nothing about whether the code is correct, which is
        rose_diagnostics.
        """;

	public const string AddUsing = """
        Ensures a file imports the namespaces you name, put where the file's own ordering puts them.
        Use this rather than editing the import block: sort position, whether System comes first,
        whether groups are separated by a blank line and where the file header has to stay are all
        things the file already decides and a splice guesses at -- and getting one wrong is IDE0055.
        It also refuses to add what is already in scope, which is not the same as what the file
        says: a global using, an implicit using from the SDK, or simply being the namespace the file
        is in all count, and importing one of those again is IDE0005. Both are build errors where
        the analyzers are turned up, which is where this matters. Prefer the usings argument on
        rose_replace_member, rose_replace_body and rose_add_member when you are writing the code
        that needs the import -- same work, no second call. This is for code that arrived some other
        way. It reports what it added, what was already covered and why, and how many errors the
        import resolved.
        """;

	public const string Outline = """
        What a type or a file declares: every member with its full signature, kind, accessibility,
        whether it is abstract or static, and the first line of its documentation. Name a type or
        give a file path -- one of the two. Use it instead of reading the file to find out what is in
        it, which is the read that comes before most edits and the one that puts the file in front of
        you: once it is open, the edit goes through a text tool and none of the rest of this is worth
        reaching for. The signatures are the compiler's, so implementing an interface can be written
        from this alone, and each member says where it is, so the next call names a file without
        searching. Members a generator wrote are marked, since there is no file to edit for those.
        Pass includeInherited to get what the base classes contribute too.
        """;

	public const string ProjectGraph = """
        How the solution's projects depend on each other: what each one references, everything that
        transitively references it, its framework, its output assembly, and whether it is a test
        project. Two questions this answers that nothing else does -- where a new type is allowed to
        live, and how far a change to a public member reaches. The second is the transitive list, and
        working it out by opening project files gets it wrong, because the set that breaks is
        everything depending on the project rather than everything naming the member.
        """;

	public const string DeleteMember = """
        Removes a member from a type, addressed by name, taking its documentation comment and its
        attributes with it. Use this rather than cutting a line range: the span is resolved from the
        compilation instead of counted by hand, so it cannot take a brace or a modifier with it, and
        a region opened above the member and closed below it comes out balanced rather than as
        CS1024. It refuses an ambiguous name instead of removing one of two overloads, which is the
        deletion with no symptom at all -- it compiles, and the behaviour that was meant to change
        did not. Deleting something still referenced is allowed and reported: the call sites come
        back as the errors the removal introduced, in the same call, checked across the projects
        that depend on this one when the member was visible to them.
        """;

	public const string AddFile = """
        Creates a C# file: in the project whose directory contains the path, with the namespace the
        folder implies, in the repository's own tabs, braces, line endings and final newline, and
        with the imports the code needs worked out and added. Use this rather than writing the file
        with a text tool, which is what starts most work and so is the earliest place a session
        stops being able to ask semantic questions: a file written outside the workspace leaves it
        mid-edit, and from there every read is worth less than a build. It parses the code before
        placing anything, so a refusal writes nothing, and it refuses a path that already exists
        rather than overwriting it. Pass just the declarations and a file-scoped namespace is added;
        pass a whole file and its own namespace is kept, with a notice when that disagrees with the
        folder, since IDE0130 is a build error where it is turned up. It says which project claimed
        the file -- and says so loudly when that project lists the files it compiles rather than
        globbing them, because then the file exists, looks compiled, and is not.
        """;

	public const string ReplaceDocComment = """
        Replaces a declaration's documentation comment, addressed by name, without touching the code
        under it. Use this rather than rose_replace_member or a text edit when only the prose is
        changing: composing a whole member to change one sentence is a trade nobody takes, and once
        the file is open in an editor the code half goes through the editor too. Pass the summary as
        plain text or the whole comment as XML; it emits /// in the file's own indentation and line
        endings, keeps a licence header or region directive above it, and refuses XML that does not
        parse, which would otherwise land as CS1570. It compiles afterwards and reports what changed,
        because a comment can break a build: a param tag for a parameter that is gone is CS1572 and a
        parameter with no tag is CS1573, wherever a documentation file is generated.
        """;

	public const string SetAttribute = """
        Adds, replaces or removes one attribute on a declaration, addressed by the declaration's name
        and the attribute's. Use this rather than splicing text into the brackets: the attribute is
        parsed first and refused if it does not parse, it lands in a list of its own below the
        documentation comment, and removing the last attribute in a bracket takes the brackets with
        it rather than leaving an empty pair that does not compile. action=set replaces the one
        attribute of that name and refuses when the declaration carries several -- four InlineData
        attributes is the ordinary shape of a test, and replacing the first would compile while
        changing the wrong case. action=add puts another one on; action=remove takes one away.
        Obsolete and ObsoleteAttribute are the same attribute here. It compiles afterwards, and does
        so across the dependents, since an attribute is visible to everything that uses the member.
        """;

	public const string ResolveName = """
        Works out which namespace an unresolved name needs, so you do not have to already know. Give
        it the name as the code spells it -- Encoding, List<int>, Encoding.UTF8 -- and it searches
        the compilation's own universe: this project's source, the projects it references, and every
        referenced assembly. Reach for it when a write reports CS0246, CS0103 or CS1061 and you
        cannot say what the import should be; when you can, pass usings on the write itself and skip
        this. It refuses to choose between two candidates rather than returning the first, because
        the wrong import compiles and binds to the wrong type, which is the one failure here with no
        symptom at all. It also says what an import would not fix: a nested type that has to be
        written through its container, a type that takes a different number of type arguments, one
        declared in a project this one does not reference, and a namespace already in scope. The
        IDE's own add-import fix is not reachable through rose_apply_code_fix, since it lives in an
        assembly this server does not load, so this is how to ask.
        """;
}
