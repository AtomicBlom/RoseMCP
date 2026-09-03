namespace Library;

/// <summary>Says hello, at various lengths.</summary>
public sealed class Greeter
{
	private readonly string _prefix = "Hello";

	/// <summary>How long the prefix is.</summary>
	public int PrefixLength => _prefix.Length;

	public int Count { get; set; }

	/// <summary>The greeting for one name.</summary>
	public string Greet(string name)
	{
		return $"{_prefix}, {name}!";
	}

	/// <summary>The greeting for someone with a title.</summary>
	public string Greet(string title, string name) => $"{_prefix}, {title} {name}!";

	private static string Shout(string text)
	{
		return text.ToUpperInvariant();
	}
}
