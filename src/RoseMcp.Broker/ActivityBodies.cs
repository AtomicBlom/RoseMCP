using System.Globalization;
using System.Text.Json;

using ModelContextProtocol.Protocol;

using RoseMcp.Contracts;
using RoseMcp.Solutions;

namespace RoseMcp.Broker;

/// <summary>
/// What the activity record keeps of a call's arguments, its response and a write's diff, and how each is cut
/// to a length.
/// <para>
/// Cut because one entry is one line of a file read back by every window that lists the record: a migration's
/// response or a generated file's diff can run to megabytes, and kept whole it would make every later listing
/// pay to step over it. The ceilings are generous beside what a caller is shown -- a person reading what an
/// agent did wants the code, where an agent wants only where its code landed -- and a cut value says how long
/// it really was, in its own field and in a note, so a fragment is never passed off as the whole.
/// </para>
/// </summary>
public static class ActivityBodies
{
	/// <summary>The most of a call's arguments an entry keeps, in characters.</summary>
	public const int ArgumentsCeiling = 32_000;

	/// <summary>The most of a call's response an entry keeps, in characters: twice what a caller is shown of a diff.</summary>
	public const int ResponseCeiling = 32_000;

	/// <summary>
	/// The most of what a failed call's caller was told an entry keeps, in characters. Smaller than a response, since
	/// a listing carries the error where it carries no body, and a failure that says more than this is a response
	/// that happened to be an error, whose whole text the response keeps.
	/// </summary>
	public const int ErrorCeiling = 4_000;

	/// <summary>The most of one file's diff an entry keeps, in characters.</summary>
	public const int FileDiffCeiling = 64_000;

	/// <summary>The most of a call's diffs together an entry keeps; files past it are listed with their length and no diff.</summary>
	public const int CallDiffCeiling = 256_000;

	/// <summary>
	/// <paramref name="text"/> cut to <paramref name="ceiling"/> characters, and how long it really is where it
	/// was cut. A cut never splits a surrogate pair, which would leave half a character no reader can show.
	/// </summary>
	public static (string? Text, int? FullLength) Cut(string? text, int ceiling)
	{
		if (text is null || text.Length <= ceiling) return (text, null);

		var length = Math.Max(0, ceiling);
		if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;

		return (text[..length], text.Length);
	}

	/// <summary>A note saying what was cut, from how long and to how long, for an entry's notes.</summary>
	public static string CutNote(string what, int fullLength, int kept) =>
		string.Create(
			CultureInfo.InvariantCulture,
			$"The {what} is {fullLength:N0} characters, past the {kept:N0} the activity record keeps, and is cut to its first {kept:N0}.");

	/// <summary>The arguments as the client sent them, in JSON, or null for a call that sent none.</summary>
	public static string? Arguments(IDictionary<string, JsonElement>? arguments) =>
		arguments is null ? null : JsonSerializer.Serialize(arguments, ServiceCollectionExtensions.ToolSerializerOptions);

	/// <summary>
	/// What a client was answered: the structured result where there was one, which is the answer in full, and
	/// the content blocks otherwise -- text as it was, anything else named by its type, since an image in a line
	/// of JSON is bytes nobody reads there.
	/// </summary>
	public static string Response(CallToolResult result)
	{
		if (result.StructuredContent is { } structured) return structured.GetRawText();

		return string.Join('\n', result.Content.Select(Described));
	}

	/// <summary>What a failed result said, its text blocks joined, or null where it said nothing in text.</summary>
	public static string? Error(CallToolResult result)
	{
		var text = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(block => block.Text));

		return string.IsNullOrWhiteSpace(text) ? null : text;
	}

	/// <summary>
	/// Each file a write changed, with its part of the write's diff, cut to what the record keeps: a file to
	/// <see cref="FileDiffCeiling"/>, and the call's diffs together to what is left of
	/// <paramref name="budget"/>, which this spends. A cut adds a note to <paramref name="notes"/>.
	/// </summary>
	/// <param name="files">The files as the worker named them, absolute.</param>
	/// <param name="diff">The write's unified diff, every file's together.</param>
	/// <param name="budget">What is left of <see cref="CallDiffCeiling"/> for this call; spent here.</param>
	/// <param name="notes">Where a cut is said.</param>
	public static IReadOnlyList<ActivityFileChange> Changes(
		IReadOnlyList<ChangedFile> files,
		string? diff,
		ref int budget,
		List<string> notes)
	{
		var parts = PerFile(diff, files);
		var changes = new List<ActivityFileChange>(files.Count);
		var leftOut = 0;

		foreach (var file in files)
		{
			var change = new ActivityFileChange
			{
				FilePath = file.FilePath,
				Lines = file.Lines,
				Created = file.Created,
				Normalised = file.Normalised,
			};

			if (!parts.TryGetValue(file.FilePath, out var part))
			{
				changes.Add(change);
				continue;
			}

			if (budget <= 0)
			{
				leftOut++;
				changes.Add(change with { DiffFullLength = part.Length });
				continue;
			}

			var ceiling = Math.Min(FileDiffCeiling, budget);
			var (kept, fullLength) = Cut(part, ceiling);
			budget -= kept!.Length;

			if (fullLength is { } full) notes.Add(CutNote($"diff of {file.FilePath}", full, kept.Length));

			changes.Add(change with { Diff = kept, DiffFullLength = fullLength });
		}

		if (leftOut > 0)
		{
			notes.Add(string.Create(
				CultureInfo.InvariantCulture,
				$"The diffs of {leftOut} file(s) are left out, past the {CallDiffCeiling:N0} characters of diff the activity record keeps for one call; each says how long its diff is."));
		}

		return changes;
	}

	/// <summary>
	/// The write's diff taken apart by file. A file's part starts at its header pair -- a <c>---</c> line followed
	/// by the <c>+++</c> line naming one of <paramref name="files"/> -- and runs to the next such pair, so a line of
	/// a file's own content that happens to read like a header is kept as content, the rule
	/// <see cref="WritePaths"/> reads the headers by.
	/// </summary>
	public static IReadOnlyDictionary<string, string> PerFile(string? diff, IReadOnlyList<ChangedFile> files)
	{
		var parts = new Dictionary<string, string>(PathCasing.Comparer);
		if (string.IsNullOrEmpty(diff)) return parts;

		var named = files.Select(file => file.FilePath).ToHashSet(PathCasing.Comparer);
		var starts = new List<(int Offset, string Path)>();

		var line = 0;
		while (line < diff.Length)
		{
			var end = diff.IndexOf('\n', line);
			if (end < 0) break;

			var next = end + 1;
			var nextEnd = next < diff.Length ? diff.IndexOf('\n', next) : -1;
			var nextLine = nextEnd < 0 ? diff[next..] : diff[next..nextEnd];

			var isHeaderPair = string.CompareOrdinal(diff, line, "--- ", 0, 4) == 0
				&& nextLine.StartsWith("+++ ", StringComparison.Ordinal)
				&& named.Contains(nextLine[4..]);

			if (isHeaderPair)
			{
				starts.Add((line, nextLine[4..]));
				line = nextEnd < 0 ? diff.Length : nextEnd + 1;
				continue;
			}

			line = next;
		}

		for (var index = 0; index < starts.Count; index++)
		{
			var from = starts[index].Offset;
			var to = index + 1 < starts.Count ? starts[index + 1].Offset : diff.Length;

			parts.TryAdd(starts[index].Path, diff[from..to]);
		}

		return parts;
	}

	private static string Described(ContentBlock block) => block switch
	{
		TextContentBlock text => text.Text,
		ImageContentBlock image => $"[image, {image.MimeType}]",
		_ => $"[{block.Type}]",
	};
}
