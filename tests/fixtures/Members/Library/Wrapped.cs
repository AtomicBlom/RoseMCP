namespace Library;

/// <summary>
/// A signature wrapped by hand. Roslyn's formatter has no rule about where a wrapped parameter list
/// sits, so anything that moves it moved it deliberately -- which makes this the cheapest place to
/// catch a tool re-indenting a signature it promised only to copy.
/// </summary>
public static class Wrapped
{
	public static string Join(
		string first,
		string second,
		string third)
	{
		return first + second + third;
	}
}
