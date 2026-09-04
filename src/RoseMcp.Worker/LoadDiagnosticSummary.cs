using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;

namespace RoseMcp.Worker;

/// <summary>
/// The load diagnostics as something a caller can read: complaints that differ only in the file or
/// URL they name are folded into one line carrying a count.
/// <para>
/// Measured, on Drawboard's 60-project <c>DrawboardProjects.slnx</c>: the status report was 225KB, of
/// which <c>loadDiagnostics</c> was 196KB -- 87% of the answer, over the client's token cap on every
/// call, so the result always spilled to a file and cost a round trip. 509 of its 557 entries were
/// one message, NuGet's vulnerability audit failing to reach a feed, repeated once per project per
/// unreachable feed.
/// </para>
/// <para>
/// Folding rather than truncating, because those 509 lines are one fact about the solution and not
/// 509 facts. A cap would have kept forty arbitrary copies of that one fact and hidden everything
/// else behind them; folding keeps every distinct complaint and drops only the repetition. The true
/// count is reported beside it, so a folded list cannot understate what MSBuild said.
/// </para>
/// <para>
/// Ordered by where each shape first appeared rather than by how often it occurs. The noisy family is
/// rarely the interesting one -- the single unresolved reference buried among five hundred audit
/// failures is -- and sorting by count would bury it exactly as thoroughly as the raw list did.
/// </para>
/// </summary>
public static partial class LoadDiagnosticSummary
{
	/// <summary>
	/// How many distinct shapes to list. Reached only by a solution failing in dozens of unrelated
	/// ways, which is a different problem from the one this exists to solve.
	/// </summary>
	private const int MaxShapes = 40;

	private const string Placeholder = "...";

	public static IReadOnlyList<string> Summarise(IReadOnlyList<WorkspaceDiagnostic> diagnostics) =>
		Fold([.. diagnostics.Select(diagnostic => (diagnostic.Kind.ToString(), diagnostic.Message))]);

	/// <summary>
	/// The fold itself, over kind and message rather than over Roslyn's type -- which is what lets it
	/// be tested without a workspace, and is all it ever needed.
	/// </summary>
	public static IReadOnlyList<string> Fold(IReadOnlyList<(string Kind, string Message)> diagnostics)
	{
		if (diagnostics.Count == 0) return [];

		// Dictionary enumeration order is insertion order here, which is what keeps first-appearance
		// ordering below. Nothing is ever removed from it.
		var shapes = new Dictionary<string, Shape>(StringComparer.Ordinal);

		foreach (var (kind, message) in diagnostics)
		{
			var key = $"{kind} {Generalise(message)}";

			if (shapes.TryGetValue(key, out var seen))
			{
				seen.Count++;
				continue;
			}

			shapes[key] = new Shape { First = $"[{kind}] {message}", Count = 1 };
		}

		var lines = new List<string>(Math.Min(shapes.Count, MaxShapes) + 1);

		foreach (var shape in shapes.Values.Take(MaxShapes))
		{
			lines.Add(shape.Count == 1
				? shape.First
				: $"(x{shape.Count}, differing only in the paths or URLs they name) {shape.First}");
		}

		if (shapes.Count > MaxShapes)
		{
			lines.Add($"...and {shapes.Count - MaxShapes} further distinct diagnostic(s), not listed.");
		}

		return lines;
	}

	/// <summary>
	/// The message with the parts that vary between otherwise identical complaints taken out, so two
	/// of them compare equal.
	/// <para>
	/// Paths and URLs only. Anything more aggressive -- folding numbers, say -- would merge complaints
	/// that are genuinely different, and a status report that hides a distinct failure is worse than
	/// one that is merely long.
	/// </para>
	/// </summary>
	private static string Generalise(string message)
	{
		var generalised = Url().Replace(message, Placeholder);
		generalised = WindowsPath().Replace(generalised, Placeholder);

		return PosixPath().Replace(generalised, Placeholder);
	}

	private sealed class Shape
	{
		public required string First { get; init; }

		public required int Count { get; set; }
	}

	[GeneratedRegex(@"[A-Za-z][A-Za-z0-9+.-]*://[^\s'""]+")]
	private static partial Regex Url();

	[GeneratedRegex(@"(?:[A-Za-z]:|\\\\)[\\/][^\s'""]*")]
	private static partial Regex WindowsPath();

	/// <summary>
	/// Two separators at least, so an "and/or" in prose is not mistaken for a path -- this runs over
	/// text people wrote as well as over paths.
	/// </summary>
	[GeneratedRegex(@"/[^\s'""/]+/[^\s'""]*")]
	private static partial Regex PosixPath();
}
