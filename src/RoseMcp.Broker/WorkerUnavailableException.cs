namespace RoseMcp.Broker;

/// <summary>
/// The worker for a workspace died. Distinct from a tool failing, because the workspace can be
/// brought back by starting a fresh worker whereas a bad request cannot.
/// </summary>
public sealed class WorkerUnavailableException(string solutionPath, Exception inner)
	: InvalidOperationException($"The worker for {solutionPath} is no longer running.", inner)
{
	public string SolutionPath { get; } = solutionPath;
}
