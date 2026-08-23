namespace RoseMcp.Worker;

/// <summary>Thrown once the solution file is confirmed gone and the worker is shutting down.</summary>
public sealed class SolutionUnloadedException(string solutionPath)
	: InvalidOperationException($"The solution no longer exists at {solutionPath}.")
{
	public string SolutionPath { get; } = solutionPath;
}
