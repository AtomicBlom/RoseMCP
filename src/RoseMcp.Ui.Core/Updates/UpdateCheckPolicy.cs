using System.Reflection;

namespace RoseMcp.Ui.Core.Updates;

/// <summary>
/// Whether this build should ask GitHub whether it is out of date.
/// <para>
/// Only a build the release workflow produced should. Everything else would be asking a question it
/// cannot act on and would usually get a wrong answer to: MinVer stamps a build from source with the
/// height since the last tag, so a working tree reports something like <c>1.1.1-alpha.0.9</c>, which
/// is behind the latest release by semver precedence and stays behind no matter how far ahead of it
/// the code actually is. A developer would be told to upgrade to the version they are sitting on top
/// of, every time the tray started, forever.
/// </para>
/// <para>
/// The flag is stamped into the assembly rather than decided at runtime, because "did this binary
/// come out of the release pipeline" is a fact about how it was built and nothing at runtime can see
/// it. The environment variable is for trying the thing out without cutting a release; it is read
/// second so it can turn the check on in a local build and off in a shipped one.
/// </para>
/// </summary>
public static class UpdateCheckPolicy
{
	/// <summary>The assembly metadata key the build stamps. Set by the <c>RoseMcpUpdateCheck</c> MSBuild property.</summary>
	public const string MetadataKey = "RoseMcp.UpdateCheck";

	/// <summary>The override, for testing a build that was not stamped. <c>1</c>/<c>true</c> on, <c>0</c>/<c>false</c> off.</summary>
	public const string EnvironmentVariable = "ROSEMCP_UPDATE_CHECK";

	/// <summary>
	/// Whether <paramref name="assembly"/> was built to check for updates, with
	/// <paramref name="environmentOverride"/> having the last word either way.
	/// <para>
	/// The override is passed in rather than read here, so the decision is a function of its inputs
	/// and a test does not have to set a process-wide variable to exercise it.
	/// </para>
	/// </summary>
	public static bool EnabledFor(Assembly assembly, string? environmentOverride)
	{
		if (TryReadFlag(environmentOverride, out var overridden)) return overridden;

		var stamped = assembly
			.GetCustomAttributes<AssemblyMetadataAttribute>()
			.FirstOrDefault(attribute => string.Equals(attribute.Key, MetadataKey, StringComparison.Ordinal))
			?.Value;

		return TryReadFlag(stamped, out var enabled) && enabled;
	}

	private static bool TryReadFlag(string? text, out bool value)
	{
		value = false;
		if (string.IsNullOrWhiteSpace(text)) return false;

		switch (text.Trim().ToLowerInvariant())
		{
			case "1":
			case "true":
			case "yes":
			case "on":
				value = true;

				return true;

			case "0":
			case "false":
			case "no":
			case "off":
				return true;

			default:
				// Anything else is somebody meaning something this does not understand, and guessing
				// which way they meant it is worse than leaving the build's own answer in place.
				return false;
		}
	}
}
