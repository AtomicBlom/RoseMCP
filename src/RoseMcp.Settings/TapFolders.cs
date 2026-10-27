namespace RoseMcp.Settings;

/// <summary>
/// Where dropped-in taps are looked for, in the order they are looked for.
/// <para>
/// First the <c>taps</c> folder beside <c>settings.json</c>, which is in the install root when RoseMCP is
/// installed where it installs by default, and which installing never clears. Then the folders the
/// settings name, so a tap's build output works without being copied. Then the folders
/// <see cref="Variable"/> names, for a test or a single session. The first tap found for a framework is
/// the one that serves it.
/// </para>
/// </summary>
public static class TapFolders
{
	/// <summary>The environment variable naming more folders to look for taps in, separated as a PATH is.</summary>
	public const string Variable = "ROSEMCP_TAPS";

	/// <summary>The <c>taps</c> folder beside the settings file.</summary>
	/// <param name="localAppData">Where local application data is, for a test that wants its own.</param>
	public static string Default(string? localAppData = null) =>
		Path.Combine(Path.GetDirectoryName(RoseSettingsFile.PathFor(localAppData))!, "taps");

	/// <summary>Every folder to look for taps in, without repeats, in the order they are looked in.</summary>
	/// <param name="settings">What a person has chosen, for the folders it names.</param>
	/// <param name="localAppData">Where local application data is, for a test that wants its own.</param>
	/// <param name="variable">The value of <see cref="Variable"/>, or null where it is unset.</param>
	public static IReadOnlyList<string> For(RoseSettings settings, string? localAppData, string? variable)
	{
		var named = variable is { Length: > 0 }
			? variable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			: [];

		var folders = new List<string>();
		foreach (var folder in new[] { Default(localAppData) }.Concat(settings.TapFolders).Concat(named))
		{
			if (string.IsNullOrWhiteSpace(folder)) continue;

			var full = Path.GetFullPath(folder);
			if (!folders.Contains(full, StringComparer.OrdinalIgnoreCase)) folders.Add(full);
		}

		return folders;
	}
}
