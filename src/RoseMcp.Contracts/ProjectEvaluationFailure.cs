namespace RoseMcp.Contracts;

/// <summary>
/// A project the worker's own MSBuild could not evaluate, so what it imports is unknown and the
/// worker watches only the build files it can find by name.
/// <para>
/// Structured for the reason <see cref="AnalyzerLoadFailure"/> is: the degraded reason folds these
/// into one line grouped by message, and a reason that had to be parsed back apart to group it would
/// be carrying one fact in two shapes.
/// </para>
/// </summary>
public sealed record ProjectEvaluationFailure
{
	/// <summary>Full path of the project file.</summary>
	public required string Project { get; init; }

	/// <summary>
	/// What MSBuild said, without the project path it appends, so one cause failing every project
	/// reads as one message.
	/// </summary>
	public required string Message { get; init; }

	/// <summary>
	/// Whether the project file names an SDK. One that does and still cannot be evaluated by the SDK's
	/// own MSBuild means this process's MSBuild is hurt, which degrades the workspace. One that does not
	/// is a legacy project whose targets ship only with Visual Studio's MSBuild, which the design-time
	/// build uses and this evaluation cannot, so its failure is expected and only noted.
	/// </summary>
	public required bool NamesSdk { get; init; }
}
