using RoseMcp.Broker;
using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// What the activity record keeps of a call's bodies: a value cut to a length says how long it was, and a write's
/// diff is taken apart by file the way its headers name the files.
/// </summary>
public sealed class ActivityBodiesTests
{
	private static readonly string Greeter = Path.Combine(Path.GetTempPath(), "rose-activity", "Library", "Greeter.cs");

	private static readonly string Shouter = Path.Combine(Path.GetTempPath(), "rose-activity", "Library", "Shouter.cs");

	[Test]
	public void A_value_within_the_ceiling_is_kept_whole()
	{
		var (text, fullLength) = ActivityBodies.Cut("short", 10);

		text.ShouldBe("short");
		fullLength.ShouldBeNull();
	}

	[Test]
	public void A_value_past_the_ceiling_is_cut_and_says_how_long_it_was()
	{
		var (text, fullLength) = ActivityBodies.Cut(new string('x', 25), 10);

		text.ShouldBe(new string('x', 10));
		fullLength.ShouldBe(25);
	}

	/// <summary>Half a surrogate pair is a character no reader can show, so a cut ends before the pair rather than inside it.</summary>
	[Test]
	public void A_cut_never_splits_a_surrogate_pair()
	{
		var (text, fullLength) = ActivityBodies.Cut("abc\U0001F339def", 4);

		text.ShouldBe("abc");
		fullLength.ShouldBe(8);
	}

	[Test]
	public void A_writes_diff_is_taken_apart_by_file()
	{
		var diff = Header(Greeter) + "@@ -1,1 +1,1 @@\n-old\n+new\n" + Created(Shouter) + "@@ -0,0 +1,1 @@\n+class Shouter;\n";

		var parts = ActivityBodies.PerFile(diff, [Changed(Greeter), Changed(Shouter)]);

		parts[Greeter].ShouldBe(Header(Greeter) + "@@ -1,1 +1,1 @@\n-old\n+new\n");
		parts[Shouter].ShouldBe(Created(Shouter) + "@@ -0,0 +1,1 @@\n+class Shouter;\n");
	}

	/// <summary>
	/// A removed line reading <c>-- x</c> and an added one reading <c>++ x</c> sit in a diff as <c>--- x</c> and
	/// <c>+++ x</c>; only a pair naming a file the write changed starts that file's part.
	/// </summary>
	[Test]
	public void A_line_of_content_that_reads_like_a_header_stays_in_its_file()
	{
		var diff = Header(Greeter) + "@@ -1,1 +1,1 @@\n--- x\n+++ x\n";

		var parts = ActivityBodies.PerFile(diff, [Changed(Greeter)]);

		parts.ShouldHaveSingleItem().Value.ShouldBe(diff);
	}

	[Test]
	public void Each_file_is_listed_with_its_diff_and_a_file_the_diff_has_nothing_for_without_one()
	{
		var budget = ActivityBodies.CallDiffCeiling;
		var notes = new List<string>();
		var diff = Header(Greeter) + "@@ -1,1 +1,1 @@\n-old\n+new\n";

		var changes = ActivityBodies.Changes(
			[Changed(Greeter) with { Lines = "1" }, Changed(Shouter) with { Normalised = "3 line ending(s) to CRLF" }],
			diff,
			ref budget,
			notes);

		changes[0].FilePath.ShouldBe(Greeter);
		changes[0].Lines.ShouldBe("1");
		changes[0].Diff.ShouldBe(diff);
		changes[0].DiffFullLength.ShouldBeNull();

		changes[1].Diff.ShouldBeNull("only its line endings changed, which a diff cannot show");
		changes[1].Normalised.ShouldBe("3 line ending(s) to CRLF");

		budget.ShouldBe(ActivityBodies.CallDiffCeiling - diff.Length);
		notes.ShouldBeEmpty();
	}

	/// <summary>One file's diff is cut to its ceiling, and once the call's diffs together reach theirs the rest are listed with their length.</summary>
	[Test]
	public void Diffs_past_what_the_record_keeps_are_cut_and_say_how_long_they_were()
	{
		var budget = ActivityBodies.FileDiffCeiling + 10;
		var notes = new List<string>();
		var huge = Header(Greeter) + "@@ -1,1 +1,1 @@\n+" + new string('x', ActivityBodies.FileDiffCeiling) + "\n";
		var next = Header(Shouter) + "@@ -1,1 +1,1 @@\n-old\n+new\n";
		var last = Header(Shouter + ".bak") + "@@ -1,1 +1,1 @@\n-old\n+new\n";

		var changes = ActivityBodies.Changes(
			[Changed(Greeter), Changed(Shouter), Changed(Shouter + ".bak")],
			huge + next + last,
			ref budget,
			notes);

		changes[0].Diff!.Length.ShouldBe(ActivityBodies.FileDiffCeiling);
		changes[0].DiffFullLength.ShouldBe(huge.Length);

		changes[1].Diff!.Length.ShouldBe(10, "what was left of the call's ceiling");
		changes[1].DiffFullLength.ShouldBe(next.Length);

		changes[2].Diff.ShouldBeNull();
		changes[2].DiffFullLength.ShouldBe(last.Length);

		notes.Count.ShouldBe(3);
		notes[^1].ShouldContain("1 file(s) are left out", Case.Sensitive);
	}

	private static ChangedFile Changed(string path) => new() { FilePath = path };

	private static string Header(string path) => $"--- {path}\n+++ {path}\n";

	private static string Created(string path) => $"--- /dev/null\n+++ {path}\n";
}
