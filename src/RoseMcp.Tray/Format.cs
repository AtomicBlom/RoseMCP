namespace RoseMcp.Tray;

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
}
