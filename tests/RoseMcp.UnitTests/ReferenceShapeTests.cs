using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// How rose_find_references narrows and describes what it found, over references built by hand so
/// every facet can be set without a fixture that happens to have it.
/// </summary>
public sealed class ReferenceShapeTests
{
	private static readonly SourceLocation[] References =
	[
		At("C:/repo/Core/Calculator.cs", 4, "Core", "Calculator.Add"),
		At("C:/repo/Core/Calculator.cs", 9, "Core", "Calculator.Twice"),
		At("C:/repo/App/Program.cs", 2, "App", "GlobalStatement"),
		At("C:/repo/Tests/CalculatorTests.cs", 7, "Tests", "CalculatorTests.Adds", test: true),
		At("C:/repo/Tests/CalculatorTests.cs", 12, "Tests", "CalculatorTests.Adds", test: true),
		At("C:/repo/Tests/OtherTests.cs", 3, "Tests", "OtherTests.Adds", test: true),
		At("C:/repo/obj/Gen/Calculator.g.cs", 5, "Core", "Calculator.Generated", hint: "Calculator.g.cs"),
	];

	[Test]
	public void A_test_project_filter_keeps_one_side()
	{
		var tests = new ReferenceFilter { IsTestProject = true };
		var product = new ReferenceFilter { IsTestProject = false };

		References.Count(tests.Keeps).ShouldBe(3);
		References.Count(product.Keeps).ShouldBe(4);
		References.Where(tests.Keeps).ShouldAllBe(location => location.IsTestProject);
	}

	[Test]
	public void A_generated_filter_keeps_one_side()
	{
		var generated = new ReferenceFilter { IsGenerated = true };
		var written = new ReferenceFilter { IsGenerated = false };

		References.Where(generated.Keeps).ShouldHaveSingleItem().GeneratedHintName.ShouldBe("Calculator.g.cs");
		References.Count(written.Keeps).ShouldBe(6);
	}

	/// <summary>
	/// The whole <c>Type.Member</c> picks one type's member; the name alone picks it in every type,
	/// which is the form a caller has before it has seen an answer. Neither matches a member whose name
	/// merely ends the same way.
	/// </summary>
	[Test]
	public void A_member_is_matched_whole_or_by_its_name_in_every_type()
	{
		References.Count(new ReferenceFilter { ContainingMember = "CalculatorTests.Adds" }.Keeps).ShouldBe(2);
		References.Count(new ReferenceFilter { ContainingMember = "adds" }.Keeps).ShouldBe(3);
		References.Count(new ReferenceFilter { ContainingMember = "dds" }.Keeps).ShouldBe(0);
	}

	[Test]
	public void Every_condition_given_has_to_hold()
	{
		var filter = new ReferenceFilter
		{
			Projects = new HashSet<string>(["Core"], StringComparer.Ordinal),
			Project = "Core",
			IsGenerated = false,
		};

		References.Where(filter.Keeps).Select(location => location.Line).ShouldBe([4, 9]);
		filter.KeepsAll.ShouldBeFalse();
		new ReferenceFilter().KeepsAll.ShouldBeTrue();
	}

	/// <summary>
	/// What a filter that kept nothing says names every condition, so the caller can see which
	/// question had no answer.
	/// </summary>
	[Test]
	public void A_filter_describes_every_condition_it_holds()
	{
		var filter = new ReferenceFilter
		{
			Projects = new HashSet<string>(["Core"], StringComparer.Ordinal),
			Project = "Core",
			ContainingMember = "Calculator.Add",
			IsTestProject = true,
			IsGenerated = false,
		};

		var notice = ReferenceShapes.NothingKept(7, filter);

		notice.ShouldContain("in project Core", Case.Sensitive);
		notice.ShouldContain("inside a member called Calculator.Add", Case.Sensitive);
		notice.ShouldContain("in a test project", Case.Sensitive);
		notice.ShouldContain("in a written file", Case.Sensitive);
		notice.ShouldContain("all 7", Case.Sensitive);
	}

	/// <summary>
	/// The shape counts every facet, and keys each group by the value its narrowing argument takes,
	/// so a group read off one answer is a question the next call can ask.
	/// </summary>
	[Test]
	public void A_shape_counts_every_facet()
	{
		var shape = ReferenceShapes.Of(References);

		shape.Total.ShouldBe(7);
		shape.InTestProjects.ShouldBe(3);
		shape.InGeneratedCode.ShouldBe(1);

		shape.Projects.Select(project => (project.Project, project.Count, project.IsTestProject))
			.ShouldBe([("Core", 3, false), ("Tests", 3, true), ("App", 1, false)]);

		shape.Members[0].ShouldBe(new MemberReferenceCount { ContainingMember = "CalculatorTests.Adds", Count = 2 });
		shape.MemberCount.ShouldBe(6);
		shape.Members.Sum(member => member.Count).ShouldBe(7);

		foreach (var member in shape.Members)
		{
			var asked = new ReferenceFilter { ContainingMember = member.ContainingMember };
			References.Count(asked.Keeps).ShouldBe(member.Count);
		}
	}

	/// <summary>
	/// The members are the one facet with no natural bound, so a shape names the busiest and says how
	/// many there are in all.
	/// </summary>
	[Test]
	public void A_shape_names_the_busiest_members_and_counts_the_rest()
	{
		var many = Enumerable.Range(1, ReferenceShapes.NamedMembers + 5)
			.SelectMany(index => Enumerable.Repeat(index, index))
			.Select(index => At("C:/repo/Core/Wide.cs", index, "Core", $"Wide.M{index}"))
			.ToArray();

		var shape = ReferenceShapes.Of(many);

		shape.Members.Count.ShouldBe(ReferenceShapes.NamedMembers);
		shape.MemberCount.ShouldBe(ReferenceShapes.NamedMembers + 5);
		shape.Members[0].ContainingMember.ShouldBe($"Wide.M{ReferenceShapes.NamedMembers + 5}");
	}

	/// <summary>
	/// What every reference in a file shares is said once, and a file two projects compile is two
	/// groups, since the project is part of what a group says.
	/// </summary>
	[Test]
	public void References_are_listed_by_file()
	{
		var linked = At("C:/repo/Core/Calculator.cs", 4, "Core.Tests", "Calculator.Add", test: true);

		var files = ReferenceShapes.ByFile([.. References, linked], includePreviews: false);

		files.Count.ShouldBe(6);

		var calculator = files.First(file => file.FilePath == "C:/repo/Core/Calculator.cs" && file.Project == "Core");
		calculator.References.Select(site => site.Line).ShouldBe([4, 9]);
		calculator.References.ShouldAllBe(site => site.Preview == null);

		var generated = files.Single(file => file.GeneratedHintName is not null);
		generated.GeneratedHintName.ShouldBe("Calculator.g.cs");

		files.Single(file => file.Project == "Core.Tests").IsTestProject.ShouldBeTrue();
	}

	[Test]
	public void Previews_are_kept_only_when_asked_for()
	{
		ReferenceShapes.ByFile(References, includePreviews: true)
			.SelectMany(file => file.References)
			.ShouldAllBe(site => site.Preview == "preview");
	}

	/// <summary>
	/// An overflow's notice names every narrowing argument and the maxResults that would list it all,
	/// since it is read at the one moment a caller is looking for a better question.
	/// </summary>
	[Test]
	public void An_overflow_names_the_questions_that_list_fewer()
	{
		var notice = ReferenceShapes.Overflow(412, 200);

		foreach (var argument in new[] { "project", "containingMember", "isTestProject", "isGenerated", "maxResults=412" })
		{
			notice.ShouldContain(argument, Case.Sensitive);
		}
	}

	private static SourceLocation At(
		string filePath,
		int line,
		string project,
		string member,
		bool test = false,
		string? hint = null) =>
		new()
		{
			FilePath = filePath,
			Line = line,
			Column = 1,
			Preview = "preview",
			Project = project,
			ContainingMember = member,
			IsTestProject = test,
			GeneratedHintName = hint,
		};
}
