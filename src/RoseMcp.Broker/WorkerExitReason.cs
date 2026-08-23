namespace RoseMcp.Broker;

/// <summary>Why a worker is no longer serving requests.</summary>
public enum WorkerExitReason
{
	Running,

	/// <summary>The solution went away and the worker shut itself down. Expected, not a failure.</summary>
	SolutionUnloaded,

	/// <summary>The process died on its own.</summary>
	Crashed,

	/// <summary>The broker stopped it, usually for a hard reload.</summary>
	StoppedByBroker,
}
