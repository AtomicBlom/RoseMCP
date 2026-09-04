namespace Library.Deep;

/// <summary>A container, so its nested type is reachable by importing and still not by name.</summary>
public static class Outer
{
	/// <summary>Has to be written Outer.Inner whatever is imported, which an import alone will not fix.</summary>
	public sealed class Inner
	{
		public const string Name = "inner";
	}
}
