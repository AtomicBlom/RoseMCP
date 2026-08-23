namespace RoseMcp.Worker;

/// <summary>
/// Where a long operation says how far it has got.
/// <para>
/// A percentage is of the reporting operation itself, never of the request as a whole, and a caller
/// that spans several operations hands each one a slice of its own scale. That is what keeps a
/// progress bar moving in one direction when one phase ends and the next begins.
/// </para>
/// <para>
/// A null percentage means the operation genuinely does not know how far along it is, which is not
/// the same as being at zero. Finding references has no idea up front how much of a solution it
/// will have to look at, and saying so beats a bar that sits at nothing and reads as a hang.
/// </para>
/// </summary>
public interface IWorkProgress
{
	void Report(string message, double? percentComplete = null);
}
