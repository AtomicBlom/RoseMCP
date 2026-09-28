namespace Library;

/// <summary>
/// A helper its own file calls before declaring it, which is the ordinary shape of a private helper.
/// Qualifying those calls moves every position below them, the declaration's included.
/// </summary>
public static class Relocated
{
	public static int Uses() => Helper(1) + Helper(2);

	/// <summary>One more.</summary>
	public static int Helper(int value) => value + 1;
}

/// <summary>A home for the helper in the same file, below a call site that qualifying rewrites.</summary>
public static class Resettled
{
	public static int Existing() => 0;
}
