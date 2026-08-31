namespace RoseMcp.Worker;

/// <summary>
/// The configuration and platform names a solution declares.
/// <para>
/// Read because MSBuild's defaults are not always valid. A solution is free to declare no
/// <c>Debug</c> and no <c>AnyCPU</c> -- a Revit add-in built against four API versions declares
/// Debug-2024 through Debug-2027 and x64 -- and a design-time build with an undeclared
/// configuration does not fail loudly. It evaluates to a project with no TargetFramework, which
/// surfaces thousands of lines later as "predefined type 'System.Object' is not defined".
/// </para>
/// </summary>
public sealed record SolutionConfigurations
{
	/// <summary>Nothing declared, which means MSBuild's own defaults are the right answer.</summary>
	public static SolutionConfigurations None { get; } = new();

	public IReadOnlyList<string> Configurations { get; init; } = [];

	public IReadOnlyList<string> Platforms { get; init; } = [];

	public bool IsEmpty => Configurations.Count == 0 && Platforms.Count == 0;

	/// <summary>
	/// Whether a name is one this solution declares. True when nothing is declared at all: an
	/// unknown list cannot contradict anything, so the caller's choice stands.
	/// </summary>
	public static bool Declares(IReadOnlyList<string> names, string candidate) =>
		names.Count == 0 || names.Contains(candidate, StringComparer.OrdinalIgnoreCase);
}
