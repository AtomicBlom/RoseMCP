using System.Diagnostics;

namespace RoseMcp.Worker.Tests;

/// <summary>
/// The hostile cases: git rewriting the working tree underneath a live workspace, and the solution
/// itself disappearing. Both are routine in real use and both break naive incremental tracking.
/// </summary>
public sealed class ResilienceTests
{
	[Fact]
	public async Task Picks_up_a_branch_switch_that_rewrites_a_source_file()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		var calculator = fixture.Path("Simple", "Core", "Calculator.cs");

		Git(fixture.Root, "init", "-q");
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
			TestContext.Current.CancellationToken);
		Git(fixture.Root, "commit", "-qam", "other");
		Git(fixture.Root, "checkout", "-q", "main");

		await using var session = await TestSession.OpenAsync(fixture);

		var onMain = await SourceOfAsync(session, "Calculator.cs");
		Assert.Contains("Multiply", onMain, StringComparison.Ordinal);
		Assert.DoesNotContain("Subtract", onMain, StringComparison.Ordinal);

		// Switch branches entirely behind the workspace's back.
		Git(fixture.Root, "checkout", "-q", "other");

		var onOther = await SourceOfAsync(session, "Calculator.cs");
		Assert.Contains("Subtract", onOther, StringComparison.Ordinal);
		Assert.DoesNotContain("Multiply", onOther, StringComparison.Ordinal);
	}

	/// <summary>
	/// A branch that adds a project cannot be absorbed by patching document text, because the
	/// project does not exist in the snapshot to patch. Only a reload can represent it.
	/// </summary>
	[Fact]
	public async Task Picks_up_a_branch_that_adds_a_project()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");

		Git(fixture.Root, "init", "-q");
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
			TestContext.Current.CancellationToken);
		await File.WriteAllTextAsync(
			Path.Combine(extra, "Thing.cs"),
			"namespace Extra; public sealed class Thing;",
			TestContext.Current.CancellationToken);
		Dotnet(fixture.Path("Simple"), "sln", "Simple.sln", "add", "Extra/Extra.csproj");
		Git(fixture.Root, "add", "-A");
		Git(fixture.Root, "commit", "-q", "-m", "extra");
		Git(fixture.Root, "checkout", "-q", "main");

		await using var session = await TestSession.OpenAsync(fixture);

		var before = await session.ReadAsync(TestContext.Current.CancellationToken);
		Assert.DoesNotContain(before.Solution.Projects, project => project.Name == "Extra");

		Git(fixture.Root, "checkout", "-q", "extra");

		var after = await session.ReadAsync(TestContext.Current.CancellationToken);
		Assert.Contains(after.Solution.Projects, project => project.Name == "Extra");
		Assert.Contains(after.Notices, notice => notice.Contains("reloaded", StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>
	/// Transient absence must not unload. Editors save atomically by delete-then-rename, and a
	/// branch switch can remove and restore the solution inside one operation.
	/// </summary>
	[Fact]
	public async Task Serves_a_stale_snapshot_while_the_solution_is_briefly_missing()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture, TimeSpan.FromSeconds(30));

		await session.ReadAsync(TestContext.Current.CancellationToken);

		var solution = fixture.SolutionPath;
		var saved = await File.ReadAllTextAsync(solution, TestContext.Current.CancellationToken);
		File.Delete(solution);

		var whileMissing = await session.ReadAsync(TestContext.Current.CancellationToken);

		Assert.True(whileMissing.Stale);
		Assert.False(session.Unloaded);
		Assert.NotEmpty(whileMissing.Solution.Projects);
		Assert.Contains(whileMissing.Notices, notice => notice.Contains("missing", StringComparison.OrdinalIgnoreCase));

		// Put it back inside the grace period; nothing should have been torn down.
		await File.WriteAllTextAsync(solution, saved, TestContext.Current.CancellationToken);

		var recovered = await session.ReadAsync(TestContext.Current.CancellationToken);

		Assert.False(recovered.Stale);
		Assert.False(session.Unloaded);
		Assert.NotEmpty(recovered.Solution.Projects);
	}

	[Fact]
	public async Task Unloads_once_the_solution_stays_missing_past_the_grace_period()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture, TimeSpan.FromMilliseconds(200));

		await session.ReadAsync(TestContext.Current.CancellationToken);

		File.Delete(fixture.SolutionPath);

		// First read starts the grace timer and is served stale.
		Assert.True((await session.ReadAsync(TestContext.Current.CancellationToken)).Stale);

		await Task.Delay(TimeSpan.FromMilliseconds(400), TestContext.Current.CancellationToken);

		var unloaded = await Assert.ThrowsAsync<SolutionUnloadedException>(
			() => session.ReadAsync(TestContext.Current.CancellationToken));

		Assert.Equal(fixture.SolutionPath, unloaded.SolutionPath);
		Assert.True(session.Unloaded);

		// The error has to name the path, not just say something went wrong.
		Assert.Contains(fixture.SolutionPath, unloaded.Message, StringComparison.Ordinal);
	}

	private static async Task<string> SourceOfAsync(WorkspaceSession session, string documentName)
	{
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);
		var document = snapshot.Solution.Projects
			.SelectMany(project => project.Documents)
			.Single(candidate => candidate.Name == documentName);

		return (await document.GetTextAsync(TestContext.Current.CancellationToken)).ToString();
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
