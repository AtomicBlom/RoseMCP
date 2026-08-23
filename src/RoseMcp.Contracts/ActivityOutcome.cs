namespace RoseMcp.Contracts;

/// <summary>How a tracked operation ended, or that it has not ended.</summary>
public enum ActivityOutcome
{
	Running,

	Succeeded,

	/// <summary>The operation threw. The reason is in <see cref="WorkerActivity.Error"/>.</summary>
	Failed,

	/// <summary>The caller gave up, or the worker went away underneath it.</summary>
	Cancelled,
}
