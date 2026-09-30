using System.Text;

using static RoseMcp.IntegrationTests.MemberEdits;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// That an edit gives a file back the byte order mark it had, and gives none to a file that had
/// none.
/// <para>
/// Both failures are silent. A mark is three bytes no unified diff shows, the file compiles either
/// way, and the symptom surfaces somewhere else entirely -- a repository that checks its marks, a
/// tool that reads the file without detecting encoding, or a review where every edited file has
/// changed at byte zero for no reason anyone can point at. So it is checked in bytes, through the
/// real load path, because what an <c>MSBuildWorkspace</c> hands a document as its encoding is the
/// half that cannot be reasoned out.
/// </para>
/// </summary>
public sealed class SourceEncodingTests
{
	[Test]
	public async Task An_edit_gives_back_the_byte_order_mark_the_file_had()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");

		// Before the session opens, so the mark is there for the load to see rather than for a
		// later disk sweep to pick up.
		var path = fixture.Path("Members", "Library", "Greeter.cs");
		Rewrite(path, Encoding.UTF8);

		await using var session = await TestSession.OpenAsync(fixture);

		var edited = await ReplaceAsync(
			session,
			"Library.Greeter.Greet(string)",
			"public string Greet(string name) => $\"{_prefix}, {name}!\";");

		edited.Applied.ShouldBeTrue();
		StartsWithMark(path).ShouldBeTrue("the file was written with a mark and has to keep it");
		(await File.ReadAllTextAsync(path, Token)).ShouldContain("=> $\"{_prefix}, {name}!\"", Case.Sensitive);
	}

	/// <summary>
	/// The other direction, and the one a fix for the first breaks: reading a mark-less file with a
	/// UTF-8 default that emits a preamble gives it an encoding that writes a mark it never had.
	/// </summary>
	[Test]
	public async Task An_edit_puts_no_byte_order_mark_on_a_file_that_had_none()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");

		var path = fixture.Path("Members", "Library", "Greeter.cs");
		Rewrite(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

		await using var session = await TestSession.OpenAsync(fixture);

		var edited = await ReplaceAsync(
			session,
			"Library.Greeter.Greet(string)",
			"public string Greet(string name) => name;");

		edited.Applied.ShouldBeTrue();
		StartsWithMark(path).ShouldBeFalse("nothing gave this file a mark to keep");
	}

	/// <summary>
	/// A file edited after a change on disk goes through the barrier's own reader rather than the
	/// load, so the mark has to survive that route too.
	/// </summary>
	[Test]
	public async Task A_file_re_read_from_disk_keeps_its_mark_through_the_next_edit()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		// Edited behind the workspace's back and given a mark it did not load with, so the next read
		// barrier is what picks both up.
		var path = fixture.Path("Members", "Library", "Greeter.cs");
		var text = await File.ReadAllTextAsync(path, Token);
		await File.WriteAllTextAsync(path, text.Replace("public int Count", "public int Total"), Encoding.UTF8, Token);

		var edited = await ReplaceAsync(
			session,
			"Library.Greeter.Greet(string)",
			"public string Greet(string name) => name;");

		edited.Applied.ShouldBeTrue();
		StartsWithMark(path).ShouldBeTrue();
		(await File.ReadAllTextAsync(path, Token)).ShouldContain("public int Total", Case.Sensitive);
	}

	/// <summary>
	/// The mirror of the one above, and the case the barrier's own reader decides on its own: a
	/// mark-less file changed on disk is re-read there rather than by the load, and a UTF-8 default
	/// that emits a preamble hands it an encoding that puts a mark on the next write.
	/// </summary>
	[Test]
	public async Task A_mark_less_file_re_read_from_disk_gains_no_mark()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var path = fixture.Path("Members", "Library", "Greeter.cs");
		var text = await File.ReadAllTextAsync(path, Token);

		await File.WriteAllTextAsync(
			path,
			text.Replace("public int Count", "public int Total"),
			new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
			Token);

		var edited = await ReplaceAsync(
			session,
			"Library.Greeter.Greet(string)",
			"public string Greet(string name) => name;");

		edited.Applied.ShouldBeTrue();
		StartsWithMark(path).ShouldBeFalse("re-reading a file off disk is no reason to mark it");
	}

	/// <summary>
	/// Splitting a file writes both halves from text it built itself, so neither half has an
	/// encoding unless the move carries one across. The type moves; the mark is not the type's to
	/// take with it or to leave behind.
	/// </summary>
	[Test]
	public async Task Both_halves_of_a_split_keep_the_mark_the_file_had()
	{
		using var fixture = FixtureSolution.Copy("MultiType", "MultiType.slnx");

		var source = fixture.Path("MultiType", "Shapes", "Shapes.cs");
		Rewrite(source, Encoding.UTF8);

		await using var session = await TestSession.OpenAsync(fixture);

		var request = new MoveTypeRequest { FilePath = source, Symbol = "Circle", Apply = true };
		var moved = await session.MutateAsync(
			(snapshot, token) => MoveTypeService.MoveAsync(snapshot, request, session.NoteSelfWrite, token),
			Token);

		moved.Applied.ShouldBeTrue();
		StartsWithMark(source).ShouldBeTrue("the file the type left keeps its mark");
		StartsWithMark(moved.TargetPath!).ShouldBeTrue("the file the type landed in is the same file's encoding");
	}

	private static CancellationToken Token => TestContext.Current!.Execution.CancellationToken;

	/// <summary>The same file in <paramref name="encoding"/>, content untouched.</summary>
	private static void Rewrite(string path, Encoding encoding) =>
		File.WriteAllText(path, File.ReadAllText(path), encoding);

	private static bool StartsWithMark(string path) =>
		File.ReadAllBytes(path).AsSpan().StartsWith(Encoding.UTF8.GetPreamble());
}
