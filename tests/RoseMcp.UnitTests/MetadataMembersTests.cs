using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RoseMcp.Broker.Tools;
using RoseMcp.Contracts;
using RoseMcp.Worker.Tools;

namespace RoseMcp.UnitTests;

/// <summary>
/// What rose_symbol_info lists for a type from a referenced assembly: the members a caller outside
/// that assembly can use, and nothing a caller cannot.
/// <para>
/// The library is compiled here and handed to the solution as an image rather than as a project, so
/// every type in it is a metadata type the way a package's is, and the accessibility and
/// obsolescence the assertions are about are written beside them rather than being whatever some
/// framework version happens to ship.
/// </para>
/// </summary>
public sealed class MetadataMembersTests
{
	private const string Library = """
		using System;

		namespace Shop;

		public class Till
		{
			public Till() { }

			protected Till(int float_) { }

			public string Ring(string item) => item;

			public string Ring(string item, int pence) => item;

			internal void Audit() { }

			private void Count() { }

			protected void Balance() { }

			protected internal void Close() { }

			private protected void Reconcile() { }

			[Obsolete("Use Ring.")]
			public void Charge() { }

			[Obsolete("Gone.", true)]
			public void Swipe() { }

			public int Total { get; set; }

			public string this[int slot] => "";

			public event EventHandler? Opened;

			public static Till operator +(Till left, Till right) => left;

			public Func<int> Counter()
			{
				var count = 0;
				return () => ++count;
			}

			public class Drawer { }

			internal class Ledger { }
		}

		public class Receipt
		{
			public required string Number { get; init; }

			public ref struct Slip { }

			[Shop.System.Obsolete]
			public void Reprint() { }
		}

		public record Sale(string Item);
		""";

	/// <summary>
	/// An attribute named ObsoleteAttribute in a namespace whose last segment is System, which the
	/// compiler does not treat as obsolescence and so neither may the listing.
	/// </summary>
	private const string Lookalike = """
		namespace Shop.System;

		public sealed class ObsoleteAttribute : global::System.Attribute { }
		""";

	[Test]
	public async Task Lists_what_code_outside_the_assembly_can_use_and_nothing_else()
	{
		var info = await DescribeAsync("Shop.Till");

		info.IsFromSource.ShouldBeFalse();
		var names = info.Members.ShouldNotBeNull().Select(member => member.Name).ToArray();

		names.ShouldBe(
			[".ctor", ".ctor", "Ring", "Ring", "Balance", "Close", "Charge", "Swipe", "Total", "this[]", "Opened", "op_Addition", "Counter", "Drawer"],
			ignoreOrder: true);

		// Internal, private and private protected are the assembly's own business, and a property's or
		// an event's accessors are the property and the event.
		names.ShouldNotContain("Audit");
		names.ShouldNotContain("Count");
		names.ShouldNotContain("Reconcile");
		names.ShouldNotContain("Ledger");
		names.ShouldNotContain("get_Total");
		names.ShouldNotContain("add_Opened");

		info.TotalMembers.ShouldBe(names.Length);
		info.Truncated.ShouldBeFalse();
	}

	/// <summary>
	/// Overloads are the reason signatures are on: with no line to tell them apart, two entries named
	/// Ring say nothing about what either takes.
	/// </summary>
	[Test]
	public async Task Gives_each_member_its_signature_so_overloads_differ()
	{
		var info = await DescribeAsync("Shop.Till");

		var rings = info.Members.ShouldNotBeNull().Where(member => member.Name == "Ring").Select(member => member.Signature).ToArray();

		rings.ShouldBe(["string Shop.Till.Ring(string item)", "string Shop.Till.Ring(string item, int pence)"], ignoreOrder: true);
		info.Members!.ShouldAllBe(member => member.Line == null && member.FilePath == null);

		var balance = info.Members!.Single(member => member.Name == "Balance");
		balance.Accessibility.ShouldBe("Protected");

		info.Members!.Single(member => member.Name == "op_Addition").IsStatic.ShouldBeTrue();
	}

	/// <summary>
	/// Obsolete as a warning and obsolete as an error cost different things -- the first fails only a
	/// build that treats warnings as errors, the second fails every build -- so the two are told apart,
	/// and a member that is neither says nothing.
	/// </summary>
	[Test]
	public async Task Says_which_members_are_obsolete_and_how()
	{
		var info = await DescribeAsync("Shop.Till");
		var members = info.Members.ShouldNotBeNull();

		members.Single(member => member.Name == "Charge").Obsolete.ShouldBe("warning");
		members.Single(member => member.Name == "Swipe").Obsolete.ShouldBe("error");
		members.Where(member => member.Name == "Ring").ShouldAllBe(member => member.Obsolete == null);

		// An attribute that only shares System.ObsoleteAttribute's name and last namespace segment.
		var receipt = await DescribeAsync("Shop.Receipt");
		receipt.Members.ShouldNotBeNull().Single(member => member.Name == "Reprint").Obsolete.ShouldBeNull();
	}

	/// <summary>
	/// The compiler marks a constructor of a type with required members, and a ref struct, obsolete as
	/// an error for the benefit of compilers too old to understand them, and a current one ignores the
	/// mark. Roslyn leaves it out of the attributes it reads back from metadata, which is what lets the
	/// obsolete flag read them as they are; this holds it there, since reporting the mark would call
	/// something every current build accepts unusable.
	/// </summary>
	[Test]
	public async Task Does_not_call_a_member_obsolete_for_a_mark_only_older_compilers_heed()
	{
		var info = await DescribeAsync("Shop.Receipt");
		var members = info.Members.ShouldNotBeNull();

		members.Single(member => member.Name == ".ctor").Obsolete.ShouldBeNull();
		members.Single(member => member.Name == "Slip").Obsolete.ShouldBeNull();
	}

	/// <summary>
	/// A record's members the compiler wrote are listed where a caller can name them, and left out
	/// where only the compiler can.
	/// </summary>
	[Test]
	public async Task Lists_a_records_callable_members_and_not_the_ones_no_source_can_name()
	{
		var info = await DescribeAsync("Shop.Sale");

		var names = info.Members.ShouldNotBeNull().Select(member => member.Name).ToArray();

		names.ShouldContain("Item");
		names.ShouldContain("Deconstruct");
		names.ShouldContain("ToString");
		names.ShouldNotContain("<Clone>$");
	}

	[Test]
	public async Task Narrows_by_name_and_says_so_when_nothing_matched()
	{
		var rings = await DescribeAsync("Shop.Till", members: "ring");

		rings.Members.ShouldNotBeNull().Select(member => member.Name).ShouldBe(["Ring", "Ring"]);
		rings.TotalMembers.ShouldBe(2);
		rings.Notices.ShouldBeEmpty();

		var none = await DescribeAsync("Shop.Till", members: "Refund");

		none.Members.ShouldNotBeNull().ShouldBeEmpty();
		none.Notices.ShouldHaveSingleItem().ShouldContain("No member's name contains 'Refund'", Case.Sensitive);
	}

	/// <summary>A capped list says it is capped, with the total it stopped short of.</summary>
	[Test]
	public async Task Stops_at_the_cap_and_says_how_many_it_left_out()
	{
		var info = await DescribeAsync("Shop.Till", maxMembers: 3);

		info.Members.ShouldNotBeNull().Count.ShouldBe(3);
		info.TotalMembers.ShouldBe(14);
		info.Truncated.ShouldBeTrue();
		info.Notices.ShouldHaveSingleItem().ShouldContain("Listed 3 of 14 members, stopping at maxMembers=3", Case.Sensitive);
	}

	/// <summary>
	/// A type declared in source is rose_outline's, so it gets no listing here -- and a caller who asked
	/// for one is told where it is rather than handed nothing.
	/// </summary>
	[Test]
	public async Task Lists_nothing_for_a_source_type_and_says_where_its_members_are()
	{
		var info = await DescribeAsync("Caller.Use", members: "Run");

		info.IsFromSource.ShouldBeTrue();
		info.Members.ShouldBeNull();
		info.TotalMembers.ShouldBeNull();
		info.Notices.ShouldHaveSingleItem().ShouldContain("rose_outline lists its members", Case.Sensitive);
	}

	/// <summary>And a member has no members: asking about one lists nothing, without a notice unless the caller narrowed.</summary>
	[Test]
	public async Task Lists_nothing_for_a_member()
	{
		var info = await DescribeAsync("Shop.Till.Charge");

		info.Members.ShouldBeNull();
		info.Notices.ShouldBeEmpty();
	}

	/// <summary>
	/// The broker forwards every argument, so the worker's defaults are reached only by a worker run on
	/// its own -- which is exactly where a drift between the two would go unnoticed.
	/// </summary>
	[Test]
	public void The_worker_defaults_every_argument_the_way_the_broker_does()
	{
		var broker = Defaults(typeof(BrokerAnalysisTools));
		var worker = Defaults(typeof(NavigationTools));

		broker.Remove("workspace");

		worker.Keys.Order().ShouldBe(broker.Keys.Order());

		foreach (var (name, value) in broker)
		{
			worker[name].ShouldBe(value, $"rose_symbol_info's {name} defaults differently in the worker");
		}

		broker["maxMembers"].ShouldBe(OutlineService.DefaultMaxMembers);
	}

	private static Dictionary<string, object?> Defaults(Type tools)
	{
		var method = tools.GetMethod("SymbolInfoAsync").ShouldNotBeNull();

		return method.GetParameters()
			.Where(parameter => parameter.HasDefaultValue && parameter.ParameterType != typeof(CancellationToken))
			.ToDictionary(parameter => parameter.Name!, parameter => parameter.DefaultValue);
	}

	private static async Task<SymbolInfoResult> DescribeAsync(
		string symbol,
		string? members = null,
		int maxMembers = OutlineService.DefaultMaxMembers)
	{
		using var workspace = new AdhocWorkspace();
		var snapshot = new WorkspaceSnapshot { Solution = Solution(workspace), Revision = 1 };

		return await NavigationService.DescribeAsync(
			snapshot, new SymbolTarget { Symbol = symbol }, Token, members: members, maxMembers: maxMembers);
	}

	/// <summary>
	/// One project referencing <see cref="Library"/> as a compiled image, so its types are read from
	/// metadata, and declaring one type of its own.
	/// </summary>
	private static Solution Solution(AdhocWorkspace workspace)
	{
		var framework = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);

		var library = CSharpCompilation.Create(
			"Shop",
			[
				CSharpSyntaxTree.ParseText(Library, new CSharpParseOptions(LanguageVersion.Latest)),
				CSharpSyntaxTree.ParseText(Lookalike, new CSharpParseOptions(LanguageVersion.Latest)),
			],
			[framework],
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

		using var image = new MemoryStream();
		var emitted = library.Emit(image, cancellationToken: Token);

		emitted.Success.ShouldBeTrue(string.Join(Environment.NewLine, emitted.Diagnostics));

		var projectId = ProjectId.CreateNewId();

		return workspace.CurrentSolution
			.AddProject(projectId, "Caller", "Caller", LanguageNames.CSharp)
			.AddMetadataReference(projectId, framework)
			.AddMetadataReference(projectId, MetadataReference.CreateFromImage(image.ToArray()))
			.AddDocument(
				DocumentId.CreateNewId(projectId),
				"Use.cs",
				"namespace Caller;\n\npublic class Use\n{\n\tpublic void Run() { }\n}\n",
				filePath: Path.Combine(Path.GetTempPath(), "Caller", "Use.cs"));
	}

	private static CancellationToken Token => TestContext.Current!.Execution.CancellationToken;
}
