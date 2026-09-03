namespace Library;

/// <summary>
/// A call site in a file of its own, so a changed signature breaks something the edit cannot see.
/// </summary>
public static class Caller
{
	public static string Call() => new Greeter().Greet("world");
}
