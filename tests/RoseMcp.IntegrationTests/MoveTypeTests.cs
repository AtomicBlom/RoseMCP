using Microsoft.Extensions.Logging.Abstractions;

using RoseMcp.Contracts;
using RoseMcp.TestSupport;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// Splitting a file full of types is the one refactoring where the formatting matters as much as
/// the semantics: this repository, and plenty of others, fail the build on an unnecessary using or
/// a stray blank line. So these check the text that comes out as closely as the compilation.
/// </summary>
public sealed class MoveTypeTests
{
	[Test]
	public async Task Moves_a_type_into_a_file_named_after_it()
	{
		using var fixture = FixtureSolution.Copy("MultiType", "MultiType.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await MoveAsync(session, fixture, "Circle");

		var target = fixture.Path("MultiType", "Shapes", "Circle.cs");

		result.TargetPath.ShouldBe(target, StringCompareShould.IgnoreCase);
		result.Applied.ShouldBeTrue();
		File.Exists(target).ShouldBeTrue($"{target} was not written");

		var moved = await File.ReadAllTextAsync(target, TestContext.Current!.Execution.CancellationToken);

		// The namespace and the declaration, and the doc comment that belongs to it.
		moved.ShouldContain("namespace Shapes;", Case.Sensitive);
		moved.ShouldContain("public sealed record Circle(double Radius) : IShape", Case.Sensitive);
		moved.ShouldContain("/// A circle.", Case.Sensitive);

		// And nothing else that was in the file it came from.
		moved.ShouldNotContain("interface IShape", Case.Sensitive);
		moved.ShouldNotContain("class Square", Case.Sensitive);
	}

	/// <summary>
	/// The using went across with the type that needed it and left the file that no longer does.
	/// Getting this wrong in either direction is a build error where the analyzers are turned up.
	/// </summary>
	[Test]
	public async Task Moves_the_using_the_type_needed_and_drops_it_from_the_file_it_left()
	{
		using var fixture = FixtureSolution.Copy("MultiType", "MultiType.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await MoveAsync(session, fixture, "Circle");

		var moved = await File.ReadAllTextAsync(
			fixture.Path("MultiType", "Shapes", "Circle.cs"), TestContext.Current!.Execution.CancellationToken);
		var remaining = await File.ReadAllTextAsync(
			fixture.Path("MultiType", "Shapes", "Shapes.cs"), TestContext.Current!.Execution.CancellationToken);

		moved.ShouldContain("using System.Globalization;", Case.Sensitive);
		remaining.ShouldNotContain("using System.Globalization;", Case.Sensitive);
		result.RemovedUsings.ShouldContain("using System.Globalization;");

		remaining.ShouldNotContain("record Circle", Case.Sensitive);
		remaining.ShouldContain("public interface IShape", Case.Sensitive);
		remaining.ShouldContain("public sealed class Square", Case.Sensitive);
	}

	/// <summary>
	/// Tabs, CRLF and the blank line between members, all as the fixture wrote them. A move that
	/// reformats is a move nobody can review.
	/// </summary>
	[Test]
	public async Task Keeps_the_formatting_it_found()
	{
		using var fixture = FixtureSolution.Copy("MultiType", "MultiType.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		await MoveAsync(session, fixture, "Circle");

		var moved = await File.ReadAllTextAsync(
			fixture.Path("MultiType", "Shapes", "Circle.cs"), TestContext.Current!.Execution.CancellationToken);
		var remaining = await File.ReadAllTextAsync(
			fixture.Path("MultiType", "Shapes", "Shapes.cs"), TestContext.Current!.Execution.CancellationToken);

		moved.ShouldContain("\r\n", Case.Sensitive);
		moved.Replace("\r\n", "\n").Replace("\n\n", "<blank>").ShouldNotContain("\n\n", Case.Sensitive);
		moved.ShouldContain("\tpublic double Area() => Math.PI * Radius * Radius;", Case.Sensitive);
		moved.ShouldEndWith("}\r\n", Case.Sensitive);

		// The hole the type left closed up: no double blank line, and one newline at the end.
		remaining.ShouldNotContain("\r\n\r\n\r\n", Case.Sensitive);
		remaining.ShouldEndWith("}\r\n", Case.Sensitive);
	}

	/// <summary>
	/// The point of doing this with a compiler rather than a text editor: both halves still compile,
	/// and everything that used the moved type still binds to it.
	/// </summary>
	[Test]
	public async Task Leaves_the_solution_compiling()
	{
		using var fixture = FixtureSolution.Copy("MultiType", "MultiType.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		await MoveAsync(session, fixture, "Circle");
		await MoveAsync(session, fixture, "ShapeKind");

		var diagnostics = await DiagnoseAsync(session);

		diagnostics.Diagnostics.ShouldBeEmpty();
	}

	/// <summary>
	/// A file the worker created has to join the tracking table, or the next edit anyone makes to
	/// it is invisible until something forces a reload -- the exact staleness this server exists to
	/// avoid.
	/// </summary>
	[Test]
	public async Task Watches_the_file_it_created_for_later_edits()
	{
		using var fixture = FixtureSolution.Copy("MultiType", "MultiType.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		await MoveAsync(session, fixture, "Circle");

		var target = fixture.Path("MultiType", "Shapes", "Circle.cs");
		var text = await File.ReadAllTextAsync(target, TestContext.Current!.Execution.CancellationToken);

		// Edited behind the workspace's back, exactly as an agent's own file tools would.
		await File.WriteAllTextAsync(
			target, text.Replace("Math.PI", "Math.Pie", StringComparison.Ordinal), TestContext.Current!.Execution.CancellationToken);

		var diagnostics = await DiagnoseAsync(session);

		diagnostics.Diagnostics.ShouldContain(diagnostic => diagnostic.Id == "CS0117");
	}

	[Test]
	public async Task Writes_nothing_when_previewing()
	{
		using var fixture = FixtureSolution.Copy("MultiType", "MultiType.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var before = await File.ReadAllTextAsync(
			fixture.Path("MultiType", "Shapes", "Shapes.cs"), TestContext.Current!.Execution.CancellationToken);

		var result = await MoveAsync(session, fixture, "Circle", apply: false);

		result.Applied.ShouldBeFalse("a preview writes nothing");
		File.Exists(fixture.Path("MultiType", "Shapes", "Circle.cs")).ShouldBeFalse();
		(await File.ReadAllTextAsync(
			fixture.Path("MultiType", "Shapes", "Shapes.cs"), TestContext.Current!.Execution.CancellationToken)).ShouldBe(before);

		// The diff still describes both halves of the move that did not happen.
		result.Diff.ShouldContain("+++ ", Case.Sensitive);
		result.Diff.ShouldContain("record Circle", Case.Sensitive);
		string.Join(" ", result.Notices).ShouldContain("Preview only", Case.Sensitive);
	}

	/// <summary>
	/// Moving the last type out would leave a file holding nothing but usings. Deleting files is a
	/// bigger hammer than a move should reach for, so this says what was meant instead.
	/// </summary>
	[Test]
	public async Task Refuses_to_empty_a_file()
	{
		using var fixture = FixtureSolution.Copy("MultiType", "MultiType.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var error = await Should.ThrowAsync<InvalidOperationException>(
			() => MoveAsync(session, fixture, "Usage", file: "Usage.cs")).OfExactType();

		error.Message.ShouldContain("only type", Case.Sensitive);
		error.Message.ShouldContain("Rename the file", Case.Sensitive);
	}

	[Test]
	public async Task Refuses_to_write_over_a_file_that_exists()
	{
		using var fixture = FixtureSolution.Copy("MultiType", "MultiType.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var error = await Should.ThrowAsync<InvalidOperationException>(
			() => MoveAsync(session, fixture, "Circle", targetPath: "Usage.cs")).OfExactType();

		error.Message.ShouldContain("already exists", Case.Sensitive);
	}

	/// <summary>
	/// The names in the file, when the one asked for is not among them. A caller that mistyped a
	/// name can fix it from the message rather than reading the file again.
	/// </summary>
	[Test]
	public async Task Says_what_the_file_declares_when_the_type_is_not_there()
	{
		using var fixture = FixtureSolution.Copy("MultiType", "MultiType.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var error = await Should.ThrowAsync<ArgumentException>(
			() => MoveAsync(session, fixture, "Triangle")).OfExactType();

		error.Message.ShouldContain("Triangle", Case.Sensitive);
		error.Message.ShouldContain("Circle", Case.Sensitive);
		error.Message.ShouldContain("ShapeKind", Case.Sensitive);
	}

	/// <summary>
	/// The blank lines that separated a using go with it. Otherwise the file left behind starts
	/// with the gap where the import used to be, and the file the type moved into starts with the
	/// gaps where somebody else's imports were -- which here is a build error, not an untidiness.
	/// </summary>
	[Test]
	public async Task Closes_the_gap_a_removed_using_leaves()
	{
		using var fixture = FixtureSolution.Copy("MultiType", "MultiType.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		await MoveAsync(session, fixture, "Circle");

		var moved = await File.ReadAllTextAsync(
			fixture.Path("MultiType", "Shapes", "Circle.cs"), TestContext.Current!.Execution.CancellationToken);
		var remaining = await File.ReadAllTextAsync(
			fixture.Path("MultiType", "Shapes", "Shapes.cs"), TestContext.Current!.Execution.CancellationToken);

		// The type that needed the import took it, and kept it at the top where it belongs.
		moved.ShouldStartWith("using System.Globalization;\r\n\r\nnamespace Shapes;", Case.Sensitive);

		// The file it left had only that one, so it now opens on its namespace rather than on the
		// hole the using left.
		remaining.ShouldStartWith("namespace Shapes;", Case.Sensitive);
		remaining.ShouldNotContain("\r\n\r\n\r\n", Case.Sensitive);
	}

	private static Task<MoveTypeResult> MoveAsync(
		WorkspaceSession session,
		FixtureSolution fixture,
		string typeName,
		string file = "Shapes.cs",
		string? targetPath = null,
		bool apply = true)
	{
		var request = new MoveTypeRequest
		{
			FilePath = fixture.Path("MultiType", "Shapes", file),
			Symbol = typeName,
			TargetPath = targetPath,
			Apply = apply,
		};

		return session.MutateAsync(
			(snapshot, token) => MoveTypeService.MoveAsync(snapshot, request, session.NoteSelfWrite, token),
			TestContext.Current!.Execution.CancellationToken);
	}

	private static async Task<DiagnosticsResult> DiagnoseAsync(WorkspaceSession session)
	{
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		return await new DiagnosticsService(NullLogger<DiagnosticsService>.Instance).AnalyseAsync(
			snapshot, new DiagnosticsRequest(), TestContext.Current!.Execution.CancellationToken);
	}
}
