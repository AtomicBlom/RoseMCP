namespace Library;

/// <summary>
/// Members written with <c>=&gt;</c> whose bodies wrap across lines. A continuation of an expression
/// body is layout Roslyn's formatter has no rule about -- it reindents statements and moves braces,
/// and an expression body is neither -- so anything that moves one moved it deliberately, and neither
/// IDE0055 nor dotnet format will say so.
/// </summary>
public static class Arrowed
{
	/// <summary>An expression body on a line of its own.</summary>
	/// <param name="first">The first part.</param>
	/// <param name="second">The second part.</param>
	public static string Describe(string first, string second) =>
		first + ", " + second;

	/// <summary>An expression body wrapped across several lines.</summary>
	public static string Spread(string first, string second, string third) =>
		first
			+ ", " + second
			+ ", " + third;

	/// <summary>Calls one of them, so a signature change has a call site to move.</summary>
	public static string Call() => Describe("one", "two");
}
