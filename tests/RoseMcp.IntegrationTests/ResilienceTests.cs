using System.Diagnostics;
using RoseMcp.TestSupport;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// The hostile cases: git rewriting the working tree underneath a live workspace, and the solution
/// itself disappearing. Both are routine in real use and both break naive incremental tracking.
/// </summary>
public sealed class ResilienceTests
{
	/// <summary>
	/// A branch switch that touches only source is absorbed, not reloaded. The switch rewrites HEAD and the
	/// files that differ, and the barrier reads those files like any other change -- so the read after the
	/// switch sees the other branch's code and pays a design-time build of nothing.
	/// </summary>
	[Test]
	public async Task Picks_up_a_branch_switch_that_rewrites_a_source_file()
	{
		var token = TestContext.Current!.Execution.CancellationToken;
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		var calculator = fixture.Path("Simple", "Core", "Calculator.cs");

		// -b main: git honours init.defaultBranch, which is master on plenty of machines, and this
		// test checks out "main" by name further down.
		Git(fixture.Root, "init", "-q", "-b", "main");
		Git(fixture.Root, "config", "user.email", "test@example.com");
		Git(fixture.Root, "config", "user.name", "Test");
		Git(fixture.Root, "add", "-A");
		Git(fixture.Root, "commit", "-q", "-m", "main");

		Git(fixture.Root, "checkout", "-q", "-b", "other");
		await File.WriteAllTextAsync(
			calculator,
			"namespace Core;" + Environment.NewLine
				+ "public static class Calculator" + Environment.NewLine
				+ "{" + Environment.NewLine
				+ "\tpublic static int Add(int left, int right) => left + right;" + Environment.NewLine
				+ "\tpublic static int Subtract(int left, int right) => left - right;" + Environment.NewLine
				+ "}",
			token);
		Git(fixture.Root, "commit", "-qam", "other");
		Git(fixture.Root, "checkout", "-q", "main");

		await using var session = await TestSession.OpenAsync(fixture);

		var onMain = await SourceOfAsync(session, "Calculator.cs");
		onMain.ShouldContain("Multiply", Case.Sensitive);
		onMain.ShouldNotContain("Subtract", Case.Sensitive);

		// Switch branches entirely behind the workspace's back.
		Git(fixture.Root, "checkout", "-q", "other");

		// Long enough for the watcher to have heard HEAD being rewritten, which is the event that could be
		// mistaken for a reason to reload.
		await Task.Delay(TimeSpan.FromSeconds(1), token);

		var switched = await session.ReadAsync(token);
		switched.Notices.ShouldNotContain(notice => notice.Contains("reloaded", StringComparison.OrdinalIgnoreCase));

		var onOther = await SourceOfAsync(session, "Calculator.cs");
		onOther.ShouldContain("Subtract", Case.Sensitive);
		onOther.ShouldNotContain("Multiply", Case.Sensitive);
	}

	/// <summary>
	/// A branch that adds a project cannot be absorbed by patching document text, because the
	/// project does not exist in the snapshot to patch. Only a reload can represent it.
	/// </summary>
	[Test]
	public async Task Picks_up_a_branch_that_adds_a_project()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");

		// -b main: git honours init.defaultBranch, which is master on plenty of machines, and this
		// test checks out "main" by name further down.
		Git(fixture.Root, "init", "-q", "-b", "main");
		Git(fixture.Root, "config", "user.email", "test@example.com");
		Git(fixture.Root, "config", "user.name", "Test");
		Git(fixture.Root, "add", "-A");
		Git(fixture.Root, "commit", "-q", "-m", "main");

		Git(fixture.Root, "checkout", "-q", "-b", "extra");
		var extra = fixture.Path("Simple", "Extra");
		Directory.CreateDirectory(extra);
		await File.WriteAllTextAsync(
			Path.Combine(extra, "Extra.csproj"),
			"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework>"
				+ "</PropertyGroup></Project>",
			TestContext.Current!.Execution.CancellationToken);
		await File.WriteAllTextAsync(
			Path.Combine(extra, "Thing.cs"),
			"namespace Extra; public sealed class Thing;",
			TestContext.Current!.Execution.CancellationToken);
		Dotnet(fixture.Path("Simple"), "sln", "Simple.sln", "add", "Extra/Extra.csproj");
		Git(fixture.Root, "add", "-A");
		Git(fixture.Root, "commit", "-q", "-m", "extra");
		Git(fixture.Root, "checkout", "-q", "main");

		await using var session = await TestSession.OpenAsync(fixture);

		var before = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);
		before.Solution.Projects.ShouldNotContain(project => project.Name == "Extra");

		Git(fixture.Root, "checkout", "-q", "extra");

		var after = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);
		after.Solution.Projects.ShouldContain(project => project.Name == "Extra");
		after.Notices.ShouldContain(notice => notice.Contains("reloaded", StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>
	/// Transient absence must not unload. Editors save atomically by delete-then-rename, and a
	/// branch switch can remove and restore the solution inside one operation.
	/// </summary>
	[Test]
	public async Task Serves_a_stale_snapshot_while_the_solution_is_briefly_missing()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture, TimeSpan.FromSeconds(30));

		await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var solution = fixture.SolutionPath;
		var saved = await File.ReadAllTextAsync(solution, TestContext.Current!.Execution.CancellationToken);
		File.Delete(solution);

		var whileMissing = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		whileMissing.Stale.ShouldBeTrue();
		session.Unloaded.ShouldBeFalse("the session stays loaded while the solution is missing");
		whileMissing.Solution.Projects.ShouldNotBeEmpty();
		whileMissing.Notices.ShouldContain(notice => notice.Contains("missing", StringComparison.OrdinalIgnoreCase));

		// Put it back inside the grace period; nothing should have been torn down.
		await File.WriteAllTextAsync(solution, saved, TestContext.Current!.Execution.CancellationToken);

		var recovered = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		recovered.Stale.ShouldBeFalse("the snapshot is current again once the solution is back");
		session.Unloaded.ShouldBeFalse("the session stayed loaded throughout");
		recovered.Solution.Projects.ShouldNotBeEmpty();
	}

	[Test]
	public async Task Unloads_once_the_solution_stays_missing_past_the_grace_period()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture, TimeSpan.FromMilliseconds(200));

		await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		File.Delete(fixture.SolutionPath);

		// First read starts the grace timer and is served stale.
		(await session.ReadAsync(TestContext.Current!.Execution.CancellationToken)).Stale.ShouldBeTrue();

		await Task.Delay(TimeSpan.FromMilliseconds(400), TestContext.Current!.Execution.CancellationToken);

		var unloaded = await Should.ThrowAsync<SolutionUnloadedException>(
			() => session.ReadAsync(TestContext.Current!.Execution.CancellationToken)).OfExactType();

		unloaded.SolutionPath.ShouldBe(fixture.SolutionPath);
		session.Unloaded.ShouldBeTrue();

		// The error has to name the path, not just say something went wrong.
		unloaded.Message.ShouldContain(fixture.SolutionPath, Case.Sensitive);
	}

	private static async Task<string> SourceOfAsync(WorkspaceSession session, string documentName)
	{
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);
		var document = snapshot.Solution.Projects
			.SelectMany(project => project.Documents)
			.Single(candidate => candidate.Name == documentName);

		return (await document.GetTextAsync(TestContext.Current!.Execution.CancellationToken)).ToString();
	}

	private static void Git(string workingDirectory, params string[] arguments) =>
		Run("git", workingDirectory, arguments);

	private static void Dotnet(string workingDirectory, params string[] arguments) =>
		Run("dotnet", workingDirectory, arguments);

	private static void Run(string executable, string workingDirectory, string[] arguments)
	{
		var startInfo = new ProcessStartInfo(executable)
		{
			WorkingDirectory = workingDirectory,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};

		foreach (var argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"{executable} did not start.");
		var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
		process.WaitForExit();

		if (process.ExitCode != 0)
		{
			throw new InvalidOperationException(
				$"{executable} {string.Join(' ', arguments)} failed with {process.ExitCode}:{Environment.NewLine}{output}");
		}
	}
}
