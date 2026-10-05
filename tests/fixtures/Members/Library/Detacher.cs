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

	public bool Run() => Detach() && Release();
}
