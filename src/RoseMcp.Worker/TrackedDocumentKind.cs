namespace RoseMcp.Worker;

/// <summary>How a tracked file participates in the solution, which decides how it is updated.</summary>
public enum TrackedDocumentKind
{
	Source,
	Additional,
	AnalyzerConfig,
}
