using System.Runtime.InteropServices;

using RoseMcp.Solutions;

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

	/// <summary>
	/// Whether <see cref="Platform"/> was picked from the solution's declared list rather than asked
	/// for by the caller.
	/// <para>
	/// Kept because the wrong platform is survivable and therefore silent. Nothing fails: the projects
	/// load, MSBuild resolves what it can, and the references it cannot find are output assemblies
	/// under a directory nobody has ever built. Knowing the value was a guess is what lets the status
	/// report say that afterwards, rather than leaving a caller to tell "no references" apart from
	/// "wrong platform" unaided.
	/// </para>
	/// </summary>
	public bool PlatformWasChosen { get; init; }

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
			prefer: name => name.StartsWith("Debug", StringComparison.OrdinalIgnoreCase),
			label: "Configuration",
			notices);

		var platform = Choose(
			options.Platform ?? pinned?.Platform,
			available.Platforms,
			msbuildDefault: "AnyCPU",
			prefer: HostPlatform,
			label: "Platform",
			notices);

		return new BuildProperties
		{
			Configuration = configuration.Value,
			Platform = platform.Value,
			PlatformWasChosen = platform.Chosen,
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
	/// Why the platform chosen here looks like the wrong one, or null when nothing suggests it is.
	/// <para>
	/// The signature is references that did not resolve, named under the output directory of a
	/// platform nobody asked for. That is the whole of why a wrong platform is worth reporting rather
	/// than leaving to be noticed: it does not fail. The projects load, MSBuild resolves the
	/// framework, and what it cannot find are the in-solution outputs under <c>bin\ARM64\</c> on a
	/// machine where everything has only ever been built x64. Every project still reports having
	/// loaded successfully, because each resolved plenty of references -- just not each other's -- so
	/// the workspace reads healthy while every cross-project answer is missing half its inputs.
	/// </para>
	/// <para>
	/// Measured on Drawboard's 60-project DrawboardProjects.slnx from an ARM64 machine: it declares
	/// x64 and ARM64 and no AnyCPU, ARM64 was chosen for matching the host, and 363 of the 557 load
	/// diagnostics named assemblies under <c>\ARM64\</c> that do not exist. Nothing said so.
	/// </para>
	/// <para>
	/// Only ever about a platform this server chose. A caller who named one has already decided, and
	/// telling them their own answer looks wrong is a different and much noisier thing.
	/// </para>
	/// </summary>
	public string? SuspectWrongPlatform(IEnumerable<string> diagnosticMessages)
	{
		if (this is not { PlatformWasChosen: true, Platform: { Length: > 0 } platform }) return null;

		var blamed = diagnosticMessages.Count(message =>
			message.Contains($@"\{platform}\", StringComparison.OrdinalIgnoreCase)
			|| message.Contains($"/{platform}/", StringComparison.OrdinalIgnoreCase));

		if (blamed == 0) return null;

		var alternatives = Available.Platforms
			.Where(candidate => !candidate.Equals(platform, StringComparison.OrdinalIgnoreCase))
			.ToArray();

		var instead = alternatives.Length == 0
			? "Reload with an explicit platform"
			: $"Reload with platform={string.Join(" or platform=", alternatives)}";

		return $"Platform '{platform}' was chosen here rather than asked for, and {blamed} load diagnostic(s) "
			+ "name paths under it -- which is what a wrong platform looks like, because nothing has been "
			+ "built for it and so the in-solution references do not resolve. The projects still load, so "
			+ $"this does not present as a failure. {instead}, or pin the platform in a rosemcp.json beside "
			+ "the solution.";
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

	/// <summary>
	/// Whether a declared platform is this machine's own architecture.
	/// <para>
	/// Preferred over first-declared because a solution build takes the first configuration with the
	/// first platform, which is how an ARM64-first solution ends up building ARM64 on an x64 machine.
	/// Nothing is executed during a load, so the wrong platform is survivable rather than fatal --
	/// but it changes conditional compilation and output paths, and matching the machine is the answer
	/// a person would expect.
	/// </para>
	/// </summary>
	private static bool HostPlatform(string declared) =>
		declared.Replace(" ", string.Empty, StringComparison.Ordinal)
			.Equals(RuntimeInformation.OSArchitecture.ToString(), StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// The value to load under, and whether it was chosen here rather than asked for.
	/// <para>
	/// The second half matters as much as the first. A chosen platform that turns out to be wrong does
	/// not fail: the projects still load, and the references behind them quietly do not resolve. Only
	/// something that knows the value was a guess can say so afterwards.
	/// </para>
	/// </summary>
	private static (string? Value, bool Chosen) Choose(
		string? requested,
		IReadOnlyList<string> declared,
		string msbuildDefault,
		Func<string, bool> prefer,
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

			return (requested, false);
		}

		if (SolutionConfigurations.Declares(declared, msbuildDefault)) return (null, false);

		// First declared is the fallback because that is what a solution build itself would take.
		var chosen = declared.FirstOrDefault(prefer) ?? declared[0];

		notices.Add($"This solution declares no '{msbuildDefault}' {label.ToLowerInvariant()}, so "
			+ $"'{chosen}' was chosen from {string.Join(", ", declared)}. "
			+ $"Pass {label.ToLowerInvariant()} explicitly to load a different one.");

		return (chosen, true);
	}
}
