using RoseMcp.Contracts;

namespace RoseMcp.Ui.Core.Views;

/// <summary>One file an agent's write changed, as an opened operation shows it: which file, what kind of change, and its diff.</summary>
public sealed class FileChangeView
{
	public FileChangeView(ActivityFileChange change)
	{
		FilePath = change.FilePath;
		FileName = Format.FileLine(change.FilePath, line: null);
		Diff = DiffLine.Parse(change.Diff);

		var facts = new List<string>();
		if (change.Created == true) facts.Add("created");
		if (change.Lines is { Length: > 0 } lines) facts.Add($"lines {lines}");
		if (change.Normalised is { Length: > 0 } normalised) facts.Add(normalised);
		Facts = string.Join(Format.Separator, facts);

		DiffNote = (change.Diff, change.DiffFullLength) switch
		{
			(null, { } full) => $"The diff is {full:N0} characters, more than the record keeps of one.",
			({ } kept, { } full) => $"The diff is cut at {kept.Length:N0} of {full:N0} characters.",
			(null, null) => "No diff was kept for this file: the change was its line endings alone, or the write's other files filled what the record keeps.",
			_ => string.Empty,
		};
	}

	/// <summary>The file, absolute, as the worker that wrote it named it; what Open the file opens.</summary>
	public string FilePath { get; }

	/// <summary>The file's name alone, which is what fits on the line.</summary>
	public string FileName { get; }

	/// <summary>Created, the lines it covers, and what was normalised in it, on one line.</summary>
	public string Facts { get; }

	public bool HasFacts => Facts.Length > 0;

	/// <summary>The diff, a line at a time.</summary>
	public IReadOnlyList<DiffLine> Diff { get; }

	public bool HasDiff => Diff.Count > 0;

	/// <summary>Why the diff is cut or missing, where it is.</summary>
	public string DiffNote { get; }

	public bool HasDiffNote => DiffNote.Length > 0;
}
