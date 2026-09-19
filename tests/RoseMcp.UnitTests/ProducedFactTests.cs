using System.Reflection;
using System.Text.RegularExpressions;

using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// A fact worth computing per item is a fact worth selecting on, quoting back, or showing.
/// <para>
/// Four reviewers found the same shape in four subsystems without looking for it: the expensive
/// half was built and the cheap half was not, and nothing failed, because a producer with no
/// consumer breaks nothing. <c>WorkspaceKey</c> is on every result and its summary says it is "fit
/// for a caller to quote back", and no argument accepts it. <c>ContainingMember</c> carries a
/// docstring naming "the question a caller actually had", and nothing filters on it.
/// <c>InfoAge</c> and <c>InstallLocation</c> are computed for a window that does not render them.
/// </para>
/// <para>
/// So a fact and its consumer have to exist together, or the fact appears below with a reason. The
/// list starts full on purpose: it is the worklist for the cards that fix each instance, each of
/// which deletes its own lines, and an empty list is the definition of done. What it guards
/// meanwhile is a <em>fifth</em> instance appearing while results are reshaped and windows gain
/// fields, which is when this happens.
/// </para>
/// <para>
/// The fourth known instance -- four hosts reporting a version nothing reads -- has no entry here,
/// because it is guarded by <see cref="HostVersionTests"/> against the party that reads it rather
/// than against a property on a record.
/// </para>
/// </summary>
public sealed class ProducedFactTests
{
	/// <summary>The windows that could show a fact computed for a person to read.</summary>
	private static readonly string[] Windows =
	[
		"src/RoseMcp.Ui.Core",
		"src/RoseMcp.Tray",
		"src/RoseMcp.Inspector",
	];

	/// <summary>
	/// Facts computed per item on an answer, which a caller should be able to narrow or quote back
	/// with. The consumer is an argument of that name anywhere on the surface: a result field and
	/// the argument that selects on it are the same word, or one of them is unreachable.
	/// </summary>
	private static readonly Type[] Selectable = [typeof(SourceLocation), typeof(WorkspaceScopedResult)];

	/// <summary>
	/// Facts computed for a person to read. The consumer is any mention in a window's own sources,
	/// which is a loose test on purpose -- it cannot tell rendering from a stray match, and it
	/// catches the failure it is for, which is a field nobody named anywhere at all.
	/// </summary>
	private static readonly Type[] Shown =
	[
		typeof(WorkspaceSummary),
		typeof(LiveAppSessionSummary),
		typeof(WorkerActivity),
		typeof(ProjectStatus),
	];

	/// <summary>
	/// Facts with no consumer, and why each is here rather than fixed. Keyed
	/// <c>Type.Property</c>, and every key is checked against the type, so renaming a property
	/// leaves a failing exemption rather than a silent one.
	/// </summary>
	private static readonly Dictionary<string, string> Unconsumed = new(StringComparer.Ordinal)
	{
		// The answer itself rather than a facet of it. Narrowing a search by column is not a
		// question anybody has.
		["SourceLocation.Line"] = "Part of the position being reported, not a dimension of it.",
		["SourceLocation.Column"] = "Part of the position being reported, not a dimension of it.",
		["SourceLocation.Preview"] = "The evidence for a hit, and already switchable by includePreviews.",

		// Card 11e: each is computed on every reference and offered as neither filter nor grouping,
		// which is why an overflow can only be answered with a bigger artefact.
		["SourceLocation.ContainingMember"] = "Card 11e: the grouping an overflow should be answered with.",
		["SourceLocation.IsTestProject"] = "Card 11e: the narrowing that separates 380 test hits from 32 real ones.",
		["SourceLocation.GeneratedHintName"] = "Card 11e: generated hits are not separable from written ones.",

		// Card 11c: sixteen characters an agent will echo, where a sixty-character absolute path is
		// what it drops.
		["WorkspaceScopedResult.WorkspaceKey"] = "Card 11c: the anchor every result carries and no argument accepts.",

		// Card 22: the cheapest wins in the repository, and the reason a window can report a healthy
		// workspace that is not one.
		["WorkspaceSummary.StartedUtc"] = "Uptime is rendered instead, and is derived from it.",
		["WorkspaceSummary.PrivateMemoryBytes"] = "Card 22: one of three memory figures, of which the window shows two.",
		["LiveAppSessionSummary.StartedUtc"] = "Card 22: no window says how long a session has been attached.",
		["LiveAppSessionSummary.InfoAge"] = "Card 22: how stale the report is, which is what makes the rest of it readable.",
		["LiveAppSessionSummary.InstallLocation"] = "Card 22: where a packaged target was installed from.",
		["WorkerActivity.StartedUtc"] = "Card 22: an activity's age, where the window shows only its name.",
		["ProjectStatus.FilePath"] = "Card 22: no window renders per-project health at all.",
		["ProjectStatus.TargetFramework"] = "Card 22: no window renders per-project health at all.",
		["ProjectStatus.LoadedSuccessfully"] = "Card 22: no window renders per-project health at all.",
		["ProjectStatus.DocumentCount"] = "Card 22: no window renders per-project health at all.",
		["ProjectStatus.AdditionalDocumentCount"] = "Card 22: no window renders per-project health at all.",
		["ProjectStatus.AnalyzerReferenceCount"] = "Card 22: no window renders per-project health at all.",
		["ProjectStatus.GeneratorCount"] = "Card 22: no window renders per-project health at all.",
		["ProjectStatus.GeneratedDocumentCount"] = "Card 22: no window renders per-project health at all.",
		["ProjectStatus.MissingAnalyzerOutputs"] = "Card 22: a generator that produced nothing, which is the degraded case.",
		["ProjectStatus.XamlMarkupCount"] = "Card 22: no window renders per-project health at all.",
		["ProjectStatus.XamlStubbedCount"] = "Card 22: no window renders per-project health at all.",
		["ProjectStatus.XamlDialect"] = "Card 22: no window renders per-project health at all.",
		["ProjectStatus.UnresolvedXamlTypes"] = "Card 22: the stub generator's own failures, invisible to a person.",
	};

	/// <summary>
	/// Every facet of an answer can be selected on. A result field with no argument of the same
	/// name is a dimension a caller can read and cannot ask about, so the only way to narrow an
	/// answer is to make the question smaller by hand.
	/// </summary>
	[Test]
	public void Every_facet_of_an_answer_can_be_asked_about()
	{
		var arguments = DeclaredSurface.ArgumentNames();

		foreach (var (key, property) in Facts(Selectable))
		{
			if (Unconsumed.ContainsKey(key)) continue;

			Assert.True(
				arguments.Contains(property, StringComparer.OrdinalIgnoreCase),
				$"{key} is computed on every item and no argument accepts it. Add the argument, or "
					+ "add the fact to Unconsumed with the reason nothing should.");
		}
	}

	/// <summary>
	/// Every fact computed for a window is named by one. The contract types here exist to be read
	/// by a person, and a property added to one is invisible until somebody remembers a second
	/// file -- which is how the load's own failure reasons came to be computed and never shown.
	/// </summary>
	[Test]
	public void Every_fact_computed_for_a_window_is_named_by_one()
	{
		var sources = WindowSources();

		foreach (var (key, property) in Facts(Shown))
		{
			if (Unconsumed.ContainsKey(key)) continue;

			Assert.True(
				Regex.IsMatch(sources, $@"\b{Regex.Escape(property)}\b"),
				$"{key} is computed for a window and no window names it. Show it, or add the fact "
					+ "to Unconsumed with the reason it is for an agent only.");
		}
	}

	/// <summary>
	/// Every exemption names a property that exists. A list that survives the rename of the thing
	/// it exempts stops being a worklist and becomes decoration, and this is the whole value of the
	/// list being seeded rather than empty.
	/// </summary>
	[Test]
	public void Nothing_is_exempt_for_nothing()
	{
		var known = Facts([.. Selectable, .. Shown]).Select(fact => fact.Key).ToHashSet(StringComparer.Ordinal);

		foreach (var (key, reason) in Unconsumed)
		{
			Assert.Contains(key, known);
			Assert.NotEmpty(reason);
		}
	}

	/// <summary>Each type's own properties, keyed <c>Type.Property</c>.</summary>
	private static IEnumerable<(string Key, string Property)> Facts(IEnumerable<Type> types) =>
		types.SelectMany(type => type
			.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
			.Select(property => ($"{type.Name}.{property.Name}", property.Name)));

	/// <summary>Everything the three windows are written in, as one string to search.</summary>
	private static string WindowSources() => string.Join('\n', Windows.Select(Sources));

	/// <summary>
	/// One project's sources, read from the repository rather than from the build output, so the
	/// test reads what a person edits.
	/// </summary>
	private static string Sources(string project)
	{
		var directory = Path.Combine(RepositoryRoot(), project.Replace('/', Path.DirectorySeparatorChar));

		var files = Directory
			.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
			.Where(file => file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
				|| file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
			.Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
				&& !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

		return string.Join('\n', files.Select(File.ReadAllText));
	}

	private static string RepositoryRoot()
	{
		for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
		{
			if (File.Exists(Path.Combine(directory.FullName, "RoseMcp.slnx"))) return directory.FullName;
		}

		throw new InvalidOperationException($"No RoseMcp.slnx above {AppContext.BaseDirectory}.");
	}
}
