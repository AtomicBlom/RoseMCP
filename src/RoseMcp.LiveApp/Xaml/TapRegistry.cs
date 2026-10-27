using Microsoft.Extensions.Logging;

using RoseMcp.Contracts;
using RoseMcp.Settings;
using RoseMcp.Symbols;

namespace RoseMcp.LiveApp.Xaml;

/// <summary>
/// The taps dropped into RoseMCP's tap folders, as this host found them: the ones admitted, the folders
/// read, and why any were refused.
/// <para>
/// Read when a session chooses its tap rather than once per host, so a tap dropped in while RoseMCP runs
/// -- or rebuilt into a folder the settings name -- serves the next session that needs it. Reading costs a
/// directory listing and a metadata read per assembly, which is nothing beside loading a tap into an app.
/// </para>
/// </summary>
internal sealed record TapRegistry(IReadOnlyList<DroppedInTap> Taps, IReadOnlyList<string> Folders, IReadOnlyList<string> Refused)
{
	/// <summary>
	/// Reads every tap folder: the one beside the settings file, those the settings name, and those
	/// <see cref="TapFolders.Variable"/> names. A refused tap is logged and said in <see cref="Refused"/>
	/// rather than failing the read, because one bad assembly in a folder is no reason to serve nothing.
	/// </summary>
	internal static TapRegistry Read(ILogger logger)
	{
		var localAppData = Environment.GetEnvironmentVariable(RoseSettingsFile.LocalAppDataVariable);
		var folders = TapFolders.For(RoseSettingsFile.Read(localAppData), localAppData, Environment.GetEnvironmentVariable(TapFolders.Variable));

		var taps = new List<DroppedInTap>();
		var refused = new List<string>();

		foreach (var folder in folders)
		{
			if (!Directory.Exists(folder)) continue;

			foreach (var file in Directory.EnumerateFiles(folder, "*.dll").Order(StringComparer.OrdinalIgnoreCase))
			{
				if (TapManifest.Read(file) is not { } tap) continue;

				var refusal = DroppedInTaps.Refuse(tap, taps);
				if (refusal is null)
				{
					taps.Add(tap);
					continue;
				}

				refused.Add(refusal);
				logger.LogWarning("Refused a dropped-in tap: {Refusal}", refusal);
			}
		}

		return new TapRegistry(taps, folders, refused);
	}

	/// <summary>
	/// How long <see cref="Cached"/> keeps a read. A host's self-report asks on every poll while it does not know
	/// the target's framework, and that question does not need a fresh directory listing each time.
	/// </summary>
	private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

	private static (DateTime ReadUtc, TapRegistry Registry)? _cached;

	/// <summary>
	/// The taps as read within the last half minute, for a question asked on every poll: what framework the target
	/// runs. Choosing a session's tap reads afresh with <see cref="Read"/>, so a tap dropped in is never missed by
	/// the session that needs it.
	/// </summary>
	internal static TapRegistry Cached(ILogger logger)
	{
		var now = DateTime.UtcNow;
		if (_cached is { } cached && now - cached.ReadUtc < CacheFor) return cached.Registry;

		var registry = Read(logger);
		_cached = (now, registry);
		return registry;
	}
}
