namespace RoseMcp.Contracts;

/// <summary>Which way a step goes from a stop.</summary>
public enum StepDirection
{
	/// <summary>Into the call on the current line.</summary>
	In,

	/// <summary>Over it, running it without descending.</summary>
	Over,

	/// <summary>Out of the current frame, to its caller.</summary>
	Out,
}
