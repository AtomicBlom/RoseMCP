namespace RoseMcp.Ui.Core;

/// <summary>
/// The numbers and names as the window shows them. Kept out of XAML converters so they can be
/// read, and changed, without standing up a UI.
/// </summary>
public static class Format
{
	private const double Mega = 1024 * 1024;
	private const double Giga = Mega * 1024;

	/// <summary>Whole megabytes below a gigabyte, one decimal above, which is how Task Manager rounds.</summary>
	public static string Bytes(long value) => value >= Giga ? $"{value / Giga:F1} GB" : $"{value / Mega:F0} MB";

	/// <summary>
	/// Sub-second precision below a minute, because the interesting comparison for a warm call is
	/// against the tens of milliseconds it should have taken.
	/// </summary>
	public static string Duration(TimeSpan elapsed) => elapsed.TotalMinutes >= 1
		? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:00}s"
		: $"{elapsed.TotalSeconds:0.0}s";

	/// <summary>Coarser than a duration. Nobody needs the seconds of a two-hour uptime.</summary>
	public static string Uptime(TimeSpan uptime) => uptime.TotalHours >= 1
		? $"{(int)uptime.TotalHours}h {uptime.Minutes}m"
		: uptime.TotalMinutes >= 1
			? $"{(int)uptime.TotalMinutes}m"
			: $"{(int)uptime.TotalSeconds}s";

	/// <summary>
	/// "rose_find_references" the way a person would say it. Lifecycle labels such as "load
	/// solution" have no prefix and pass through unchanged; the raw tool name stays in a tooltip,
	/// because it is also what the client asked for.
	/// </summary>
	public static string Humanise(string operation)
	{
		var trimmed = operation.StartsWith("rose_", StringComparison.Ordinal) ? operation[5..] : operation;

		return trimmed.Replace('_', ' ');
	}

	/// <summary>"1 solution", "2 solutions".</summary>
	public static string Count(int count, string singular, string? plural = null) =>
		count == 1 ? $"1 {singular}" : $"{count} {plural ?? singular + "s"}";

	/// <summary>
	/// The one line every card carries facts on. A middle dot reads as punctuation, where a dash
	/// reads as a range.
	/// </summary>
	public const string Separator = "  ·  ";

	/// <summary>
	/// How long ago something happened, said as a person would. Null is a dash rather than "0s ago":
	/// nothing having happened yet and something happening this instant are opposite facts.
	/// </summary>
	public static string Age(TimeSpan? age) => age is not { } value
		? "--"
		: value.TotalMinutes >= 60
			? $"{(int)value.TotalHours}h ago"
			: value.TotalSeconds >= 60
				? $"{(int)value.TotalMinutes}m ago"
				: value.TotalSeconds >= 10
					? $"{(int)value.TotalSeconds}s ago"
					: $"{value.TotalSeconds:0.0}s ago";

	/// <summary>
	/// Time left, for a deadline a reader is watching: a hold expiring, a target about to resume.
	/// Never negative, because a deadline that has passed and not yet been acted on reads better as
	/// "now" than as a count upwards from it.
	/// </summary>
	public static string Countdown(TimeSpan remaining) => remaining <= TimeSpan.Zero
		? "now"
		: remaining.TotalMinutes >= 1
			? $"{(int)remaining.TotalMinutes}m {remaining.Seconds:00}s"
			: $"{(int)Math.Ceiling(remaining.TotalSeconds)}s";

	/// <summary>
	/// An IL offset as a debugger writes one. Four hex digits because a method that needs five is
	/// rare enough that widening every other offset to match would be the wrong trade.
	/// </summary>
	public static string IlOffset(int offset) => $"IL_{offset:X4}";

	/// <summary>
	/// What separates a directory from a file name in a path this reads, which is either character on
	/// either operating system -- because the path is a record of where something was compiled rather
	/// than a path on the machine reading it.
	/// </summary>
	private static readonly char[] PathSeparators = ['/', '\\'];

	/// <summary>
	/// A source position as <c>File.cs:42</c>, or an empty string when there is no line to give.
	/// <para>
	/// The file name only. A card is one line wide and an absolute path pushes everything else off
	/// it; the full path belongs in the tooltip, where a reader who wants it can find it.
	/// </para>
	/// <para>
	/// Both separators are cut on, rather than asking <see cref="Path.GetFileName(string?)"/>. The path
	/// came out of a PDB or a XAML source record, so it was written by whichever machine compiled the
	/// code and is a Windows path however it is being read -- and on Linux a backslash is an ordinary
	/// character in a file name, so <c>GetFileName</c> hands the whole path back. The caption that
	/// should read <c>MainPage.xaml:31</c> becomes an absolute path in a space that has room for
	/// neither.
	/// </para>
	/// </summary>
	public static string FileLine(string? file, int? line)
	{
		if (file is not { Length: > 0 }) return string.Empty;

		var cut = file.LastIndexOfAny(PathSeparators);
		var name = cut < 0 ? file : file[(cut + 1)..];

		return line is { } number ? $"{name}:{number}" : name;
	}

	/// <summary>A process id, or the fact that there is not one.</summary>
	public static string Pid(int? processId) => processId is { } id ? $"pid {id}" : "no process";
}
