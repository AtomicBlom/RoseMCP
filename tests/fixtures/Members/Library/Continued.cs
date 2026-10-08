namespace Library;

/// <summary>
/// Calls wrapped by hand in the two ways a long argument list is: once after the parenthesis with
/// every argument sharing the continuation line, and one argument to a line. An argument added to
/// either has to land the way its neighbours are laid out, and nothing downstream reindents a
/// continuation line if it does not.
/// </summary>
public static class Continued
{
	/// <summary>The method both calls go to.</summary>
	public static string Combine(string first, string second, string third, string last) =>
		first + second + third + last;

	/// <summary>Every argument on the one line after the parenthesis.</summary>
	public static string Shared()
	{
		return Combine(
			"one", "two", "three", "four");
	}

	/// <summary>Every argument on a line of its own.</summary>
	public static string Stacked()
	{
		return Combine(
			"one",
			"two",
			"three",
			"four");
	}
}
