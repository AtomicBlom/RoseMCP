namespace Library.Extras;

/// <summary>
/// An extension method in a namespace nothing imports, which is the third way a name fails to
/// resolve: the method is not on the type, and the name being looked for is not a type at all.
/// </summary>
public static class StringExtras
{
	public static string Shouted(this string text) => text.ToUpperInvariant();
}
