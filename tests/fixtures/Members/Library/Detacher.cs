namespace Library;

/// <summary>Lets go of something, and can say why it could not.</summary>
public sealed class Detacher
{
	/// <summary>Lets go.</summary>
	public bool Detach() => true;

	/// <summary>Lets go, keeping what went wrong in a local.</summary>
	public bool Release()
	{
		var failure = "none";
		return failure.Length > 0;
	}

	/// <summary>Counts the items through a lambda whose parameter is named item.</summary>
	public int Tally(int[] items) => items.Select(item => item).Count();

	/// <summary>Counts the items through a query whose range variable is named item.</summary>
	public int Queried(int[] items) => (from item in items select item).Count();

	public bool Run() => Detach() && Release();
}
