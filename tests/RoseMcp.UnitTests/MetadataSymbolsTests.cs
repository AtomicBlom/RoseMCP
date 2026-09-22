using Microsoft.CodeAnalysis;

namespace RoseMcp.UnitTests;

/// <summary>
/// Resolving a name to a symbol in metadata, and the one thing the resolver promises not to do:
/// choose between candidates.
/// <para>
/// Overloads are the case where choosing is invisible. Two candidates in different assemblies at
/// least look different in any error that names them, but eight overloads of one method share a
/// name, a containing type and an assembly, so a resolver keyed on those reports one candidate and
/// discards the rest with nothing to show it happened. The read that follows then answers
/// confidently about an overload nobody asked for -- and "0 references" is a well-formed answer
/// that says nothing about the question having been ambiguous.
/// </para>
/// </summary>
public sealed class MetadataSymbolsTests
{
	private const string Source = """
		namespace Shop;

		public class Till
		{
			public string Ring(string item) => item;

			public string Ring(string item, int pence) => item;

			public string Wrap() => "wrapped";
		}
		""";

	/// <summary>
	/// The reported bug in miniature: a name matching two overloads has to refuse and name both,
	/// rather than return whichever the enumeration reached first.
	/// </summary>
	[Test]
	public async Task Refuses_a_name_that_matches_two_overloads_and_names_both()
	{
		using var workspace = Workspace(out var solution);

		var failure = await Assert.ThrowsAsync<ArgumentException>(
			() => MetadataSymbols.FindAsync(solution, SymbolAddress.Parse("Shop.Till.Ring"), Token));

		Assert.Contains("2 different symbols", failure.Message, StringComparison.Ordinal);
		Assert.Contains("Shop.Till.Ring(string)", failure.Message, StringComparison.Ordinal);
		Assert.Contains("Shop.Till.Ring(string, int)", failure.Message, StringComparison.Ordinal);
		Assert.Contains("Qualify it further", failure.Message, StringComparison.Ordinal);
	}

	/// <summary>
	/// And the refusal is not the tool giving up: what it lists is what settles the question, so each
	/// candidate it names has to be an address that resolves on its own.
	/// </summary>
	[Test]
	[Arguments("Shop.Till.Ring(string)")]
	[Arguments("Shop.Till.Ring(string, int)")]
	public async Task Resolves_an_overload_the_refusal_named(string address)
	{
		using var workspace = Workspace(out var solution);

		var found = await MetadataSymbols.FindAsync(solution, SymbolAddress.Parse(address), Token);

		Assert.NotNull(found);
		Assert.Equal("Ring", found.Name);
	}

	/// <summary>A name matching exactly one member still resolves, refusal or no refusal.</summary>
	[Test]
	public async Task Resolves_a_name_that_matches_one_member()
	{
		using var workspace = Workspace(out var solution);

		var found = await MetadataSymbols.FindAsync(solution, SymbolAddress.Parse("Shop.Till.Wrap"), Token);

		Assert.NotNull(found);
		Assert.Equal("Wrap", found.Name);
	}

	/// <summary>
	/// The reported case itself, against the real framework rather than a fixture, because the count
	/// of overloads is the framework's business and the promise is only that it does not pick one.
	/// </summary>
	[Test]
	public async Task Refuses_an_overloaded_framework_method_rather_than_picking_one()
	{
		using var workspace = Workspace(out var solution);

		var failure = await Assert.ThrowsAsync<ArgumentException>(
			() => MetadataSymbols.FindAsync(
				solution, SymbolAddress.Parse("System.IO.File.WriteAllTextAsync"), Token));

		Assert.Contains("different symbols", failure.Message, StringComparison.Ordinal);
		Assert.Contains("System.IO.File.WriteAllTextAsync(string, string", failure.Message, StringComparison.Ordinal);
	}

	private static CancellationToken Token => TestContext.Current!.Execution.CancellationToken;

	/// <summary>One project holding <see cref="Source"/> and referencing the framework.</summary>
	private static AdhocWorkspace Workspace(out Solution solution)
	{
		var workspace = new AdhocWorkspace();
		var projectId = ProjectId.CreateNewId();

		solution = workspace.CurrentSolution
			.AddProject(projectId, "Shop", "Shop", LanguageNames.CSharp)
			.AddMetadataReference(projectId, MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
			.AddMetadataReference(projectId, MetadataReference.CreateFromFile(typeof(File).Assembly.Location))
			.AddDocument(DocumentId.CreateNewId(projectId), "Till.cs", Source);

		return workspace;
	}
}
