using RoseMcp.Contracts;

namespace RoseMcp.Broker;

/// <summary>
/// The paths a read answers with, made relative to the directory the calling session runs in.
/// <para>
/// A file's absolute path repeats the workspace root on every file of an answer, and the root is the one
/// part of it the caller already has. Relative to the calling session's directory rather than to the
/// workspace, because that is the directory a relative path the caller sends back is measured from: the
/// path a result gives resolves to the same file when it is handed to the next call, with nothing else
/// sent beside it, and a caller cannot get a relative path back that means something else where it is
/// standing. Measured from the workspace instead, it would round-trip only where the session happens to
/// run in the solution's own directory.
/// </para>
/// <para>
/// Only a path under that directory is shortened. One outside it -- another checkout, a project
/// referenced from beside the repository -- stays absolute, since a relative path climbing out of the
/// caller's directory is longer than the one it replaces and is the shape a mistyped path takes. A
/// generated document's path is left as it is: it names no file a caller can open, and the hint name
/// beside it is what reads it back. The broker does this and the worker never does: the worker cannot
/// know where the caller is standing, and the hop to it is absolute-only.
/// </para>
/// </summary>
public static class ResultPaths
{
	/// <summary>
	/// <paramref name="path"/> relative to <paramref name="origin"/>, with forward slashes, where it lies
	/// under it; otherwise <paramref name="path"/> unchanged.
	/// </summary>
	/// <param name="path">A path a worker answered with, absolute.</param>
	/// <param name="origin">The calling session's directory, absolute.</param>
	public static string Relative(string path, string origin)
	{
		var qualified = Path.IsPathFullyQualified(path) && Path.IsPathFullyQualified(origin);
		if (!qualified) return path;

		var relative = Path.GetRelativePath(origin, path);

		var outside = relative == "."
			|| relative == ".."
			|| relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
			|| relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
			|| Path.IsPathRooted(relative);

		return outside ? path : relative.Replace('\\', '/');
	}

	/// <summary>
	/// A reference search's files and definitions with their paths relative to <paramref name="origin"/>,
	/// and whether any was shortened.
	/// </summary>
	/// <param name="result">One symbol's references, as the worker answered.</param>
	/// <param name="origin">The calling session's directory.</param>
	/// <param name="shortened">Whether any path was made relative.</param>
	public static ReferencesResult RelativeTo(ReferencesResult result, string origin, out bool shortened)
	{
		var any = false;

		string Shorten(string path, string? hintName)
		{
			if (hintName is not null) return path;

			var relative = Relative(path, origin);
			any |= !string.Equals(relative, path, StringComparison.Ordinal);

			return relative;
		}

		var answer = result with
		{
			Files = [.. result.Files.Select(file => file with { FilePath = Shorten(file.FilePath, file.GeneratedHintName) })],
			Definitions = [.. result.Definitions.Select(location => location with { FilePath = Shorten(location.FilePath, location.GeneratedHintName) })],
		};

		shortened = any;
		return answer;
	}

	/// <summary>
	/// Every answer of a reference batch with its paths relative to <paramref name="origin"/>, and
	/// <see cref="ReadBatch{T}.RelativeTo"/> naming it, once, wherever a path was shortened.
	/// </summary>
	/// <param name="batch">The worker's answer.</param>
	/// <param name="origin">The calling session's directory.</param>
	public static ReadBatch<ReferencesResult> RelativeTo(ReadBatch<ReferencesResult> batch, string origin)
	{
		var shortened = false;
		var results = new List<ReadEntry<ReferencesResult>>();

		foreach (var entry in batch.Results)
		{
			if (entry.Answer is null)
			{
				results.Add(entry);
				continue;
			}

			results.Add(entry with { Answer = RelativeTo(entry.Answer, origin, out var any) });
			shortened |= any;
		}

		return batch with { Results = results, RelativeTo = shortened ? origin : null };
	}
}
