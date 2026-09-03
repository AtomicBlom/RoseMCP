using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using RoseMcp.Broker;
using RoseMcp.TestSupport;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// Which workspace a call is routed to, asked without starting a worker for it.
/// <para>
/// The ordering these cover used to be spread across three places that disagreed: the relay resolved
/// the session's directory before reading any argument, each tool wrote its own
/// <c>workspace ?? filePath</c> by hand, and the manager's last resort was whichever single solution
/// happened to be loaded. A session in
/// <c>D:\Drawboard\Windows\Windows.IntegrationFramework</c> -- three solutions at the root -- could
/// therefore be refused for an ambiguity it had already resolved by naming a solution outright.
/// </para>
/// </summary>
public sealed class WorkspaceRoutingTests
{
	/// <summary>
	/// The failure that prompted all this, reduced to its shape: the caller names a solution, the
	/// directory they are calling from holds several, and the call must go where they said.
	/// </summary>
	[Fact]
	public void A_named_workspace_beats_an_ambiguous_origin()
	{
		using var repository = new SeveralSolutions();
		var manager = Manager(rootedAt: repository.Root);

		var routed = manager.WorkspaceFor(WorkspaceHints.From(repository.Second));

		Assert.Equal(repository.Second, routed, ignoreCase: true);
	}

	/// <summary>
	/// And the same call with nothing named is still refused, because the directory genuinely does
	/// not say. Being able to answer the first case is not a licence to guess at this one.
	/// </summary>
	[Fact]
	public void An_ambiguous_origin_with_nothing_named_is_still_refused()
	{
		using var repository = new SeveralSolutions();
		var manager = Manager(rootedAt: repository.Root);

		var error = Assert.Throws<AmbiguousSolutionException>(() => manager.WorkspaceFor(WorkspaceHints.None));

		Assert.Equal(3, error.Candidates.Count);
	}

	/// <summary>
	/// A path the call carries for its own reasons decides by containment, which is what the relay's
	/// pre-emptive resolve used to make impossible: every rose_find_references in a multi-solution
	/// root failed on the directory before the file path it was given could settle it.
	/// </summary>
	[Fact]
	public void A_path_in_the_call_decides_where_the_origin_cannot()
	{
		using var repository = new SeveralSolutions();
		var manager = Manager(rootedAt: repository.Root);

		var routed = manager.WorkspaceFor(
			WorkspaceHints.From(null, Path.Combine(repository.Root, "Second", "Thing.cs")));

		Assert.Equal(repository.Second, routed, ignoreCase: true);
	}

	/// <summary>
	/// Not every hint is a path. rose_diagnostics takes a <c>target</c> that is a file under document
	/// scope and a project name under project scope, and resolving "Second" as a path would make it
	/// relative to the process directory -- answering from whichever solution is sitting there.
	/// </summary>
	[Fact]
	public void A_hint_that_names_nothing_on_disk_is_passed_over()
	{
		using var repository = new SeveralSolutions();
		var manager = Manager(rootedAt: repository.Root);

		// "Second" is a project name here, not a path.
		Assert.Throws<AmbiguousSolutionException>(
			() => manager.WorkspaceFor(WorkspaceHints.From(null, "Second")));
	}

	/// <summary>
	/// With nothing named and nothing in the arguments, the session's own directory answers. This is
	/// what makes every tool work with no setup call, which is the whole reason the tools get used.
	/// </summary>
	[Fact]
	public void The_origin_directory_answers_a_bare_call()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		var manager = Manager(rootedAt: Path.GetDirectoryName(fixture.SolutionPath)!);

		Assert.Equal(
			fixture.SolutionPath, manager.WorkspaceFor(WorkspaceHints.None), ignoreCase: true);
	}

	/// <summary>
	/// The origin a relay sent outranks the broker's own working directory, which in http mode is the
	/// tray's install directory and describes nothing.
	/// </summary>
	[Fact]
	public void A_relayed_origin_outranks_the_brokers_own_directory()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		var manager = Manager(rootedAt: NowhereDirectory.Path());

		using var origin = CallOrigin.Use(Path.GetDirectoryName(fixture.SolutionPath)!);

		Assert.Equal(
			fixture.SolutionPath, manager.WorkspaceFor(WorkspaceHints.None), ignoreCase: true);
	}

	/// <summary>
	/// An ambiguity about a path the caller named explains more than one about a directory they only
	/// happened to be in, so that is the failure they get.
	/// </summary>
	[Fact]
	public void Reports_the_ambiguity_about_the_path_over_the_one_about_the_directory()
	{
		using var repository = new SeveralSolutions();
		var manager = Manager(rootedAt: NowhereDirectory.Path());

		var error = Assert.Throws<AmbiguousSolutionException>(
			() => manager.WorkspaceFor(WorkspaceHints.From(null, repository.Root)));

		Assert.Equal(repository.Root, error.Directory, ignoreCase: true);
	}

	private static WorkspaceManager Manager(string rootedAt) => new(
		Options.Create(new BrokerOptions { DefaultWorkspaceRoot = rootedAt }),
		NullLoggerFactory.Instance,
		NullLogger<WorkspaceManager>.Instance);

	/// <summary>
	/// A root holding three solutions, none of which encloses the root itself -- the shape of
	/// Drawboard's integration framework, where DrawboardProjects.slnx sits beside Drawboard.Pdf.slnx
	/// and Shared.slnx and the largest is not a superset of the others.
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
