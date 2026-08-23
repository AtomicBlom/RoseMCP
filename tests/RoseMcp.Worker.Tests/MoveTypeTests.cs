using Microsoft.Extensions.Logging.Abstractions;

using RoseMcp.Contracts;

namespace RoseMcp.Worker.Tests;

/// <summary>
/// Splitting a file full of types is the one refactoring where the formatting matters as much as
/// the semantics: this repository, and plenty of others, fail the build on an unnecessary using or
/// a stray blank line. So these check the text that comes out as closely as the compilation.
/// </summary>
public sealed class MoveTypeTests
{
	[Fact]
	public async Task Moves_a_type_into_a_file_named_after_it()
	{
		using var fixture = FixtureSolution.Copy("MultiType", "MultiType.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await MoveAsync(session, fixture, "Circle");

		var target = fixture.Path("MultiType", "Shapes", "Circle.cs");

		Assert.Equal(target, result.TargetPath, ignoreCase: true);
		Assert.True(result.Applied);
		Assert.True(File.Exists(target), $"{target} was not written");

		var moved = await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken);

		// The namespace and the declaration, and the doc comment that belongs to it.
		Assert.Contains("namespace Shapes;", moved, StringComparison.Ordinal);
		Assert.Contains("public sealed record Circle(double Radius) : IShape", moved, StringComparison.Ordinal);
		Assert.Contains("/// A circle.", moved, StringComparison.Ordinal);

		// And nothing else that was in the file it came from.
		Assert.DoesNotContain("interface IShape", moved, StringComparison.Ordinal);
		Assert.DoesNotContain("class Square", moved, StringComparison.Ordinal);
	}

	/// <summary>
	/// The using went across with the type that needed it and left the file that no longer does.
	/// Getting this wrong in either direction is a build error where the analyzers are turned up.
	/// </summary>
	[Fact]
	public async Task Moves_the_using_the_type_needed_and_drops_it_from_the_file_it_left()
	{
		using var fixture = FixtureSolution.Copy("MultiType", "MultiType.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await MoveAsync(session, fixture, "Circle");

		var moved = await File.ReadAllTextAsync(
			fixture.Path("MultiType", "Shapes", "Circle.cs"), TestContext.Current.CancellationToken);
		var remaining = await File.ReadAllTextAsync(
			fixture.Path("MultiType", "Shapes", "Shapes.cs"), TestContext.Current.CancellationToken);

		Assert.Contains("using System.Globalization;", moved, StringComparison.Ordinal);
		Assert.DoesNotContain("using System.Globalization;", remaining, StringComparison.Ordinal);
		Assert.Contains("using System.Globalization;", result.RemovedUsings);

		Assert.DoesNotContain("record Circle", remaining, StringComparison.Ordinal);
		Assert.Contains("public interface IShape", remaining, StringComparison.Ordinal);
		Assert.Contains("public sealed class Square", remaining, StringComparison.Ordinal);
	}

	/// <summary>
	/// Tabs, CRLF and the blank line between members, all as the fixture wrote them. A move that
	/// reformats is a move nobody can review.
	/// </summary>
	[Fact]
	public async Task Keeps_the_formatting_it_found()
	{
		using var fixture = FixtureSolution.Copy("MultiType", "MultiType.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		await MoveAsync(session, fixture, "Circle");

		var moved = await File.ReadAllTextAsync(
			fixture.Path("MultiType", "Shapes", "Circle.cs"), TestContext.Current.CancellationToken);
		var remaining = await File.ReadAllTextAsync(
			fixture.Path("MultiType", "Shapes", "Shapes.cs"), TestContext.Current.CancellationToken);

		Assert.Contains("\r\n", moved, StringComparison.Ordinal);
		Assert.DoesNotContain("\n\n", moved.Replace("\r\n", "\n").Replace("\n\n", "<blank>"), StringComparison.Ordinal);
		Assert.Contains("\tpublic double Area() => Math.PI * Radius * Radius;", moved, StringComparison.Ordinal);
		Assert.EndsWith("}\r\n", moved, StringComparison.Ordinal);

		// The hole the type left closed up: no double blank line, and one newline at the end.
		Assert.DoesNotContain("\r\n\r\n\r\n", remaining, StringComparison.Ordinal);
		Assert.EndsWith("}\r\n", remaining, StringComparison.Ordinal);
	}

	/// <summary>
	/// The point of doing this with a compiler rather than a text editor: both halves still compile,
	/// and everything that used the moved type still binds to it.
	/// </summary>
	[Fact]
	public async Task Leaves_the_solution_compiling()
	{
		using var fixture = FixtureSolution.Copy("MultiType", "MultiType.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		await MoveAsync(session, fixture, "Circle");
		await MoveAsync(session, fixture, "ShapeKind");

		var diagnostics = await DiagnoseAsync(session);

		Assert.Empty(diagnostics.Diagnostics);
	}

	/// <summary>
	/// A file the worker created has to join the tracking table, or the next edit anyone makes to
	/// it is invisible until something forces a reload -- the exact staleness this server exists to
	/// avoid.
	/// </summary>
	[Fact]
	public async Task Watches_the_file_it_created_for_later_edits()
	{
		using var fixture = FixtureSolution.Copy("MultiType", "MultiType.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		await MoveAsync(session, fixture, "Circle");

		var target = fixture.Path("MultiType", "Shapes", "Circle.cs");
		var text = await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken);

		// Edited behind the workspace's back, exactly as an agent's own file tools would.
		await File.WriteAllTextAsync(
			target, text.Replace("Math.PI", "Math.Pie", StringComparison.Ordinal), TestContext.Current.CancellationToken);

		var diagnostics = await DiagnoseAsync(session);

		Assert.Contains(diagnostics.Diagnostics, diagnostic => diagnostic.Id == "CS0117");
	}

	[Fact]
	public async Task Writes_nothing_when_previewing()
	{
		using var fixture = FixtureSolution.Copy("MultiType", "MultiType.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var before = await File.ReadAllTextAsync(
			fixture.Path("MultiType", "Shapes", "Shapes.cs"), TestContext.Current.CancellationToken);

		var result = await MoveAsync(session, fixture, "Circle", apply: false);

		Assert.False(result.Applied);
		Assert.False(File.Exists(fixture.Path("MultiType", "Shapes", "Circle.cs")));
		Assert.Equal(before, await File.ReadAllTextAsync(
			fixture.Path("MultiType", "Shapes", "Shapes.cs"), TestContext.Current.CancellationToken));

		// The diff still describes both halves of the move that did not happen.
		Assert.Contains("+++ ", result.Diff, StringComparison.Ordinal);
		Assert.Contains("record Circle", result.Diff, StringComparison.Ordinal);
		Assert.Contains("Preview only", string.Join(" ", result.Notices), StringComparison.Ordinal);
	}

	/// <summary>
	/// Moving the last type out would leave a file holding nothing but usings. Deleting files is a
	/// bigger hammer than a move should reach for, so this says what was meant instead.
	/// </summary>
	[Fact]
	public async Task Refuses_to_empty_a_file()
	{
		using var fixture = FixtureSolution.Copy("MultiType", "MultiType.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var error = await Assert.ThrowsAsync<InvalidOperationException>(
			() => MoveAsync(session, fixture, "Usage", file: "Usage.cs"));

		Assert.Contains("only type", error.Message, StringComparison.Ordinal);
		Assert.Contains("Rename the file", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Refuses_to_write_over_a_file_that_exists()
	{
		using var fixture = FixtureSolution.Copy("MultiType", "MultiType.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var error = await Assert.ThrowsAsync<InvalidOperationException>(
			() => MoveAsync(session, fixture, "Circle", targetPath: "Usage.cs"));

		Assert.Contains("already exists", error.Message, StringComparison.Ordinal);
	}

	/// <summary>
	/// The names in the file, when the one asked for is not among them. A caller that mistyped a
	/// name can fix it from the message rather than reading the file again.
	/// </summary>
	[Fact]
	public async Task Says_what_the_file_declares_when_the_type_is_not_there()
	{
		using var fixture = FixtureSolution.Copy("MultiType", "MultiType.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var error = await Assert.ThrowsAsync<ArgumentException>(
			() => MoveAsync(session, fixture, "Triangle"));

		Assert.Contains("Triangle", error.Message, StringComparison.Ordinal);
		Assert.Contains("Circle", error.Message, StringComparison.Ordinal);
		Assert.Contains("ShapeKind", error.Message, StringComparison.Ordinal);
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
			TypeName = typeName,
			TargetPath = targetPath,
			Apply = apply,
		};

		return session.MutateAsync(
			(snapshot, token) => MoveTypeService.MoveAsync(snapshot, request, session.NoteSelfWrite, token),
			TestContext.Current.CancellationToken);
	}

	private static async Task<DiagnosticsResult> DiagnoseAsync(WorkspaceSession session)
	{
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		return await new DiagnosticsService(NullLogger<DiagnosticsService>.Instance).AnalyseAsync(
			snapshot, new DiagnosticsRequest(), TestContext.Current.CancellationToken);
	}
}
