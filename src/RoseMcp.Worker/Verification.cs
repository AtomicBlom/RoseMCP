using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>What compiling after an edit found, or that nothing was compiled.</summary>
public sealed record Verification
{
	public static readonly Verification NotRun = new();

	/// <summary>
	/// False when no compilation happened. Kept separate from an empty introduced list because the
	/// two look identical to a caller and mean opposite things.
	/// </summary>
	public bool Ran { get; init; }

	/// <summary>Errors that exist now and did not before.</summary>
	public IReadOnlyList<DiagnosticEntry> Introduced { get; init; } = [];

	/// <summary>Errors that existed before and do not now.</summary>
	public int ResolvedCount { get; init; }

	/// <summary>Errors the checked projects reported before the edit and still report, whoever caused them.</summary>
	public int PreexistingCount { get; init; }

	/// <summary>
	/// How many of <see cref="PreexistingCount"/> an analyzer reported rather than the compiler or a
	/// generator. Counted because the write ran analyzers where it wrote and <c>rose_diagnostics</c> leaves
	/// them out unless asked, so without it a count of errors beside that tool's answer of none reads as
	/// the two tools disagreeing.
	/// </summary>
	public int PreexistingAnalyzerCount { get; init; }

	/// <summary>
	/// The sentence for <see cref="PreexistingAnalyzerCount"/>, or null where it is zero: the one case the
	/// count of errors already there needs explaining is when part of it is a kind another tool hides.
	/// </summary>
	public string? PreexistingAdvice()
	{
		if (PreexistingAnalyzerCount == 0) return null;

		var what = PreexistingAnalyzerCount == 1 ? "is an analyzer error" : "are analyzer errors";

		return $"{PreexistingAnalyzerCount} of the {PreexistingCount} errors already there {what}, which "
			+ "rose_diagnostics leaves out unless includeAnalyzers=true.";
	}

	/// <summary>Whether the checked projects report no error at all now, this edit's or anyone else's.</summary>
	public bool HasNoErrors => Introduced.Count == 0 && PreexistingCount == 0;

	/// <summary>The projects that were compiled.</summary>
	public IReadOnlyList<string> Projects { get; init; } = [];

	/// <summary>
	/// The projects whose analyzers ran, which is where an IDE0055 or an IDE0005 comes from. A subset
	/// of <see cref="Projects"/>: the analyzers are run where the edit wrote, and everything else in
	/// scope is compiled without them.
	/// </summary>
	public IReadOnlyList<string> AnalyzedProjects { get; init; } = [];

	/// <summary>
	/// What the verification itself has to say, as opposed to what it found. Written here rather than
	/// by each write tool so a tool added later cannot promise a check that did not happen.
	/// </summary>
	public IReadOnlyList<string> Notices { get; init; } = [];

	/// <summary>
	/// What would import the names that did not resolve, one line each.
	/// <para>
	/// Computed here rather than by each caller so a write tool added later cannot forget it. The
	/// compilation this reads has just been built to work out what the edit broke, so the answer
	/// is a lookup rather than work.
	/// </para>
	/// </summary>
	public IReadOnlyList<string> Suggestions { get; init; } = [];

	/// <summary>
	/// The .editorconfig and .globalconfig files on disk that apply to a file the edit wrote and that its project
	/// was never given, so the compile ran under Roslyn's default severities where the build will not. Each is
	/// named in <see cref="Notices"/>.
	/// </summary>
	public IReadOnlyList<string> UnreadConfigs { get; init; } = [];

	/// <summary>
	/// The sentence for a compile that found no errors in <paramref name="compiled"/>, which is not the build's
	/// answer while <see cref="UnreadConfigs"/> holds anything: a rule one of them raises to an error was not
	/// applied, and saying clean without that is the confident wrong answer verifying exists to prevent.
	/// </summary>
	public string Clean(string compiled) =>
		UnreadConfigs.Count == 0
			? $"{compiled} compiles clean."
			: $"{compiled} compiles clean without the {UnreadConfigs.Count} analyzer config file(s) named above, so a "
				+ "build that reads them can still fail.";
}
