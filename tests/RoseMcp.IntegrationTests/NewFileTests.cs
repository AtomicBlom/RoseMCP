using Microsoft.CodeAnalysis;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// Files that appear rather than change.
/// <para>
/// The sweep behind every other read cannot find these: it stats the documents it knows about, and
/// a file that has just been created is not one of them. Until it is absorbed the workspace answers
/// questions about a solution that does not contain it, so every reference to the new type reports
/// CS0103 -- which is indistinguishable from the code being wrong, and is the worst failure this
/// server can have. Not an error, but a confident answer about a file that is not there.
/// </para>
/// <para>
/// Found by walking rather than by trusting the watcher, so these tests read immediately after
/// writing rather than waiting for an event that may not have arrived. That is also how an agent
/// works: create a file, then ask.
/// </para>
/// </summary>
public sealed class NewFileTests
{
	[Fact]
	public async Task Sees_a_new_file_on_the_very_next_read()
	{
		await using var scope = await OpenAsync();

		var before = await scope.Session.ReadAsync(TestContext.Current.CancellationToken);
		Assert.DoesNotContain(Documents(before), name => name == "Doubler.cs");

		await WriteAsync(scope, "Doubler.cs", """
			namespace Core;

			public static class Doubler
			{
				public static int Double(int value) => value * 2;
			}
			""");

		var after = await scope.Session.ReadAsync(TestContext.Current.CancellationToken);

		Assert.Contains(Documents(after), name => name == "Doubler.cs");
		Assert.True(after.Revision > before.Revision, "absorbing a new file must advance the revision");
		Assert.Contains(after.Notices, notice => notice.Contains("Doubler.cs", StringComparison.Ordinal));
		Assert.Empty(await ErrorsAsync(after));
	}

	/// <summary>
	/// The symptom that made this urgent. Code referring to a type in a file the workspace has not
	/// absorbed reports CS0103 against perfectly good code, and nothing about the answer says the
	/// file is missing rather than the code wrong.
	/// </summary>
	[Fact]
	public async Task Resolves_a_reference_to_a_type_in_a_file_that_has_just_appeared()
	{
		await using var scope = await OpenAsync();

		await WriteAsync(scope, "Tripler.cs", """
			namespace Core;

			public static class Tripler
			{
				public static int Triple(int value) => value * 3;
			}
			""");

		await WriteAsync(scope, "UsesTripler.cs", """
			namespace Core;

			public static class UsesTripler
			{
				public static int Nine() => Tripler.Triple(3);
			}
			""");

		var snapshot = await scope.Session.ReadAsync(TestContext.Current.CancellationToken);

		Assert.Empty(await ErrorsAsync(snapshot));
	}

	/// <summary>
	/// And the new file's own errors are reported, which is the other half: a file nobody compiles
	/// is a file whose mistakes are found at the build.
	/// </summary>
	[Fact]
	public async Task Reports_the_errors_in_a_file_that_has_just_appeared()
	{
		await using var scope = await OpenAsync();

		await WriteAsync(scope, "Broken.cs", """
			namespace Core;

			public static class Broken
			{
				public static int Value => Missing.Thing;
			}
			""");

		var snapshot = await scope.Session.ReadAsync(TestContext.Current.CancellationToken);
		var errors = await ErrorsAsync(snapshot);

		Assert.Contains(errors, error => error.Id == "CS0103");
		Assert.All(errors, error => Assert.EndsWith("Broken.cs", error.Location.SourceTree!.FilePath, StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>
	/// A new file joins the project whose directory holds it, and the closest one at that -- a
	/// repository nests projects inside other projects' folders often enough that the outermost
	/// would otherwise claim half the tree.
	/// </summary>
	[Fact]
	public async Task Adds_the_file_to_the_project_whose_directory_holds_it()
	{
		await using var scope = await OpenAsync();

		await WriteAsync(scope, Path.Combine("Nested", "Deep.cs"), """
			namespace Core.Nested;

			public static class Deep
			{
				public static int Value => 1;
			}
			""");

		var snapshot = await scope.Session.ReadAsync(TestContext.Current.CancellationToken);

		var document = snapshot.Solution.Projects
			.SelectMany(project => project.Documents)
			.Single(candidate => candidate.Name == "Deep.cs");

		Assert.Equal("Core", document.Project.Name);
		Assert.Equal(["Nested"], document.Folders);
	}

	/// <summary>
	/// Build output is not source. A generated file under obj belongs to the compiler, which puts it
	/// into the compilation itself -- absorbing it as well would compile it twice.
	/// </summary>
	[Fact]
	public async Task Ignores_files_under_build_output()
	{
		await using var scope = await OpenAsync();

		await WriteAsync(scope, Path.Combine("obj", "Generated.cs"), """
			namespace Core;

			public static class Generated
			{
				public static int Value => 1;
			}
			""");

		var snapshot = await scope.Session.ReadAsync(TestContext.Current.CancellationToken);

		Assert.DoesNotContain(Documents(snapshot), name => name == "Generated.cs");
	}

	/// <summary>
	/// A file appearing is absorbed; a build file appearing is not something a snapshot can
	/// represent, since it changes how every project below it evaluates.
	/// </summary>
	[Fact]
	public async Task Reloads_when_a_build_file_appears()
	{
		await using var scope = await OpenAsync();

		await scope.Session.ReadAsync(TestContext.Current.CancellationToken);

		await File.WriteAllTextAsync(
			scope.Fixture.Path("Simple", "Directory.Build.props"),
			"<Project>\r\n  <PropertyGroup>\r\n    <DefineConstants>$(DefineConstants);APPEARED</DefineConstants>\r\n"
				+ "  </PropertyGroup>\r\n</Project>\r\n",
			TestContext.Current.CancellationToken);

		// The watcher is what notices this one, so it is the one place a wait is honest.
		var snapshot = await EventuallyAsync(
			scope,
			candidate => candidate.Notices.Any(notice => notice.Contains("reloaded", StringComparison.OrdinalIgnoreCase)));

		Assert.Contains(snapshot.Notices, notice => notice.Contains("reloaded", StringComparison.OrdinalIgnoreCase));

		var core = snapshot.Solution.Projects.Single(project => project.Name == "Core");
		Assert.Contains("APPEARED", core.ParseOptions!.PreprocessorSymbolNames);
	}

	/// <summary>
	/// A read that finds nothing new must not churn the revision, or a caller can never tell whether
	/// two answers describe one world. The walk runs on every barrier, so this is the case that
	/// proves it stays quiet.
	/// </summary>
	[Fact]
	public async Task Says_nothing_when_no_file_appeared()
	{
		await using var scope = await OpenAsync();

		var first = await scope.Session.ReadAsync(TestContext.Current.CancellationToken);
		var second = await scope.Session.ReadAsync(TestContext.Current.CancellationToken);

		Assert.Equal(first.Revision, second.Revision);
		Assert.Same(first.Solution, second.Solution);
		Assert.Empty(second.Notices);
	}

	private static async Task<WorkspaceSnapshot> EventuallyAsync(
		SessionScope scope,
		Func<WorkspaceSnapshot, bool> satisfied)
	{
		WorkspaceSnapshot snapshot;

		for (var attempt = 0; attempt < 40; attempt++)
		{
			snapshot = await scope.Session.ReadAsync(TestContext.Current.CancellationToken);
			if (satisfied(snapshot)) return snapshot;

			await Task.Delay(100, TestContext.Current.CancellationToken);
		}

		return await scope.Session.ReadAsync(TestContext.Current.CancellationToken);
	}

	private static async Task WriteAsync(SessionScope scope, string relativePath, string source)
	{
		var path = scope.Fixture.Path("Simple", "Core", relativePath);

		Directory.CreateDirectory(Path.GetDirectoryName(path)!);

		await File.WriteAllTextAsync(
			path,
			source.ReplaceLineEndings("\r\n") + "\r\n",
			TestContext.Current.CancellationToken);
	}

	private static IEnumerable<string> Documents(WorkspaceSnapshot snapshot) =>
		snapshot.Solution.Projects.SelectMany(project => project.Documents).Select(document => document.Name);

	private static async Task<IReadOnlyList<Diagnostic>> ErrorsAsync(WorkspaceSnapshot snapshot)
	{
		var errors = new List<Diagnostic>();

		foreach (var project in snapshot.Solution.Projects)
		{
			var compilation = await project.GetCompilationAsync(TestContext.Current.CancellationToken);

			errors.AddRange(compilation!.GetDiagnostics(TestContext.Current.CancellationToken)
				.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
		}

		return errors;
	}

	private static Task<SessionScope> OpenAsync() => SessionScope.OpenAsync("Simple", "Simple.sln");
}
