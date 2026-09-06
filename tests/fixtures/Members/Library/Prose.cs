namespace Library;

/// <summary>
/// Text a token stream cannot see: a line comment, a string constant's body, and a sentence
/// inside an attribute argument. Each is a kind of C# that had no tool at all.
/// </summary>
public static class Prose
{
	/// <summary>A description, of the shape every tool description in this repository has.</summary>
	public const string Description = "Reads a thing. Pass a path to say which thing.";

	public static int Counted(int[] values)
	{
		// Counts what is there, which is not the same as what was asked for.
		var total = 0;

		foreach (var value in values) total += value;

		return total;
	}

	public static string Label()
	{
		// The label, which is not the value.
		return "total";
	}
}
