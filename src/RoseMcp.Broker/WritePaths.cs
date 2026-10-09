using RoseMcp.Contracts;
using RoseMcp.Solutions;

namespace RoseMcp.Broker;

/// <summary>
/// A write's result with every path under the calling session's directory named relative to it.
/// <para>
/// Without this, one edited file costs its absolute path five times over -- on the changed file, on
/// both diff headers, on each diagnostic and in each notice -- and it is the largest constant in a
/// write result. Relative to the session's directory rather than to the workspace's, because that is
/// the one base every way a path comes back agrees on: a relative argument is measured from the
/// session's directory with or without a <c>workspaceKey</c> (<see cref="CallerPaths"/>), and so is a
/// relative path in the caller's own file tools. A path relative to the workspace would mean a
/// different file sent back without the key -- in another worktree of the same repository, the file at
/// the same place there, which exists and is written to.
/// </para>
/// <para>
/// What does not lie under the session's directory stays absolute rather than ascending with
/// <c>..</c>: a workspace the caller named in another checkout, a file a project links from elsewhere,
/// another drive, and a document a generator produced, whose path names nothing on disk and is read
/// back by its hint name instead. So does every path of a call whose session's directory the broker
/// does not know (<see cref="CallerPaths.KnownOrigin"/>), since relative to anywhere else it names
/// a file the caller's own tools cannot find.
/// </para>
/// <para>
/// Applied in <see cref="WorkspaceManager"/>, beside attribution and after the sibling-solution notice,
/// which needs the absolute paths. Each result type with a path field of its own is named in
/// <see cref="Handled"/>, and a test fails a write result type that is not, so a field added later
/// cannot go out absolute because nobody remembered it here.
/// </para>
/// </summary>
public static class WritePaths
{
	/// <summary>Every write result type whose paths this names relative.</summary>
	public static readonly IReadOnlyList<Type> Handled =
	[
		typeof(MemberEditResult),
		typeof(AddFileResult),
		typeof(UsingResult),
		typeof(SignatureChangeResult),
		typeof(PatternRewriteResult),
		typeof(RenameResult),
		typeof(CodeFixResult),
		typeof(FormatResult),
		typeof(MoveTypeResult),
	];

	/// <summary>
	/// <paramref name="result"/> with its paths named relative to <paramref name="directory"/>, the
	/// calling session's own directory, and the help links left off its diagnostics. A null directory,
	/// for a call whose session is not known, leaves every path absolute.
	/// </summary>
	public static T Relative<T>(T result, string? directory)
		where T : WorkspaceMutationResult
	{
		var root = directory is not null && Path.IsPathFullyQualified(directory)
			? Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar
			: null;

		WorkspaceMutationResult common = result with
		{
			ChangedFiles = [.. result.ChangedFiles.Select(file => file with { FilePath = Of(file.FilePath, root) })],
			Diff = result.Diff is { } diff ? Headers(diff, result.ChangedFiles, root) : null,
			Notices = Prose(result.Notices, root),
		};

		WorkspaceMutationResult specific = common switch
		{
			MemberEditResult member => member with { IntroducedDiagnostics = Diagnostics(member.IntroducedDiagnostics, root) },
			AddFileResult added => added with { IntroducedDiagnostics = Diagnostics(added.IntroducedDiagnostics, root) },
			UsingResult imported => imported with { IntroducedDiagnostics = Diagnostics(imported.IntroducedDiagnostics, root) },
			SignatureChangeResult signature => signature with
			{
				IntroducedDiagnostics = Diagnostics(signature.IntroducedDiagnostics, root),
				UpdatedDeclarations = Locations(signature.UpdatedDeclarations, root),
				UpdatedCallSites = Locations(signature.UpdatedCallSites, root),
				UnchangedCallSites =
				[
					.. signature.UnchangedCallSites.Select(site => site with { Location = Location(site.Location, root) }),
				],
				DocumentationUpdated = Prose(signature.DocumentationUpdated, root),
			},
			PatternRewriteResult pattern => pattern with
			{
				IntroducedDiagnostics = Diagnostics(pattern.IntroducedDiagnostics, root),
				Files = [.. pattern.Files.Select(file => file with { FilePath = Of(file.FilePath, root) })],
				Skipped = [.. pattern.Skipped.Select(group => group with { Examples = Locations(group.Examples, root) })],
				Unmatched = [.. pattern.Unmatched.Select(group => group with { Examples = Locations(group.Examples, root) })],
				Rules =
				[
					.. pattern.Rules.Select(rule => rule.Sample is { } sample
						? rule with { Sample = sample with { Location = Prose(sample.Location, root) } }
						: rule),
				],
			},
			RenameResult rename => rename with
			{
				Conflicts = Prose(rename.Conflicts, root),
				XamlMentions = [.. rename.XamlMentions.Select(mention => mention with { FilePath = Of(mention.FilePath, root) })],
			},
			_ => common,
		};

		return (T)specific;
	}

	/// <summary>
	/// One path, relative to <paramref name="root"/> where it lies under it, and as it was otherwise: a
	/// path that is not absolute, one outside the root, or one on another drive.
	/// </summary>
	/// <param name="path">The path as the worker gave it.</param>
	/// <param name="root">The session's directory, ending in a separator, or null where it is not known.</param>
	public static string Of(string path, string? root)
	{
		if (root is null) return path;

		var isUnder = Path.IsPathFullyQualified(path)
			&& path.Length > root.Length
			&& path.StartsWith(root, PathCasing.IsInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

		return isUnder ? path[root.Length..] : path;
	}

	/// <summary>
	/// Sentences with every path under <paramref name="root"/> named relative to it, which is the root and
	/// its separator taken out wherever they start a path. A notice names files the way a field does, and
	/// a sentence naming a file absolutely beside a field naming it relatively reads as two files.
	/// </summary>
	private static IReadOnlyList<string> Prose(IReadOnlyList<string> sentences, string? root) =>
		[.. sentences.Select(sentence => Prose(sentence, root))];

	private static string Prose(string sentence, string? root) =>
		root is null ? sentence : sentence.Replace(root, string.Empty, PathCasing.IsInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

	/// <summary>
	/// The diff with each file's header lines naming it the way <see cref="WorkspaceMutationResult.ChangedFiles"/>
	/// does. Only a header pair naming a changed file is rewritten -- a <c>---</c> line followed by the
	/// <c>+++</c> line for that file -- so a line of the file's own content that happens to read like one
	/// is left as it was written.
	/// </summary>
	private static string Headers(string diff, IReadOnlyList<ChangedFile> files, string? root)
	{
		var named = files.Select(file => file.FilePath).ToHashSet(PathCasing.Comparer);
		var lines = diff.Split('\n');

		for (var index = 0; index + 1 < lines.Length; index++)
		{
			var old = lines[index];
			var @new = lines[index + 1];
			var isHeaderPair = old.StartsWith("--- ", StringComparison.Ordinal)
				&& @new.StartsWith("+++ ", StringComparison.Ordinal)
				&& named.Contains(@new[4..]);
			if (!isHeaderPair) continue;

			if (named.Contains(old[4..])) lines[index] = old[..4] + Of(old[4..], root);
			lines[index + 1] = @new[..4] + Of(@new[4..], root);
			index++;
		}

		return string.Join('\n', lines);
	}

	/// <summary>
	/// Diagnostics with their files relative and their help links left off. A generated document keeps its
	/// path, which names nothing on disk; it is read back by its hint name. A help link is the same for
	/// every occurrence of an id and no agent opens one.
	/// </summary>
	private static IReadOnlyList<DiagnosticEntry> Diagnostics(IReadOnlyList<DiagnosticEntry> entries, string? root) =>
		[
			.. entries.Select(entry => entry with
			{
				FilePath = entry.FilePath is { } path && entry.GeneratedHintName is null ? Of(path, root) : entry.FilePath,
				HelpLink = null,
			}),
		];

	private static IReadOnlyList<SourceLocation> Locations(IReadOnlyList<SourceLocation> locations, string? root) =>
		[.. locations.Select(location => Location(location, root))];

	private static SourceLocation Location(SourceLocation location, string? root) =>
		location.GeneratedHintName is null ? location with { FilePath = Of(location.FilePath, root) } : location;
}
