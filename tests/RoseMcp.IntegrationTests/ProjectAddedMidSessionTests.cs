using Microsoft.Extensions.Logging.Abstractions;

using RoseMcp.Contracts;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// A project's .editorconfig, where the design-time build had nothing to walk up from. The build lists the
/// analyzer config files above each source it compiles, so a project added to the solution before anything is
/// written into it is built with none, and a folder with no source in it contributes nothing. Left there, a
/// file written into either is formatted and compiled under Roslyn's defaults and called clean, while the
/// build -- which reads the .editorconfig once the file is there -- fails on the rule it raises to an error.
/// <para>
/// Each test raises CS0168, an unused local, to an error. It is a compiler warning, so whether the compile
/// reports it is exactly whether the .editorconfig was applied, with no analyzer package in the way.
/// </para>
/// </summary>
public sealed class ProjectAddedMidSessionTests
{
	private const string Unused =
		"namespace Patterns;\n\npublic sealed class Unused\n{\n    public void Run()\n    {\n        int never;\n    }\n}\n";

	/// <summary>
	/// The order a project usually arrives in mid-session: the project file written, the project added to the
	/// solution, the reload that brings it in, and then the first file. The project is given the .editorconfig above it at that
	/// reload, so the write follows its tabs and CRLF and the compile applies its severity.
	/// </summary>
	[Test]
	public async Task A_project_added_with_nothing_in_it_is_given_the_editorconfig_above_it()
	{
		var token = TestContext.Current!.Execution.CancellationToken;
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");

		var editorConfig = fixture.Path("Members", ".editorconfig");
		await File.AppendAllTextAsync(editorConfig, "dotnet_diagnostic.CS0168.severity = error\n", token);

		await using var session = await TestSession.OpenAsync(fixture);
		await session.ReadAsync(token);

		var directory = fixture.Path("Members", "Patterns");
		Directory.CreateDirectory(directory);
		await File.WriteAllTextAsync(
			Path.Combine(directory, "Patterns.csproj"),
			"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>",
			token);
		await File.WriteAllTextAsync(
			fixture.SolutionPath,
			"<Solution>\n  <Project Path=\"Library/Library.csproj\" />\n  <Project Path=\"Patterns/Patterns.csproj\" />\n</Solution>\n",
			token);

		var reloaded = await session.ReadAsync(token);
		reloaded.Notices.ShouldContain(notice => notice.Contains("reloaded", StringComparison.Ordinal));

		var patterns = reloaded.Solution.Projects.Single(project => project.Name == "Patterns");
		patterns.AnalyzerConfigDocuments.Select(config => config.FilePath).ShouldContain(editorConfig);

		var path = Path.Combine(directory, "Unused.cs");
		var result = await AddAsync(session, path, Unused);

		result.Applied.ShouldBeTrue();
		result.IntroducedDiagnostics.Select(diagnostic => diagnostic.Id).ShouldBe(["CS0168"]);
		result.Notices.ShouldNotContain(notice => notice.Contains("compiles clean", StringComparison.Ordinal));
		result.Notices.ShouldNotContain(notice => notice.Contains("was never given", StringComparison.Ordinal));

		var text = await File.ReadAllTextAsync(path, token);
		text.ShouldContain("\r\n\tpublic void Run()\r\n", Case.Sensitive);
	}

	/// <summary>
	/// A folder that held no source when the project was built, with an .editorconfig of its own. The project
	/// was given the one above it and not this one, so the compile cannot apply what it raises; the write says
	/// which file it ran without rather than calling the project clean, and is laid out by it all the same,
	/// since that is the file dotnet format will read.
	/// </summary>
	[Test]
	public async Task A_write_under_an_editorconfig_its_project_was_never_given_is_not_called_clean()
	{
		var token = TestContext.Current!.Execution.CancellationToken;
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");

		var directory = fixture.Path("Members", "Library", "Rules");
		Directory.CreateDirectory(directory);
		var nested = Path.Combine(directory, ".editorconfig");
		await File.WriteAllTextAsync(
			nested,
			"[*.cs]\nindent_style = space\nindent_size = 2\ndotnet_diagnostic.CS0168.severity = error\n",
			token);

		await using var session = await TestSession.OpenAsync(fixture);

		var path = Path.Combine(directory, "Unused.cs");
		var result = await AddAsync(session, path, Unused.Replace("namespace Patterns;", "namespace Library.Rules;", StringComparison.Ordinal));

		result.Applied.ShouldBeTrue();
		result.IntroducedDiagnostics.ShouldBeEmpty();
		result.Notices.ShouldContain(notice => notice.StartsWith(nested + " applies to Unused.cs and was never given to Library", StringComparison.Ordinal));
		result.Notices.ShouldContain("Library compiles clean without the 1 analyzer config file(s) named above, so a build that reads them can still fail.");
		result.Notices.ShouldNotContain("Library compiles clean.");

		var text = await File.ReadAllTextAsync(path, token);
		text.ShouldContain("\r\n  public void Run()\r\n", Case.Sensitive);
	}

	private static Task<AddFileResult> AddAsync(WorkspaceSession session, string filePath, string code)
	{
		var diagnostics = new DiagnosticsService(NullLogger<DiagnosticsService>.Instance);
		var request = new AddFileRequest { FilePath = filePath, Code = code, Apply = true, Usings = [] };

		return session.MutateAsync(
			(snapshot, token) => AddFileService.AddAsync(snapshot, diagnostics, request, session.NoteSelfWrite, token),
			TestContext.Current!.Execution.CancellationToken);
	}
}
