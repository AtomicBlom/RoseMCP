namespace RoseMcp.Contracts;

/// <summary>
/// The kinds a reason in a workspace's health can have, one per cause Rose recognises.
/// <para>
/// A kind is what an acceptance is keyed on, with the solution and the project, so it has to be the
/// same at every load: a kind derived from a reason's wording would change the moment the wording did,
/// and every acceptance a person had made would stop matching without a word. So they are named here,
/// once, and the reason's text is free to change.
/// </para>
/// <para>
/// Strings rather than an enum, because the persisted acceptances name them and an unknown name read
/// back from a newer build has to survive being read by an older one rather than fail to parse.
/// </para>
/// </summary>
public static class ReasonKinds
{
	/// <summary>Restore ran and failed.</summary>
	public const string RestoreFailed = "restore-failed";

	/// <summary>Projects left with no restore output, whether or not restore ran.</summary>
	public const string Unrestored = "unrestored";

	/// <summary>The platform this load chose looks like the wrong one.</summary>
	public const string WrongPlatform = "wrong-platform";

	/// <summary>A project whose design-time build resolved nothing.</summary>
	public const string ProjectNotLoaded = "project-not-loaded";

	/// <summary>A project expects an analyzer assembly that is not on disk.</summary>
	public const string AnalyzerOutputMissing = "analyzer-output-missing";

	/// <summary>Analyzer or generator assemblies that are on disk and would not load.</summary>
	public const string AnalyzerLoadFailed = "analyzer-load-failed";

	/// <summary>This worker's own MSBuild evaluated none of the SDK projects the design-time build loaded.</summary>
	public const string WorkerMsBuildBroken = "worker-msbuild-broken";

	/// <summary>Projects naming an SDK that this worker's own MSBuild could not evaluate.</summary>
	public const string ProjectNotEvaluated = "project-not-evaluated";

	/// <summary>MSBuild's load failures, counted, where some project came back impaired.</summary>
	public const string MsBuildLoadFailures = "msbuild-load-failures";

	/// <summary>XAML files no dialect could be chosen for, so nothing stood in for the markup compiler.</summary>
	public const string XamlNoDialect = "xaml-no-dialect";

	/// <summary>Projects referencing more than one XAML framework, so the stubs' dialect is a guess.</summary>
	public const string XamlDialectAmbiguous = "xaml-dialect-ambiguous";

	/// <summary>Named XAML elements whose type the project cannot see.</summary>
	public const string XamlUnresolvedTypes = "xaml-unresolved-types";

	/// <summary>Assemblies this worker's own code could not load, so some tools fail on every call.</summary>
	public const string AssemblyLoadFault = "assembly-load-fault";

	/// <summary>The solution file is missing, and answers come from the last good snapshot.</summary>
	public const string SolutionMissing = "solution-missing";

	/// <summary>The solution went away and the worker unloaded it.</summary>
	public const string SolutionUnloaded = "solution-unloaded";

	/// <summary>Loading the solution failed outright.</summary>
	public const string LoadFailed = "load-failed";

	/// <summary>The worker has stopped: evicted, crashed or stopped by the broker.</summary>
	public const string WorkerStopped = "worker-stopped";

	/// <summary>A configuration or platform this load chose without being asked.</summary>
	public const string ConfigurationChosen = "configuration-chosen";

	/// <summary>Projects naming no SDK that this worker's own MSBuild could not evaluate, as expected of a legacy project.</summary>
	public const string LegacyProjectNotEvaluated = "legacy-project-not-evaluated";

	/// <summary>A reference unresolved because the in-solution project that writes it has not been built.</summary>
	public const string UnbuiltReference = "unbuilt-reference";

	/// <summary>Projects with sources newer than their last build output.</summary>
	public const string StaleBuildOutput = "stale-build-output";

	/// <summary>The worker is a different build from the broker.</summary>
	public const string WorkerVersionMismatch = "worker-version-mismatch";
}
