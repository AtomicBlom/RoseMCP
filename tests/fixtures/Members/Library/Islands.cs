namespace Library;

/// <summary>
/// Two jobs in one class: a delivery queue and a failure log, sharing no state and never calling
/// each other. What a state island looks like from the outside.
/// </summary>
public sealed class TwoJobs
{
	private readonly List<string> _inbox = [];
	private int _delivered;
	private readonly List<string> _errors = [];
	private int _retries;

	public void Accept(string message) => _inbox.Add(message);

	public int Pending => _inbox.Count;

	public void Deliver()
	{
		_inbox.Clear();
		_delivered++;
	}

	public int Delivered => _delivered;

	public void Fail(string reason) => _errors.Add(reason);

	public int Failures => _errors.Count;

	public void Retry()
	{
		_errors.Clear();
		_retries++;
	}

	public int Retries => _retries;
}

/// <summary>
/// One public method and the helpers only it can reach, alongside a helper something else reaches
/// too. What a reach island looks like, and why the shared helper has to stay behind.
/// </summary>
public sealed class OneDoor
{
	public string Render(string text) => Wrap(Trim(Pad(text)));

	private static string Pad(string text) => Indent(text) + " ";

	private static string Trim(string text) => text.Trim();

	private static string Wrap(string text) => "[" + Indent(text) + "]";

	/// <summary>Reached from the island and from outside it, so it belongs to neither.</summary>
	private static string Indent(string text) => "  " + text;

	public string Plain(string text) => Indent(text);

	public string Bare(string text) => text;
}

/// <summary>
/// One field every member reads, which is a spine rather than a seam. Nothing to split.
/// </summary>
public sealed class Cohesive
{
	private readonly List<string> _lines = [];

	public void Add(string line) => _lines.Add(line);

	public int Count => _lines.Count;

	public string Text => string.Join("\n", _lines);

	public void Clear() => _lines.Clear();
}

/// <summary>
/// Islands inside islands: one door onto a world, and a second door onto part of it. Sized so that
/// neither is most of the type, because a member reaching nearly everything is the front door
/// rather than a finding, and is left out for saying nothing.
/// </summary>
public sealed class Layered
{
	public string Publish(string text) => Envelope(text) + Sign(text);

	private static string Envelope(string text) => Header(text) + Body(text) + Footer(text);

	private static string Header(string text) => "H" + Stamp(text);

	private static string Body(string text) => "B" + text;

	private static string Footer(string text) => "F" + Stamp(text);

	private static string Stamp(string text) => text.Length.ToString();

	private static string Sign(string text) => "S" + text;

	public string Raw(string text) => text;

	public string Upper(string text) => text.ToUpperInvariant();

	public string Lower(string text) => text.ToLowerInvariant();

	public int Size(string text) => text.Length;

	public bool Blank(string text) => text.Length == 0;
}
