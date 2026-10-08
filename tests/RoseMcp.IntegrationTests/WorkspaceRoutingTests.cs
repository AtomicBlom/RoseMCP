using ModelContextProtocol;

using RoseMcp.Broker;
using RoseMcp.Broker.Tools;
using RoseMcp.TestSupport;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// Which workspace a call is routed to, asked without starting a worker for it.
/// <para>
/// The ordering these cover is one rule in <c>WorkspaceManager.WorkspaceFor</c>, because anything
/// less disagrees with itself. A relay that resolves the session's directory before reading any
/// argument refuses a session in a root holding three solutions for an ambiguity the call settled by
/// naming one outright; tools that each write their own <c>workspace ?? filePath</c> drift apart; and
/// a last resort of whichever single solution happens to be loaded answers from another repository.
/// </para>
/// </summary>
public sealed class WorkspaceRoutingTests
{
	/// <summary>
	/// The failure that prompted all this, reduced to its shape: the caller names a solution, the
	/// directory they are calling from holds several, and the call must go where they said.
	/// </summary>
	[Test]
	public void A_named_workspace_beats_an_ambiguous_origin()
	{
		using var repository = new SeveralSolutions();
		var manager = Manager(rootedAt: repository.Root);

		var routed = manager.WorkspaceFor(WorkspaceHints.From(RootedPath.Absolute(repository.Second)));

		routed.ShouldBe(repository.Second, StringCompareShould.IgnoreCase);
	}

	/// <summary>
	/// And the same call with nothing named is still refused, because the directory genuinely does
	/// not say. Being able to answer the first case is not a licence to guess at this one.
	/// </summary>
	[Test]
	public void An_ambiguous_origin_with_nothing_named_is_still_refused()
	{
		using var repository = new SeveralSolutions();
		var manager = Manager(rootedAt: repository.Root);

		var error = Should.Throw<AmbiguousSolutionException>(() => manager.WorkspaceFor(WorkspaceHints.None)).ShouldBeOfType<AmbiguousSolutionException>();

		error.Candidates.Count.ShouldBe(3);
	}

	/// <summary>
	/// A path the call carries for its own reasons decides by containment, which a relay that
	/// resolves the session's directory first makes impossible: every rose_find_references in a
	/// multi-solution root would fail on the directory before the file path it was given could settle it.
	/// </summary>
	[Test]
	public void A_path_in_the_call_decides_where_the_origin_cannot()
	{
		using var repository = new SeveralSolutions();
		var manager = Manager(rootedAt: repository.Root);

		var routed = manager.WorkspaceFor(
			WorkspaceHints.From(null, RootedPath.Absolute(Path.Combine(repository.Root, "Second", "Thing.cs"))));

		routed.ShouldBe(repository.Second, StringCompareShould.IgnoreCase);
	}

	/// <summary>
	/// Not every hint is a path. rose_diagnostics takes a <c>target</c> that is a file under document
	/// scope and a project name under project scope, and a name that describes nothing where the
	/// caller is standing says nothing about which workspace they meant.
	/// </summary>
	[Test]
	public void A_hint_that_names_nothing_on_disk_is_passed_over()
	{
		using var repository = new SeveralSolutions();
		var manager = Manager(rootedAt: repository.Root);
		var paths = Paths(rootedAt: repository.Root);

		// A project name with no folder of its own, which is the ordinary shape: the project is named
		// for what it does and lives beside its siblings.
		Should.Throw<AmbiguousSolutionException>(
			() => manager.WorkspaceFor(WorkspaceHints.From(null, paths.Of("Second.Core")))).ShouldBeOfType<AmbiguousSolutionException>();
	}

	/// <summary>
	/// The failure this card was cut for. Six worktrees of one repository hold the same relative
	/// paths, so a relative hint measured from anywhere but the calling session names a real file in
	/// the wrong checkout, resolves by containment, and wins the ranking outright -- and the write
	/// that follows is invisible to the working copy the session can see.
	/// </summary>
	[Test]
	public void A_relative_path_resolves_in_the_checkout_the_call_came_from()
	{
		using var main = FixtureSolution.Copy("Simple", "Simple.sln");
		using var worktree = FixtureSolution.Copy("Simple", "Simple.sln");

		var elsewhere = Path.GetDirectoryName(main.SolutionPath)!;
		var here = Path.GetDirectoryName(worktree.SolutionPath)!;

		// The broker is sitting in one checkout and the session is calling from the other.
		var manager = Manager(rootedAt: elsewhere);
		var paths = Paths(rootedAt: elsewhere);

		using var origin = CallOrigin.Use(here);

		var routed = manager.WorkspaceFor(WorkspaceHints.From(null, paths.Of(Path.Combine("Core", "Calculator.cs"))));

		routed.ShouldBe(worktree.SolutionPath, StringCompareShould.IgnoreCase);
	}

	/// <summary>
	/// With nothing named and nothing in the arguments, the session's own directory answers. This is
	/// what makes every tool work with no setup call, which is the whole reason the tools get used.
	/// </summary>
	[Test]
	public void The_origin_directory_answers_a_bare_call()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		var manager = Manager(rootedAt: Path.GetDirectoryName(fixture.SolutionPath)!);

		manager.WorkspaceFor(WorkspaceHints.None).ShouldBe(
			fixture.SolutionPath, StringCompareShould.IgnoreCase);
	}

	/// <summary>
	/// The origin a relay sent outranks the broker's own working directory, which in http mode is the
	/// tray's install directory and describes nothing.
	/// </summary>
	[Test]
	public void A_relayed_origin_outranks_the_brokers_own_directory()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		var manager = Manager(rootedAt: NowhereDirectory.Path());

		using var origin = CallOrigin.Use(Path.GetDirectoryName(fixture.SolutionPath)!);

		manager.WorkspaceFor(WorkspaceHints.None).ShouldBe(
			fixture.SolutionPath, StringCompareShould.IgnoreCase);
	}

	/// <summary>
	/// An ambiguity about a path the caller named explains more than one about a directory they only
	/// happened to be in, so that is the failure they get.
	/// </summary>
	[Test]
	public void Reports_the_ambiguity_about_the_path_over_the_one_about_the_directory()
	{
		using var repository = new SeveralSolutions();
		var manager = Manager(rootedAt: NowhereDirectory.Path());

		var error = Should.Throw<AmbiguousSolutionException>(
			() => manager.WorkspaceFor(WorkspaceHints.From(null, RootedPath.Absolute(repository.Root)))).ShouldBeOfType<AmbiguousSolutionException>();

		error.Directory.ShouldBe(repository.Root, StringCompareShould.IgnoreCase);
	}

	/// <summary>
	/// rose_add_file's path names nothing on disk by definition, and it is the only argument saying
	/// where the call belongs. Passed over like any hint naming nothing, a new file in another checkout
	/// is answered by the session's own workspace; routed by the directory it will be placed under,
	/// it reaches the solution whose project will compile it, folders not yet made included.
	/// </summary>
	[Test]
	public void A_new_file_routes_by_the_directory_it_will_be_placed_under()
	{
		using var origin = FixtureSolution.Copy("Simple", "Simple.sln");
		using var elsewhere = new SeveralSolutions();
		var manager = Manager(rootedAt: Path.GetDirectoryName(origin.SolutionPath)!);

		var file = RootedPath.Absolute(Path.Combine(elsewhere.Root, "Second", "Made", "Later", "Thing2.cs"));

		manager.WorkspaceFor(WorkspaceHints.ForNewFile(null, file)).ShouldBe(
			elsewhere.Second, StringCompareShould.IgnoreCase);
	}

	/// <summary>
	/// The same path as an ordinary hint is still passed over: only a path the call will create is
	/// routed by where it is going, so a hint that is not a path at all keeps falling through.
	/// </summary>
	[Test]
	public void A_missing_path_that_the_call_will_not_create_is_still_passed_over()
	{
		using var origin = FixtureSolution.Copy("Simple", "Simple.sln");
		using var elsewhere = new SeveralSolutions();
		var manager = Manager(rootedAt: Path.GetDirectoryName(origin.SolutionPath)!);

		var file = RootedPath.Absolute(Path.Combine(elsewhere.Root, "Second", "Thing2.cs"));

		manager.WorkspaceFor(WorkspaceHints.From(null, file)).ShouldBe(
			origin.SolutionPath, StringCompareShould.IgnoreCase);
	}

	/// <summary>
	/// A new file nothing encloses says nothing about the call, so the session's directory answers it,
	/// as it does for an existing path with no solution near it.
	/// </summary>
	[Test]
	public void A_new_file_with_no_solution_above_it_falls_back_to_the_origin()
	{
		using var origin = FixtureSolution.Copy("Simple", "Simple.sln");
		var manager = Manager(rootedAt: Path.GetDirectoryName(origin.SolutionPath)!);

		var empty = Path.Combine(Path.GetTempPath(), "rosemcp-tests", $"empty-{Guid.NewGuid():N}");
		Directory.CreateDirectory(empty);

		try
		{
			var file = RootedPath.Absolute(Path.Combine(empty, "Made", "Thing.cs"));
			var nowhere = RootedPath.Absolute(Path.Combine(NowhereDirectory.Path(), "Thing.cs"));

			manager.WorkspaceFor(WorkspaceHints.ForNewFile(null, file)).ShouldBe(
				origin.SolutionPath, StringCompareShould.IgnoreCase);
			manager.WorkspaceFor(WorkspaceHints.ForNewFile(null, nowhere)).ShouldBe(
				origin.SolutionPath, StringCompareShould.IgnoreCase);
		}
		finally
		{
			Directory.Delete(empty, recursive: true);
		}
	}

	/// <summary>
	/// A new file whose nearest directory holds several solutions, none compiling it, is no basis for
	/// a guess: with nowhere else to go it is the ambiguity about that directory the caller hears.
	/// </summary>
	[Test]
	public void A_new_file_among_several_solutions_still_raises_the_ambiguity()
	{
		using var repository = new SeveralSolutions();
		var manager = Manager(rootedAt: NowhereDirectory.Path());

		var file = RootedPath.Absolute(Path.Combine(repository.Root, "Made", "Loose.cs"));

		var error = Should.Throw<AmbiguousSolutionException>(
			() => manager.WorkspaceFor(WorkspaceHints.ForNewFile(null, file))).ShouldBeOfType<AmbiguousSolutionException>();

		error.Directory.ShouldBe(repository.Root, StringCompareShould.IgnoreCase);
	}

	/// <summary>
	/// A failure answered by a workspace the path is not in says which one it is in and what to pass,
	/// since the worker can only describe its own solution and its "not inside any project" is false
	/// about the path itself.
	/// </summary>
	[Test]
	public void A_failure_answered_by_another_workspace_names_the_one_the_path_is_in()
	{
		using var origin = FixtureSolution.Copy("Simple", "Simple.sln");
		using var elsewhere = new SeveralSolutions();
		var manager = Manager(rootedAt: Path.GetDirectoryName(origin.SolutionPath)!);

		var file = RootedPath.Absolute(Path.Combine(elsewhere.Root, "Second", "Made", "Thing2.cs"));
		var hints = WorkspaceHints.ForNewFile(RootedPath.Absolute(origin.SolutionPath), file);

		var said = manager.Elsewhere(hints, origin.SolutionPath).ShouldNotBeNull();

		said.ShouldContain($"inside a project of {elsewhere.Second}", Case.Sensitive);
		said.ShouldContain("workspace argument", Case.Sensitive);
	}

	/// <summary>
	/// Nothing is added where the workspace that answered compiles the path: the failure was about
	/// something else, and a sentence pointing elsewhere would send the caller the wrong way.
	/// </summary>
	[Test]
	public void A_failure_answered_by_the_workspace_the_path_is_in_gains_nothing()
	{
		using var repository = new SeveralSolutions();
		var manager = Manager(rootedAt: NowhereDirectory.Path());

		var file = RootedPath.Absolute(Path.Combine(repository.Root, "Second", "Thing2.cs"));

		manager.Elsewhere(WorkspaceHints.ForNewFile(null, file), repository.Second).ShouldBeNull();
	}

	/// <summary>
	/// Where the path's own directory holds several solutions and none compiles it, the session's
	/// directory answers and nothing is added: naming any of them would send the caller to the same
	/// refusal from the other side.
	/// </summary>
	[Test]
	public void A_failure_for_a_path_no_solution_compiles_gains_nothing()
	{
		using var origin = FixtureSolution.Copy("Simple", "Simple.sln");
		using var elsewhere = new SeveralSolutions();
		var manager = Manager(rootedAt: Path.GetDirectoryName(origin.SolutionPath)!);

		var file = RootedPath.Absolute(Path.Combine(elsewhere.Root, "Loose.cs"));

		manager.WorkspaceFor(WorkspaceHints.ForNewFile(null, file)).ShouldBe(
			origin.SolutionPath, StringCompareShould.IgnoreCase);

		manager.Elsewhere(WorkspaceHints.ForNewFile(null, file), origin.SolutionPath).ShouldBeNull();
	}

	/// <summary>
	/// Where several solutions sharing the path's directory compile it, the caller hears which, and
	/// only those: the ones beside them that do not compile it are no answer about it.
	/// </summary>
	[Test]
	public void A_failure_for_a_path_several_solutions_compile_names_only_those()
	{
		using var repository = new SeveralSolutions();
		repository.Solution("Delta.slnx", "Second", "Third");
		var manager = Manager(rootedAt: NowhereDirectory.Path());

		var file = RootedPath.Absolute(Path.Combine(repository.Root, "Second", "Made", "New.cs"));

		var said = manager.Elsewhere(WorkspaceHints.ForNewFile(null, file), repository.First).ShouldNotBeNull();

		said.ShouldContain("inside no project of Alpha.slnx", Case.Sensitive);
		said.ShouldContain($"2 solutions in {repository.Root} compile it: Beta.slnx, Delta.slnx.", Case.Sensitive);
		said.ShouldNotContain("Gamma", Case.Sensitive);
		said.ShouldContain("workspace argument", Case.Sensitive);
	}

	/// <summary>
	/// The key a result carried names the workspace that produced it, which is what makes it worth
	/// carrying: a short name an agent echoes, where the absolute path is what it drops.
	/// </summary>
	[Test]
	public async Task A_key_names_the_loaded_workspace_it_came_from()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = Manager(rootedAt: NowhereDirectory.Path());

		var worker = await manager.GetOrStartAsync(
			WorkspaceHints.From(RootedPath.Absolute(fixture.SolutionPath)), TestContext.Current!.Execution.CancellationToken);

		manager.WorkspaceFor(WorkspaceHints.From(null, worker.Key)).ShouldBe(
			fixture.SolutionPath, StringCompareShould.IgnoreCase);
	}

	/// <summary>
	/// A key is the caller naming a workspace, so it outranks a path the call carries for its own
	/// reasons exactly as the workspace argument does -- a file in another checkout included, which the
	/// worker the key named then says it does not compile.
	/// </summary>
	[Test]
	public async Task A_key_beats_a_path_in_another_checkout()
	{
		using var main = FixtureSolution.Copy("Simple", "Simple.sln");
		using var worktree = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = Manager(rootedAt: NowhereDirectory.Path());

		var worker = await manager.GetOrStartAsync(
			WorkspaceHints.From(RootedPath.Absolute(worktree.SolutionPath)), TestContext.Current!.Execution.CancellationToken);
		var elsewhere = RootedPath.Absolute(main.Path("Simple", "Core", "Calculator.cs"));

		manager.WorkspaceFor(WorkspaceHints.From(null, elsewhere)).ShouldBe(
			main.SolutionPath, StringCompareShould.IgnoreCase);
		manager.WorkspaceFor(WorkspaceHints.From(null, worker.Key, elsewhere)).ShouldBe(
			worktree.SolutionPath, StringCompareShould.IgnoreCase);
	}

	/// <summary>
	/// A key nothing loaded carries is refused naming the keys that are loaded and the argument that
	/// works regardless -- never passed over for the path beside it, which would answer from a
	/// workspace the caller did not name.
	/// </summary>
	[Test]
	public async Task An_unknown_key_is_refused_naming_the_loaded_keys()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = Manager(rootedAt: NowhereDirectory.Path());

		var worker = await manager.GetOrStartAsync(
			WorkspaceHints.From(RootedPath.Absolute(fixture.SolutionPath)), TestContext.Current!.Execution.CancellationToken);
		var file = RootedPath.Absolute(fixture.Path("Simple", "Core", "Calculator.cs"));

		var error = Should.Throw<McpException>(
			() => manager.WorkspaceFor(WorkspaceHints.From(null, "Simple-00000000", file))).ShouldBeOfType<McpException>();

		error.Message.ShouldContain("Simple-00000000", Case.Sensitive);
		error.Message.ShouldContain(worker.Key, Case.Sensitive);
		error.Message.ShouldContain("workspace with the solution's path", Case.Sensitive);
	}

	/// <summary>
	/// A path and a key in one call are two answers to one question, and nothing here can say which
	/// the caller meant, so the call is refused naming both.
	/// </summary>
	[Test]
	public void A_key_and_a_workspace_together_are_refused()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		var manager = Manager(rootedAt: NowhereDirectory.Path());
		var key = Solutions.WorkspaceKey.For(fixture.SolutionPath);

		var error = Should.Throw<McpException>(
			() => manager.WorkspaceFor(WorkspaceHints.From(RootedPath.Absolute(fixture.SolutionPath), key)))
			.ShouldBeOfType<McpException>();

		error.Message.ShouldContain(fixture.SolutionPath, Case.Sensitive);
		error.Message.ShouldContain(key, Case.Sensitive);
		error.Message.ShouldContain("Send only one", Case.Sensitive);
	}

	/// <summary>
	/// The round trip, through the broker's own tool: a result names its file absolutely, a caller
	/// makes that relative to where it stands and sends it back with the result's key, and the same
	/// file in the same workspace answers. A key decides which worker answers and not where a
	/// relative path is measured from, so the session's directory still measures it.
	/// </summary>
	[Test]
	public async Task A_relative_path_sent_back_with_a_key_names_the_file_the_result_did()
	{
		using var main = FixtureSolution.Copy("Simple", "Simple.sln");
		using var worktree = FixtureSolution.Copy("Simple", "Simple.sln");
		var here = Path.GetDirectoryName(worktree.SolutionPath)!;

		await using var manager = Manager(rootedAt: Path.GetDirectoryName(main.SolutionPath)!);
		var tools = new BrokerAnalysisTools(manager, Paths(rootedAt: Path.GetDirectoryName(main.SolutionPath)!));
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		using var origin = CallOrigin.Use(here);

		var worker = await manager.GetOrStartAsync(WorkspaceHints.From(RootedPath.Absolute(worktree.SolutionPath)), cancellationToken);

		var first = await tools.OutlineAsync(
			new Progress<ProgressNotificationValue>(), symbol: "Core.Calculator", workspaceKey: worker.Key, cancellationToken: cancellationToken);
		var absolute = first.Types.ShouldHaveSingleItem().FilePath.ShouldNotBeNull();

		absolute.ShouldBe(worktree.Path("Simple", "Core", "Calculator.cs"), StringCompareShould.IgnoreCase);

		var second = await tools.OutlineAsync(
			new Progress<ProgressNotificationValue>(),
			filePath: Path.GetRelativePath(here, absolute),
			workspaceKey: first.WorkspaceKey,
			cancellationToken: cancellationToken);

		second.WorkspaceKey.ShouldBe(first.WorkspaceKey);
		second.Workspace.ShouldBe(worktree.SolutionPath, StringCompareShould.IgnoreCase);
		second.Types.ShouldHaveSingleItem().FilePath.ShouldBe(absolute, StringCompareShould.IgnoreCase);
	}

	private static WorkspaceManager Manager(string rootedAt) => BrokerHarness.CreateManager(rootedAt);

	/// <summary>What a tool uses to make its path arguments absolute, rooted where the manager is.</summary>
	private static CallerPaths Paths(string rootedAt) => BrokerHarness.CreatePaths(rootedAt);

	/// <summary>
	/// A root holding three solutions, none of which encloses the root itself -- the shape of a real
	/// repository where the largest solution sits beside two smaller ones and is not a superset of them.
	/// </summary>
	private sealed class SeveralSolutions : IDisposable
	{
		public SeveralSolutions()
		{
			Root = Path.Combine(Path.GetTempPath(), "rosemcp-tests", $"several-{Guid.NewGuid():N}");

			Project("First");
			Project("Second");
			Project("Third");

			First = Solution("Alpha.slnx", "First");
			Second = Solution("Beta.slnx", "Second");
			Third = Solution("Gamma.slnx", "Third");
		}

		public string Root { get; }

		public string First { get; }

		public string Second { get; }

		public string Third { get; }

		public void Dispose()
		{
			try
			{
				Directory.Delete(Root, recursive: true);
			}
			catch (IOException)
			{
				// A temp directory that outlives the run is not worth failing a test over.
			}
		}

		private void Project(string name)
		{
			var directory = Path.Combine(Root, name);
			Directory.CreateDirectory(directory);

			File.WriteAllText(Path.Combine(directory, $"{name}.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
			File.WriteAllText(Path.Combine(directory, "Thing.cs"), "public sealed class Thing;");
		}

		/// <summary>A solution at the root over the named projects, beside whatever is already there.</summary>
		public string Solution(string fileName, params string[] projects)
		{
			var entries = projects.Select(name => $"  <Project Path=\"{name}/{name}.csproj\" />");
			var path = Path.Combine(Root, fileName);

			File.WriteAllText(path, $"<Solution>\n{string.Join("\n", entries)}\n</Solution>");

			return path;
		}
	}
}
