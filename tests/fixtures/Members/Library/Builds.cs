namespace Library;

/// <summary>
/// Call sites for the two constructed types, in a file of their own so a changed signature breaks
/// something the edit cannot see.
/// </summary>
public static class Builds
{
	public static string One() => new Assembled("one").Name;

	public static string Two() => new Composed("two").Describe();

	/// <summary>A call site in another file, so moving the member has somewhere to point.</summary>
	public static int Doubled() => Regioned.Twice(21);
}
