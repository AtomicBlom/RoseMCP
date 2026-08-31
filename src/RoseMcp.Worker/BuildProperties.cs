namespace RoseMcp.Worker;

/// <summary>
/// The MSBuild global properties a load runs under: configuration, platform, and anything else the
/// caller pins.
/// <para>
/// A design-time build is a build, so it obeys the same properties one does. Most repositories never
/// need this because Debug|AnyCPU is what they declare. The ones that do need it need it absolutely:
/// where TargetFramework is derived from the configuration name, the wrong configuration produces a
/// project with no framework, no references, and every file reporting that System.Object is missing.
/// </para>
/// <para>
/// Arbitrary properties are supported and not just the two named ones, because the derivation is
/// sometimes from neither. A Revit add-in whose CI builds every API version as Release tells them
/// apart with a RevitVersion property alone.
/// </para>
/// </summary>
public sealed record BuildProperties
{
	/// <summary>MSBuild's own defaults, which is what a repository that declares nothing wants.</summary>
	public static BuildProperties Default { get; } = new();

	public string? Configuration { get; init; }

	public string? Platform { get; init; }

	public IReadOnlyDictionary<string, string> Extra { get; init; } =
		new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Why these were chosen, when they were not simply asked for. Reported rather than logged: a
	/// solution loaded under a configuration nobody named is a fact the caller has to be able to see.
	/// </summary>
	public string? Notice { get; init; }

	/// <summary>
	/// What the solution declared, kept alongside the choice so a caller told "loaded as Debug-2024"
	/// can also be told what else it could have asked for.
	/// </summary>
	public SolutionConfigurations Available { get; init; } = SolutionConfigurations.None;

	/// <summary>
	/// Chooses the properties to load under.
	/// <para>
	/// What the caller asked for always wins. Otherwise the declared list decides: MSBuild's default
	/// is left alone when the solution declares it or declares nothing at all, and only replaced when
	/// it is demonstrably not on the list. That keeps every ordinary repository loading exactly as it
	/// did before.
	/// </para>
	/// </summary>
	public static BuildProperties Select(
		WorkerOptions options,
		SolutionConfigurations available,
		WorkspaceConfigFile? pinned = null)
	{
		var notices = new List<string>();

		if (pinned is not null)
		{
			notices.Add($"Properties pinned by {pinned.Path}.");
		}

		var configuration = Choose(
			options.Configuration ?? pinned?.Configuration,
			available.Configurations,
			msbuildDefault: "Debug",
			preferPrefix: "Debug",
			label: "Configuration",
			notices);

		var platform = Choose(
			options.Platform ?? pinned?.Platform,
			available.Platforms,
			msbuildDefault: "AnyCPU",
			preferPrefix: null,
			label: "Platform",
			notices);

		return new BuildProperties
		{
			Configuration = configuration,
			Platform = platform,
			Extra = Merge(pinned?.Properties, options.Properties),
			Available = available,
			Notice = notices.Count == 0 ? null : string.Join(" ", notices),
		};
	}

	/// <summary>For <c>MSBuildWorkspace.Create</c>. Empty when nothing needs overriding.</summary>
	public IReadOnlyDictionary<string, string> AsGlobalProperties()
	{
		var properties = new Dictionary<string, string>(Extra, StringComparer.OrdinalIgnoreCase);

		if (Configuration is { Length: > 0 } configuration) properties["Configuration"] = configuration;
		if (Platform is { Length: > 0 } platform) properties["Platform"] = platform;

		return properties;
	}

	/// <summary>
	/// The same properties as <c>dotnet restore</c> arguments. Restore has to agree with the load:
	/// a repository that moves BaseIntermediateOutputPath per configuration writes its assets file
	/// somewhere the load will not look if the two disagree.
	/// </summary>
	public IEnumerable<string> AsRestoreArguments() =>
		AsGlobalProperties().Select(property => $"-p:{property.Key}={property.Value}");

	/// <summary>Short form for status output: <c>Debug-2027|x64</c>, plus any pinned properties.</summary>
	public string Describe()
	{
		var head = $"{Configuration ?? "Debug"}|{Platform ?? "AnyCPU"}";
		if (Extra.Count == 0) return head;

		return $"{head} ({string.Join(", ", Extra.Select(property => $"{property.Key}={property.Value}"))})";
	}

	/// <summary>
	/// The file's properties, then the caller's over the top: what was asked for this time wins over
	/// what the repository pinned, the same way it does for configuration and platform.
	/// </summary>
	private static IReadOnlyDictionary<string, string> Merge(
		IReadOnlyDictionary<string, string>? pinned,
		IReadOnlyDictionary<string, string> requested)
	{
		if (pinned is null or { Count: 0 }) return requested;

		var merged = new Dictionary<string, string>(pinned, StringComparer.OrdinalIgnoreCase);

		foreach (var property in requested)
		{
			merged[property.Key] = property.Value;
		}

		return merged;
	}

	private static string? Choose(
		string? requested,
		IReadOnlyList<string> declared,
		string msbuildDefault,
		string? preferPrefix,
		string label,
		List<string> notices)
	{
		if (requested is { Length: > 0 })
		{
			if (!SolutionConfigurations.Declares(declared, requested))
			{
				notices.Add($"{label} '{requested}' is not one this solution declares "
					+ $"({string.Join(", ", declared)}); loading with it anyway because it was asked for.");
			}

			return requested;
		}

		if (SolutionConfigurations.Declares(declared, msbuildDefault)) return null;

		var chosen = declared.FirstOrDefault(name =>
			preferPrefix is not null && name.StartsWith(preferPrefix, StringComparison.OrdinalIgnoreCase))
			?? declared[0];

		notices.Add($"This solution declares no '{msbuildDefault}' {label.ToLowerInvariant()}, so "
			+ $"'{chosen}' was chosen from {string.Join(", ", declared)}. "
			+ $"Pass {label.ToLowerInvariant()} explicitly to load a different one.");

		return chosen;
	}
}
