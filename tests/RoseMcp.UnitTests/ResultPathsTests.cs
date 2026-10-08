using RoseMcp.Broker;
using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// A path a read answers with is relative to where the caller is standing, so that the same path sent
/// back names the same file: the round trip is the property, and the shorter answer is what it buys.
/// </summary>
public sealed class ResultPathsTests
{
	private static readonly string Origin = Path.Combine(Path.GetTempPath(), "checkout");

	[Test]
	public void Shortens_a_path_under_the_callers_directory_to_one_that_resolves_back_to_it()
	{
		var file = Path.Combine(Origin, "src", "Library", "Greeter.cs");

		var relative = ResultPaths.Relative(file, Origin);

		relative.ShouldBe("src/Library/Greeter.cs");
		RootedPath.From(relative, Origin)!.Value.ShouldBe(file);
	}

	[Test]
	public void Leaves_a_path_outside_the_callers_directory_absolute()
	{
		var sibling = Path.Combine(Path.GetTempPath(), "checkout-two", "src", "A.cs");
		var above = Path.Combine(Path.GetTempPath(), "A.cs");

		ResultPaths.Relative(sibling, Origin).ShouldBe(sibling);
		ResultPaths.Relative(above, Origin).ShouldBe(above);
		ResultPaths.Relative(Origin, Origin).ShouldBe(Origin);
	}

	[Test]
	public void Leaves_a_path_it_cannot_measure_as_it_is()
	{
		ResultPaths.Relative("Widget.g.cs", Origin).ShouldBe("Widget.g.cs");
	}

	[Test]
	public void Names_the_directory_once_where_any_path_was_shortened_and_leaves_generated_ones_alone()
	{
		var result = new ReferencesResult
		{
			Revision = 1,
			Symbol = "A.B",
			Definitions = [new SourceLocation { FilePath = Path.Combine(Origin, "A.cs"), Line = 1, Column = 1 }],
			Files =
			[
				new ReferenceFile { FilePath = Path.Combine(Origin, "B.cs"), References = [] },
				new ReferenceFile { FilePath = Path.Combine(Origin, "obj", "B.g.cs"), GeneratedHintName = "B.g.cs", References = [] },
			],
			TotalCount = 0,
			Truncated = false,
		};

		var relative = ResultPaths.RelativeTo(result, Origin);

		relative.RelativeTo.ShouldBe(Origin);
		relative.Definitions.ShouldHaveSingleItem().FilePath.ShouldBe("A.cs");
		relative.Files.Select(file => file.FilePath).ShouldBe(["B.cs", Path.Combine(Origin, "obj", "B.g.cs")]);
	}

	[Test]
	public void Names_no_directory_where_every_path_stayed_absolute()
	{
		var elsewhere = Path.Combine(Path.GetTempPath(), "other", "A.cs");
		var result = new ReferencesResult
		{
			Revision = 1,
			Symbol = "A.B",
			Definitions = [new SourceLocation { FilePath = elsewhere, Line = 1, Column = 1 }],
			Files = [],
			TotalCount = 0,
			Truncated = false,
		};

		ResultPaths.RelativeTo(result, Origin).RelativeTo.ShouldBeNull();
	}
}
