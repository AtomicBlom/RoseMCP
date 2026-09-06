namespace Library;

/// <summary>A type built through a constructor written out in full.</summary>
public sealed class Assembled
{
	private readonly string _name;

	/// <summary>Names it.</summary>
	/// <param name="name">What to call it.</param>
	public Assembled(string name)
	{
		_name = name;
	}

	public string Name => _name;
}

/// <summary>
/// A type whose constructor parameters are written on the type itself, so its declaration is the
/// type declaration and not a member of it.
/// </summary>
/// <param name="name">What to call it.</param>
public sealed class Composed(string name)
{
	public string Describe() => $"{name}";
}
