using Microsoft.CodeAnalysis;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// Compares each project's build output against the sources it was built from.
/// <para>
/// It answers a question nothing else in the toolchain does. An agent's instinct is to run a build
/// and trust a green result, and that is exactly what does not help: in the failures this comes
/// from, the solution built fine and the binary being executed was somewhere else and older. The
/// question is not whether the code compiles but whether the thing about to run is the thing that
/// was just compiled.
/// </para>
/// <para>
/// It needs no build of its own. The design-time build already knows every project's output path
/// and every file it compiles, so this is a stat per file against one stat for the output -- the
/// same order of cost the read barrier already pays on every call.
/// </para>
/// </summary>
public static class BuildFreshness
{
	public static IReadOnlyList<ProjectFreshness> Of(
		Solution solution,
		string? project,
		CancellationToken cancellationToken)
	{
		var selected = string.IsNullOrWhiteSpace(project)
			? solution.Projects
			: solution.Projects.Where(candidate =>
				string.Equals(candidate.Name, project, StringComparison.OrdinalIgnoreCase)
					|| SamePath(candidate.FilePath, project));

		return [.. selected.Select(candidate => Describe(candidate, cancellationToken))];
	}

	private static ProjectFreshness Describe(Project project, CancellationToken cancellationToken)
	{
		if (project.OutputFilePath is not { Length: > 0 } output)
		{
			return new ProjectFreshness
			{
				Project = project.Name,
				SourcesNewerThanOutput = 0,
				Stale = false,
				Verdict = "The project declares no output assembly, so there is nothing to compare.",
			};
		}

		var written = File.Exists(output) ? File.GetLastWriteTimeUtc(output) : (DateTime?)null;
		var (newest, newestAt, newer) = Newest(project, written, cancellationToken);

		if (written is null)
		{
			return new ProjectFreshness
			{
				Project = project.Name,
				OutputPath = output,
				NewestSourcePath = newest,
				NewestSourceWrittenUtc = newestAt,
				SourcesNewerThanOutput = newer,
				Stale = true,
				Verdict = "Nothing has been built: the output is not on disk. Anything that loads it will "
					+ "either fail or find an older copy somewhere else.",
			};
		}

		if (newer == 0)
		{
			return new ProjectFreshness
			{
				Project = project.Name,
				OutputPath = output,
				OutputWrittenUtc = written,
				NewestSourcePath = newest,
				NewestSourceWrittenUtc = newestAt,
				SourcesNewerThanOutput = 0,
				Stale = false,
				Verdict = "The output is newer than every source it was built from.",
			};
		}

		return new ProjectFreshness
		{
			Project = project.Name,
			OutputPath = output,
			OutputWrittenUtc = written,
			NewestSourcePath = newest,
			NewestSourceWrittenUtc = newestAt,
			SourcesNewerThanOutput = newer,
			Stale = true,
			Verdict = $"{newer} source(s) have changed since the output was built, the most recent being "
				+ $"{Path.GetFileName(newest)}. Build before running anything from it.",
		};
	}

	/// <summary>
	/// The newest source, when it was written, and how many are newer than the output. Every file
	/// the project compiles counts, plus the project file itself -- a changed csproj changes what
	/// the assembly is even when no code moved.
	/// </summary>
	private static (string? Path, DateTime? WrittenUtc, int Newer) Newest(
		Project project,
		DateTime? output,
		CancellationToken cancellationToken)
	{
		string? newest = null;
		DateTime? newestAt = null;
		var newer = 0;

		foreach (var path in Sources(project))
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (FileStamp.For(path) is not { } stamp) continue;

			if (newestAt is null || stamp.LastWriteUtc > newestAt)
			{
				newest = path;
				newestAt = stamp.LastWriteUtc;
			}

			if (output is { } built && stamp.LastWriteUtc > built) newer++;
		}

		return (newest, newestAt, newer);
	}

	/// <summary>
	/// Every file on disk that feeds the compilation. Source-generated documents are not among them:
	/// they exist only inside the compilation and have no timestamp to compare.
	/// </summary>
	private static IEnumerable<string> Sources(Project project)
	{
		IEnumerable<TextDocument> documents =
			[.. project.Documents, .. project.AdditionalDocuments, .. project.AnalyzerConfigDocuments];

		foreach (var document in documents)
		{
			if (document.FilePath is { Length: > 0 } path) yield return path;
		}

		if (project.FilePath is { Length: > 0 } file) yield return file;
	}

	private static bool SamePath(string? candidate, string requested)
	{
		if (string.IsNullOrEmpty(candidate)) return false;

		try
		{
			return string.Equals(
				Path.GetFullPath(candidate), Path.GetFullPath(requested), StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			// A project name that is not a path at all, which is the ordinary way to name one.
			return false;
		}
	}
}
