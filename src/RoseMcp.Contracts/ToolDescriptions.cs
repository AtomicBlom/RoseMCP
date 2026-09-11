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

	/// <summary>
	/// auto compiles the file's own projects for a body change or an effectively private member, and
	/// their dependents otherwise -- a public member added, reshaped or removed breaks its dependents
	/// by construction. The reasoning is here rather than in the argument text, which a caller reads
	/// only to decide whether to narrow it.
	/// </summary>
	public const string VerifyScopeArgument =
		"How much to compile: auto, file, dependents, or solution. Defaults to auto. Narrowing it to "
			+ "file is faster and the result names the dependents nobody looked at.";

	/// <summary>
	/// The workspace argument, on nearly every tool. It was 258 characters and appeared thirty times
	/// -- 21% of every <c>inputSchema</c> byte on the wire, spending the caller's context on an
	/// argument they are being told to leave out. The clause it drops, about a directory holding
	/// several solutions, is already in the error the broker returns when that happens, which is the
	/// only moment it is worth reading.
	/// </summary>
	public const string WorkspaceArgument =
		"Solution, project or file path that picks the workspace. Usually omitted: inferred from the "
			+ "other arguments or the working directory.";

	/// <summary>Which live-app session, on every tool that works against one.</summary>
	public const string SessionArgument = "The session id returned by rose_debug_attach.";

	/// <summary>
	/// The arguments themselves, from here rather than from each host's own copy.
	/// <para>
	/// Both hosts declared their own, and eight had drifted -- in every case the worker's said more
	/// and the broker's, the one an MCP client actually reads, said less: whether a solution-relative
	/// path is accepted was stated only on the side no client can see. One constant per argument is
	/// what a parity test can assert; two copies are what it found.
	/// </para>
	/// </summary>
	public const string ApplyArgument =
		"Write the change. False returns the diff without touching disk. Defaults to true.";

	public const string ExpectedRevisionArgument =
		"Fail rather than apply if the workspace has moved past this revision.";

	public const string VerifyArgument =
		"Compile afterwards and report what the change broke and what it resolved. Defaults to true.";

	public const string VerifySolutionArgument =
		"Compile the whole solution afterwards and report what the change broke. Defaults to true.";

	public const string MemberArgument =
		"The member, as Namespace.Type.Member. Add a parameter list to pick an overload.";

	public const string DeclarationArgument =
		"The member or type, as Namespace.Type.Member. Add a parameter list to pick an overload.";

	public const string PartialFilePathArgument =
		"Which file, when the name is declared in more than one -- a partial type or member.";

	public const string UsingsArgument =
		"Namespaces the code needs imported, ensured in the same file. One already in scope is reported, "
			+ "not added.";

	public const string ProjectFilterArgument = "Limit to one project by name. Defaults to the whole solution.";

	public const string ProjectOrPathFilterArgument =
		"Limit to one project by name or path. Defaults to every project.";

	public const string SingleFilePathArgument = "Absolute or solution-relative path to the file.";

	public const string DiagnosticFilePathArgument =
		"One file to analyse. Giving it is what says the scope is that document.";

	public const string DiagnosticProjectArgument =
		"One project to analyse, by name or path. Giving it is what says the scope is that project.";

	public const string DiagnosticScopeArgument = "document, project, or solution. Defaults to solution.";

	public const string MinimumSeverityArgument =
		"Lowest severity to report: hidden, info, warning, or error. Defaults to warning.";

	public const string IncludeAnalyzersArgument =
		"Run analyzers as well as the compiler. Much slower over a whole solution; off by default.";

	public const string MaxDiagnosticsArgument = "Maximum diagnostics to return. Defaults to 200.";

	public const string MaxReferencesArgument = "Maximum references to return. Defaults to 200.";

	public const string DefinitionsOnlyArgument =
		"Return where it is declared and how many uses there are, without listing them.";

	public const string ReferenceProjectArgument = "Only references compiled by this project.";

	public const string IncludePreviewsArgument =
		"Give each location its line of source. On by default; off is much smaller.";

	public const string MaxImplementationsArgument = "Maximum matches to return. Defaults to 200.";

	public const string SearchQueryArgument = "Name or abbreviation to search for.";

	public const string MaxSearchMatchesArgument = "Maximum matches to return. Defaults to 50.";

	public const string IncludeInheritedArgument = "Also list what the base classes contribute. Off by default.";

	public const string IncludeDocumentationArgument =
		"Give each type and member the first line of its documentation. On by default; off is much smaller.";

	public const string IncludeSignaturesArgument =
		"Give each member its full signature. On by default; off leaves the name, kind and location, "
			+ "which is what a search through a large type needs.";

	public const string IncludeTriviaArgument =
		"Match find against the body's text rather than its tokens, so it can lie inside a // comment or "
			+ "a string. Spacing then matters and the replacement is written exactly as given. A match "
			+ "half inside a comment or string and half in the code is refused.";

	public const string AttributeParameterArgument =
		"Put the attribute on this parameter of the named member, by name, rather than on the member "
			+ "itself. A parameter's attribute is otherwise reachable only by rewriting the whole signature.";

	public const string ArityArgument =
		"How many type arguments the use site supplies, where the name is not written with them.";

	public const string MaxCandidatesArgument = "Maximum candidates to return. Defaults to 20.";

	public const string HintNameArgument =
		"Hint name of the generated document, for example Widget.Greeting.g.cs.";

	public const string NewNameArgument = "The new name.";

	public const string RenameOverloadsArgument = "Also rename overloads of the same method.";

	public const string RenameInCommentsArgument = "Also rename occurrences inside comments.";

	public const string RenameInStringsArgument = "Also rename occurrences inside string literals.";

	public const string DiagnosticIdArgument = "The diagnostic id to fix, for example CA1822.";

	public const string FixScopeFilePathArgument = "A file in the scope to fix; the fix has to start somewhere.";

	/// <summary>
	/// Defaults to document, where <c>rose_diagnostics</c>' scope defaults to solution -- the same
	/// name and the same values at opposite ends of the range, which is worth saying rather than
	/// leaving to be discovered. Reading the whole solution is cheap and answers a question the caller
	/// probably has; rewriting every occurrence in it is a change they have to have asked for.
	/// </summary>
	public const string FixScopeArgument =
		"document, project, or solution. Defaults to document -- narrower than rose_diagnostics, since "
			+ "this one writes.";

	public const string RemoveUnusedUsingsArgument =
		"Also drop using directives the file does not need. Off by default.";

	public const string MovedTypeArgument =
		"The type to move out, as Namespace.Type. Type arguments and qualification are ignored.";

	public const string AddToTypeArgument = "The type to add to, as Namespace.Type.";

	public const string MembersCodeArgument = "One or more whole declarations.";

	public const string AfterArgument = "Put them after this member, by name.";

	public const string BeforeArgument = "Put them before this member, by name.";

	public const string ParametersArgument =
		"The parameters it should have, written as they would go between the parentheses.";

	public const string ArgumentsArgument =
		"What to pass at existing call sites for a new parameter with no default, as name=expression.";

	public const string NamespacesArgument = "Namespaces to ensure, as System.Text or using System.Text;.";

	public const string MoveMemberArgument =
		"The member to move, as Namespace.Type.Member. Add a parameter list to pick an overload.";

	public const string TargetTypeArgument = "The type it moves into, as Namespace.Type.";

	public const string NewFilePathArgument = "Where the file goes. Absolute, or relative to the solution.";

	public const string ExtraUsingsArgument =
		"Namespaces to import on top of whatever the code turns out to need.";

	public const string NewFileProjectArgument =
		"Which project compiles it, where the path is inside more than one project's directory.";

	public const string ResolveUsingsArgument =
		"Work out the namespaces the code needs and add the ones with a single answer. Defaults to true.";

	public const string CommentArgument =
		"The comment: plain text taken as the summary, or the whole thing as XML.";

	public const string AttributeActionArgument =
		"set, add, or remove. Defaults to set, which replaces the one of that name and refuses where "
			+ "there are several.";

	public const string ConfigurationArgument = "MSBuild configuration to load under, for example Debug-2027.";

	public const string PlatformArgument = "MSBuild platform to load under, for example x64.";

	public const string PropertiesArgument = "Further MSBuild properties, each as Name=Value.";

	public const string IncludeSourceArgument =
		"Also return the declaration's own source text, so understanding a member does not end in a file "
			+ "read.";

	public const string OutlineTypeArgument =
		"The type, as Namespace.Type. One of this and filePath.";

	public const string OutlineFilePathArgument =
		"The file to outline. One of this and type; also narrows a partial type to one of its files.";

	public const string ResolveNameArgument =
		"The name as the code spells it: Encoding, List<int>, or Encoding.UTF8.";

	public const string ResolveFilePathArgument =
		"The file it is used in. Scopes the search to what that project can reach, and is the only way to "
			+ "know what is in scope there already.";

	public const string FormatFilePathsArgument = "Absolute or solution-relative paths of the files to format.";

	public const string SplitFilePathArgument = "Absolute or solution-relative path to the file to split.";

	public const string TargetPathArgument =
		"Where to put it. Defaults to <typeName>.cs beside the source file.";

	public const string DeclarationCodeArgument =
		"The whole declaration, attributes and documentation comment included.";

	public const string ReplaceArgument = "What to put in place of find. Empty removes the matched code.";

	public const string PositionArgument =
		"start or end, to insert code rather than replace the body. end means before a closing return or "
			+ "throw, since anything after one is unreachable.";

	public const string CallSitesArgument =
		"qualify to write the new type in front of every call, or usingStatic to import it in each "
			+ "calling file. Defaults to qualify.";

	public const string FileCodeArgument =
		"The C#: a whole file, or just the declarations, in which case a namespace is added.";

	public const string FixTitleArgument =
		"Which fix, when the diagnostic offers several. Matched against the fix titles; the first offered "
			+ "runs when it is omitted.";

	public const string BodyCodeArgument =
		"The body: statements, a block in braces, or => expression;. Leave it off when using find, and "
			+ "pass just the statements to insert when using position.";

	public const string FindArgument =
		"Code to find inside this body and replace, matched on the tokens so indentation and line endings "
			+ "do not matter. A comment in it is refused, since the matching cannot see one.";

	public const string AttributeArgument =
		"The attribute as it appears in source, brackets optional: Obsolete(\"use Parse\").";




	/// <summary>
	/// The live-app arguments, cited by the broker's tools and by the host's alike.
	/// <para>
	/// Four layers spelled each of these: the broker declared it, the session forwarded it, the host
	/// declared it again and the session object took it. <c>justMyXaml</c> had two descriptions, one
	/// on each declaring end, and nothing said they should agree -- so a caller reading the broker's
	/// and a maintainer reading the host's were told different things about the same argument.
	/// </para>
	/// </summary>
	public const string ProcessIdArgument =
		"The process id to attach to. Must be a local process owned by the current user.";

	/// <summary>
	/// Whether to open the inspector window on the new session.
	/// <para>
	/// Under the per-argument budget, so it says what the values mean and when to touch them, and
	/// leaves out that only an http broker can honour it -- a caller that hits that gets the whole
	/// sentence back in the session's notice, which is where it is actually useful.
	/// </para>
	/// </summary>
	public const string ShowInspectorArgument =
		"Open the RoseMCP Inspector on this session: userPreference (default) honours what the person "
			+ "chose in the tray, always opens it, never does not. Leave it alone unless somebody asked "
			+ "to see it, or asked not to be interrupted.";

	public const string ExecutablePathArgument = "Path to a local .NET executable (.exe).";

	public const string LaunchArgumentsArgument = "Optional command-line arguments.";

	public const string AppUserModelIdArgument = "The app user-model id, e.g. MyApp_1a2b3c4d5e6f7!App.";

	public const string AfterSequenceArgument =
		"Return only events whose sequence is greater than this; 0 for everything buffered.";

	public const string MaxEventsArgument =
		"Maximum events in this page (default 500). Lower it when you only need to see whether something "
			+ "is happening.";

	/// <summary>
	/// The debugger's location grammar, said to be the one the rest of the surface uses. It is the
	/// same <c>Namespace.Type.Method</c> a <c>symbol</c> argument takes, with an assembly prefix for
	/// the case the debugger cannot infer -- and nothing said so, so a caller who had just addressed
	/// the same method by name for an edit had no reason to think this took the same string.
	/// </summary>
	public const string TracepointLocationArgument =
		"The method to trace, as the same Namespace.Type.Method the rose_* tools take, with Assembly! "
			+ "in front where the assembly name is not the namespace's first segment.";

	public const string LogMessageArgument =
		"Optional message logged on each hit (literal text; expression interpolation comes later).";

	public const string LogEveryNthHitArgument =
		"Optional: log only every Nth hit to thin a hot path; every hit is still counted.";

	public const string TracepointIdArgument = "The tracepoint id returned by rose_debug_add_tracepoint.";

	public const string BreakpointLocationArgument =
		"The method to break on, as the same Namespace.Type.Method the rose_* tools take, with "
			+ "Assembly! in front where the assembly name is not the namespace's first segment.";

	public const string AutoContinueSecondsArgument =
		"Seconds a hit is held before the target auto-continues on its own; default 30.";

	public const string BreakpointIdArgument = "The breakpoint id returned by rose_debug_set_breakpoint.";

	public const string StepModeArgument = "in, over, or out.";

	public const string EvaluateExpressionArgument =
		"A field-access expression, e.g. this.field or state.Inner.Count.";

	public const string FrameThreadIdArgument =
		"The thread to read, or omitted for the one the debugger is holding. rose_live_app_threads lists them.";

	public const string FrameOffsetArgument =
		"Where in the stack to start, zero being the innermost frame. Omitted starts at the innermost.";

	public const string FrameLimitArgument =
		"How many frames to return. Omitted returns a page of the innermost ones.";

	public const string FrameIndexArgument =
		"Which frame, by its index in the stack rose_live_app_frames reports for this thread.";

	/// <summary>
	/// The grammar a value carries and a caller passes back. Spelled out because the slot forms are
	/// not guessable, and passing back a variable's own path is the reliable way to reach a value
	/// whose name is a compiler temporary or is shared between two blocks.
	/// </summary>
	public const string ValuePathArgument =
		"The value to expand, as the path a variable reported: arg:0 or local:2 for a frame's own "
			+ "values, then .field and [3] into what they hold. The name of an argument or local also "
			+ "works as a root.";

	public const string HoldSecondsArgument =
		"How long to suspend the stop's auto-continue timer for, capped at ten minutes. Omitted holds "
			+ "for five.";

	public const string HoldReleaseArgument =
		"True gives the stop back to its safety timer instead of holding it.";

	/// <summary>
	/// How a live element is named to the tools that read one. All three spellings, because a caller
	/// has all three to hand -- the tree and the selection report a handle and an address, and an
	/// x:Name is in the markup -- and could use only the handle at each tool. The address is the form
	/// that exists for the elements that matter: everything inside a control template is unnamed.
	/// </summary>
	public const string XamlElementArgument =
		"The element: its handle, an x:Name as #name, or the address rose_xaml_tree and "
			+ "rose_xaml_selection report. A name that matches several elements is refused.";

	public const string XamlRootArgument =
		"Root the tree at this element's subtree; omit for the whole tree. A handle, an x:Name as "
			+ "#name, or an address.";

	public const string XamlRootNameArgument =
		"Root the tree at this named element's subtree; omit for the whole tree.";

	public const string XamlOffsetArgument = "Skip this many nodes, for paging a large tree.";

	public const string XamlLimitArgument =
		"Return at most this many nodes; 0 for all. Total says how many matched.";

	public const string XamlHandleArgument = "The element handle from rose_xaml_tree.";

	public const string XamlNewMarkupArgument =
		"The new XAML to apply, when it is markup rather than a file. Pass oldXaml with it.";

	public const string EventKindsArgument =
		"Event kinds to return; omit for all. The cursor still advances over what is filtered out, and "
			+ "'skipped' says how many those were. A name that is not a kind is refused.";

	public const string TracepointConditionArgument =
		"Optional condition gating each hit, as 'name OP literal' over the method's arguments/locals, "
			+ "e.g. count >= 100. Only simple value compares; expressions need eval.";

	public const string BreakpointConditionArgument =
		"Optional condition gating each hit, as 'name OP literal' over the method's arguments/locals, "
			+ "e.g. id == 42. Only simple value compares; expressions need eval.";

	/// <summary>
	/// Capped at 60 so the call cannot outlive the caller's own timeout, and a wait that ends empty
	/// has lost nothing: events are buffered and the same cursor picks up whatever arrives next.
	/// </summary>
	public const string WaitSecondsArgument =
		"Seconds to wait for a matching event rather than returning what is there now; 0 answers at "
			+ "once. Use it with kinds to wait for one thing instead of polling. Capped at 60.";

	public const string IncludeDefaultsArgument =
		"Include the framework defaults, not only what the framework reports as set. Note that those "
			+ "are not quite the same question as what the XAML sets: a property the framework "
			+ "materialises while being inspected counts as set from then on.";

	public const string XamlFilePathArgument =
		"The XAML file to apply. What it holds now is diffed against what this session last sent to "
			+ "the app, so an edit-and-apply loop needs only this.";

	public const string XamlOldMarkupArgument =
		"The previous XAML. Only needed for the first apply of a file this session has not seen "
			+ "before, or with newXaml for markup that is not on disk.";

	/// <summary>
	/// Off by default because such an element -- an empty Grid with no Background, something with
	/// IsHitTestVisible false -- can cover the whole window and shadow everything the user can
	/// actually click.
	/// </summary>
	public const string IncludeAllElementsArgument =
		"Also pick elements the framework would not route a click to. Off by default; turn it on only "
			+ "to inspect an invisible host deliberately.";

	/// <summary>
	/// Decided on the element's source -- ms-appx: is the app's markup, ms-resource: the framework's
	/// -- and it falls back to the framework's own pick when nothing under the click came from the
	/// app. That mechanism is in the doc comment rather than in the argument text, which a caller
	/// reads to decide whether to pass it.
	/// </summary>
	public const string JustMyXamlArgument =
		"Prefer the element the app's own XAML declares over a control template's parts, the way "
			+ "Visual Studio's Just My XAML does. On by default. Off selects template internals.";

	/// <summary>
	/// Arming lays a pointer-capturing layer over the app and waits for a click, and picking by
	/// handle does not take it away -- so a session that never disarms leaves the person using the app
	/// in a mode they did not ask for.
	/// </summary>
	public const string ArmArgument =
		"False disarms select mode instead of arming it, the same as the toolbar's Idle button. "
			+ "Disarm when you have finished: arming captures the pointer until something does.";

	public const string WorkspaceOpen = """
		Starts loading a solution and returns within about a second, without waiting for the load.
		Never required: every other tool loads the enclosing solution itself and waits for it, so
		asking a real question early is safe. Use this only when a large solution is about to be needed
		and you have other work to do first. Call it again to see how far the load has got -- it answers
		from the broker's own view (state, project count once known, running operations with
		percentages), so polling never waits on the load it reports on, and calling it on a solution
		already loading or loaded changes nothing. rose_workspace_status gives the loaded detail and
		waits for it.
		""";

	public const string WorkspaceStatus = """
		What state a solution's workspace is in and whether its answers can be trusted: the projects
		loaded, the MSBuild configuration it chose, the restore, load diagnostics, and degradedReasons
		-- each with its fix -- when they cannot. Ask it when answers look wrong rather than assuming
		the code is. Thousands of errors about System.Object being undefined means the solution loaded
		under a configuration it does not declare, which rose_workspace_reload takes; a project whose
		design-time build failed answers unreliably and is named. It waits for the load, unlike
		rose_workspace_open.
		""";

	public const string WorkspaceReload = """
		Restarts a solution's Roslyn host from scratch. Rarely needed -- edits by other tools are
		absorbed on the next call -- but it is the only way to pick up a rebuilt analyzer or source
		generator, since an assembly once loaded cannot be unloaded from a process. Also how to change
		the MSBuild configuration a solution loaded under, which is the fix when everything reports
		System.Object undefined. It costs a full design-time build, so use it for those and not to
		refresh state.
		""";

	public const string WorkspaceClose = """
		Stops the worker for a workspace and releases the memory its solution was holding. A loaded
		solution costs a gigabyte or more, so this is worth doing when moving off one for good.
		""";

	public const string Diagnostics = """
		Compiler diagnostics, and optionally analyzer ones, from a live Roslyn compilation of the
		current state of disk -- edits by other tools are absorbed before the analysis runs, so results
		are never stale. Pass filePath for one file or project for one project; leave both off for the
		solution. Use it as the edit loop in place of building after every change, from a warm
		compilation. It is not a substitute for a build: it compiles but does not emit and runs no
		MSBuild targets, so it cannot see emit-time errors, anything a build step generates or repacks,
		or a failure in a project reference's own build, and analyzers are opt-in here while they can
		be errors there. Diagnostics inside source-generated code are included, tagged with the hint
		name that reads that code back. To repair what it reports, ask rose_list_code_fixes.
		""";

	public const string SymbolInfo = """
		What a symbol actually is: full signature, kind, accessibility, containing type, XML
		documentation, every declaration site, and what it overrides or implements -- which is usually
		where an override's documentation lives. Name it as Namespace.Type.Member, which needs no grep
		first and does not go stale when an earlier edit moves a line; a file position works too, and
		is the way to reach a local or a parameter. Pass includeSource so understanding a member does
		not end in a file read. Each declaration reports its first and last line, so where a member
		stops is known rather than approximated. Resolved from the compilation, so it answers from a
		use site as well as a declaration, and about a type in a referenced assembly -- isFromSource
		false means it cannot be renamed or edited.
		""";

	public const string FindReferences = """
		Every reference to a symbol, resolved semantically across the solution. Unlike a text search
		this follows overrides, interface implementations and aliases, and will not match comments,
		strings or unrelated identifiers that share a name. Name the symbol as Namespace.Type.Member;
		a position still reaches a local or a parameter, and needs the column on the identifier itself,
		since one on a neighbour answers completely and correctly about a different symbol. Each hit
		names the member it sits inside, which turns a flat list into "used by these six methods". A
		large answer narrows three ways: definitionsOnly for the count alone, project for one project,
		includePreviews=false to drop the line of source. For the opposite direction, use
		rose_find_implementations.
		""";

	public const string FindImplementations = """
		What implements, overrides or derives from a symbol -- derived types for a class, implementing
		types for an interface, overriding members for a virtual or abstract one. Grep cannot answer
		this at all: an implementation need not mention the interface's name anywhere near the member.
		Name the symbol as Namespace.Type.Member, which also works for a type in a referenced assembly,
		so "what here implements IDisposable" is one call. The answer says which of those three
		questions it actually answered, since that depends on what the symbol turns out to be.
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
		Renames a symbol everywhere it is used, through Roslyn's renamer, so overrides, interface
		implementations, partial declarations and cref references all move together -- none of which
		find-and-replace gets right. Name the symbol as Namespace.Type.Member: renames arrive in
		batches more than any other edit, and a line and column found by reading the file is wrong the
		moment an earlier rename in the same batch lands. A position still reaches a local or a
		parameter. Conflicts, where the new name would bind to something else or shadow an existing
		member, are applied and listed rather than refused -- pass apply=false first and look before
		committing to it. Also reports XAML that still names the old identifier and does not change it,
		since markup is text to the compiler and a broken binding builds and runs. A symbol from
		metadata is refused: there is no source to write. Returns a unified diff of every file changed.
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
		Applies an analyzer's own fix for a diagnostic id across a document, project or solution, using
		Roslyn's fix-all. Use it rather than editing each occurrence: the fix is the analyzer author's,
		so it is the change the rule actually wants, and one call covers every site. Ask
		rose_list_code_fixes first for what is on offer -- a diagnostic several providers claim, or
		claim and then decline, is reported there rather than guessed at here. With fixTitle it matches
		a title case-insensitively; without one it runs the first the provider offers, which for a
		diagnostic with several fixes may not be the one you meant. Only the analyzers that report the
		id are run, so this costs far less than a full analyzer pass.
		""";

	public const string ReplaceMember = """
		Writes over one member -- a method, property, field, constructor, or a whole type -- addressed
		by name rather than by line and column. Use it instead of a text edit: the code is parsed as a
		declaration first and the call refuses without touching the file if it does not parse, and what
		it writes is formatted to the repository's own .editorconfig. A name also does not go stale the
		way a line number does the moment an earlier edit lands. The documentation comment is kept
		unless the code supplies one, and so are attributes the code leaves off, with a notice --
		dropping one silently takes the member out of whatever it enrolled it in. A name matching two
		overloads is refused. It then compiles: the file's own projects for a body change, their
		dependents too when the edit reshapes something visible outside them, with the analyzers where
		it wrote -- so edit and check is one call rather than an edit and a build.
		""";

	public const string ReplaceBody = """
		Replaces a member's body and nothing else: the signature that comes out is the one that was
		there, copied rather than rewritten, so it cannot drift. Use it rather than a line-range edit,
		which is how a member gets broken -- splicing against moved line numbers drops a brace, and the
		damage is found at the next build. Three payloads, exactly one per call. code is the whole body:
		statements, a block, or => expression;. find and replace change part of it, matched on the
		tokens inside this one member, so indentation cannot cause a miss; nothing, more than one match,
		or a comment in find is refused -- includeTrivia matches the text instead, which reaches a //
		comment or the words inside a string. position (start or end) with code inserts, end meaning
		before a closing return or throw. A field or property initialiser counts as a body, which is
		what reaches a string constant. What is written is always a whole body: parsed, refused if it
		does not parse, formatted, then compiled.
		""";

	public const string AddMember = """
		Adds one or more members to a type, addressed by name, placed with after or before rather
		than appended blindly. Use this rather than finding the closing brace and inserting text:
		the code is parsed first and refused if it does not parse, it lands with a blank line around
		it and the repository's own indentation, and a member the type already declares is refused
		instead of written as a duplicate the compiler would reject. It returns the errors the
		addition introduced, so there is no build in the loop. Imports the new code needs are
		worked out and added; pass usings to name one explicitly.
		""";

	public const string ChangeSignature = """
		Changes a member's parameters and everything that must change with them: the declaration you
		named, the one it overrides or implements, every override and implementation of that, the
		arguments at every call site, and the param tags in its documentation comment. Use it rather
		than grep and an edit per layer -- a missed forwarder compiles at some layers and not others,
		so a build tells you about the wrong half. Give the full parameter list as it should read
		between the parentheses; what changed is worked out from it. Existing parameters cannot be
		reordered: an argument's meaning at a call site is not always recoverable from its position. A
		new one needs a default or an arguments entry. Every use left alone is listed with the reason:
		a nameof or method group, a base or this initialiser, one that does not compile, and the ones
		that still compile because a new parameter has a default -- a forwarder passing the old default
		is the bug that hides. Verified against the whole solution.
		""";

	public const string BuildFreshness = """
		Whether each project's build output is newer than the sources it was built from, and which
		files are newer if not. Ask it before running anything out of bin -- a test, a tool, a
		generator -- because a green build a few edits ago does not answer the question, and a stale
		assembly presents as a test failing for a reason that has nothing to do with the change. It
		compares timestamps only, so it cannot tell whether the last build succeeded; a project with no
		output at all is reported as never built.
		""";

	public const string AddUsing = """
		Ensures a file imports the namespaces named, placed by the file's own ordering and grouping so
		IDE0055 stays quiet. Use it rather than editing the import block, which guesses at sort
		position, whether System comes first, whether groups are separated and where a file header has
		to stay. Refuses one already in scope -- a global using, an implicit using from the SDK, or the
		file's own namespace -- since importing it again is IDE0005. Both of those are build errors
		where the analyzers are turned up, which is where this matters. Reports what was added, what
		was already covered and why, and how many errors the import resolved. Prefer the usings
		argument on rose_replace_member, rose_replace_body and rose_add_member when you are writing the
		code; this is for code that arrived some other way.
		""";

	public const string MoveMember = """
		Moves a static member from one type to another and takes its call sites with it, in one
		change. Use this rather than adding it to the new type and deleting it from the old: those
		are two writes, and a failure between them leaves the member declared twice. The call sites
		are the part that gets forgotten -- callSites=qualify writes the new type in front of each
		one, callSites=usingStatic adds a using static to each calling file and leaves the calls as
		they are, and the choice is made once here rather than once per file. The declaration moves
		exactly as written, documentation comment and attributes included, reindented for where it
		lands. Instance members are refused: moving one changes what 'this' means inside it and every
		call site would need a receiver it has no reason to have to hand.
		""";

	public const string Outline = """
		What a type or a file declares: every member with its full signature, kind, accessibility,
		whether it is abstract or static, where it is, and the first line of its documentation. Name a
		type or give a file path -- one of the two. Use it instead of reading the file to find out what
		is in it, which is the read that comes before most edits and the one that puts the file in front
		of you: once it is open, the edit goes through a text tool. The signatures are the compiler's,
		so an interface implementation can be written from this alone. Members a generator wrote are
		marked, since there is no file to edit for those. Pass includeInherited for what the base
		classes contribute.
		""";

	public const string ProjectGraph = """
		How the solution's projects depend on each other: what each one references, everything that
		transitively references it, its framework, where its output goes, and whether it is a test
		project. Two questions this answers that nothing else does -- where a new type is allowed to
		live, and how far a change to a public member reaches. The second is the transitive list, and
		reading project files gets it wrong, because what breaks is everything depending on the project
		rather than everything naming the member.
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
		folder implies, in the repository's own tabs, braces, line endings and final newline, and with
		the imports the code needs worked out and added. Use it rather than writing the file with a text
		tool -- that is what starts most work, and so the earliest place a session stops being able to
		ask semantic questions at all. It parses the code before placing anything, so a refusal writes
		nothing, and it refuses a path that already exists rather than overwriting it. Pass just the
		declarations and a file-scoped namespace is added; pass a whole file and its own namespace is
		kept, with a notice when that disagrees with the folder, since IDE0130 is a build error where it
		is turned up. It says which project claimed the file -- loudly when that project lists the files
		it compiles rather than globbing them, because then the file exists, looks compiled, and is not.
		""";

	public const string ReplaceDocComment = """
		Replaces a declaration's documentation comment, addressed by name, without touching the code
		under it. Use it rather than rose_replace_member or a text edit when only the prose is
		changing: composing a whole member to change one sentence is a trade nobody takes, and once the
		file is open in an editor the code half goes through the editor too. Pass the summary as plain
		text or the whole comment as XML; it emits /// in the file's own indentation and line endings,
		goes exactly where the old comment was so a blank line above the member and a licence header
		stay where they are, and refuses XML that does not parse, which would otherwise land as CS1570.
		It compiles afterwards, because a comment can break a build: a param tag for a parameter that
		is gone is CS1572 and a parameter with no tag is CS1573, wherever documentation is generated.
		""";

	public const string SetAttribute = """
		Adds, replaces or removes one attribute on a declaration, addressed by the declaration's name
		and the attribute's. Use it rather than splicing text into the brackets: the attribute is
		parsed first and refused if it does not parse, it lands in a list of its own below the
		documentation comment and indented for where it goes, and removing the last attribute in a
		bracket takes the brackets with it rather than leaving an empty pair that does not compile.
		action=set replaces the one attribute of that name and refuses when the declaration carries
		several -- four InlineData attributes is the ordinary shape of a test, and replacing the first
		would compile while changing the wrong case. action=add puts another one on; action=remove
		takes one away, and refuses if it is not there. Obsolete and ObsoleteAttribute are the same
		attribute here. It compiles across the dependents, since an attribute is visible to everything
		that uses the member.
		""";

	public const string ResolveName = """
		Finds which namespace an unresolved name needs. Give the name as the code spells it --
		Encoding, List<int>, Encoding.UTF8 -- and it searches this project's source, the projects it
		references and every referenced assembly. Use it when a write reports CS0246, CS0103 or CS1061
		and you cannot say what the import is; when you can, pass usings on the write instead. Two
		candidates are both returned, never a first pick: the wrong import compiles and binds to the
		wrong type. It also says what an import would not fix -- a nested type, a mismatched arity, a
		type in a project this one does not reference, or a namespace already in scope. The IDE's own
		add-import fix is not reachable through rose_apply_code_fix, so this is how to ask.
		""";
}
