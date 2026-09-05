namespace Library;

/// <summary>
/// Call sites for the two constructed types, in a file of their own so a changed signature breaks
/// something the edit cannot see.
/// </summary>
public static class Builds
{
	public static string One() => new Assembled("one").Name;

	public static string Two() => new Composed("two").Describe();
}
