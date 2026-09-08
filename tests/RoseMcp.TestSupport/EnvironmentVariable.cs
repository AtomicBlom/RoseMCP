namespace RoseMcp.TestSupport;

/// <summary>
/// Sets a process-wide environment variable for the length of a test and puts back exactly what was
/// there, an absent variable included.
/// <para>
/// Process-wide is the only shape available: the variables that matter here are read by child
/// processes a test starts, and a child inherits its parent's environment rather than the test's
/// idea of one. So a test using this has to already hold whatever gate serialises the thing it is
/// about to start, or it changes the environment under somebody else's child.
/// </para>
/// </summary>
public sealed class EnvironmentVariable : IDisposable
{
	private readonly string _name;
	private readonly string? _previous;

	public EnvironmentVariable(string name, string? value)
	{
		_name = name;
		_previous = Environment.GetEnvironmentVariable(name);
		Environment.SetEnvironmentVariable(name, value);
	}

	/// <summary>
	/// Puts the previous value back. Setting null is what removes a variable that was not there
	/// before, which is a different state from one set to the empty string -- and the difference
	/// matters to every reader that tests for presence rather than for content.
	/// </summary>
	public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
}
