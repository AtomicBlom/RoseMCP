using System.Text;

using RoseMcp.Symbols;

namespace RoseMcp.UnitTests;

/// <summary>
/// Reading the source a PDB points at, so a method can be shown and a line in it picked.
/// <para>
/// The absent case carries the weight. A PDB records the path of the machine that built the module,
/// so anything out of a package or off a build agent names a directory this machine has never had --
/// and a picker that showed an empty listing for it would read as a method with no code in it.
/// </para>
/// </summary>
public sealed class SourceLinesTests
{
	[Test]
	public void The_lines_of_a_method_come_back_with_a_little_either_side()
	{
		using var file = TemporaryFile.Holding("one", "two", "three", "four", "five", "six");

		var excerpt = SourceLines.Read(file.Path, firstLine: 3, lastLine: 4, context: 1);

		Assert.Equal(2, excerpt.FirstLine);
		Assert.Equal(["two", "three", "four", "five"], excerpt.Lines);
		Assert.Null(excerpt.Problem);
		Assert.True(excerpt.HasText);
	}

	/// <summary>Context that would run off either end is clamped rather than refused.</summary>
	[Test]
	public void A_method_at_the_edges_of_a_file_reads_as_much_as_there_is()
	{
		using var file = TemporaryFile.Holding("one", "two", "three");

		var excerpt = SourceLines.Read(file.Path, firstLine: 1, lastLine: 3, context: 5);

		Assert.Equal(1, excerpt.FirstLine);
		Assert.Equal(["one", "two", "three"], excerpt.Lines);
	}

	/// <summary>
	/// The case a breakpoint still has to work in. It says which path it looked for and why that path
	/// is one this machine may never have had, because the reader's next question is whether they
	/// have done something wrong.
	/// </summary>
	[Test]
	public void A_file_the_build_machine_had_and_this_one_does_not_says_so()
	{
		var path = Path.Combine(Path.GetTempPath(), $"rose-absent-{Guid.NewGuid():n}", "Widget.cs");

		var excerpt = SourceLines.Read(path, firstLine: 10, lastLine: 20);

		Assert.False(excerpt.HasText);
		Assert.NotNull(excerpt.Problem);
		Assert.Contains(path, excerpt.Problem!);
		Assert.Contains("built", excerpt.Problem!);
	}

	/// <summary>
	/// A file of the right name and the wrong content is the dangerous one: showing its line 40 as
	/// the method's would put the reader's click on code that is not what is running.
	/// </summary>
	[Test]
	public void A_file_too_short_for_the_line_the_symbols_name_is_refused()
	{
		using var file = TemporaryFile.Holding("one", "two");

		var excerpt = SourceLines.Read(file.Path, firstLine: 40, lastLine: 45);

		Assert.False(excerpt.HasText);
		Assert.Contains("not the one the module was built from", excerpt.Problem!);
	}

	[Test]
	public void A_method_with_no_recorded_file_says_there_is_none()
	{
		var excerpt = SourceLines.Read(string.Empty, firstLine: 1, lastLine: 2);

		Assert.False(excerpt.HasText);
		Assert.Equal("The symbols name no file for this method.", excerpt.Problem);
	}

	private sealed class TemporaryFile : IDisposable
	{
		private TemporaryFile(string path) => Path = path;

		internal string Path { get; }

		internal static TemporaryFile Holding(params string[] lines)
		{
			var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rose-source", Guid.NewGuid().ToString("n"));
			Directory.CreateDirectory(directory);

			var path = System.IO.Path.Combine(directory, "Widget.cs");
			File.WriteAllLines(path, lines, Encoding.UTF8);

			return new TemporaryFile(path);
		}

		public void Dispose()
		{
			try
			{
				Directory.Delete(System.IO.Path.GetDirectoryName(Path)!, recursive: true);
			}
			catch (IOException)
			{
				// A temp directory left behind is litter rather than a failure.
			}
		}
	}
}
