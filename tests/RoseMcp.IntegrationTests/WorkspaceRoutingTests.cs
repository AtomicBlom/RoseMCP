using RoseMcp.Broker;
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

		private string Solution(string fileName, params string[] projects)
		{
			var entries = projects.Select(name => $"  <Project Path=\"{name}/{name}.csproj\" />");
			var path = Path.Combine(Root, fileName);

			File.WriteAllText(path, $"<Solution>\n{string.Join("\n", entries)}\n</Solution>");

			return path;
		}
	}
}
