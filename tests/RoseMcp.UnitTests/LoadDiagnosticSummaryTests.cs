namespace RoseMcp.UnitTests;

/// <summary>
/// Folding the load diagnostics. Measured need: on a 60-project solution, 509 of 557 entries were one
/// message repeated once per project per unreachable NuGet feed, and that field alone was 196KB of a
/// 225KB status report -- over the client's token cap on every call.
/// </summary>
public sealed class LoadDiagnosticSummaryTests
{
	private const string Audit =
		"Msbuild failed when processing the file '{0}' with message: Error occurred while getting package "
			+ "vulnerability data: Unable to load the service index for source {1}.";

	[Fact]
	public void Folds_complaints_that_differ_only_in_the_file_they_name()
	{
		var folded = LoadDiagnosticSummary.Fold(
		[
			("Failure", Complaint(@"D:\repo\A\A.csproj", "https://feed/index.json")),
			("Failure", Complaint(@"D:\repo\B\B.csproj", "https://feed/index.json")),
			("Failure", Complaint(@"D:\repo\C\C.csproj", "https://feed/index.json")),
		]);

		var line = Assert.Single(folded);

		Assert.StartsWith("(x3,", line, StringComparison.Ordinal);

		// The example kept is a real one, verbatim, rather than a message with the paths blanked out.
		Assert.Contains(@"D:\repo\A\A.csproj", line, StringComparison.Ordinal);
	}

	/// <summary>The URL varies too, and it varies independently of the file.</summary>
	[Fact]
	public void Folds_complaints_that_differ_only_in_the_url_they_name()
	{
		var folded = LoadDiagnosticSummary.Fold(
		[
			("Failure", Complaint(@"D:\repo\A\A.csproj", "https://one/index.json")),
			("Failure", Complaint(@"D:\repo\A\A.csproj", "https://two/index.json")),
		]);

		Assert.Single(folded);
	}

	/// <summary>
	/// The whole risk of folding: a distinct failure quietly merged into the noisy family. Two
	/// different complaints about the same file must stay two lines.
	/// </summary>
	[Fact]
	public void Keeps_complaints_that_differ_in_anything_but_a_path()
	{
		var folded = LoadDiagnosticSummary.Fold(
		[
			("Failure", Complaint(@"D:\repo\A\A.csproj", "https://feed/index.json")),
			("Failure", @"The imported project 'D:\repo\A\Missing.targets' was not found."),
		]);

		Assert.Equal(2, folded.Count);
	}

	/// <summary>Kind is part of the identity: the same text as a warning and as a failure is two facts.</summary>
	[Fact]
	public void Keeps_the_same_message_reported_under_two_kinds_apart()
	{
		var folded = LoadDiagnosticSummary.Fold(
		[
			("Failure", "Something went wrong."),
			("Warning", "Something went wrong."),
		]);

		Assert.Equal(2, folded.Count);
	}

	/// <summary>
	/// Ordered by where each shape first appeared, not by how often it occurs. The one unresolved
	/// reference among five hundred audit failures is the interesting line, and sorting by count would
	/// bury it as thoroughly as the raw list did.
	/// </summary>
	[Fact]
	public void Keeps_shapes_in_the_order_they_first_appeared()
	{
		var folded = LoadDiagnosticSummary.Fold(
		[
			("Warning", "Found project reference without a matching metadata reference: A.csproj"),
			("Failure", Complaint(@"D:\repo\A\A.csproj", "https://feed/index.json")),
			("Failure", Complaint(@"D:\repo\B\B.csproj", "https://feed/index.json")),
		]);

		Assert.Equal(2, folded.Count);
		Assert.Contains("Found project reference", folded[0], StringComparison.Ordinal);
	}

	/// <summary>A message that occurs once reads exactly as it did before any of this existed.</summary>
	[Fact]
	public void Leaves_a_message_that_occurs_once_exactly_as_it_was()
	{
		var folded = LoadDiagnosticSummary.Fold([("Warning", "A lone complaint.")]);

		Assert.Equal("[Warning] A lone complaint.", Assert.Single(folded));
	}

	[Fact]
	public void Says_nothing_about_nothing()
	{
		Assert.Empty(LoadDiagnosticSummary.Fold([]));
	}

	/// <summary>
	/// Prose is not a path. Generalising too eagerly would merge complaints that differ, which is the
	/// one failure of this whole idea that has no symptom.
	/// </summary>
	[Fact]
	public void Does_not_mistake_a_slash_in_prose_for_a_path()
	{
		var folded = LoadDiagnosticSummary.Fold(
		[
			("Warning", "The and/or setting is ignored."),
			("Warning", "The either/neither setting is ignored."),
		]);

		Assert.Equal(2, folded.Count);
	}

	/// <summary>Posix paths fold too, so this reads the same on Linux as it does here.</summary>
	[Fact]
	public void Folds_posix_paths_as_well_as_windows_ones()
	{
		var folded = LoadDiagnosticSummary.Fold(
		[
			("Failure", "Msbuild failed when processing the file '/home/me/repo/A/A.csproj'."),
			("Failure", "Msbuild failed when processing the file '/home/me/repo/B/B.csproj'."),
		]);

		Assert.Single(folded);
	}

	[Fact]
	public void Caps_the_shapes_it_lists_and_says_how_many_it_left_out()
	{
		var many = Enumerable.Range(0, 45)
			.Select(index => ("Warning", $"Distinct complaint number {index}."))
			.ToArray();

		var folded = LoadDiagnosticSummary.Fold(many);

		Assert.Equal(41, folded.Count);
		Assert.Contains("5 further distinct diagnostic(s)", folded[^1], StringComparison.Ordinal);
	}

	private static string Complaint(string path, string url) => string.Format(null, Audit, path, url);
}
