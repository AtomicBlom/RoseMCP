using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoseMcp.UnitTests;

/// <summary>
/// One resolver for every tool that takes a symbol by name. What these pin are the addresses that
/// two resolvers would answer differently -- a positional record property, a type named for its
/// namespace, a library symbol whose last segment this solution also uses -- and that each resolves
/// the same way whichever tool asks, and is refused the same way where it should be.
/// </summary>
public sealed class SymbolResolverTests
{
	private const string Source = """
		namespace Shop
		{
			public sealed record Line(string TypeName, int Count);

			public sealed class Other
			{
				public string TypeName => "other";

				public void Add(int item)
				{
				}
			}

			public sealed class StringBuilder
			{
			}
		}

		namespace Shop.Widget
		{
			public sealed class Widget
			{
			}
		}

		namespace Shop.Gadget
		{
			public sealed class Gadget
			{
				public Gadget(int size)
				{
				}
			}
		}
		""";

	/// <summary>
	/// A positional property is declared by a parameter, which no declaration of its name exists to
	/// find. Missed, it is unreachable by name whenever anything else carries the name, from the rename
	/// that asks for it by name above all.
	/// </summary>
	[Test]
	public async Task Reaches_a_positional_record_property_whose_name_another_type_also_declares()
	{
		using var workspace = Workspace(out var solution);

		var target = await DeclarationLocator.FindSymbolAsync(solution, "Shop.Line.TypeName", null, Token);

		target.Symbol.ShouldBeAssignableTo<IPropertySymbol>();
		target.Symbol.ContainingType.Name.ShouldBe("Line");

		var read = await Target("Line.TypeName").ResolveAsync(Snapshot(solution), Token, includeMetadata: true);

		read.ShouldBe(target.Symbol, SymbolEqualityComparer.Default);
	}

	/// <summary>A bare name reaches it too, and so is ambiguous with the other declaration of the name.</summary>
	[Test]
	public async Task Counts_a_positional_property_among_the_declarations_of_a_bare_name()
	{
		using var workspace = Workspace(out var solution);

		var refusal = await Should.ThrowAsync<ArgumentException>(
			() => DeclarationLocator.FindSymbolAsync(solution, "TypeName", null, Token));

		refusal.Message.ShouldContain("Shop.Line.TypeName", Case.Sensitive);
		refusal.Message.ShouldContain("Shop.Other.TypeName", Case.Sensitive);
	}

	/// <summary>
	/// The only declaration of a positional property is the record around it, and writing over that in
	/// the property's name would replace the whole record. Refused, naming the tools that do reach it.
	/// </summary>
	[Test]
	public async Task Refuses_to_write_over_a_positional_property_and_says_what_reaches_it()
	{
		using var workspace = Workspace(out var solution);

		var refusal = await Should.ThrowAsync<ArgumentException>(
			() => DeclarationLocator.FindMemberAsync(solution, "Shop.Line.TypeName", null, Token));

		refusal.Message.ShouldContain("positional property", Case.Sensitive);
		refusal.Message.ShouldContain("rose_rename_symbol", Case.Sensitive);
	}

	/// <summary>
	/// A type named for the namespace it is in. Read only as a constructor, its qualified name is
	/// answered with "declares no constructor ... add one", which is advice about a type nobody asked
	/// about.
	/// </summary>
	[Test]
	[Arguments("Shop.Widget.Widget")]
	[Arguments("Widget.Widget")]
	public async Task Reads_a_repeated_last_segment_as_a_type_when_that_is_what_is_there(string requested)
	{
		using var workspace = Workspace(out var solution);

		var target = await DeclarationLocator.FindSymbolAsync(solution, requested, null, Token);

		target.Symbol.ShouldBeAssignableTo<INamedTypeSymbol>();
		(await DeclarationLocator.FindTypeAsync(solution, requested, null, Token)).Symbol.Name.ShouldBe("Widget");
	}

	/// <summary>
	/// Where both readings are there, which is a type named for its namespace that declares a
	/// constructor, the shorter name is refused and says how to write each.
	/// </summary>
	[Test]
	public async Task Refuses_a_name_that_is_both_a_type_and_its_constructor_and_says_how_to_write_each()
	{
		using var workspace = Workspace(out var solution);

		var refusal = await Should.ThrowAsync<ArgumentException>(
			() => DeclarationLocator.FindSymbolAsync(solution, "Gadget.Gadget", null, Token));

		refusal.Message.ShouldContain("as a type and as that type's constructor", Case.Sensitive);
		refusal.Message.ShouldContain("Gadget..ctor", Case.Sensitive);
	}

	/// <summary>And each spelling the refusal recommends resolves on its own.</summary>
	[Test]
	[Arguments("Shop.Gadget.Gadget", SymbolKind.NamedType)]
	[Arguments("Gadget..ctor", SymbolKind.Method)]
	[Arguments("Gadget.Gadget(int)", SymbolKind.Method)]
	[Arguments("Shop.Gadget.Gadget.Gadget", SymbolKind.Method)]
	public async Task Resolves_each_reading_written_so_only_it_fits(string requested, SymbolKind kind)
	{
		using var workspace = Workspace(out var solution);

		(await DeclarationLocator.FindSymbolAsync(solution, requested, null, Token)).Symbol.Kind.ShouldBe(kind);
	}

	/// <summary>
	/// The constructor refusal names the reading it set aside, so a caller who meant a type is not told
	/// to add a constructor without being told that nothing is a type there either.
	/// </summary>
	[Test]
	public async Task Says_the_type_reading_found_nothing_when_refusing_a_constructor()
	{
		using var workspace = Workspace(out var solution);

		var refusal = await Should.ThrowAsync<ArgumentException>(
			() => DeclarationLocator.FindSymbolAsync(solution, "Shop.StringBuilder.StringBuilder", null, Token));

		refusal.Message.ShouldContain("'StringBuilder' declares no constructor", Case.Sensitive);
		refusal.Message.ShouldContain("Read as a type instead", Case.Sensitive);
	}

	/// <summary>
	/// A library symbol is reached by its address whatever this solution declares under its last
	/// segment: a Shop.StringBuilder with no constructor of its own says nothing about
	/// System.Text.StringBuilder's, and an Add declared here is not List.Add.
	/// </summary>
	[Test]
	[Arguments("System.Text.StringBuilder.StringBuilder()", ".ctor")]
	[Arguments("System.Text.StringBuilder", "StringBuilder")]
	[Arguments("System.Collections.Generic.List.Add", "Add")]
	[Arguments("Text.StringBuilder.AppendLine(string)", "AppendLine")]
	public async Task Reaches_a_referenced_assembly_whatever_source_declares_under_the_last_segment(
		string requested,
		string name)
	{
		using var workspace = Workspace(out var solution);

		var symbol = await Target(requested).ResolveAsync(Snapshot(solution), Token, includeMetadata: true);

		symbol.Name.ShouldBe(name);
		symbol.Locations.ShouldAllBe(location => location.IsInMetadata);
	}

	/// <summary>
	/// A bare name source carries is answered from source, where it is the caller's own code being
	/// named. The library's Add is one qualification away; picking it here would answer about
	/// somebody else's method.
	/// </summary>
	[Test]
	public async Task Answers_a_bare_name_from_source_when_source_carries_it()
	{
		using var workspace = Workspace(out var solution);

		var symbol = await Target("Add").ResolveAsync(Snapshot(solution), Token, includeMetadata: true);

		symbol.ContainingType.Name.ShouldBe("Other");
	}

	/// <summary>A refusal after a metadata search says it ran, so "not in your source" reads differently from "not anywhere".</summary>
	[Test]
	public async Task Says_when_a_referenced_assembly_was_searched_and_held_nothing()
	{
		using var workspace = Workspace(out var solution);

		var refusal = await Should.ThrowAsync<SymbolNotFoundException>(
			() => Target("System.Text.Nowhere.Add").ResolveAsync(Snapshot(solution), Token, includeMetadata: true));

		refusal.Message.ShouldContain("Nothing is declared at 'System.Text.Nowhere.Add'", Case.Sensitive);
		refusal.Message.ShouldContain("Nothing in a referenced assembly is declared there either", Case.Sensitive);
	}

	/// <summary>A write never answers from metadata, however the library spells it: there is no file to edit there.</summary>
	[Test]
	public async Task Does_not_search_a_referenced_assembly_for_a_write()
	{
		using var workspace = Workspace(out var solution);

		var refusal = await Should.ThrowAsync<SymbolNotFoundException>(
			() => DeclarationLocator.FindMemberAsync(solution, "System.Text.StringBuilder.AppendLine", null, Token));

		refusal.Message.ShouldNotContain("referenced assembly is declared there", Case.Sensitive);
	}

	/// <summary>
	/// The resolver is the only thing in the worker that asks a declaration index or a compilation
	/// what a name is. A tool that asked for itself would answer some address differently from every
	/// other tool, which is how a positional property came to be readable and not renameable.
	/// </summary>
	[Test]
	public void No_tool_looks_a_name_up_except_through_the_resolver()
	{
		string[] lookups =
		[
			"Microsoft.CodeAnalysis.FindSymbols.SymbolFinder.FindSourceDeclarationsAsync",
			"Microsoft.CodeAnalysis.FindSymbols.SymbolFinder.FindDeclarationsAsync",
			"Microsoft.CodeAnalysis.Compilation.GetSymbolsWithName",
			"Microsoft.CodeAnalysis.Compilation.GetTypesByMetadataName",
			"Microsoft.CodeAnalysis.Compilation.GetTypeByMetadataName",
		];

		string[] resolver = [typeof(SymbolResolver).FullName!, typeof(MetadataSymbols).FullName!];

		var calls = MethodCalls.In(typeof(SymbolResolver).Assembly)
			.Where(call => lookups.Contains(call.Callee, StringComparer.Ordinal))
			.ToArray();

		calls.ShouldContain(call => call.Owner == typeof(SymbolResolver).FullName, "the check sees the resolver itself");

		calls.Where(call => !resolver.Contains(call.Owner, StringComparer.Ordinal))
			.Select(call => $"{call.Owner} calls {call.Callee}")
			.Distinct(StringComparer.Ordinal)
			.ShouldBeEmpty("a name is looked up through SymbolResolver and nothing else");
	}

	private static CancellationToken Token => TestContext.Current!.Execution.CancellationToken;

	private static SymbolTarget Target(string symbol) => new() { Symbol = symbol };

	private static WorkspaceSnapshot Snapshot(Solution solution) => new() { Solution = solution, Revision = 1 };

	/// <summary>One project holding <see cref="Source"/> in a file on a path, referencing the framework.</summary>
	private static AdhocWorkspace Workspace(out Solution solution)
	{
		var workspace = new AdhocWorkspace();
		var projectId = ProjectId.CreateNewId();
		var path = Path.Combine(Path.GetTempPath(), "SymbolResolverTests", "Shop.cs");

		solution = workspace.CurrentSolution
			.AddProject(projectId, "Shop", "Shop", LanguageNames.CSharp)
			.AddMetadataReference(projectId, MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
			.AddMetadataReference(projectId, MetadataReference.CreateFromFile(typeof(List<>).Assembly.Location))
			.AddDocument(DocumentId.CreateNewId(projectId), "Shop.cs", SourceText.From(Source), filePath: path);

		return workspace;
	}
}
